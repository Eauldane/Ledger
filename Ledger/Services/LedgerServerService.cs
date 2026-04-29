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
using Dalamud.Plugin.Services;
using ElezenTools.Data;
using Ledger.Config;
using Ledger.Models;

namespace Ledger.Services;

public sealed class LedgerServerService : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RequestCooldown = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan SyncCooldown = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AutoSyncCheckInterval = TimeSpan.FromSeconds(3);
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly CollectionDataService _collections;
    private readonly ConfigService _config;
    private readonly IFramework _framework;
    private readonly PlayerIdentityService _playerIdentity;
    private readonly HttpClient _httpClient = new()
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };
    private readonly object _sync = new();
    private readonly Dictionary<ComparisonKey, ComparisonStateSnapshot> _comparisonStates = new();

    private LedgerServerStatus _status = LedgerServerStatus.Initial;
    private Task? _syncTask;
    private Task? _healthCheckTask;
    private PreparedSyncState? _lastSuccessfulSyncState;
    private DateTimeOffset _nextAutoSyncCheckUtc = DateTimeOffset.MinValue;

    public LedgerServerService(
        CollectionDataService collections,
        ConfigService config,
        PlayerIdentityService playerIdentity,
        IFramework framework)
    {
        _collections = collections;
        _config = config;
        _playerIdentity = playerIdentity;
        _framework = framework;
        _framework.Update += OnFrameworkUpdate;
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
                StatusMessage = "Syncing loaded collection data and friend list data to the server...",
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

    public void Dispose()
    {
        _framework.Update -= OnFrameworkUpdate;
        _httpClient.Dispose();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (framework.IsFrameworkUnloading || !_config.Current.EnableServerComparison)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now < _nextAutoSyncCheckUtc)
        {
            return;
        }

        _nextAutoSyncCheckUtc = now + AutoSyncCheckInterval;

        var currentState = CaptureCurrentSyncState(loadedOnly: true);
        if (currentState is null || currentState.Collections.Count == 0)
        {
            return;
        }

        PreparedSyncState? lastSuccessfulState;
        lock (_sync)
        {
            lastSuccessfulState = _lastSuccessfulSyncState;
        }

        if (lastSuccessfulState is not null && SyncStatesMatch(lastSuccessfulState, currentState))
        {
            return;
        }

        RequestSyncAll();
    }

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

            var syncState = await CaptureCurrentSyncStateAsync(loadedOnly: true).ConfigureAwait(false);
            if (syncState is null)
            {
                UpdateStatusFailure("Log in on a character before syncing.", null, clearBusyFlags: true);
                return;
            }

            if (syncState.Collections.Count == 0)
            {
                UpdateStatusFailure("No loaded collection data is ready to sync yet.", null, clearBusyFlags: true);
                return;
            }

            var syncPlayerRequest = ToSyncPlayerRequest(syncState);
            using (var cts = CreateTimeoutTokenSource())
            {
                await PostJsonAsync<SyncPlayerRequest, SyncPlayerResponse>(new Uri(baseUri, "api/players/sync"), syncPlayerRequest, cts.Token)
                    .ConfigureAwait(false);
            }

            var friendSyncMessage = await TrySyncFriendsAsync(baseUri, syncState.Ident).ConfigureAwait(false);

            UpdateStatusSuccess(
                $"Synced {syncState.Collections.Count:N0} collections.",
                isSync: true,
                friendSyncMessage: friendSyncMessage);

            ComparisonKey[] comparisonKeys;
            lock (_sync)
            {
                _lastSuccessfulSyncState = syncState;
                comparisonKeys = _comparisonStates.Keys.ToArray();
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
                    StatusMessage = string.Empty,
                    LastUpdatedUtc = DateTimeOffset.UtcNow,
                    Result = result,
                };
            }

            UpdateStatusSuccess("Data refreshed.", isSync: false, friendSyncMessage: null);
        }
        catch (Exception ex)
        {
            UpdateComparisonFailure(key, "Failed to load data.", ex);
        }
    }

    private async Task<string?> TrySyncFriendsAsync(Uri baseUri, string ident)
    {
        try
        {
            var friendIdents = (await ElezenData.Friends.RefreshAsync().ConfigureAwait(false))
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Name) && entry.HomeWorldId != 0)
                .Select(entry => PlayerIdentityService.ComputeIdent(entry.Name, entry.HomeWorldId))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (friendIdents.Length == 0)
            {
                return null;
            }

            using var cts = CreateTimeoutTokenSource();
            await PostJsonAsync<SyncFriendsRequest, SyncFriendsResponse>(
                    new Uri(baseUri, $"api/players/{Uri.EscapeDataString(ident)}/friends"),
                    new SyncFriendsRequest
                    {
                        FriendIdents = friendIdents,
                    },
                    cts.Token)
                .ConfigureAwait(false);

            return null;
        }
        catch (Exception ex)
        {
            return $"Friend sync skipped. {GetFriendlyErrorMessage(ex)}";
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

    private void UpdateStatusSuccess(string message, bool isSync, string? friendSyncMessage)
    {
        lock (_sync)
        {
            _status = ApplyConfiguration(_status) with
            {
                IsSyncing = false,
                IsCheckingConnection = false,
                StatusMessage = message,
                FriendSyncMessage = friendSyncMessage,
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
        if (!_config.Current.EnableServerComparison)
        {
            baseUri = null!;
            errorMessage = "Server sync is disabled.";
            return false;
        }

        if (!Uri.TryCreate(LedgerServerDefaults.BaseUrl, UriKind.Absolute, out var parsedUri))
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
        var configuredStatus = status with
        {
            Enabled = _config.Current.EnableServerComparison,
            Configured = true,
            ServerBaseUrl = LedgerServerDefaults.BaseUrl,
        };

        if (!configuredStatus.Enabled)
        {
            return configuredStatus with
            {
                IsSyncing = false,
                IsCheckingConnection = false,
                StatusMessage = "Server sync is disabled.",
                LastErrorMessage = null,
            };
        }

        return configuredStatus;
    }

    private PreparedSyncState? CaptureCurrentSyncState(bool loadedOnly)
    {
        var identity = _playerIdentity.GetLocalPlayerIdentityOnFrameworkThread();
        return identity is null ? null : CreatePreparedSyncState(identity, loadedOnly);
    }

    private async Task<PreparedSyncState?> CaptureCurrentSyncStateAsync(bool loadedOnly)
    {
        var identity = await _playerIdentity.GetLocalPlayerIdentityAsync().ConfigureAwait(false);
        return identity is null ? null : CreatePreparedSyncState(identity, loadedOnly);
    }

    private PreparedSyncState CreatePreparedSyncState(LocalPlayerIdentity identity, bool loadedOnly)
    {
        var collections = _collections.BuildSyncCollections(loadedOnly)
            .Select(collection => new PreparedCollectionSync(
                collection.Kind,
                collection.OwnershipLoaded,
                collection.OwnedIds.ToArray()))
            .ToArray();

        return new PreparedSyncState(
            identity.Ident,
            identity.HomeWorldId,
            identity.DatacenterId,
            identity.RegionId,
            collections);
    }

    private static SyncPlayerRequest ToSyncPlayerRequest(PreparedSyncState state)
        => new()
        {
            Ident = state.Ident,
            HomeWorldId = state.HomeWorldId,
            DatacenterId = state.DatacenterId,
            RegionId = state.RegionId,
            Collections = state.Collections
                .Select(collection => new SyncCollectionRequest
                {
                    Kind = collection.Kind,
                    OwnershipLoaded = collection.OwnershipLoaded,
                    OwnedIds = collection.OwnedIds,
                })
                .ToArray(),
        };

    private static bool SyncStatesMatch(PreparedSyncState left, PreparedSyncState right)
    {
        if (!string.Equals(left.Ident, right.Ident, StringComparison.Ordinal)
            || left.HomeWorldId != right.HomeWorldId
            || left.DatacenterId != right.DatacenterId
            || left.RegionId != right.RegionId
            || left.Collections.Count != right.Collections.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Collections.Count; i++)
        {
            var leftCollection = left.Collections[i];
            var rightCollection = right.Collections[i];
            if (leftCollection.Kind != rightCollection.Kind
                || leftCollection.OwnershipLoaded != rightCollection.OwnershipLoaded
                || !leftCollection.OwnedIds.SequenceEqual(rightCollection.OwnedIds))
            {
                return false;
            }
        }

        return true;
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

    private sealed record PreparedSyncState(
        string Ident,
        int HomeWorldId,
        int DatacenterId,
        int RegionId,
        IReadOnlyList<PreparedCollectionSync> Collections);

    private sealed record PreparedCollectionSync(
        CollectionKind Kind,
        bool OwnershipLoaded,
        int[] OwnedIds);
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
