using System.Text.Json.Serialization;

namespace Galah2.Models;

/// <summary>Document model of settings.json (design §3.3). Every key has a default; unknown keys are ignored.</summary>
public sealed class SettingsDocument : Persistence.ISchemaBearing
{
    /// <summary>Schema version written by this build.</summary>
    public const int CurrentSchema = 1;

    [JsonPropertyName("schema")]
    public int Schema { get; set; } = CurrentSchema;

    /// <summary>Global hotkey as a modifier+key string, e.g. "alt+space".</summary>
    [JsonPropertyName("hotkey")]
    public string Hotkey { get; set; } = DefaultHotkey;

    /// <summary>HKCU Run-key autostart preference (kept in sync with the registry on load).</summary>
    [JsonPropertyName("autostart")]
    public bool AutoStart { get; set; }

    /// <summary>"system" | "light" | "dark".</summary>
    [JsonPropertyName("theme")]
    public string Theme { get; set; } = ThemeModes.System;

    /// <summary>"single" | "double".</summary>
    [JsonPropertyName("clickMode")]
    public string ClickMode { get; set; } = ClickModes.Single;

    /// <summary>"small" | "medium" | "large".</summary>
    [JsonPropertyName("iconSize")]
    public string IconSize { get; set; } = IconSizes.Medium;

    /// <summary>Tab strip placement: "top" (horizontal) or "left" (vertical sidebar).</summary>
    [JsonPropertyName("tabPosition")]
    public string TabPosition { get; set; } = TabPositions.Top;

    /// <summary>Category chip label size: "small" | "medium" (default, 12 DIP) | "large" | "xlarge".</summary>
    [JsonPropertyName("tabFontSize")]
    public string TabFontSize { get; set; } = ChipFontSizes.Medium;

    /// <summary>Hide the panel after a successful launch (spec §3.3, default true per spec §9-Q1).</summary>
    [JsonPropertyName("hideOnLaunch")]
    public bool HideOnLaunch { get; set; } = true;

    /// <summary>Panel surface material: opaque (default) or translucent so the desktop shows through.</summary>
    [JsonPropertyName("panelMaterial")]
    public string PanelMaterial { get; set; } = PanelMaterials.None;

    /// <summary>Where a summon puts the panel: remembered position, cursor, or centre.</summary>
    [JsonPropertyName("panelPlacement")]
    public string PanelPlacement { get; set; } = PanelPositions.Remember;

    /// <summary>Wheel at the grid edge switches categories.</summary>
    [JsonPropertyName("wheelSwitchTab")]
    public bool WheelSwitchTab { get; set; } = true;

    /// <summary>Ctrl+1..9 jumps to a category.</summary>
    [JsonPropertyName("numberKeysSwitchTab")]
    public bool NumberKeysSwitchTab { get; set; } = true;

    /// <summary>Hide the panel when it loses focus. Default OFF (design §2.1 P-3).</summary>
    [JsonPropertyName("hideOnBlur")]
    public bool HideOnBlur { get; set; }

    /// <summary>
    /// Show the dashed "add" placeholder tile at the end of every category grid. Default OFF:
    /// the grid ends with the real entries, and adding goes through the blank-space right-click
    /// or the ☰ menu. Absent in older files deserializes to false, which is the intended default.
    /// </summary>
    [JsonPropertyName("showAddTile")]
    public bool ShowAddTile { get; set; }

    /// <summary>启用 GPU 硬件加速渲染（默认关闭以大幅降低内存占用；需重启生效）。</summary>
    [JsonPropertyName("gpuAcceleration")]
    public bool GpuAcceleration { get; set; }

    [JsonPropertyName("language")]
    public string Language { get; set; } = "zh-CN";

    // ── RandPass (spec 2026-11-24-randpass; length/count changed to 8/10 in §7) ──
    [JsonPropertyName("randPassLength")]
    public int RandPassLength { get; set; } = 8;
    [JsonPropertyName("randPassCount")]
    public int RandPassCount { get; set; } = 10;
    [JsonPropertyName("randPassLower")]
    public bool RandPassLower { get; set; } = true;
    [JsonPropertyName("randPassUpper")]
    public bool RandPassUpper { get; set; } = true;
    [JsonPropertyName("randPassDigits")]
    public bool RandPassDigits { get; set; } = true;
    [JsonPropertyName("randPassDoubleDigits")]
    public bool RandPassDoubleDigits { get; set; } = true;
    [JsonPropertyName("randPassNoConfusables")]
    public bool RandPassNoConfusables { get; set; } = true;
    [JsonPropertyName("randPassCustomChars")]
    public string RandPassCustomChars { get; set; } = PasswordOptions.DefaultCustomChars;
    [JsonPropertyName("randPassCustomEnabled")]
    public bool RandPassCustomEnabled { get; set; } = true;
    [JsonPropertyName("randPassUseFormat")]
    public bool RandPassUseFormat { get; set; }
    [JsonPropertyName("randPassFormat")]
    public string RandPassFormat { get; set; } = PasswordFormats.FourStarFour;

    /// <summary>Documented default hotkey (design §1.1 S1 — deliberately not the research subject's value).</summary>
    public const string DefaultHotkey = "alt+space";

    /// <summary>Normalize out-of-range values back to defaults; never throws (settings forward compatibility).</summary>
    public SettingsDocument Sanitized()
    {
        var s = Clone();
        if (Array.IndexOf(ThemeModes.All, s.Theme) < 0) s.Theme = ThemeModes.System;
        if (Array.IndexOf(ClickModes.All, s.ClickMode) < 0) s.ClickMode = ClickModes.Single;
        if (Array.IndexOf(IconSizes.All, s.IconSize) < 0) s.IconSize = IconSizes.Medium;
        if (Array.IndexOf(TabPositions.All, s.TabPosition) < 0) s.TabPosition = TabPositions.Top;
        if (Array.IndexOf(ChipFontSizes.All, s.TabFontSize) < 0) s.TabFontSize = ChipFontSizes.Medium;
        if (Array.IndexOf(PanelPositions.All, s.PanelPlacement) < 0) s.PanelPlacement = PanelPositions.Remember;
        if (Array.IndexOf(PanelMaterials.All, s.PanelMaterial) < 0) s.PanelMaterial = PanelMaterials.None;
        if (string.IsNullOrWhiteSpace(s.Hotkey)) s.Hotkey = DefaultHotkey;
        if (string.IsNullOrWhiteSpace(s.Language)) s.Language = "zh-CN";
        if (s.RandPassLength is < PasswordOptions.MinLength or > PasswordOptions.MaxLength) s.RandPassLength = 8;
        if (s.RandPassCount is < PasswordOptions.MinCount or > PasswordOptions.MaxCount) s.RandPassCount = 10;
        s.RandPassCustomChars ??= PasswordOptions.DefaultCustomChars;
        if (Array.IndexOf(PasswordFormats.All, s.RandPassFormat) < 0) s.RandPassFormat = PasswordFormats.FourStarFour;
        return s;
    }

    /// <summary>Deep copy used for the in-memory snapshot handed to the UI layer.</summary>
    public SettingsDocument Clone() => new()
    {
        Schema = Schema,
        Hotkey = Hotkey,
        AutoStart = AutoStart,
        Theme = Theme,
        ClickMode = ClickMode,
        IconSize = IconSize,
        TabPosition = TabPosition,
        TabFontSize = TabFontSize,
        PanelPlacement = PanelPlacement,
        PanelMaterial = PanelMaterial,
        WheelSwitchTab = WheelSwitchTab,
        NumberKeysSwitchTab = NumberKeysSwitchTab,
        HideOnLaunch = HideOnLaunch,
        HideOnBlur = HideOnBlur,
        ShowAddTile = ShowAddTile,
        GpuAcceleration = GpuAcceleration,
        Language = Language,
        RandPassLength = RandPassLength,
        RandPassCount = RandPassCount,
        RandPassLower = RandPassLower,
        RandPassUpper = RandPassUpper,
        RandPassDigits = RandPassDigits,
        RandPassDoubleDigits = RandPassDoubleDigits,
        RandPassNoConfusables = RandPassNoConfusables,
        RandPassCustomChars = RandPassCustomChars,
        RandPassCustomEnabled = RandPassCustomEnabled,
        RandPassUseFormat = RandPassUseFormat,
        RandPassFormat = RandPassFormat,
    };
}

/// <summary>Theme option constants (design §1.1 S10).</summary>
public static class ThemeModes
{
    public const string System = "system";
    public const string Light = "light";
    public const string Dark = "dark";
    public static readonly string[] All = [System, Light, Dark];
}

/// <summary>Tile click semantics (spec §3.2).</summary>
public static class ClickModes
{
    public const string Single = "single";
    public const string Double = "double";
    public static readonly string[] All = [Single, Double];
}

/// <summary>Three icon size steps (design §3.3); no free-form size to keep layout stable (AC-14).</summary>
public static class IconSizes
{
    public const string Small = "small";
    public const string Medium = "medium";
    public const string Large = "large";
    public static readonly string[] All = [Small, Medium, Large];

    /// <summary>Icon edge length in DIPs for a step.</summary>
    public static double IconEdge(string? size) => size switch
    {
        Small => 24,
        Large => 56,
        _ => 40,
    };

    /// <summary>Tile width in DIPs for a step.</summary>
    public static double TileWidth(string? size) => size switch
    {
        Small => 72,
        Large => 124,
        _ => 96,
    };
}

/// <summary>
/// Category chip label size steps. Four steps (11/12/14/16 DIP, user-fixed 2026-11-24), no
/// free-form value: the strip is the panel's navigation spine and a runaway font size would
/// break its row height. Medium is the shipped default (the global Button size of 12 DIP).
/// </summary>
public static class ChipFontSizes
{
    public const string Small = "small";
    public const string Medium = "medium";
    public const string Large = "large";
    public const string XLarge = "xlarge";
    public static readonly string[] All = [Small, Medium, Large, XLarge];

    /// <summary>Label font size in DIPs for a step.</summary>
    public static double FontSize(string? size) => size switch
    {
        Small => 11,
        Large => 14,
        XLarge => 16,
        _ => 12,
    };
}

/// <summary>Tab strip placement values (design: 标签位置 顶部/左侧).</summary>
public static class TabPositions
{
    public const string Top = "top";
    public const string Left = "left";
    public static readonly string[] All = { Top, Left };
}

/// <summary>Panel summon position modes (Sprint 3 / ROADMAP E1).</summary>
public static class PanelPositions
{
    /// <summary>Restore the last position the user dragged the panel to (default).</summary>
    public const string Remember = "remember";

    /// <summary>Centre on the pointer when summoned.</summary>
    public const string Mouse = "mouse";

    /// <summary>Centre on the work area.</summary>
    public const string Center = "center";

    /// <summary>Accepted values.</summary>
    public static readonly string[] All = { Remember, Mouse, Center };
}

/// <summary>
/// Panel surface materials. True Mica/Acrylic is deliberately NOT offered: it requires a
/// non-layered window (our panel uses AllowsTransparency for its 18px rounded corners) and it
/// does not composite at all in remote sessions - measured, see IMPLEMENTATION-NOTES. Translucent
/// alpha blending works everywhere and is what this build ships.
/// </summary>
public static class PanelMaterials
{
    /// <summary>Opaque surface (default).</summary>
    public const string None = "none";

    /// <summary>Alpha-blended surface: the desktop shows through the panel.</summary>
    public const string Translucent = "translucent";

    /// <summary>Accepted values.</summary>
    public static readonly string[] All = { None, Translucent };
}
