using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Ledger.Data;
using Ledger.Models;
using Ledger.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace Ledger.UI;

public sealed class LedgerWindow : Window
{
    private static readonly Vector4 OwnedColor = new(0.55f, 0.88f, 0.55f, 1f);
    private static readonly Vector4 MissingColor = new(0.95f, 0.55f, 0.42f, 1f);
    private static readonly Vector4 WarningColor = new(0.95f, 0.74f, 0.35f, 1f);

    private readonly CollectionDataService _collections;
    private readonly ConfigService _config;
    private CollectionKind _selectedKind = CollectionKind.Achievements;
    private string _searchText = string.Empty;
    private bool _missingOnly = true;
    private bool _showExcluded;

    public LedgerWindow(CollectionDataService collections, ConfigService config)
        : base("Ledger")
    {
        _collections = collections;
        _config = config;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(760, 520),
            MaximumSize = new Vector2(1800, 1400),
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
        if (ImGui.Button("Refresh"))
        {
            _collections.Invalidate();
        }

        var changed = false;
        ImGui.SameLine();
        var excludeLimited = _config.Current.ExcludeLimitedFromAttainable;
        if (ImGui.Checkbox("Exclude limited", ref excludeLimited))
        {
            _config.Current.ExcludeLimitedFromAttainable = excludeLimited;
            changed = true;
        }

        ImGui.SameLine();
        var excludePremium = _config.Current.ExcludePremiumFromAttainable;
        if (ImGui.Checkbox("Exclude premium", ref excludePremium))
        {
            _config.Current.ExcludePremiumFromAttainable = excludePremium;
            changed = true;
        }

        ImGui.SameLine();
        var excludeRetiredPvp = _config.Current.ExcludeRetiredPvpFromAttainable;
        if (ImGui.Checkbox("Exclude retired PvP", ref excludeRetiredPvp))
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
        ImGui.TextUnformatted(snapshot.Kind.ToDisplayName());
        if (snapshot.OwnershipLoaded)
        {
            var label = $"{snapshot.AttainableOwned:N0} / {snapshot.AttainableTotal:N0} available";
            ImGui.ProgressBar(snapshot.CompletionRatio, new Vector2(-1, 18), label);
            ImGui.TextDisabled($"{snapshot.MissingAttainable:N0} missing");
        }
        else
        {
            ImGui.ProgressBar(0f, new Vector2(-1, 18), $"{snapshot.AttainableTotal:N0} tracked");
            ImGui.TextColored(WarningColor, snapshot.StatusMessage);
        }

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

        ImGui.EndTabBar();
    }

    private void DrawSelectedCategory(CollectionSnapshot snapshot)
    {
        DrawSnapshotHeader(snapshot);
        ImGui.Separator();
        DrawFilters(snapshot);
        ImGui.Separator();
        DrawEntryTable(snapshot);
    }

    private static void DrawSnapshotHeader(CollectionSnapshot snapshot)
    {
        ImGui.TextUnformatted(snapshot.Kind.ToDisplayName());
        if (snapshot.OwnershipLoaded)
        {
            ImGui.TextUnformatted($"{snapshot.AttainableOwned:N0} of {snapshot.AttainableTotal:N0} attainable collected");
            ImGui.SameLine();
            ImGui.TextDisabled($"({snapshot.OwnedTotal:N0} of {snapshot.Total:N0} total, {snapshot.ExcludedOwned:N0} owned excluded)");
            ImGui.ProgressBar(snapshot.CompletionRatio, new Vector2(-1, 20), $"{snapshot.CompletionRatio:P1}");
        }
        else
        {
            ImGui.TextColored(WarningColor, snapshot.StatusMessage);
            ImGui.TextDisabled($"{snapshot.Total:N0} entries loaded from game data.");
        }
    }

    private void DrawFilters(CollectionSnapshot snapshot)
    {
        ImGui.SetNextItemWidth(Math.Max(220f, ImGui.GetContentRegionAvail().X - 320f));
        ImGui.InputTextWithHint("##ledgerSearch", "Search by name, detail, or ID", ref _searchText, 256);
        ImGui.SameLine();

        ImGui.BeginDisabled(!snapshot.OwnershipLoaded);
        ImGui.Checkbox("Missing only", ref _missingOnly);
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.Checkbox("Show excluded", ref _showExcluded);
    }

    private void DrawEntryTable(CollectionSnapshot snapshot)
    {
        var visibleEntries = snapshot.Entries
            .Where(entry => EntryMatches(snapshot, entry))
            .ToArray();

        ImGui.TextDisabled($"Showing {visibleEntries.Length:N0} of {snapshot.Entries.Count:N0}");

        var tableHeight = Math.Max(260f, ImGui.GetContentRegionAvail().Y - 8f);
        if (!ImGui.BeginTable("##ledgerEntries", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY, new Vector2(0, tableHeight)))
        {
            return;
        }

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Owned", ImGuiTableColumnFlags.WidthFixed, 90f);
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 0f, 1);
        ImGui.TableSetupColumn("Detail", ImGuiTableColumnFlags.WidthStretch, 0f, 2);
        ImGui.TableHeadersRow();

        foreach (var entry in visibleEntries)
        {
            ImGui.PushID($"{entry.Kind}_{entry.Id}");
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            DrawOwned(entry, snapshot.OwnershipLoaded);

            ImGui.TableNextColumn();
            ImGui.TextWrapped(entry.Name);

            ImGui.TableNextColumn();
            if (string.IsNullOrWhiteSpace(entry.Detail))
            {
                ImGui.TextDisabled("-");
            }
            else
            {
                ImGui.TextWrapped(entry.Detail);
            }

            ImGui.PopID();
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

    private static void AddPolicy(bool excluded, string label, ICollection<string> excludedLabels, ICollection<string> countedLabels)
    {
        if (excluded)
        {
            excludedLabels.Add(label);
            return;
        }

        countedLabels.Add(label);
    }

    private static string JoinPolicyList(IReadOnlyCollection<string> labels)
    {
        if (labels.Count == 1)
        {
            return Capitalize(labels.First());
        }

        var values = labels.ToArray();
        if (values.Length == 2)
        {
            return $"{Capitalize(values[0])} and {values[1]}";
        }

        return $"{Capitalize(string.Join(", ", values[..^1]))}, and {values[^1]}";
    }

    private static string Capitalize(string value)
        => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];
}
