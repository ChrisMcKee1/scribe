namespace Scribe.Core.Appearance;

/// <summary>The theme a plan is made for.</summary>
public enum AppearanceTheme
{
    Unknown,
    Light,
    Dark,
    HighContrast,
}

/// <summary>
/// The theme colours a plan reads, named for what they are in WPF-UI 4.3.0's themes. The shell maps each one to the
/// resource it comes from, so this folder holds the decisions and none of the resource plumbing.
/// </summary>
public enum ThemeColor
{
    /// <summary><c>SolidBackgroundFillColorBase</c>: the page, and what a translucent fill is seen over.</summary>
    Surface,

    /// <summary><c>SystemAccentColorPrimary</c>: selected list items, checked boxes, switches that are on.</summary>
    AccentPrimary,

    /// <summary><c>SystemAccentColorSecondary</c>: a checked toggle button while hovered.</summary>
    AccentSecondary,

    /// <summary><c>SystemAccentColorTertiary</c>: a checked toggle button while pressed.</summary>
    AccentTertiary,

    /// <summary><c>AccentFillColorDefault</c>: accent buttons, and the accent fills Scribe draws itself.</summary>
    AccentFill,

    /// <summary><c>AccentFillColorSecondary</c>: that fill at 90%, what a hovered accent control shows.</summary>
    AccentFillHover,

    /// <summary><c>AccentFillColorTertiary</c>: that fill at 80%, what a pressed accent control shows.</summary>
    AccentFillPressed,

    /// <summary><c>PaletteOrangeColor</c>: a caution badge.</summary>
    PaletteOrange,

    /// <summary><c>PaletteLightBlueColor</c>: an info badge.</summary>
    PaletteLightBlue,

    /// <summary><c>PaletteRedColor</c>: a danger badge or button.</summary>
    PaletteRed,

    /// <summary><c>PaletteGreenColor</c>: a success badge.</summary>
    PaletteGreen,
}

/// <summary>A foreground drawn on a coloured fill, one per kind of control that draws one.</summary>
public enum AccentForegroundRole
{
    /// <summary>Text on an accent button, at rest, hovered and pressed.</summary>
    AccentButton,

    /// <summary>Text and glyphs Scribe draws on the accent fill itself (word chips, the About mark).</summary>
    AccentFill,

    /// <summary>Text of a selected list or navigation item, on the primary accent.</summary>
    SelectedItem,

    /// <summary>Text of a checked toggle button.</summary>
    CheckedToggleButton,

    /// <summary>Today's date in a calendar.</summary>
    CalendarToday,

    /// <summary>The check, or the dash, in a checked check box.</summary>
    CheckGlyph,

    /// <summary>The dot in a selected radio button.</summary>
    RadioGlyph,

    /// <summary>The knob of a switch that is on.</summary>
    SwitchKnob,

    /// <summary>Text of a caution badge.</summary>
    CautionBadge,

    /// <summary>Text of an info badge.</summary>
    InfoBadge,

    /// <summary>Text of a danger badge.</summary>
    DangerBadge,

    /// <summary>Text of a success badge.</summary>
    SuccessBadge,

    /// <summary>Text of a danger button.</summary>
    DangerButton,
}

/// <summary>Whether a foreground is text (SC 1.4.3) or a glyph that marks a state (SC 1.4.11).</summary>
public enum ForegroundKind
{
    Text,
    Glyph,
}

/// <summary>Why a plan holds foregrounds or leaves the theme's own.</summary>
public enum AccentContrastMode
{
    /// <summary>A light or dark theme: every role whose fills could be read has its foreground chosen.</summary>
    Applied,

    /// <summary>
    /// A contrast theme, Windows' or WPF-UI's: nothing is chosen, so the theme's system colour pairs stay exactly as
    /// they are (https://learn.microsoft.com/windows/apps/design/accessibility/high-contrast-themes#contrast-colors).
    /// </summary>
    ContrastTheme,

    /// <summary>No theme is known yet, so nothing is chosen either.</summary>
    UnknownTheme,
}

/// <summary>The foreground one role takes, and what it measures on that role's fills.</summary>
public sealed record RoleForeground(AccentForegroundRole Role, ForegroundKind Kind, AccentForegroundChoice Choice)
{
    // The theme dictionaries' own secondary tones for each hue: #B3FFFFFF in the light theme, #80000000 in the dark.
    private const byte WhiteSecondaryAlpha = 0xB3;
    private const byte BlackSecondaryAlpha = 0x80;

    public SrgbColor Foreground => Choice.Foreground;

    /// <summary>The fainter tone of the same foreground, for pressed text, as the themes pair it.</summary>
    public SrgbColor SecondaryForeground =>
        Choice.Foreground == SrgbColor.White
            ? SrgbColor.White.WithAlpha(WhiteSecondaryAlpha)
            : SrgbColor.Black.WithAlpha(BlackSecondaryAlpha);

    /// <summary>The WCAG 2.2 minimum that applies to this kind: 4.5:1 for text, 3:1 for a glyph.</summary>
    public double ApplicableMinimum => Kind == ForegroundKind.Text ? WcagContrast.TextMinimum : WcagContrast.NonTextMinimum;
}

/// <summary>What a theme's coloured fills need: a foreground per role, and the selection cues that do not rely on the accent.</summary>
/// <param name="Mode">Whether anything was chosen.</param>
/// <param name="Foregrounds">One per role whose fills could be read; none in a contrast theme.</param>
/// <param name="SelectedRowCue">
/// Whether a selected library row gets its outline and SemiBold name. Their subtle fill and accent pill carry the
/// selection only weakly (1.18:1 and 1.63:1 in the dark theme), so this is on in every light and dark theme.
/// </param>
/// <param name="SelectedItemWeightCue">
/// Whether a selected list item's text is SemiBold: only where its accent fill is under 3:1 against the page, the
/// SC 1.4.11 minimum for a state, so the selection shows without relying on a fill that may not.
/// </param>
/// <param name="SelectedItemFillRatio">The selected-item fill against the page, when it could be read.</param>
public sealed record AccentContrastPlan(
    AccentContrastMode Mode,
    IReadOnlyList<RoleForeground> Foregrounds,
    bool SelectedRowCue,
    bool SelectedItemWeightCue,
    double? SelectedItemFillRatio)
{
    public RoleForeground? For(AccentForegroundRole role) => Foregrounds.FirstOrDefault(f => f.Role == role);
}

/// <summary>
/// Plans the foreground of every coloured fill in a theme, with <see cref="AccentForegroundChooser"/>, from the colours
/// the theme actually has: the user's accent as WPF-UI derived it, and WPF-UI's palette.
/// </summary>
public static class AccentContrastPlanner
{
    // WPF-UI's own base colours, which Windows uses as Mica's fallback: what the page is when its resource is unreadable.
    private static readonly SrgbColor LightSurface = SrgbColor.FromRgb(0xF3, 0xF3, 0xF3);
    private static readonly SrgbColor DarkSurface = SrgbColor.FromRgb(0x20, 0x20, 0x20);

    // Which fills each foreground sits on, from WPF-UI 4.3.0's control templates and theme dictionaries. Hover and
    // pressed fills are listed apart: legibility at rest comes first, and in those states only where it can.
    private static readonly RoleSpec[] Roles =
    [
        // Button.xaml, Primary: AccentButtonBackground, then its PointerOver and Pressed tones.
        new(AccentForegroundRole.AccentButton, ForegroundKind.Text, TextConvention.OnAccent,
            [new(ThemeColor.AccentFill)], [new(ThemeColor.AccentFillHover), new(ThemeColor.AccentFillPressed)]),

        // Scribe draws on AccentFillColorDefaultBrush (word chips) and AccentFillColorSecondaryBrush (the About mark).
        new(AccentForegroundRole.AccentFill, ForegroundKind.Text, TextConvention.OnAccent,
            [new(ThemeColor.AccentFill), new(ThemeColor.AccentFillHover)], [new(ThemeColor.AccentFillPressed)]),

        // ListBoxItem.xaml: a selected item stays on ListBoxItemSelectedBackgroundThemeBrush while hovered.
        new(AccentForegroundRole.SelectedItem, ForegroundKind.Text, TextConvention.OnAccent,
            [new(ThemeColor.AccentPrimary)], []),

        // ToggleButton.xaml: checked, then ToggleButtonForegroundCheckedPointerOver, which it uses as the hovered
        // background. Pressed, it draws the theme's fainter secondary tone on the tertiary shade, as Fluent's pressed
        // text is meant to be; Scribe draws no WPF-UI toggle button.
        new(AccentForegroundRole.CheckedToggleButton, ForegroundKind.Text, TextConvention.OnAccent,
            [new(ThemeColor.AccentPrimary)], [new(ThemeColor.AccentSecondary)]),

        new(AccentForegroundRole.CalendarToday, ForegroundKind.Text, TextConvention.OnAccent,
            [new(ThemeColor.AccentPrimary)], []),

        // CheckBox.xaml: CheckBoxCheckBackgroundFillChecked, then its PointerOver and Pressed fills.
        new(AccentForegroundRole.CheckGlyph, ForegroundKind.Glyph, TextConvention.OnAccent,
            [new(ThemeColor.AccentPrimary)], [new(ThemeColor.AccentFillHover), new(ThemeColor.AccentFillPressed)]),

        // RadioButton.xaml: RadioButtonOuterEllipseCheckedStroke, then its PointerOver fill.
        new(AccentForegroundRole.RadioGlyph, ForegroundKind.Glyph, TextConvention.OnAccent,
            [new(ThemeColor.AccentPrimary)], [new(ThemeColor.AccentFillPressed)]),

        // ToggleSwitch.xaml: ToggleSwitchFillOn, then its PointerOver and Pressed fills.
        new(AccentForegroundRole.SwitchKnob, ForegroundKind.Glyph, TextConvention.OnAccent,
            [new(ThemeColor.AccentPrimary)], [new(ThemeColor.AccentFillHover), new(ThemeColor.AccentFillPressed)]),

        // Badge.xaml draws every appearance's text in BadgeForeground, the text-on-accent colour.
        new(AccentForegroundRole.CautionBadge, ForegroundKind.Text, TextConvention.OnAccent, [new(ThemeColor.PaletteOrange)], []),
        new(AccentForegroundRole.InfoBadge, ForegroundKind.Text, TextConvention.OnAccent, [new(ThemeColor.PaletteLightBlue)], []),
        new(AccentForegroundRole.DangerBadge, ForegroundKind.Text, TextConvention.OnAccent, [new(ThemeColor.PaletteRed)], []),
        new(AccentForegroundRole.SuccessBadge, ForegroundKind.Text, TextConvention.OnAccent, [new(ThemeColor.PaletteGreen)], []),

        // Button.xaml, Danger: the palette red at rest, 90% hovered and 70% pressed, with the ordinary button text.
        new(AccentForegroundRole.DangerButton, ForegroundKind.Text, TextConvention.Body,
            [new(ThemeColor.PaletteRed)], [new(ThemeColor.PaletteRed, 0.9), new(ThemeColor.PaletteRed, 0.7)]),
    ];

    /// <summary>Every role a plan in a light or dark theme can hold, for the shell's mapping to check itself against.</summary>
    public static IReadOnlyList<AccentForegroundRole> AllRoles { get; } = Roles.Select(r => r.Role).ToArray();

    /// <param name="theme">The theme WPF-UI applied.</param>
    /// <param name="systemHighContrast">Whether Windows is in a contrast theme, whatever WPF-UI applied.</param>
    /// <param name="colors">The theme's colours as they are now; a missing one leaves the roles that need it out.</param>
    public static AccentContrastPlan Plan(
        AppearanceTheme theme,
        bool systemHighContrast,
        IReadOnlyDictionary<ThemeColor, SrgbColor> colors)
    {
        ArgumentNullException.ThrowIfNull(colors);

        // Windows can be in a contrast theme while WPF-UI still has a light or dark dictionary loaded (Scribe applies
        // the light or dark app theme itself), and either way the system pairs decide, not a chosen foreground.
        if (theme == AppearanceTheme.HighContrast || systemHighContrast)
        {
            return new AccentContrastPlan(AccentContrastMode.ContrastTheme, [], false, false, null);
        }

        if (theme == AppearanceTheme.Unknown)
        {
            return new AccentContrastPlan(AccentContrastMode.UnknownTheme, [], false, false, null);
        }

        var fallbackSurface = theme == AppearanceTheme.Light ? LightSurface : DarkSurface;
        var surface = colors.TryGetValue(ThemeColor.Surface, out var readSurface)
            ? readSurface.Over(fallbackSurface)
            : fallbackSurface;

        var foregrounds = new List<RoleForeground>(Roles.Length);
        foreach (var spec in Roles)
        {
            if (TryResolve(spec.Rest, out var rest) && TryResolve(spec.Interaction, out var interaction))
            {
                var themeForeground = (spec.Convention, theme) switch
                {
                    (TextConvention.OnAccent, AppearanceTheme.Light) => SrgbColor.White,
                    (TextConvention.OnAccent, _) => SrgbColor.Black,
                    (TextConvention.Body, AppearanceTheme.Light) => SrgbColor.Black,
                    _ => SrgbColor.White,
                };

                // Glyphs are held to the text minimum as well: a check or a knob then matches the label beside it on
                // the same fill, and clears SC 1.4.11's 3:1 with room.
                var choice = AccentForegroundChooser.Choose(rest, interaction, themeForeground, WcagContrast.TextMinimum);
                foregrounds.Add(new RoleForeground(spec.Role, spec.Kind, choice));
            }
        }

        double? selectedItemFillRatio = colors.TryGetValue(ThemeColor.AccentPrimary, out var primary)
            ? WcagContrast.Ratio(primary.Over(surface), surface)
            : null;

        // A fill that could not be read might be invisible, so the weight cue errs on the side of showing.
        var weightCue = selectedItemFillRatio is not { } ratio || ratio < WcagContrast.NonTextMinimum;
        return new AccentContrastPlan(AccentContrastMode.Applied, foregrounds, true, weightCue, selectedItemFillRatio);

        bool TryResolve(Fill[] fills, out SrgbColor[] resolved)
        {
            resolved = new SrgbColor[fills.Length];
            for (var i = 0; i < fills.Length; i++)
            {
                if (!colors.TryGetValue(fills[i].Color, out var color))
                {
                    return false;
                }

                resolved[i] = color.WithOpacity(fills[i].Opacity).Over(surface);
            }

            return true;
        }
    }

    // Which foreground a theme itself puts on a role's fill: the text-on-accent pair (white in the light theme,
    // black in the dark one) or the ordinary text colour (the reverse), for a fill that uses it.
    private enum TextConvention
    {
        OnAccent,
        Body,
    }

    private readonly record struct Fill(ThemeColor Color, double Opacity = 1);

    private sealed record RoleSpec(
        AccentForegroundRole Role,
        ForegroundKind Kind,
        TextConvention Convention,
        Fill[] Rest,
        Fill[] Interaction);
}
