using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;

namespace Ledger.UI;

internal static class LedgerTheme
{
    public static readonly Vector4 Text = Colour.HexToVector4("#F0EDDF");
    public static readonly Vector4 Warning = Colour.HexToVector4("#E6C57E");
    public static readonly ModernPalette Palette = new(
        Accent: Colour.HexToVector4("#D7B978"),
        BooleanTrue: Colour.HexToVector4("#91C7A3"),
        BooleanFalse: Colour.HexToVector4("#E59988"),
        CompactBg: Colour.HexToVector4("#151E1B"),
        CompactPanel: Colour.HexToVector4("#1C2923"),
        CompactPanelAlt: Colour.HexToVector4("#293B31"),
        CompactBorder: Colour.HexToVector4("#58705C"),
        CompactBorderSubtle: Colour.HexToVector4("#344A3D"),
        CompactTextMuted: Colour.HexToVector4("#ADBBAD"),
        CompactOffline: Colour.HexToVector4("#8D9F93"),
        TitleBg: Colour.HexToVector4("#101813"),
        TitleBgActive: Colour.HexToVector4("#233427"),
        TitleBgCollapsed: Colour.HexToVector4("#101813"),
        Button: Colour.HexToVector4("#354C39"),
        ButtonHovered: Colour.HexToVector4("#48624B"),
        ButtonActive: Colour.HexToVector4("#59765A"),
        TabActive: Colour.HexToVector4("#48624B"),
        TabHovered: Colour.HexToVector4("#59765A"));

    public static IDisposable Push() => new ThemeScope();

    private sealed class ThemeScope : IDisposable
    {
        private readonly IDisposable _theme;
        private readonly IDisposable _content;
        private readonly IDisposable _colours;
        private readonly IDisposable _spacing;

        public ThemeScope()
        {
            _theme = ModernTheme.Push(Palette);
            ModernWindowStyle.PushTitleBar();
            _content = ModernWindowStyle.PushContentPalette();
            var scale = ImGuiHelpers.GlobalScale;
            _spacing = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(12f, 10f) * scale)
                .Push(ImGuiStyleVar.FramePadding, new Vector2(8f, 5f) * scale);
            // Override the helper's fixed blue colours, as BigMac does for its palette.
            _colours = ImRaii.PushColor(ImGuiCol.Text, Text)
                .Push(ImGuiCol.TextDisabled, Palette.CompactTextMuted)
                .Push(ImGuiCol.ChildBg, Palette.CompactPanel)
                .Push(ImGuiCol.PopupBg, Palette.CompactPanel)
                .Push(ImGuiCol.FrameBg, Palette.CompactBg)
                .Push(ImGuiCol.FrameBgHovered, Palette.CompactPanelAlt)
                .Push(ImGuiCol.FrameBgActive, Palette.CompactPanelAlt)
                .Push(ImGuiCol.Header, Palette.Button)
                .Push(ImGuiCol.HeaderHovered, Palette.ButtonHovered)
                .Push(ImGuiCol.HeaderActive, Palette.ButtonActive)
                .Push(ImGuiCol.Separator, Palette.CompactBorderSubtle)
                .Push(ImGuiCol.SeparatorHovered, Palette.Accent)
                .Push(ImGuiCol.SeparatorActive, Palette.Accent)
                .Push(ImGuiCol.TableHeaderBg, Palette.CompactPanelAlt)
                .Push(ImGuiCol.TableBorderStrong, Palette.CompactBorder)
                .Push(ImGuiCol.TableBorderLight, Palette.CompactBorderSubtle)
                .Push(ImGuiCol.TableRowBg, Vector4.Zero)
                .Push(ImGuiCol.TableRowBgAlt, Colour.WithAlpha(Palette.CompactPanelAlt, 0.45f))
                .Push(ImGuiCol.PlotHistogram, Palette.Accent)
                .Push(ImGuiCol.ResizeGrip, Colour.WithAlpha(Palette.Accent, 0.25f))
                .Push(ImGuiCol.ResizeGripHovered, Colour.WithAlpha(Palette.Accent, 0.65f))
                .Push(ImGuiCol.ResizeGripActive, Palette.Accent)
                .Push(ImGuiCol.TextSelectedBg, Colour.WithAlpha(Palette.Accent, 0.30f))
                .Push(ImGuiCol.ScrollbarBg, Palette.CompactBg)
                .Push(ImGuiCol.ScrollbarGrab, Palette.CompactBorder)
                .Push(ImGuiCol.ScrollbarGrabHovered, Palette.ButtonHovered)
                .Push(ImGuiCol.ScrollbarGrabActive, Palette.Accent)
                .Push(ImGuiCol.NavHighlight, Palette.Accent);
        }

        public void Dispose()
        {
            _colours.Dispose();
            _spacing.Dispose();
            _content.Dispose();
            ModernWindowStyle.PopTitleBar();
            _theme.Dispose();
        }
    }
}
