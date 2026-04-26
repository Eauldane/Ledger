using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;

namespace Ledger.Services;

public sealed class FriendListDebugService
{
    private static readonly FriendListDebugSnapshot InitialSnapshot = new(
        false,
        "Refresh the friend list to snapshot names, worlds, and Snowcloak-compatible idents.",
        null,
        []);

    private readonly IFramework _framework;
    private readonly IClientState _clientState;
    private readonly IReadOnlyDictionary<ushort, string> _worldNames;
    private readonly object _sync = new();

    private FriendListDebugSnapshot _snapshot = InitialSnapshot;
    private Task<FriendListDebugSnapshot>? _refreshTask;

    public FriendListDebugService(IFramework framework, IClientState clientState, IDataManager dataManager)
    {
        _framework = framework;
        _clientState = clientState;
        _worldNames = LoadWorldNames(dataManager);
    }

    public FriendListDebugSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return _snapshot;
        }
    }

    public void RequestRefresh()
        => _ = RefreshAsync();

    public Task<FriendListDebugSnapshot> RefreshAsync()
    {
        lock (_sync)
        {
            if (_refreshTask is { IsCompleted: false })
            {
                return _refreshTask;
            }

            _snapshot = _snapshot with
            {
                IsRefreshing = true,
                StatusMessage = "Refreshing friend list snapshot...",
            };

            _refreshTask = RefreshInternalAsync();
            return _refreshTask;
        }
    }

    private async Task<FriendListDebugSnapshot> RefreshInternalAsync()
    {
        try
        {
            var snapshot = await RefreshSnapshotAsync().ConfigureAwait(false);
            lock (_sync)
            {
                _snapshot = snapshot;
            }

            return snapshot;
        }
        catch (Exception ex)
        {
            var failure = new FriendListDebugSnapshot(
                false,
                $"Failed to refresh the friend list: {ex.Message}",
                DateTimeOffset.UtcNow,
                []);

            lock (_sync)
            {
                _snapshot = failure;
            }

            return failure;
        }
    }

    private async Task<FriendListDebugSnapshot> RefreshSnapshotAsync()
    {
        var cachedCapture = await _framework
            .RunOnFrameworkThread(() => CaptureSnapshotUnsafe(requestIfEmpty: false))
            .ConfigureAwait(false);

        if (cachedCapture.Snapshot.Entries.Count > 0 || !cachedCapture.CanRequestData)
        {
            return cachedCapture.Snapshot;
        }

        var requestedCapture = await _framework
            .RunOnFrameworkThread(() => CaptureSnapshotUnsafe(requestIfEmpty: true))
            .ConfigureAwait(false);

        if (requestedCapture.Snapshot.Entries.Count > 0 || !requestedCapture.RequestedData)
        {
            return requestedCapture.Snapshot;
        }

        for (var attempt = 0; attempt < 12; attempt++)
        {
            await Task.Delay(250).ConfigureAwait(false);

            var polledCapture = await _framework
                .RunOnFrameworkThread(() => CaptureSnapshotUnsafe(requestIfEmpty: false))
                .ConfigureAwait(false);

            if (polledCapture.Snapshot.Entries.Count > 0)
            {
                return polledCapture.Snapshot with
                {
                    StatusMessage = "Friend list refreshed.",
                };
            }
        }

        return requestedCapture.Snapshot with
        {
            StatusMessage = "Requested friend list data, but it did not populate in time. Open Social > Friend List once and refresh again.",
        };
    }

    private unsafe FriendListCapture CaptureSnapshotUnsafe(bool requestIfEmpty)
    {
        if (!_clientState.IsLoggedIn)
        {
            return new FriendListCapture(
                new FriendListDebugSnapshot(
                    false,
                    "Log in on a character to read the friend list.",
                    DateTimeOffset.UtcNow,
                    []),
                false,
                false);
        }

        var infoProxy = InfoProxyFriendList.Instance();
        if (infoProxy == null)
        {
            return new FriendListCapture(
                new FriendListDebugSnapshot(
                    false,
                    "The friend list info proxy is unavailable.",
                    DateTimeOffset.UtcNow,
                    []),
                false,
                false);
        }

        var entryCount = (int)Math.Min(infoProxy->GetEntryCount(), int.MaxValue);
        if (entryCount <= 0)
        {
            var requested = requestIfEmpty && infoProxy->RequestData();
            var message = requested
                ? "Requested friend list data. Waiting for the client to repopulate the list..."
                : "Friend list data is empty right now.";

            return new FriendListCapture(
                new FriendListDebugSnapshot(
                    false,
                    message,
                    DateTimeOffset.UtcNow,
                    []),
                true,
                requested);
        }

        var entries = new List<FriendListDebugEntry>(entryCount);
        for (var i = 0; i < entryCount; i++)
        {
            var characterDataPtr = infoProxy->GetEntry((uint)i);
            if (characterDataPtr == null)
            {
                continue;
            }

            var name = characterDataPtr->NameString;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var homeWorldId = characterDataPtr->HomeWorld;
            var currentWorldId = characterDataPtr->CurrentWorld;
            var homeWorldName = ResolveWorldName(homeWorldId);
            var currentWorldName = ResolveWorldName(currentWorldId);
            var worldLabel = currentWorldId != 0 && currentWorldId != homeWorldId
                ? $"{homeWorldName} (visiting {currentWorldName})"
                : homeWorldName;

            entries.Add(new FriendListDebugEntry(
                name,
                homeWorldId,
                worldLabel,
                ComputeIdent(name, homeWorldId),
                characterDataPtr->ContentId));
        }

        entries.Sort(static (left, right) =>
        {
            var byName = string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
            if (byName != 0)
            {
                return byName;
            }

            var byWorld = string.Compare(left.World, right.World, StringComparison.OrdinalIgnoreCase);
            return byWorld != 0
                ? byWorld
                : string.Compare(left.Ident, right.Ident, StringComparison.Ordinal);
        });

        return new FriendListCapture(
            new FriendListDebugSnapshot(
                false,
                "Friend list read from cached client data.",
                DateTimeOffset.UtcNow,
                entries),
            true,
            false);
    }

    private string ResolveWorldName(ushort worldId)
    {
        if (worldId == 0)
        {
            return "Unknown";
        }

        return _worldNames.TryGetValue(worldId, out var worldName)
            ? worldName
            : worldId.ToString(CultureInfo.InvariantCulture);
    }

    private static IReadOnlyDictionary<ushort, string> LoadWorldNames(IDataManager dataManager)
    {
        var sheet = dataManager.GetExcelSheet<World>(dataManager.Language);
        if (sheet is null)
        {
            return new Dictionary<ushort, string>();
        }

        return sheet
            .Where(row => row.RowId > 0 && !string.IsNullOrWhiteSpace(row.Name.ToString()))
            .ToDictionary(row => (ushort)row.RowId, row => row.Name.ToString());
    }

    private static string ComputeIdent(string name, ushort worldId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(name + worldId.ToString(CultureInfo.InvariantCulture)));
        return Convert.ToHexString(bytes);
    }
}

internal sealed record FriendListCapture(
    FriendListDebugSnapshot Snapshot,
    bool CanRequestData,
    bool RequestedData);

public sealed record FriendListDebugSnapshot(
    bool IsRefreshing,
    string StatusMessage,
    DateTimeOffset? LastUpdatedUtc,
    IReadOnlyList<FriendListDebugEntry> Entries);

public sealed record FriendListDebugEntry(
    string Name,
    ushort HomeWorldId,
    string World,
    string Ident,
    ulong ContentId);
