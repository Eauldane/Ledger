using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using ElezenTools.UI;
using Ledger.Services;

namespace Ledger.UI;

public sealed class LedgerSettingsWindow : Window
{
    private readonly CollectionDataService _collections;
    private readonly ConfigService _config;
    private readonly LedgerServerService _server;

    public LedgerSettingsWindow(
        CollectionDataService collections,
        ConfigService config,
        LedgerServerService server)
        : base("Ledger Settings")
    {
        _collections = collections;
        _config = config;
        _server = server;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 260),
            MaximumSize = new Vector2(900, 700),
        };
        Size = new Vector2(520, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        DrawServerSettings();
        ModernSection.SoftSeparator();
        DrawCollectionSettings();
        ModernSection.SoftSeparator();
        DrawViewSettings();
    }

    private void DrawServerSettings()
    {
        ModernSection.Header(FontAwesomeIcon.Cloud, "Server");
        var enableServerComparison = _config.Current.EnableServerComparison;
        if (ImGui.Checkbox("Enable sync and comparison", ref enableServerComparison))
        {
            _config.Current.EnableServerComparison = enableServerComparison;
            _config.Save();
            _server.ResetSession();

            if (enableServerComparison)
            {
                _server.RequestSyncAll(force: true);
            }
        }

        var serverStatus = _server.GetStatus();
        if (!string.IsNullOrWhiteSpace(serverStatus.StatusMessage))
        {
            ElezenImgui.WrappedText(serverStatus.StatusMessage);
        }
        if (!string.IsNullOrWhiteSpace(serverStatus.FriendSyncMessage))
        {
            ElezenImgui.ColouredWrappedText(serverStatus.FriendSyncMessage, LedgerTheme.Warning);
        }
    }

    private void DrawCollectionSettings()
    {
        ModernSection.Header(FontAwesomeIcon.Book, "Collection rules");

        var changed = false;
        var excludeLimited = _config.Current.ExcludeLimitedFromAttainable;
        if (DrawCheckboxWithHelp(
                "Exclude limited-time items",
                ref excludeLimited,
                "Excludes items that were only available for a limited time - for example, achievements only obtainable during Valentione's Day events, etc."))
        {
            _config.Current.ExcludeLimitedFromAttainable = excludeLimited;
            changed = true;
        }

        var excludePremium = _config.Current.ExcludePremiumFromAttainable;
        if (DrawCheckboxWithHelp(
                "Exclude Mog Station items",
                ref excludePremium,
                "Excludes items that are only available through Mog Station."))
        {
            _config.Current.ExcludePremiumFromAttainable = excludePremium;
            changed = true;
        }

        var excludeRetiredPvp = _config.Current.ExcludeRetiredPvpFromAttainable;
        if (DrawCheckboxWithHelp(
                "Exclude retired PvP items",
                ref excludeRetiredPvp,
                "Excludes retired PvP rewards."))
        {
            _config.Current.ExcludeRetiredPvpFromAttainable = excludeRetiredPvp;
            changed = true;
        }

        if (!changed)
        {
            return;
        }

        _config.Save();
        _collections.Invalidate();
    }

    private void DrawViewSettings()
    {
        ModernSection.Header(FontAwesomeIcon.Eye, "View");

        var changed = false;
        var missingOnly = _config.Current.MissingOnly;
        if (DrawCheckboxWithHelp(
                "Missing only",
                ref missingOnly,
                "Show only items you have not collected. Disable to include owned items."))
        {
            _config.Current.MissingOnly = missingOnly;
            changed = true;
        }
        var showExcluded = _config.Current.ShowExcluded;
        if (DrawCheckboxWithHelp(
                "Show excluded",
                ref showExcluded,
                "Include items currently excluded from attainable totals in entry lists."))
        {
            _config.Current.ShowExcluded = showExcluded;
            changed = true;
        }

        if (changed)
        {
            _config.Save();
        }
    }

    private static bool DrawCheckboxWithHelp(string label, ref bool value, string helpText)
    {
        var changed = ImGui.Checkbox(label, ref value);
        ElezenImgui.ColouredWrappedText(helpText, LedgerTheme.Palette.CompactTextMuted);
        ImGui.Spacing();
        return changed;
    }
}
