using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Ledger.Models;

namespace Ledger.Services;

public sealed class LedgerServerService : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RequestCooldown = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan SyncCooldown = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly CollectionDataService _collections;
    private readonly ConfigService _config;
    private readonly FriendListDebugService _friendListDebug;
    private readonly PlayerIdentityService _playerIdentity;
    private readonly HttpClient _httpClient = new()
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };
    private readonly object _sync = new();
    private readonly Dictionary<ComparisonKey, ComparisonStateSnapshot> _comparisonStates = new();
    private readonly Dictionary<OwnerKey, OwnerStateSnapshot> _ownerStates = new();

    private LedgerServerStatus _status = LedgerServerStatus.Initial;
    private Task? _syncTask;
    private Task? _healthCheckTask;

    public LedgerServerService(
        CollectionDataService collections,
        ConfigService config,
        FriendListDebugService friendListDebug,
        PlayerIdentityService playerIdentity)
    {
        _collections = collections;
        _config = config;
        _friendListDebug = friendListDebug;
        _playerIdentity = playerIdentity;
    }

    public LedgerServerStatus GetStatus()
    {
        lock (_sync)
        {
            return ApplyConfiguration(_status);
        }
    }

    public ComparisonStateSnapshot GetComparisonState(CollectionKind kind, CompareScope scope)
    {
        lock (_sync)
        {
            return _comparisonStates.TryGetValue(new ComparisonKey(kind, scope), out var state)
                ? state
                : CreateComparisonState(scope);
        }
    }

    public OwnerStateSnapshot GetOwnerState(CollectionKind kind, CompareScope scope, int collectableId)
    {
        lock (_sync)
        {
            return _ownerStates.TryGetValue(new OwnerKey(kind, scope, collectableId), out var state)
                ? state
                : OwnerStateSnapshot.Initial;
        }
    }

    public void RequestHealthCheck(bool force = false)
    {
        lock (_sync)
        {
            if (_healthCheckTask is { IsCompleted: false })
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            if (!force && _status.LastAttemptUtc.HasValue && now - _status.LastAttemptUtc.Value < RequestCooldown)
            {
                return;
            }

            _status = ApplyConfiguration(_status) with
            {
                IsCheckingConnection = true,
                StatusMessage = "Checking server connection...",
                LastAttemptUtc = now,
                LastErrorMessage = null,
            };

            _healthCheckTask = HealthCheckAsync();
        }
    }

    public void RequestSyncAll(bool force = false)
    {
        lock (_sync)
        {
            if (_syncTask is { IsCompleted: false })
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            if (!force && _status.LastAttemptUtc.HasValue && now - _status.LastAttemptUtc.Value < SyncCooldown)
            {
                return;
            }

            _status = ApplyConfiguration(_status) with
            {
                IsSyncing = true,
                StatusMessage = "Syncing player data to the server...",
                LastAttemptUtc = now,
                LastErrorMessage = null,
            };

            _syncTask = SyncAllAsync();
        }
    }

    public void RequestComparison(CollectionKind kind, CompareScope scope, bool force = false)
    {
        ComparisonKey key;
        lock (_sync)
        {
            key = new ComparisonKey(kind, scope);
            var current = _comparisonStates.TryGetValue(key, out var state)
                ? state
                : CreateComparisonState(scope);

            var now = DateTimeOffset.UtcNow;
            if (current.IsLoading)
            {
                return;
            }

            if (!force && current.LastAttemptUtc.HasValue && now - current.LastAttemptUtc.Value < RequestCooldown)
            {
                return;
            }

            _comparisonStates[key] = current with
            {
                IsLoading = true,
                StatusMessage = "Loading comparison data...",
                LastAttemptUtc = now,
            };
        }

        _ = LoadComparisonAsync(key);
    }

    public void RequestOwners(CollectionKind kind, CompareScope scope, int collectableId, bool force = false)
    {
        if (collectableId <= 0)
        {
            return;
        }

        OwnerKey key;
        lock (_sync)
        {
            key = new OwnerKey(kind, scope, collectableId);
            var current = _ownerStates.TryGetValue(key, out var state)
                ? state
                : OwnerStateSnapshot.Initial;

            var now = DateTimeOffset.UtcNow;
            if (current.IsLoading)
            {
                return;
            }

            if (!force && current.LastAttemptUtc.HasValue && now - current.LastAttemptUtc.Value < RequestCooldown)
            {
                return;
            }

            _ownerStates[key] = current with
            {
                IsLoading = true,
                StatusMessage = "Loading owner list...",
                LastAttemptUtc = now,
            };
        }

        _ = LoadOwnersAsync(key);
    }

    public void Dispose()
        => _httpClient.Dispose();

    private async Task HealthCheckAsync()
    {
        try
        {
            if (!TryGetServerBaseUri(out var baseUri, out var configurationError))
            {
                UpdateStatusFailure(configurationError, null, clearBusyFlags: true);
                return;
            }

            using var cts = CreateTimeoutTokenSource();
            using var response = await _httpClient.GetAsync(new Uri(baseUri, "health"), cts.Token).ConfigureAwait(false);
            await EnsureSuccessStatusCodeAsync(response, cts.Token).ConfigureAwait(false);
            UpdateStatusSuccess("Server reachable.", isSync: false, friendSyncMessage: null);
        }
        catch (Exception ex)
        {
            UpdateStatusFailure("Failed to reach the server.", ex, clearBusyFlags: true);
        }
    }

    private async Task SyncAllAsync()
    {
        try
        {
            if (!TryGetServerBaseUri(out var baseUri, out var configurationError))
            {
                UpdateStatusFailure(configurationError, null, clearBusyFlags: true);
                return;
            }

            var identity = await _playerIdentity.GetLocalPlayerIdentityAsync().ConfigureAwait(false);
            if (identity is null)
            {
                UpdateStatusFailure("Log in on a character before syncing.", null, clearBusyFlags: true);
                return;
            }

            var collections = CollectionKindExtensions.All
                .Select(kind => _collections.GetSnapshot(kind))
                .Select(snapshot => new SyncCollectionRequest
                {
                    Kind = snapshot.Kind,
                    OwnershipLoaded = snapshot.OwnershipLoaded,
                    OwnedIds = snapshot.OwnershipLoaded
                        ? snapshot.Entries.Where(entry => entry.Owned).Select(entry => (int)entry.Id).ToArray()
                        : [],
                })
                .ToArray();

            var syncPlayerRequest = new SyncPlayerRequest
            {
                Ident = identity.Ident,
                CharacterName = identity.CharacterName,
                HomeWorldId = identity.HomeWorldId,
                DatacenterId = identity.DatacenterId,
                RegionId = identity.RegionId,
                Collections = collections,
            };

            using (var cts = CreateTimeoutTokenSource())
            {
                await PostJsonAsync<SyncPlayerRequest, SyncPlayerResponse>(new Uri(baseUri, "api/players/sync"), syncPlayerRequest, cts.Token)
                    .ConfigureAwait(false);
            }

            string? friendSyncMessage = null;
            var friendSnapshot = await _friendListDebug.RefreshAsync().ConfigureAwait(false);
            var friendIdents = friendSnapshot.Entries
                .Select(entry => entry.Ident)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (friendIdents.Length > 0)
            {
                using var cts = CreateTimeoutTokenSource();
                var friendsResponse = await PostJsonAsync<SyncFriendsRequest, SyncFriendsResponse>(
                        new Uri(baseUri, $"api/players/{Uri.EscapeDataString(identity.Ident)}/friends"),
                        new SyncFriendsRequest
                        {
                            FriendIdents = friendIdents,
                        },
                        cts.Token)
                    .ConfigureAwait(false);

                friendSyncMessage = $"Friends synced: {friendsResponse.FriendCount:N0} total, {friendsResponse.ResolvedFriendCount:N0} resolved.";
            }
            else if (friendSnapshot.Entries.Count == 0 && !string.IsNullOrWhiteSpace(friendSnapshot.StatusMessage))
            {
                friendSyncMessage = $"Friend sync skipped: {friendSnapshot.StatusMessage}";
            }

            UpdateStatusSuccess(
                $"Synced {collections.Length:N0} collections for {identity.CharacterName}.",
                isSync: true,
                friendSyncMessage: friendSyncMessage);

            ComparisonKey[] comparisonKeys;
            lock (_sync)
            {
                comparisonKeys = _comparisonStates.Keys.ToArray();
                _ownerStates.Clear();
            }

            foreach (var key in comparisonKeys)
            {
                RequestComparison(key.Kind, key.Scope, force: true);
            }
        }
        catch (Exception ex)
        {
            UpdateStatusFailure("Failed to sync with the server.", ex, clearBusyFlags: true);
        }
    }

    private async Task LoadComparisonAsync(ComparisonKey key)
    {
        try
        {
            if (!TryGetServerBaseUri(out var baseUri, out var configurationError))
            {
                UpdateComparisonFailure(key, configurationError, null);
                return;
            }

            var identity = await _playerIdentity.GetLocalPlayerIdentityAsync().ConfigureAwait(false);
            if (identity is null)
            {
                UpdateComparisonFailure(key, "Log in on a character before loading comparison data.", null);
                return;
            }

            var relativeUri = $"api/compare/{key.Scope.ToApiValue()}?requesterIdent={Uri.EscapeDataString(identity.Ident)}&collectionKind={Uri.EscapeDataString(key.Kind.ToString())}";
            CompareCollectionResponse result;
            using (var cts = CreateTimeoutTokenSource())
            {
                result = await GetJsonAsync<CompareCollectionResponse>(new Uri(baseUri, relativeUri), cts.Token).ConfigureAwait(false);
            }

            lock (_sync)
            {
                var current = _comparisonStates.TryGetValue(key, out var state)
                    ? state
                    : CreateComparisonState(key.Scope);

                _comparisonStates[key] = current with
                {
                    IsLoading = false,
                    StatusMessage = $"Loaded {result.LoadedPlayerCount:N0} synced players from {key.Scope.ToDisplayName().ToLowerInvariant()}.",
                    LastUpdatedUtc = DateTimeOffset.UtcNow,
                    Result = result,
                };
            }

            UpdateStatusSuccess("Comparison data refreshed.", isSync: false, friendSyncMessage: null);
        }
        catch (Exception ex)
        {
            UpdateComparisonFailure(key, "Failed to load comparison data.", ex);
        }
    }

    private async Task LoadOwnersAsync(OwnerKey key)
    {
        try
        {
            if (!TryGetServerBaseUri(out var baseUri, out var configurationError))
            {
                UpdateOwnerFailure(key, configurationError, null);
                return;
            }

            var identity = await _playerIdentity.GetLocalPlayerIdentityAsync().ConfigureAwait(false);
            if (identity is null)
            {
                UpdateOwnerFailure(key, "Log in on a character before loading owner details.", null);
                return;
            }

            var relativeUri = $"api/compare/{key.Scope.ToApiValue()}/owners?requesterIdent={Uri.EscapeDataString(identity.Ident)}&collectionKind={Uri.EscapeDataString(key.Kind.ToString())}&collectableId={key.CollectableId}";
            CompareOwnersResponse result;
            using (var cts = CreateTimeoutTokenSource())
            {
                result = await GetJsonAsync<CompareOwnersResponse>(new Uri(baseUri, relativeUri), cts.Token).ConfigureAwait(false);
            }

            lock (_sync)
            {
                _ownerStates[key] = new OwnerStateSnapshot(
                    false,
                    result.OwnerCount == 0 ? "Nobody in this scope has that unlock yet." : $"Loaded {result.OwnerCount:N0} owners.",
                    DateTimeOffset.UtcNow,
                    _ownerStates.TryGetValue(key, out var state) ? state.LastAttemptUtc : DateTimeOffset.UtcNow,
                    result);
            }

            UpdateStatusSuccess("Owner detail refreshed.", isSync: false, friendSyncMessage: null);
        }
        catch (Exception ex)
        {
            UpdateOwnerFailure(key, "Failed to load owner detail.", ex);
        }
    }

    private void UpdateComparisonFailure(ComparisonKey key, string message, Exception? ex)
    {
        lock (_sync)
        {
            var current = _comparisonStates.TryGetValue(key, out var state)
                ? state
                : CreateComparisonState(key.Scope);

            var detail = ex is null ? message : $"{message} {GetFriendlyErrorMessage(ex)}";
            if (current.Result is not null)
            {
                detail += " Showing cached data.";
            }

            _comparisonStates[key] = current with
            {
                IsLoading = false,
                StatusMessage = detail,
                Result = current.Result,
            };
        }

        UpdateStatusFailure(message, ex, clearBusyFlags: false);
    }

    private void UpdateOwnerFailure(OwnerKey key, string message, Exception? ex)
    {
        lock (_sync)
        {
            var current = _ownerStates.TryGetValue(key, out var state)
                ? state
                : OwnerStateSnapshot.Initial;

            _ownerStates[key] = current with
            {
                IsLoading = false,
                StatusMessage = ex is null ? message : $"{message} {GetFriendlyErrorMessage(ex)}",
            };
        }

        UpdateStatusFailure(message, ex, clearBusyFlags: false);
    }

    private void UpdateStatusSuccess(string message, bool isSync, string? friendSyncMessage)
    {
        lock (_sync)
        {
            _status = ApplyConfiguration(_status) with
            {
                IsSyncing = false,
                IsCheckingConnection = false,
                StatusMessage = message,
                FriendSyncMessage = friendSyncMessage ?? _status.FriendSyncMessage,
                LastErrorMessage = null,
                LastSuccessfulRequestUtc = DateTimeOffset.UtcNow,
                LastSuccessfulSyncUtc = isSync ? DateTimeOffset.UtcNow : _status.LastSuccessfulSyncUtc,
            };
        }
    }

    private void UpdateStatusFailure(string message, Exception? ex, bool clearBusyFlags)
    {
        lock (_sync)
        {
            _status = ApplyConfiguration(_status) with
            {
                IsSyncing = clearBusyFlags ? false : _status.IsSyncing,
                IsCheckingConnection = clearBusyFlags ? false : _status.IsCheckingConnection,
                StatusMessage = ex is null ? message : $"{message} {GetFriendlyErrorMessage(ex)}",
                LastErrorMessage = ex is null ? message : $"{message} {GetFriendlyErrorMessage(ex)}",
            };
        }
    }

    private bool TryGetServerBaseUri(out Uri baseUri, out string errorMessage)
    {
        var serverBaseUrl = _config.Current.ServerBaseUrl?.Trim() ?? string.Empty;
        if (!_config.Current.EnableServerComparison)
        {
            baseUri = null!;
            errorMessage = "Server sync is disabled.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(serverBaseUrl))
        {
            baseUri = null!;
            errorMessage = "Configure a server URL before using comparisons.";
            return false;
        }

        if (!Uri.TryCreate(serverBaseUrl.EndsWith('/') ? serverBaseUrl : $"{serverBaseUrl}/", UriKind.Absolute, out var parsedUri))
        {
            baseUri = null!;
            errorMessage = "The configured server URL is invalid.";
            return false;
        }

        baseUri = parsedUri;
        errorMessage = string.Empty;
        return true;
    }

    private async Task<TResponse> GetJsonAsync<TResponse>(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        return await ReadJsonResponseAsync<TResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TResponse> PostJsonAsync<TRequest, TResponse>(Uri uri, TRequest request, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync(uri, request, JsonOptions, cancellationToken).ConfigureAwait(false);
        return await ReadJsonResponseAsync<TResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<TResponse> ReadJsonResponseAsync<TResponse>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await EnsureSuccessStatusCodeAsync(response, cancellationToken).ConfigureAwait(false);

        var result = await response.Content.ReadFromJsonAsync<TResponse>(JsonOptions, cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            throw new InvalidOperationException("The server returned an empty response.");
        }

        return result;
    }

    private static async Task EnsureSuccessStatusCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException("The server does not know this player yet. Sync first.");
        }

        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(responseBody)
                ? $"The server returned {(int)response.StatusCode} {response.ReasonPhrase}."
                : $"The server returned {(int)response.StatusCode} {response.ReasonPhrase}: {responseBody}");
    }

    private static string GetFriendlyErrorMessage(Exception ex)
        => ex switch
        {
            TaskCanceledException => "The request timed out.",
            HttpRequestException => "The server is unavailable.",
            _ => ex.Message,
        };

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static CancellationTokenSource CreateTimeoutTokenSource()
    {
        var cts = new CancellationTokenSource();
        cts.CancelAfter(RequestTimeout);
        return cts;
    }

    private LedgerServerStatus ApplyConfiguration(LedgerServerStatus status)
    {
        var serverBaseUrl = string.IsNullOrWhiteSpace(_config.Current.ServerBaseUrl)
            ? null
            : _config.Current.ServerBaseUrl.Trim();

        return status with
        {
            Enabled = _config.Current.EnableServerComparison,
            Configured = serverBaseUrl is not null,
            ServerBaseUrl = serverBaseUrl,
        };
    }

    private static ComparisonStateSnapshot CreateComparisonState(CompareScope scope)
        => new(
            scope,
            false,
            "Refresh comparison data to load this scope.",
            null,
            null,
            null);

    private readonly record struct ComparisonKey(CollectionKind Kind, CompareScope Scope);

    private readonly record struct OwnerKey(CollectionKind Kind, CompareScope Scope, int CollectableId);
}

public sealed record LedgerServerStatus(
    bool Enabled,
    bool Configured,
    bool IsSyncing,
    bool IsCheckingConnection,
    string StatusMessage,
    string? FriendSyncMessage,
    string? LastErrorMessage,
    DateTimeOffset? LastAttemptUtc,
    DateTimeOffset? LastSuccessfulRequestUtc,
    DateTimeOffset? LastSuccessfulSyncUtc,
    string? ServerBaseUrl)
{
    public static readonly LedgerServerStatus Initial = new(
        false,
        false,
        false,
        false,
        "Server sync is disabled.",
        null,
        null,
        null,
        null,
        null,
        null);

    public bool IsBusy
        => IsSyncing || IsCheckingConnection;
}

public sealed record ComparisonStateSnapshot(
    CompareScope Scope,
    bool IsLoading,
    string StatusMessage,
    DateTimeOffset? LastUpdatedUtc,
    DateTimeOffset? LastAttemptUtc,
    CompareCollectionResponse? Result);

public sealed record OwnerStateSnapshot(
    bool IsLoading,
    string StatusMessage,
    DateTimeOffset? LastUpdatedUtc,
    DateTimeOffset? LastAttemptUtc,
    CompareOwnersResponse? Result)
{
    public static readonly OwnerStateSnapshot Initial = new(
        false,
        "Select an item to load owner detail.",
        null,
        null,
        null);
}
