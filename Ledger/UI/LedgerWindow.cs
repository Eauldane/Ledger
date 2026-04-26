using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using ElezenTools.UI;
using Ledger.Data;
using Ledger.Models;
using Ledger.Services;

namespace Ledger.UI;

public sealed class LedgerWindow : Window
{
    private static readonly Vector4 OwnedColor = new(0.55f, 0.88f, 0.55f, 1f);
    private static readonly Vector4 MissingColor = new(0.95f, 0.55f, 0.42f, 1f);
    private static readonly Vector4 WarningColor = new(0.95f, 0.74f, 0.35f, 1f);

    private readonly CollectionDataService _collections;
    private readonly ConfigService _config;
    private readonly FriendListDebugService _friendListDebug;
    private readonly LedgerServerService _server;
    private readonly PlayerIdentityService _playerIdentity;
    private readonly Dictionary<CollectionKind, CompareScope> _selectedScopes = [];
    private readonly Dictionary<CollectionKind, int?> _selectedCollectableIds = [];

    private CollectionKind _selectedKind = CollectionKind.Achievements;
    private string _searchText = string.Empty;
    private bool _missingOnly = true;
    private bool _showExcluded;
    private string _serverBaseUrlInput;

    public LedgerWindow(
        CollectionDataService collections,
        ConfigService config,
        FriendListDebugService friendListDebug,
        LedgerServerService server,
        PlayerIdentityService playerIdentity)
        : base("Ledger")
    {
        _collections = collections;
        _config = config;
        _friendListDebug = friendListDebug;
        _server = server;
        _playerIdentity = playerIdentity;
        _serverBaseUrlInput = config.Current.ServerBaseUrl;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(860, 620),
            MaximumSize = new Vector2(1900, 1400),
        };
    }

    public override void Draw()
    {
        DrawOverview();
        ImGui.Separator();
        DrawCategoryTabs();
    }

    private void DrawOverview()
    {
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.SyncAlt, "Refresh"))
        {
            _collections.Invalidate();
        }
        ElezenImgui.AttachTooltip("Rebuild the cached collection snapshots and refresh the progress view.");

        ImGui.SameLine();
        if (ImGui.Button("Sync Server"))
        {
            _server.RequestSyncAll(force: true);
        }
        ElezenImgui.DrawHelpText("Sync your currently loaded collection data and friend list snapshot to the Ledger server.");

        var serverStatus = _server.GetStatus();
        if (!string.IsNullOrWhiteSpace(serverStatus.StatusMessage))
        {
            ImGui.SameLine();
            var statusColor = serverStatus.LastErrorMessage is null ? OwnedColor : WarningColor;
            ImGui.TextColored(statusColor, serverStatus.StatusMessage);
        }

        var changed = false;
        ImGui.SameLine();
        var excludeLimited = _config.Current.ExcludeLimitedFromAttainable;
        if (DrawCheckboxWithHelp(
                "Exclude limited-time items",
                ref excludeLimited,
                "Excludes items that were only available for a limited time - for example, achievements only obtainable during Valentione's Day events, etc."))
        {
            _config.Current.ExcludeLimitedFromAttainable = excludeLimited;
            changed = true;
        }

        ImGui.SameLine();
        var excludePremium = _config.Current.ExcludePremiumFromAttainable;
        if (DrawCheckboxWithHelp(
                "Exclude Mog Station items",
                ref excludePremium,
                "Excludes items that are only available through Mog Station."))
        {
            _config.Current.ExcludePremiumFromAttainable = excludePremium;
            changed = true;
        }

        ImGui.SameLine();
        var excludeRetiredPvp = _config.Current.ExcludeRetiredPvpFromAttainable;
        if (DrawCheckboxWithHelp(
                "Exclude retired PvP items",
                ref excludeRetiredPvp,
                "Excludes retired PvP rewards."))
        {
            _config.Current.ExcludeRetiredPvpFromAttainable = excludeRetiredPvp;
            changed = true;
        }

        if (changed)
        {
            _config.Save();
            _collections.Invalidate();
        }

        if (!LimitedCollectables.IsLoaded)
        {
            ImGui.TextColored(WarningColor, $"Exclusion data was not loaded: {LimitedCollectables.LoadError}");
        }

        if (serverStatus.LastSuccessfulSyncUtc.HasValue)
        {
            ImGui.TextDisabled($"Last server sync {serverStatus.LastSuccessfulSyncUtc.Value.ToLocalTime():HH:mm:ss}");
        }

        if (!string.IsNullOrWhiteSpace(serverStatus.FriendSyncMessage))
        {
            ImGui.TextDisabled(serverStatus.FriendSyncMessage);
        }

        ImGui.Spacing();

        ImGui.Columns(2, "##ledgerOverview", false);
        foreach (var kind in CollectionKindExtensions.All)
        {
            DrawOverviewEntry(_collections.GetSnapshot(kind));
            ImGui.NextColumn();
        }

        ImGui.Columns(1);
    }

    private static void DrawOverviewEntry(CollectionSnapshot snapshot)
    {
        DrawAttainableProgress(
            snapshot,
            snapshot.Kind.ToDisplayName(),
            $"{snapshot.AttainableOwned:N0} / {snapshot.AttainableTotal:N0} available",
            18f);

        ImGui.Spacing();
    }

    private void DrawCategoryTabs()
    {
        if (!ImGui.BeginTabBar("##ledgerCategories"))
        {
            return;
        }

        foreach (var kind in CollectionKindExtensions.All)
        {
            if (!ImGui.BeginTabItem(kind.ToDisplayName()))
            {
                continue;
            }

            _selectedKind = kind;
            DrawSelectedCategory(_collections.GetSnapshot(kind));
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Debug"))
        {
            DrawDebugTab();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawSelectedCategory(CollectionSnapshot snapshot)
    {
        var scope = GetSelectedScope(snapshot.Kind);
        var comparisonState = _server.GetComparisonState(snapshot.Kind, scope);
        if (_config.Current.EnableServerComparison
            && !string.IsNullOrWhiteSpace(_config.Current.ServerBaseUrl)
            && comparisonState.Result is null
            && !comparisonState.IsLoading)
        {
            _server.RequestComparison(snapshot.Kind, scope);
            comparisonState = _server.GetComparisonState(snapshot.Kind, scope);
        }

        DrawSnapshotHeader(snapshot);
        ImGui.Separator();
        DrawComparisonPanel(snapshot, ref scope, comparisonState);
        comparisonState = _server.GetComparisonState(snapshot.Kind, scope);
        ImGui.Separator();
        DrawFilters(snapshot);
        ImGui.Separator();
        DrawEntryTable(snapshot, scope, comparisonState);
    }

    private void DrawComparisonPanel(CollectionSnapshot snapshot, ref CompareScope scope, ComparisonStateSnapshot comparisonState)
    {
        var serverStatus = _server.GetStatus();

        ImGui.TextUnformatted("Comparison");
        ImGui.SameLine();
        ImGui.TextDisabled("Syncs your local collection state to the server, then compares this collection against the selected cohort.");

        if (ImGui.BeginCombo("Scope", scope.ToDisplayName()))
        {
            foreach (var option in CompareScopeExtensions.AllScopes)
            {
                var selected = option == scope;
                if (ImGui.Selectable(option.ToDisplayName(), selected))
                {
                    scope = option;
                    _selectedScopes[snapshot.Kind] = option;
                    _selectedCollectableIds[snapshot.Kind] = null;
                    _server.RequestComparison(snapshot.Kind, option, force: true);
                }

                if (selected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        if (ImGui.Button("Refresh Comparison"))
        {
            _server.RequestComparison(snapshot.Kind, scope, force: true);
        }

        ImGui.SameLine();
        if (ImGui.Button("Sync and Refresh"))
        {
            _server.RequestSyncAll(force: true);
        }

        if (!serverStatus.Enabled)
        {
            ImGui.TextColored(WarningColor, "Server comparison is disabled. Enable it in the Debug tab.");
            return;
        }

        if (!serverStatus.Configured)
        {
            ImGui.TextColored(WarningColor, "Configure a server URL in the Debug tab before using comparisons.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(comparisonState.StatusMessage))
        {
            var stateColor = comparisonState.Result is not null && !comparisonState.IsLoading
                ? OwnedColor
                : WarningColor;
            ImGui.TextColored(stateColor, comparisonState.StatusMessage);
        }

        if (comparisonState.LastUpdatedUtc.HasValue)
        {
            ImGui.TextDisabled($"Last comparison refresh {comparisonState.LastUpdatedUtc.Value.ToLocalTime():HH:mm:ss}");
        }

        if (comparisonState.Result is null)
        {
            return;
        }

        DrawSummaryCards(snapshot, comparisonState.Result);

        if (comparisonState.Result.TopPlayers.Count == 0)
        {
            return;
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Top Players");
        if (!ImGui.BeginTable(
                $"##ledgerTopPlayers_{snapshot.Kind}_{scope}",
                4,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
        {
            return;
        }

        ImGui.TableSetupColumn("Rank", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthFixed, 160f);
        ImGui.TableSetupColumn("Owned", ImGuiTableColumnFlags.WidthFixed, 80f);
        ImGui.TableHeadersRow();

        foreach (var player in comparisonState.Result.TopPlayers)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(player.Rank.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(string.IsNullOrWhiteSpace(player.CharacterName) ? player.Ident : player.CharacterName);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(_playerIdentity.ResolveWorldName(player.HomeWorldId));

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(player.OwnedCount.ToString("N0"));
        }

        ImGui.EndTable();
    }

    private static void DrawSummaryCards(CollectionSnapshot snapshot, CompareCollectionResponse result)
    {
        if (!ImGui.BeginTable(
                $"##ledgerSummary_{snapshot.Kind}_{result.Scope}",
                4,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchSame))
        {
            return;
        }

        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        DrawSummaryCell(
            "You",
            result.RequesterOwnedCount?.ToString("N0") ?? "Not synced",
            result.RequesterOwnedCount.HasValue
                ? "Raw synced unlock count for this collection."
                : "Open the relevant game window if needed, then sync again.");

        ImGui.TableNextColumn();
        DrawSummaryCell(
            "Cohort",
            result.LoadedPlayerCount == 0 ? "No data" : $"{result.LoadedPlayerCount:N0} / {result.ScopePlayerCount:N0}",
            "Loaded players / total players in this scope.");

        ImGui.TableNextColumn();
        DrawSummaryCell(
            "Rank",
            result.RequesterRank?.ToString("N0") ?? "-",
            result.RequesterPercentile.HasValue
                ? $"Top {(1d - result.RequesterPercentile.Value) * 100d:0.#}% below you, percentile {(result.RequesterPercentile.Value * 100d):0.#}%."
                : "Rank is unavailable until your collection is synced and loaded.");

        ImGui.TableNextColumn();
        DrawSummaryCell(
            "Average",
            result.AverageOwnedCount.HasValue ? $"{result.AverageOwnedCount.Value:0.#}" : "-",
            result.MedianOwnedCount.HasValue ? $"Median {result.MedianOwnedCount.Value:0.#}" : "Median unavailable.");

        ImGui.EndTable();
    }

    private static void DrawSummaryCell(string label, string value, string detail)
    {
        ImGui.TextDisabled(label);
        ImGui.TextUnformatted(value);
        ImGui.TextWrapped(detail);
    }

    private void DrawDebugTab()
    {
        DrawServerSettings();
        ImGui.Separator();
        DrawFriendListDebug();
    }

    private void DrawServerSettings()
    {
        var serverStatus = _server.GetStatus();

        ImGui.TextUnformatted("Server Settings");
        var enableServerComparison = _config.Current.EnableServerComparison;
        if (ImGui.Checkbox("Enable server comparison", ref enableServerComparison))
        {
            _config.Current.EnableServerComparison = enableServerComparison;
            _config.Save();
        }

        ImGui.SetNextItemWidth(Math.Max(320f, ImGui.GetContentRegionAvail().X - 290f));
        if (ImGui.InputText("Server URL", ref _serverBaseUrlInput, 256))
        {
            _config.Current.ServerBaseUrl = _serverBaseUrlInput.Trim();
            _config.Save();
        }

        ImGui.SameLine();
        if (ImGui.Button("Test Connection"))
        {
            _server.RequestHealthCheck(force: true);
        }

        ImGui.SameLine();
        if (ImGui.Button("Sync Now"))
        {
            _server.RequestSyncAll(force: true);
        }

        ImGui.TextDisabled(serverStatus.ServerBaseUrl ?? "No server URL configured.");
        if (!string.IsNullOrWhiteSpace(serverStatus.StatusMessage))
        {
            var color = serverStatus.LastErrorMessage is null ? OwnedColor : WarningColor;
            ImGui.TextColored(color, serverStatus.StatusMessage);
        }

        if (!string.IsNullOrWhiteSpace(serverStatus.FriendSyncMessage))
        {
            ImGui.TextDisabled(serverStatus.FriendSyncMessage);
        }
    }

    private void DrawFriendListDebug()
    {
        var snapshot = _friendListDebug.GetSnapshot();

        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.SyncAlt, "Refresh friend list"))
        {
            _friendListDebug.RequestRefresh();
        }
        ElezenImgui.AttachTooltip("Refresh the local friend list snapshot and compute Snowcloak-compatible idents.");

        ImGui.SameLine();
        if (snapshot.LastUpdatedUtc.HasValue)
        {
            var text = snapshot.IsRefreshing
                ? $"Refreshing... last successful refresh {snapshot.LastUpdatedUtc.Value.ToLocalTime():HH:mm:ss}"
                : $"Last refreshed {snapshot.LastUpdatedUtc.Value.ToLocalTime():HH:mm:ss}";
            ImGui.TextDisabled(text);
        }
        else if (snapshot.IsRefreshing)
        {
            ImGui.TextDisabled("Refreshing...");
        }
        else
        {
            ImGui.TextDisabled("No friend list snapshot yet.");
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Friend List Debug");
        ImGui.TextDisabled("Ident is SHA-256 of character name + home world ID, matching Snowcloak.");

        if (!string.IsNullOrWhiteSpace(snapshot.StatusMessage))
        {
            ImGui.TextColored(snapshot.Entries.Count > 0 ? OwnedColor : WarningColor, snapshot.StatusMessage);
        }

        ImGui.TextDisabled($"Entries: {snapshot.Entries.Count:N0}");

        var tableHeight = Math.Max(240f, ImGui.GetContentRegionAvail().Y - 8f);
        if (!ImGui.BeginTable(
                "##ledgerFriendDebug",
                4,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollX | ImGuiTableFlags.ScrollY,
                new Vector2(0, tableHeight)))
        {
            return;
        }

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthFixed, 180f);
        ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthFixed, 220f);
        ImGui.TableSetupColumn("Ident", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Content ID", ImGuiTableColumnFlags.WidthFixed, 130f);
        ImGui.TableHeadersRow();

        foreach (var entry in snapshot.Entries)
        {
            ImGui.PushID(entry.Ident);
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.Name);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.World);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.Ident);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.ContentId.ToString());

            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private static void DrawSnapshotHeader(CollectionSnapshot snapshot)
    {
        ImGui.TextUnformatted(snapshot.Kind.ToDisplayName());
        if (snapshot.OwnershipLoaded)
        {
            ImGui.TextUnformatted($"{snapshot.AttainableOwned:N0} of {snapshot.AttainableTotal:N0} attainable collected");
            ImGui.SameLine();
            ImGui.TextDisabled($"({snapshot.OwnedTotal:N0} of {snapshot.Total:N0} total, {snapshot.ExcludedOwned:N0} owned excluded)");
            DrawAttainableProgress(snapshot, "Attainable completion", $"{snapshot.CompletionRatio:P1}", 20f);
        }
        else
        {
            ImGui.TextColored(WarningColor, snapshot.StatusMessage);
            ImGui.TextDisabled($"{snapshot.Total:N0} entries loaded from game data.");
            DrawAttainableProgress(snapshot, "Attainable completion", $"{snapshot.CompletionRatio:P1}", 20f, showUnloadedStatus: false);
        }
    }

    private static void DrawAttainableProgress(
        CollectionSnapshot snapshot,
        string label,
        string loadedBarText,
        float height,
        bool showUnloadedStatus = true)
    {
        if (snapshot.OwnershipLoaded)
        {
            ElezenImgui.DrawProgressBarOption(
                label,
                snapshot.CompletionRatio,
                barText: loadedBarText,
                statusText: $"{snapshot.MissingAttainable:N0} missing",
                height: height);
            return;
        }

        ElezenImgui.DrawProgressBarOption(
            label,
            0f,
            barText: $"{snapshot.AttainableTotal:N0} tracked",
            statusText: showUnloadedStatus ? snapshot.StatusMessage : null,
            statusColour: WarningColor,
            height: height);
    }

    private void DrawFilters(CollectionSnapshot snapshot)
    {
        ImGui.SetNextItemWidth(Math.Max(220f, ImGui.GetContentRegionAvail().X - 420f));
        ImGui.InputTextWithHint("##ledgerSearch", "Search by name, detail, or ID", ref _searchText, 256);
        ImGui.SameLine();

        ImGui.BeginDisabled(!snapshot.OwnershipLoaded);
        ImGui.Checkbox("Missing only", ref _missingOnly);
        ImGui.EndDisabled();
        ElezenImgui.DrawHelpText("Only show entries you do not own. This filter is unavailable until ownership data is loaded.");

        ImGui.SameLine();
        ImGui.Checkbox("Show excluded", ref _showExcluded);
        ElezenImgui.DrawHelpText("Include items currently excluded from attainable totals in the entry list.");
    }

    private static bool DrawCheckboxWithHelp(string label, ref bool value, string helpText)
    {
        var changed = ImGui.Checkbox(label, ref value);
        ElezenImgui.DrawHelpText(helpText);
        return changed;
    }

    private void DrawEntryTable(CollectionSnapshot snapshot, CompareScope scope, ComparisonStateSnapshot comparisonState)
    {
        var comparisonLookup = comparisonState.Result?.Collectables.ToDictionary(entry => entry.CollectableId)
                               ?? new Dictionary<int, CollectableOwnershipCount>();
        var visibleEntries = GetVisibleEntries(snapshot, comparisonLookup);

        ImGui.TextDisabled($"Showing {visibleEntries.Length:N0} of {snapshot.Entries.Count:N0}");

        var selectedCollectableId = _selectedCollectableIds.TryGetValue(snapshot.Kind, out var selectedId)
            ? selectedId
            : null;

        var ownerPanelHeight = selectedCollectableId.HasValue ? 210f : 0f;
        var tableHeight = Math.Max(220f, ImGui.GetContentRegionAvail().Y - ownerPanelHeight - 8f);
        var tableFlags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY;
        var columnCount = comparisonState.Result is null ? 3 : 5;
        if (!ImGui.BeginTable("##ledgerEntries", columnCount, tableFlags, new Vector2(0, tableHeight)))
        {
            return;
        }

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Owned", ImGuiTableColumnFlags.WidthFixed, 90f);
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
        if (comparisonState.Result is not null)
        {
            ImGui.TableSetupColumn("Scope", ImGuiTableColumnFlags.WidthFixed, 160f);
            ImGui.TableSetupColumn("Who", ImGuiTableColumnFlags.WidthFixed, 95f);
        }

        ImGui.TableSetupColumn("Detail", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();

        foreach (var item in visibleEntries)
        {
            ImGui.PushID($"{item.Entry.Kind}_{item.Entry.Id}");
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            DrawOwned(item.Entry, snapshot.OwnershipLoaded);

            ImGui.TableNextColumn();
            if (comparisonState.Result is null)
            {
                ImGui.TextWrapped(item.Entry.Name);
            }
            else
            {
                var label = selectedCollectableId == (int)item.Entry.Id
                    ? $"> {item.Entry.Name}"
                    : item.Entry.Name;
                ImGui.TextWrapped(label);
            }

            if (comparisonState.Result is not null)
            {
                ImGui.TableNextColumn();
                if (comparisonState.Result.LoadedPlayerCount == 0)
                {
                    ImGui.TextDisabled("-");
                }
                else
                {
                    var ownerCount = item.Comparison?.OwnerCount ?? 0;
                    var ownerRate = item.Comparison?.OwnerRate ?? 0;
                    ImGui.TextUnformatted($"{ownerCount:N0} / {comparisonState.Result.LoadedPlayerCount:N0} ({ownerRate:P0})");
                }

                ImGui.TableNextColumn();
                var hasOwners = (item.Comparison?.OwnerCount ?? 0) > 0;
                ImGui.BeginDisabled(comparisonState.Result.LoadedPlayerCount == 0);
                if (ImGui.SmallButton(hasOwners ? "Owners" : "Check"))
                {
                    _selectedCollectableIds[snapshot.Kind] = (int)item.Entry.Id;
                    _server.RequestOwners(snapshot.Kind, scope, (int)item.Entry.Id, force: true);
                }

                ImGui.EndDisabled();
            }

            ImGui.TableNextColumn();
            if (string.IsNullOrWhiteSpace(item.Entry.Detail))
            {
                ImGui.TextDisabled("-");
            }
            else
            {
                ImGui.TextWrapped(item.Entry.Detail);
            }

            ImGui.PopID();
        }

        ImGui.EndTable();

        if (!selectedCollectableId.HasValue)
        {
            return;
        }

        var selectedEntry = snapshot.Entries.FirstOrDefault(entry => entry.Id == (uint)selectedCollectableId.Value);
        if (selectedEntry is null)
        {
            return;
        }

        var ownerState = _server.GetOwnerState(snapshot.Kind, scope, selectedCollectableId.Value);
        DrawOwnerPanel(scope, selectedEntry, ownerState);
    }

    private DisplayEntry[] GetVisibleEntries(CollectionSnapshot snapshot, IReadOnlyDictionary<int, CollectableOwnershipCount> comparisonLookup)
    {
        var query = snapshot.Entries
            .Where(entry => EntryMatches(snapshot, entry))
            .Select(entry => new DisplayEntry(
                entry,
                comparisonLookup.TryGetValue((int)entry.Id, out var comparison) ? comparison : null));

        if (comparisonLookup.Count == 0)
        {
            return query.ToArray();
        }

        return query
            .OrderByDescending(item => item.Comparison?.OwnerCount ?? 0)
            .ThenBy(item => item.Entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void DrawOwnerPanel(CompareScope scope, CollectableEntry entry, OwnerStateSnapshot ownerState)
    {
        ImGui.Spacing();
        ImGui.TextUnformatted($"Owner Detail: {entry.Name}");
        ImGui.SameLine();
        ImGui.TextDisabled(scope.ToDisplayName());

        ImGui.SameLine();
        if (ImGui.SmallButton("Clear"))
        {
            _selectedCollectableIds[entry.Kind] = null;
            return;
        }

        if (!string.IsNullOrWhiteSpace(ownerState.StatusMessage))
        {
            var color = ownerState.Result is not null ? OwnedColor : WarningColor;
            ImGui.TextColored(color, ownerState.StatusMessage);
        }

        if (ownerState.Result is null)
        {
            return;
        }

        if (!ImGui.BeginTable(
                $"##ledgerOwners_{entry.Kind}_{entry.Id}_{scope}",
                3,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY,
                new Vector2(0, 180f)))
        {
            return;
        }

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthFixed, 160f);
        ImGui.TableSetupColumn("Ident", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();

        foreach (var owner in ownerState.Result.Owners)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(string.IsNullOrWhiteSpace(owner.CharacterName) ? owner.Ident : owner.CharacterName);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(_playerIdentity.ResolveWorldName(owner.HomeWorldId));

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(owner.Ident);
        }

        ImGui.EndTable();
    }

    private bool EntryMatches(CollectionSnapshot snapshot, CollectableEntry entry)
    {
        if (!_showExcluded && entry.ExcludedFromAttainable)
        {
            return false;
        }

        if (_missingOnly && snapshot.OwnershipLoaded && entry.Owned)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(_searchText))
        {
            return true;
        }

        return entry.Id.ToString().Contains(_searchText, StringComparison.OrdinalIgnoreCase)
               || entry.Name.Contains(_searchText, StringComparison.OrdinalIgnoreCase)
               || entry.Detail.Contains(_searchText, StringComparison.OrdinalIgnoreCase)
               || (entry.Limited && "limited".Contains(_searchText, StringComparison.OrdinalIgnoreCase))
               || (entry.Premium && "premium".Contains(_searchText, StringComparison.OrdinalIgnoreCase))
               || (entry.Unobtainable && "unobtainable".Contains(_searchText, StringComparison.OrdinalIgnoreCase))
               || (entry.RetiredPvp && "retired pvp".Contains(_searchText, StringComparison.OrdinalIgnoreCase));
    }

    private static void DrawOwned(CollectableEntry entry, bool ownershipLoaded)
    {
        if (!ownershipLoaded)
        {
            ImGui.TextDisabled("Unknown");
            return;
        }

        if (entry.Owned)
        {
            ImGui.TextColored(OwnedColor, "Yes");
            return;
        }

        ImGui.TextColored(MissingColor, "No");
    }

    private CompareScope GetSelectedScope(CollectionKind kind)
    {
        if (_selectedScopes.TryGetValue(kind, out var scope))
        {
            return scope;
        }

        _selectedScopes[kind] = CompareScope.Server;
        return CompareScope.Server;
    }

    private readonly record struct DisplayEntry(CollectableEntry Entry, CollectableOwnershipCount? Comparison);
}
