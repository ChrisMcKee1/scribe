namespace Scribe.Core.Appearance;

/// <summary>The system colour a contrast theme draws in place of one of WPF-UI's placeholders.</summary>
public enum ContrastSystemColor
{
    WindowText,
    GrayText,
}

/// <summary>Whether a placeholder resource is a brush or a colour.</summary>
public enum ContrastResourceKind
{
    Brush,
    Color,
}

/// <summary>One placeholder resource and the system colour drawn in its place.</summary>
public sealed record ContrastRepair(string Key, ContrastResourceKind Kind, ContrastSystemColor Color);

/// <summary>
/// The resources WPF-UI 4.3.0's contrast dictionaries (HC1, HC2, HCBlack and HCWhite) leave as the placeholder
/// <c>#FF0000</c> that Scribe draws, directly or through a WPF-UI control it uses, as text or a line on the contrast
/// dictionary's own Window-coloured surfaces, each with the system colour WinUI's own contrast dictionaries use for the
/// same role (Common_themeresources_any.xaml and MenuFlyout_themeresources.xaml in microsoft-ui-xaml). Without them a
/// contrast theme drew every status text and icon red, and disabled menu items and menu separators red. They apply only
/// while WPF-UI's contrast dictionary is loaded: WindowText and GrayText are right only on its surfaces.
/// </summary>
/// <remarks>
/// The menu highlight (<c>MenuBarItemBackgroundSelected</c>, <c>MenuBarItemBackgroundPressed</c>,
/// <c>MenuBarItemTextForegroundPressed</c>) is deliberately not repaired. Its fill is also WPF-UI's ListBoxItem hover, and
/// WPF-UI's MenuItem templates draw a highlighted item's label in the item's own inherited Foreground, so a Highlight
/// fill would need HighlightText on every label drawn over it and on none drawn elsewhere: a submenu's items inherit
/// their parent's Foreground, a checked item's mark sits on its own box, and a profile description or a shortcut hint
/// keeps its own colour. Only replaced templates can draw that pair correctly (the tray menu's does); until then WPF-UI's
/// placeholder red stays (WindowText on it measures 4.0:1 in the dark contrast themes and 2.7:1 in HCWhite).
/// </remarks>
public static class ContrastPlaceholderRepairs
{
    /// <summary>The placeholder colour WPF-UI's contrast dictionaries leave in these keys.</summary>
    public static readonly SrgbColor Placeholder = SrgbColor.FromRgb(0xFF, 0x00, 0x00);

    public static IReadOnlyList<ContrastRepair> All { get; } =
    [
        // A menu separator (WPF-UI's Separator style for menus). WinUI: MenuFlyoutSeparatorBackground is WindowText.
        new("MenuBarItemBorderBrush", ContrastResourceKind.Brush, ContrastSystemColor.WindowText),

        // A disabled menu item's text (WPF-UI's MenuItem templates and the tray menu's, as a colour). WinUI:
        // MenuFlyoutItemForegroundDisabled is GrayText.
        new("TextFillColorDisabled", ContrastResourceKind.Color, ContrastSystemColor.GrayText),

        // Scribe's status text and icons (and WPF-UI's DataGrid row error icon). WinUI: SystemFillColorSuccessBrush,
        // ...CautionBrush and ...CriticalBrush are WindowText.
        new("SystemFillColorSuccessBrush", ContrastResourceKind.Brush, ContrastSystemColor.WindowText),
        new("SystemFillColorCautionBrush", ContrastResourceKind.Brush, ContrastSystemColor.WindowText),
        new("SystemFillColorCriticalBrush", ContrastResourceKind.Brush, ContrastSystemColor.WindowText),
    ];
}
