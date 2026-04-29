using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;
using ElezenTools.UI;
using Ledger.Data;
using Ledger.Models;
using Ledger.Services;

namespace Ledger.UI;

public sealed class LedgerWindow : Window
{
    private const string AllAchievementCategoriesLabel = "All categories";
    private static readonly Vector4 OwnedColor = new(0.55f, 0.88f, 0.55f, 1f);
    private static readonly Vector4 MissingColor = new(0.95f, 0.55f, 0.42f, 1f);
    private static readonly Vector4 WarningColor = new(0.95f, 0.74f, 0.35f, 1f);

    private readonly CollectionDataService _collections;
    private readonly ConfigService _config;
    private readonly LedgerServerService _server;
    private readonly PlayerIdentityService _playerIdentity;
    private readonly Action _openSettings;
    private readonly Dictionary<CollectionKind, CompareScope> _selectedScopes = [];

    private string _searchText = string.Empty;
    private string? _selectedAchievementCategory;

    public LedgerWindow(
        CollectionDataService collections,
        ConfigService config,
        LedgerServerService server,
        PlayerIdentityService playerIdentity,
        Action openSettings)
        : base("Ledger")
    {
        _collections = collections;
        _config = config;
        _server = server;
        _playerIdentity = playerIdentity;
        _openSettings = openSettings;
        this.TitleBarButtons =
        [
            new()
            {
                Icon = FontAwesomeIcon.GlobeEurope,
                ShowTooltip = () => ImGui.SetTooltip("Discord"),
                Click = (btn) => Util.OpenLink("https://discord.gg/elznmods")
            },
            new()
            {
                Icon = FontAwesomeIcon.Heart,
                ShowTooltip = () => ImGui.SetTooltip("Patreon"),
                Click = (btn) => Util.OpenLink("https://patreon.com/elznmods")
            }
        ];
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
        if (ImGui.Button("Settings"))
        {
            _openSettings();
        }
        ElezenImgui.DrawHelpText("Open Ledger settings.");

        ImGui.SameLine();
        ImGui.BeginDisabled(!_config.Current.EnableServerComparison);
        if (ImGui.Button("Sync with server"))
        {
            _server.RequestSyncAll(force: true);
        }
        ImGui.EndDisabled();
        ElezenImgui.DrawHelpText("Sync your currently loaded collection data and friend list data to the Ledger server.");

        var serverStatus = _server.GetStatus();
        if (!string.IsNullOrWhiteSpace(serverStatus.StatusMessage))
        {
            ImGui.SameLine();
            var statusColor = serverStatus.LastErrorMessage is null ? OwnedColor : WarningColor;
            ImGui.TextColored(statusColor, serverStatus.StatusMessage);
        }

        if (!LimitedCollectables.IsLoaded)
        {
            ImGui.TextColored(WarningColor, $"Exclusion data was not loaded: {LimitedCollectables.LoadError}");
        }

        if (serverStatus.LastSuccessfulSyncUtc.HasValue)
        {
            ImGui.TextDisabled($"Last server sync {serverStatus.LastSuccessfulSyncUtc.Value.ToLocalTime():HH:mm:ss}");
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

            DrawSelectedCategory(_collections.GetSnapshot(kind));
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawSelectedCategory(CollectionSnapshot snapshot)
    {
        var scope = GetSelectedScope(snapshot.Kind);
        var comparisonState = _server.GetComparisonState(snapshot.Kind, scope);
        if (_config.Current.EnableServerComparison
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
        DrawEntryTable(snapshot, comparisonState);
    }

    private void DrawComparisonPanel(CollectionSnapshot snapshot, ref CompareScope scope, ComparisonStateSnapshot comparisonState)
    {
        var serverStatus = _server.GetStatus();

        ImGui.TextUnformatted("Comparison");

        if (ImGui.BeginCombo("Scope", scope.ToDisplayName()))
        {
            foreach (var option in CompareScopeExtensions.AllScopes)
            {
                var selected = option == scope;
                if (ImGui.Selectable(option.ToDisplayName(), selected))
                {
                    scope = option;
                    _selectedScopes[snapshot.Kind] = option;
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
            ImGui.TextColored(WarningColor, "Server comparison is disabled. Enable sync in Settings.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(comparisonState.StatusMessage))
        {
            var stateColor = comparisonState.Result is not null && !comparisonState.IsLoading
                ? OwnedColor
                : WarningColor;
            ImGui.TextColored(stateColor, comparisonState.StatusMessage);
        }

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
        var showAchievementCategory = snapshot.Kind == CollectionKind.Achievements;
        var searchWidth = showAchievementCategory
            ? Math.Max(220f, ImGui.GetContentRegionAvail().X - 460f)
            : Math.Max(220f, ImGui.GetContentRegionAvail().X - 240f);

        ImGui.SetNextItemWidth(searchWidth);
        ImGui.InputTextWithHint("##ledgerSearch", "Search by name, category, detail, or ID", ref _searchText, 256);

        if (showAchievementCategory)
        {
            DrawAchievementCategoryFilter(snapshot);
        }
        
    }

    private void DrawEntryTable(CollectionSnapshot snapshot, ComparisonStateSnapshot comparisonState)
    {
        var comparisonLookup = comparisonState.Result?.Collectables.ToDictionary(entry => entry.CollectableId)
                               ?? new Dictionary<int, CollectableOwnershipCount>();
        var filteredEntries = GetVisibleEntries(snapshot, comparisonLookup);
        var showCategoryColumn = snapshot.Kind == CollectionKind.Achievements;
        var showComparisonColumn = comparisonState.Result is not null;
        var comparisonResult = comparisonState.Result;

        ImGui.TextDisabled($"Showing {filteredEntries.Length:N0} of {snapshot.Entries.Count:N0}");

        var tableHeight = Math.Max(220f, ImGui.GetContentRegionAvail().Y - 8f);
        var tableFlags = ImGuiTableFlags.Borders
                         | ImGuiTableFlags.RowBg
                         | ImGuiTableFlags.Resizable
                         | ImGuiTableFlags.ScrollY
                         | ImGuiTableFlags.Sortable;
        var columnCount = showComparisonColumn
            ? (showCategoryColumn ? 5 : 4)
            : (showCategoryColumn ? 4 : 3);
        if (!ImGui.BeginTable("##ledgerEntries", columnCount, tableFlags, new Vector2(0, tableHeight)))
        {
            return;
        }

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Owned", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.PreferSortDescending, 90f);
        ImGui.TableSetupColumn(
            "Name",
            ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.PreferSortAscending,
            0f);
        if (showComparisonColumn)
        {
            var scopeName = comparisonResult!.Scope.ToDisplayName();
            ImGui.TableSetupColumn(
                $"{scopeName} % Unlocked",
                ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.PreferSortDescending,
                190f);
        }

        ImGui.TableSetupColumn("Detail", ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.NoSort);
        if (showCategoryColumn)
        {
            ImGui.TableSetupColumn("Category", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.PreferSortAscending, 180f);
        }
        ImGui.TableHeadersRow();

        var sortedEntries = SortVisibleEntries(filteredEntries, snapshot.OwnershipLoaded, showCategoryColumn, showComparisonColumn);

        foreach (var item in sortedEntries)
        {
            ImGui.PushID($"{item.Entry.Kind}_{item.Entry.Id}");
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            DrawOwned(item.Entry, snapshot.OwnershipLoaded);

            ImGui.TableNextColumn();
            ImGui.TextWrapped(item.Entry.Name);

            if (showComparisonColumn)
            {
                ImGui.TableNextColumn();
                if (comparisonResult!.LoadedPlayerCount == 0)
                {
                    ImGui.TextDisabled("-");
                }
                else
                {
                    DrawComparisonProgress(item.Comparison, comparisonResult.LoadedPlayerCount);
                }
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

            if (showCategoryColumn)
            {
                ImGui.TableNextColumn();
                if (string.IsNullOrWhiteSpace(item.Entry.Category))
                {
                    ImGui.TextDisabled("-");
                }
                else
                {
                    ImGui.TextWrapped(item.Entry.Category);
                }
            }

            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private void DrawAchievementCategoryFilter(CollectionSnapshot snapshot)
    {
        var categories = snapshot.Entries
            .Select(entry => entry.Category)
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(category => category, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (_selectedAchievementCategory is not null
            && !categories.Contains(_selectedAchievementCategory, StringComparer.Ordinal))
        {
            _selectedAchievementCategory = null;
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(Math.Max(180f, Math.Min(260f, ImGui.GetContentRegionAvail().X - 16f)));
        var selectedLabel = _selectedAchievementCategory ?? AllAchievementCategoriesLabel;
        if (!ImGui.BeginCombo("Category", selectedLabel))
        {
            return;
        }

        var showAll = _selectedAchievementCategory is null;
        if (ImGui.Selectable(AllAchievementCategoriesLabel, showAll))
        {
            _selectedAchievementCategory = null;
        }

        if (showAll)
        {
            ImGui.SetItemDefaultFocus();
        }

        foreach (var category in categories)
        {
            var selected = string.Equals(_selectedAchievementCategory, category, StringComparison.Ordinal);
            if (ImGui.Selectable(category, selected))
            {
                _selectedAchievementCategory = category;
            }

            if (selected)
            {
                ImGui.SetItemDefaultFocus();
            }
        }

        ImGui.EndCombo();
    }

    private DisplayEntry[] GetVisibleEntries(CollectionSnapshot snapshot, IReadOnlyDictionary<int, CollectableOwnershipCount> comparisonLookup)
        => snapshot.Entries
            .Where(entry => EntryMatches(snapshot, entry))
            .Select(entry => new DisplayEntry(
                entry,
                comparisonLookup.TryGetValue((int)entry.Id, out var comparison) ? comparison : null))
            .ToArray();

    private bool EntryMatches(CollectionSnapshot snapshot, CollectableEntry entry)
    {
        if (snapshot.Kind == CollectionKind.Achievements
            && _selectedAchievementCategory is not null
            && !string.Equals(entry.Category, _selectedAchievementCategory, StringComparison.Ordinal))
        {
            return false;
        }

        if (!_config.Current.ShowExcluded && entry.ExcludedFromAttainable)
        {
            return false;
        }

        if (_config.Current.MissingOnly && snapshot.OwnershipLoaded && entry.Owned)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(_searchText))
        {
            return true;
        }

        return entry.Id.ToString().Contains(_searchText, StringComparison.OrdinalIgnoreCase)
               || entry.Name.Contains(_searchText, StringComparison.OrdinalIgnoreCase)
               || entry.Category.Contains(_searchText, StringComparison.OrdinalIgnoreCase)
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

    private static DisplayEntry[] SortVisibleEntries(DisplayEntry[] entries, bool ownershipLoaded, bool showCategoryColumn, bool showComparisonColumn)
    {
        if (TryGetSortSpec(out var columnIndex, out var direction))
        {
            var descending = direction == ImGuiSortDirection.Descending;
            var comparisonColumnIndex = showComparisonColumn ? 2 : -1;
            var detailColumnIndex = showComparisonColumn ? 3 : 2;
            var categoryColumnIndex = showCategoryColumn ? detailColumnIndex + 1 : -1;

            if (columnIndex == 0)
            {
                return descending
                    ? entries.OrderByDescending(item => GetOwnedSortValue(item.Entry, ownershipLoaded)).ThenBy(item => item.Entry.Name, StringComparer.OrdinalIgnoreCase).ToArray()
                    : entries.OrderBy(item => GetOwnedSortValue(item.Entry, ownershipLoaded)).ThenBy(item => item.Entry.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            }

            if (columnIndex == 1)
            {
                return descending
                    ? entries.OrderByDescending(item => item.Entry.Name, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Entry.Id).ToArray()
                    : entries.OrderBy(item => item.Entry.Name, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Entry.Id).ToArray();
            }

            if (showCategoryColumn && columnIndex == categoryColumnIndex)
            {
                return descending
                    ? entries.OrderByDescending(item => item.Entry.Category, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Entry.Name, StringComparer.OrdinalIgnoreCase).ToArray()
                    : entries.OrderBy(item => item.Entry.Category, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Entry.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            }

            if (showComparisonColumn && columnIndex == comparisonColumnIndex)
            {
                return descending
                    ? entries.OrderByDescending(item => item.Comparison?.OwnerRate ?? 0d).ThenBy(item => item.Entry.Name, StringComparer.OrdinalIgnoreCase).ToArray()
                    : entries.OrderBy(item => item.Comparison?.OwnerRate ?? 0d).ThenBy(item => item.Entry.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }

        return entries
            .OrderBy(item => item.Entry.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Entry.Id)
            .ToArray();
    }

    private static bool TryGetSortSpec(out int columnIndex, out ImGuiSortDirection direction)
    {
        columnIndex = -1;
        direction = ImGuiSortDirection.None;

        var sortSpecs = ImGui.TableGetSortSpecs();
        if (sortSpecs.IsNull || sortSpecs.SpecsCount <= 0)
        {
            return false;
        }

        var sortSpec = sortSpecs.Specs;
        columnIndex = sortSpec.ColumnIndex;
        direction = sortSpec.SortDirection;
        return true;
    }

    private static void DrawComparisonProgress(CollectableOwnershipCount? comparison, int loadedPlayerCount)
    {
        var ownerRate = Math.Clamp((float)(comparison?.OwnerRate ?? 0d), 0f, 1f);
        var ownerCount = comparison?.OwnerCount ?? 0;
        var width = Math.Max(1f, ImGui.GetContentRegionAvail().X);
        ImGui.ProgressBar(ownerRate, new Vector2(width, 0f), $"{ownerRate:P0} unlocked");
        if (!ImGui.IsItemHovered())
        {
            return;
        }

        ImGui.SetTooltip($"{ownerCount:N0} of {loadedPlayerCount:N0} synced players have this unlocked.");
    }

    private static int GetOwnedSortValue(CollectableEntry entry, bool ownershipLoaded)
        => !ownershipLoaded
            ? -1
            : entry.Owned ? 1 : 0;

    private readonly record struct DisplayEntry(CollectableEntry Entry, CollectableOwnershipCount? Comparison);
}
