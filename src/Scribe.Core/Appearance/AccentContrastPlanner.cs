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
/// The theme colours a plan reads, named for what they are in WPF-UI 4.3.0's themes and in WPF. The shell maps each one
/// to the resource it comes from, so this folder holds the decisions and none of the resource plumbing.
/// </summary>
public enum ThemeColor
{
    /// <summary><c>SolidBackgroundFillColorBase</c>: the page (Mica's fallback), and what translucent fills are seen over.</summary>
    Surface,

    /// <summary><c>ApplicationBackgroundColor</c>: a window's background where it has no Mica.</summary>
    WindowBackground,

    /// <summary><c>CardBackgroundFillColorDefault</c>, translucent over the page: a card.</summary>
    CardBackground,

    /// <summary><c>ControlFillColorDefault</c>, translucent over the page: a filled row, such as the clean-up list's.</summary>
    ControlFill,

    /// <summary>
    /// <c>ControlFillColorSecondary</c>, translucent over the page: the panel behind the Diagnostics figures, whose best
    /// pace is accent text, and the lightest surface accent text is drawn on in the dark theme.
    /// </summary>
    ControlFillSecondary,

    /// <summary><c>ControlStrongStrokeColorDefault</c>, translucent: the border of an unchecked box.</summary>
    StrongStroke,

    /// <summary><c>TextFillColorPrimary</c>: ordinary text, and a button's label.</summary>
    BodyText,

    /// <summary><c>TextFillColorSecondary</c>: the pressed label WPF-UI means a standard or danger button to have.</summary>
    BodyTextSecondary,

    /// <summary>
    /// <c>Control.Foreground</c>'s default, <c>SystemColors.ControlTextColor</c>: what WPF-UI 4.3.0 actually draws the label
    /// of every pressed <c>ui:Button</c> in, because its template's binding to <c>PressedForeground</c> never resolves.
    /// </summary>
    ControlText,

    /// <summary><c>ButtonBackgroundPressed</c>: a pressed standard or transparent button, translucent.</summary>
    ButtonPressedFill,

    /// <summary><c>SystemAccentColorPrimary</c>: selected list items, checked boxes, switches that are on.</summary>
    AccentPrimary,

    /// <summary><c>AccentFillColorDefault</c>: accent buttons, and the accent fills Scribe draws itself.</summary>
    AccentFill,

    /// <summary><c>AccentFillColorSecondary</c>: the accent fill at alpha 229, a hovered accent control.</summary>
    AccentFillHover,

    /// <summary><c>AccentFillColorTertiary</c>: the accent fill at alpha 204, a pressed accent control.</summary>
    AccentFillPressed,

    /// <summary><c>AccentTextFillColorPrimaryBrush</c> as WPF-UI's accent manager wrote it: headings and accent text.</summary>
    AccentTextPrimary,

    /// <summary><c>AccentTextFillColorSecondaryBrush</c> as WPF-UI wrote it.</summary>
    AccentTextSecondary,

    /// <summary><c>AccentTextFillColorTertiaryBrush</c> as WPF-UI wrote it.</summary>
    AccentTextTertiary,

    /// <summary><c>SystemColors.HotTrackColor</c>: the colour WPF's own Hyperlink style draws a link in.</summary>
    Hyperlink,

    /// <summary>The red WPF's own Hyperlink style draws a hovered link in.</summary>
    HyperlinkHover,

    /// <summary><c>PaletteOrangeColor</c>: a caution badge.</summary>
    PaletteOrange,

    /// <summary><c>PaletteLightBlueColor</c>: an info badge.</summary>
    PaletteLightBlue,

    /// <summary><c>PaletteRedColor</c>: a danger badge or button.</summary>
    PaletteRed,

    /// <summary><c>PaletteGreenColor</c>: a success badge.</summary>
    PaletteGreen,
}

/// <summary>
/// A foreground drawn on a coloured fill, one per state a template draws with its own resource, so a state whose fill
/// needs another foreground gets one without changing the others.
/// </summary>
public enum AccentForegroundRole
{
    /// <summary>An accent button's label at rest and hovered.</summary>
    AccentButton,

    /// <summary>An accent button's label while pressed.</summary>
    AccentButtonPressed,

    /// <summary>Text and glyphs Scribe draws on the accent fill itself (word chips, the About mark).</summary>
    AccentFill,

    /// <summary>The text of a selected list or navigation item, on the primary accent.</summary>
    SelectedItem,

    /// <summary>The check, or the dash, in a checked box, at rest, hovered and pressed (WPF-UI draws it with one brush).</summary>
    CheckGlyph,

    /// <summary>The knob of a switch that is on, at rest.</summary>
    SwitchKnob,

    /// <summary>The knob of a switch that is on, hovered.</summary>
    SwitchKnobHover,

    /// <summary>The knob of a switch that is on, pressed.</summary>
    SwitchKnobPressed,

    /// <summary>The text of a caution badge.</summary>
    CautionBadge,

    /// <summary>The text of an info badge.</summary>
    InfoBadge,

    /// <summary>The text of a danger badge.</summary>
    DangerBadge,

    /// <summary>The text of a success badge.</summary>
    SuccessBadge,

    /// <summary>A danger button's label at rest and hovered.</summary>
    DangerButton,

    /// <summary>A danger button's label while pressed.</summary>
    DangerButtonPressed,

    /// <summary>A standard or transparent button's label or icon while pressed.</summary>
    SecondaryButtonPressed,
}

/// <summary>A colour that is itself what the user reads or sees, kept or corrected in lightness only.</summary>
public enum AccentShadeRole
{
    /// <summary>Accent-coloured text: headings, the About and Welcome headings and icons.</summary>
    AccentTextPrimary,

    /// <summary>WPF-UI's secondary accent text brush.</summary>
    AccentTextSecondary,

    /// <summary>WPF-UI's tertiary accent text brush.</summary>
    AccentTextTertiary,

    /// <summary>A link at rest.</summary>
    Hyperlink,

    /// <summary>A link while hovered.</summary>
    HyperlinkHover,

    /// <summary>The track of a switch that is on, at rest.</summary>
    SwitchTrack,

    /// <summary>The track of a switch that is on, hovered.</summary>
    SwitchTrackHover,

    /// <summary>The track of a switch that is on, pressed.</summary>
    SwitchTrackPressed,
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
    /// <summary>A light or dark theme: every role whose colours could be read is planned.</summary>
    Applied,

    /// <summary>
    /// A contrast theme, Windows' or WPF-UI's: nothing is planned, so the theme's system colour pairs stay exactly as
    /// they are (https://learn.microsoft.com/windows/apps/design/accessibility/high-contrast-themes#contrast-colors).
    /// </summary>
    ContrastTheme,

    /// <summary>No theme is known yet, so nothing is planned either.</summary>
    UnknownTheme,
}

/// <summary>The foreground one role takes, and what it measures on that role's fills.</summary>
public sealed record RoleForeground(AccentForegroundRole Role, ForegroundKind Kind, AccentForegroundChoice Choice)
{
    public SrgbColor Foreground => Choice.Foreground;

    /// <summary>The WCAG 2.2 minimum that applies to this kind: 4.5:1 for text, 3:1 for a glyph.</summary>
    public double ApplicableMinimum => Kind == ForegroundKind.Text ? WcagContrast.TextMinimum : WcagContrast.NonTextMinimum;
}

/// <summary>A shade the plan kept or corrected.</summary>
/// <param name="Role">What the shade is.</param>
/// <param name="Original">The theme's colour, translucent if the theme's is.</param>
/// <param name="Color">The colour to draw: the original, or the same hue and saturation at another lightness, opaque.</param>
/// <param name="Required">The contrast it must reach against every surface.</param>
/// <param name="OriginalRatio">The original's lowest contrast against the surfaces, as it shows on each.</param>
/// <param name="Ratio">The drawn colour's lowest contrast against the surfaces.</param>
public sealed record ShadeCorrection(AccentShadeRole Role, SrgbColor Original, SrgbColor Color, double Required, double OriginalRatio, double Ratio)
{
    public bool Changed => Color != Original;

    public bool Meets => Ratio >= Required;
}

/// <summary>The border a checked box gets where its fill does not stand out from the page.</summary>
/// <param name="Color">The border's colour, drawn over the fill (translucent when it is the strong stroke).</param>
/// <param name="IsStrongStroke">True when it is the theme's own unchecked border, false when the check's colour.</param>
/// <param name="FillRatio">The lowest contrast of a checked fill, at rest, hovered or pressed, against its surface.</param>
/// <param name="Ratio">The lowest contrast of the border, drawn over each checked fill, against the surface under it.</param>
public sealed record CheckBoxPerimeter(SrgbColor Color, bool IsStrongStroke, double FillRatio, double Ratio);

/// <summary>The outline a selected list item gets where its accent fill does not stand out from the page.</summary>
/// <param name="Color">The outline's colour: the item's own text colour on that fill, opaque.</param>
/// <param name="AgainstFill">Its contrast with the fill it encloses, when the fill could be read.</param>
/// <param name="AgainstSurfaces">Its lowest contrast against the surfaces the list is drawn on.</param>
public sealed record SelectionOutline(SrgbColor Color, double? AgainstFill, double AgainstSurfaces);

/// <summary>What a theme's coloured fills and accent-coloured text need, and the selection cues.</summary>
/// <param name="Mode">Whether anything was planned.</param>
/// <param name="Foregrounds">One per foreground role whose fills could be read; none in a contrast theme.</param>
/// <param name="Shades">One per shade role whose colour could be read; none in a contrast theme.</param>
/// <param name="CheckBoxPerimeter">
/// The border a checked box gets because its fill, at rest, hovered or pressed, is under 3:1 against a surface it is
/// drawn on (SC 1.4.11 asks that the box be identifiable, not only its tick); null where every fill stands out.
/// </param>
/// <param name="SelectedRowCue">
/// Whether a selected library row gets its outline and SemiBold name: their subtle fill and accent pill carry the
/// selection only weakly (1.18:1 and 1.63:1 in the dark theme), so this is on in every light and dark theme.
/// </param>
/// <param name="SelectedItemOutline">
/// The outline of a selected list item (the rail, Snippets, Profiles) where its accent fill is under 3:1 against the
/// page; null where the fill stands out. An outline in the ring the item's template already reserves, not a weight, so
/// it changes no width.
/// </param>
/// <param name="SelectedItemFillRatio">The selected-item fill against the surfaces, when it could be read.</param>
public sealed record AccentContrastPlan(
    AccentContrastMode Mode,
    IReadOnlyList<RoleForeground> Foregrounds,
    IReadOnlyList<ShadeCorrection> Shades,
    CheckBoxPerimeter? CheckBoxPerimeter,
    bool SelectedRowCue,
    SelectionOutline? SelectedItemOutline,
    double? SelectedItemFillRatio)
{
    /// <summary>
    /// Whether Scribe's own overrides apply at all: true only in a light or dark theme. Every Scribe trigger that changes
    /// a WPF-UI or WPF colour is gated on this, so in a contrast theme none is active, whatever its precedence.
    /// </summary>
    public bool Applies => Mode == AccentContrastMode.Applied;

    public RoleForeground? For(AccentForegroundRole role) => Foregrounds.FirstOrDefault(f => f.Role == role);

    public ShadeCorrection? For(AccentShadeRole role) => Shades.FirstOrDefault(s => s.Role == role);
}

/// <summary>
/// Plans, from the colours a theme actually has (the user's accent as WPF-UI derived it, WPF-UI's palette, WPF's link
/// colours), the foreground of every coloured fill in each state a template draws it, the lightness of every
/// accent-coloured text and state fill that must read on its own, and the selection cues.
/// </summary>
public static class AccentContrastPlanner
{
    // WPF-UI's own base colours, which Windows uses as Mica's fallback: what the page is when its resource is unreadable.
    private static readonly SrgbColor LightSurface = SrgbColor.FromRgb(0xF3, 0xF3, 0xF3);
    private static readonly SrgbColor DarkSurface = SrgbColor.FromRgb(0x20, 0x20, 0x20);

    // The themes' own text tones, where the resources cannot be read (Light.xaml and Dark.xaml in WPF-UI 4.3.0).
    private static readonly SrgbColor LightBodyText = SrgbColor.Parse("#E4000000");
    private static readonly SrgbColor DarkBodyText = SrgbColor.White;
    private static readonly SrgbColor LightBodyTextSecondary = SrgbColor.Parse("#9E000000");
    private static readonly SrgbColor DarkBodyTextSecondary = SrgbColor.Parse("#C5FFFFFF");

    // Colours that are read on their own: accent text and links against every surface at the text minimum, and a
    // switch's track, which marks its state, at the non-text one.
    private static readonly ShadeSpec[] ShadeSpecs =
    [
        new(AccentShadeRole.AccentTextPrimary, ThemeColor.AccentTextPrimary, WcagContrast.TextMinimum),
        new(AccentShadeRole.AccentTextSecondary, ThemeColor.AccentTextSecondary, WcagContrast.TextMinimum),
        new(AccentShadeRole.AccentTextTertiary, ThemeColor.AccentTextTertiary, WcagContrast.TextMinimum),
        new(AccentShadeRole.Hyperlink, ThemeColor.Hyperlink, WcagContrast.TextMinimum),
        new(AccentShadeRole.HyperlinkHover, ThemeColor.HyperlinkHover, WcagContrast.TextMinimum),

        // ToggleSwitch.xaml: once on, the stroked track fades out and the visible one has no stroke, so the fill is
        // the only thing that can stand out from the page: ToggleSwitchFillOn, then its PointerOver and Pressed tones.
        new(AccentShadeRole.SwitchTrack, ThemeColor.AccentPrimary, WcagContrast.NonTextMinimum),
        new(AccentShadeRole.SwitchTrackHover, ThemeColor.AccentFillHover, WcagContrast.NonTextMinimum),
        new(AccentShadeRole.SwitchTrackPressed, ThemeColor.AccentFillPressed, WcagContrast.NonTextMinimum),
    ];

    // Which fills each foreground sits on, from WPF-UI 4.3.0's templates, one role per resource a template reads. A
    // translucent fill is measured as it shows on each surface the control can sit on.
    private static readonly RoleSpec[] Roles =
    [
        // Button.xaml, Primary: AccentButtonForeground at rest and while hovered (over AccentButtonBackgroundPointerOver).
        new(AccentForegroundRole.AccentButton, ForegroundKind.Text, [TextConvention.OnAccent],
            [Fill.Of(ThemeColor.AccentFill)], [Fill.Of(ThemeColor.AccentFillHover)]),

        // Pressed, over AccentButtonBackgroundPressed. What shows today is the failed binding's black, so it is kept
        // where it reads; else the label the template means to draw, AccentButtonForegroundPointerOver.
        new(AccentForegroundRole.AccentButtonPressed, ForegroundKind.Text, [TextConvention.PressedAsDrawn, TextConvention.OnAccent],
            [Fill.Of(ThemeColor.AccentFillPressed)], []),

        // Scribe's word chips on AccentFillColorDefaultBrush and the About mark on AccentFillColorSecondaryBrush, which
        // is the accent fill with brush opacity 0.9 (WPF draws it as alpha 230, #53538F for #0E0E70 in dark).
        new(AccentForegroundRole.AccentFill, ForegroundKind.Text, [TextConvention.OnAccent],
            [Fill.Of(ThemeColor.AccentFill), Fill.Of(ThemeColor.AccentFill, 0.9)], []),

        // ListBoxItem.xaml: a selected item stays on ListBoxItemSelectedBackgroundThemeBrush while hovered.
        new(AccentForegroundRole.SelectedItem, ForegroundKind.Text, [TextConvention.OnAccent],
            [Fill.Of(ThemeColor.AccentPrimary)], []),

        // CheckBox.xaml: CheckBoxCheckGlyphForeground on CheckBoxCheckBackgroundFillChecked, and on its PointerOver and
        // Pressed fills; one brush for all three states.
        new(AccentForegroundRole.CheckGlyph, ForegroundKind.Glyph, [TextConvention.OnAccent],
            [Fill.Of(ThemeColor.AccentPrimary)], [Fill.Of(ThemeColor.AccentFillHover), Fill.Of(ThemeColor.AccentFillPressed)]),

        // ToggleSwitch.xaml: a knob brush per state, each on its own track, as corrected.
        new(AccentForegroundRole.SwitchKnob, ForegroundKind.Glyph, [TextConvention.OnAccent],
            [Fill.OfShade(AccentShadeRole.SwitchTrack)], []),
        new(AccentForegroundRole.SwitchKnobHover, ForegroundKind.Glyph, [TextConvention.OnAccent],
            [Fill.OfShade(AccentShadeRole.SwitchTrackHover)], []),
        new(AccentForegroundRole.SwitchKnobPressed, ForegroundKind.Glyph, [TextConvention.OnAccent],
            [Fill.OfShade(AccentShadeRole.SwitchTrackPressed)], []),

        // Badge.xaml draws every appearance's text in BadgeForeground, the text-on-accent colour.
        new(AccentForegroundRole.CautionBadge, ForegroundKind.Text, [TextConvention.OnAccent], [Fill.Of(ThemeColor.PaletteOrange)], []),
        new(AccentForegroundRole.InfoBadge, ForegroundKind.Text, [TextConvention.OnAccent], [Fill.Of(ThemeColor.PaletteLightBlue)], []),
        new(AccentForegroundRole.DangerBadge, ForegroundKind.Text, [TextConvention.OnAccent], [Fill.Of(ThemeColor.PaletteRed)], []),
        new(AccentForegroundRole.SuccessBadge, ForegroundKind.Text, [TextConvention.OnAccent], [Fill.Of(ThemeColor.PaletteGreen)], []),

        // Button.xaml, Danger: the palette red at rest and at brush opacity 0.9 hovered, with the ordinary button text.
        new(AccentForegroundRole.DangerButton, ForegroundKind.Text, [TextConvention.Body],
            [Fill.Of(ThemeColor.PaletteRed)], [Fill.Of(ThemeColor.PaletteRed, 0.9)]),

        // At brush opacity 0.7 pressed: today's black where it reads, else the pressed tone the style sets.
        new(AccentForegroundRole.DangerButtonPressed, ForegroundKind.Text, [TextConvention.PressedAsDrawn, TextConvention.BodySecondary],
            [Fill.Of(ThemeColor.PaletteRed, 0.7)], []),

        // A standard or transparent button pressed, over ButtonBackgroundPressed: as above.
        new(AccentForegroundRole.SecondaryButtonPressed, ForegroundKind.Text, [TextConvention.PressedAsDrawn, TextConvention.BodySecondary],
            [Fill.Of(ThemeColor.ButtonPressedFill)], []),
    ];

    /// <summary>Every foreground role a plan in a light or dark theme can hold, for the shell's mapping to check itself against.</summary>
    public static IReadOnlyList<AccentForegroundRole> AllRoles { get; } = Roles.Select(r => r.Role).ToArray();

    /// <summary>Every shade role a plan in a light or dark theme can hold.</summary>
    public static IReadOnlyList<AccentShadeRole> AllShades { get; } = ShadeSpecs.Select(s => s.Role).ToArray();

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
        // the light or dark app theme itself), and either way the system pairs decide, not a planned colour.
        if (theme == AppearanceTheme.HighContrast || systemHighContrast)
        {
            return new AccentContrastPlan(AccentContrastMode.ContrastTheme, [], [], null, false, null, null);
        }

        if (theme == AppearanceTheme.Unknown)
        {
            return new AccentContrastPlan(AccentContrastMode.UnknownTheme, [], [], null, false, null, null);
        }

        var light = theme == AppearanceTheme.Light;
        var fallbackPage = light ? LightSurface : DarkSurface;
        var page = colors.TryGetValue(ThemeColor.Surface, out var readPage) ? readPage.Over(fallbackPage) : fallbackPage;
        var surfaces = Surfaces(colors, page);

        var shades = new List<ShadeCorrection>(ShadeSpecs.Length);
        foreach (var spec in ShadeSpecs)
        {
            if (colors.TryGetValue(spec.Source, out var source))
            {
                // Lighter in the dark theme, darker in the light one: away from the page, whatever side the colour is on.
                var result = ContrastShade.Ensure(source, surfaces, spec.Required, lighter: !light);
                shades.Add(new ShadeCorrection(spec.Role, result.Original, result.Color, spec.Required, result.OriginalRatio, result.Ratio));
            }
        }

        var foregrounds = new List<RoleForeground>(Roles.Length);
        foreach (var spec in Roles)
        {
            var preferred = spec.Preferred.Select(Convention).OfType<SrgbColor>().ToArray();
            if (preferred.Length > 0 && TryResolve(spec.Rest, out var rest) && TryResolve(spec.Interaction, out var interaction))
            {
                // Each kind is held to its own minimum, so a glyph keeps the theme's own where it reaches SC 1.4.11's
                // 3:1. At 4.5:1 the default accent's switch knob would turn from white to black only while pressed
                // in the light theme (white is 3.86:1 there), a flicker that fixes nothing.
                var required = spec.Kind == ForegroundKind.Text ? WcagContrast.TextMinimum : WcagContrast.NonTextMinimum;
                var choice = AccentForegroundChooser.Choose(rest, interaction, preferred, required);
                foregrounds.Add(new RoleForeground(spec.Role, spec.Kind, choice));
            }
        }

        var perimeter = PlanPerimeter(colors, surfaces, foregrounds);

        double? selectedItemFillRatio = colors.TryGetValue(ThemeColor.AccentPrimary, out var primary)
            ? ContrastShade.Lowest(primary, surfaces)
            : null;

        // A fill that could not be read might not show, so the outline errs on the side of showing. The item's own text
        // colour reads on the fill by construction, and where the fill is too close to the page to stand out, that
        // colour is the one far from the page.
        SelectionOutline? itemOutline = null;
        if (selectedItemFillRatio is not { } fillRatio || fillRatio < WcagContrast.NonTextMinimum)
        {
            var outline = foregrounds.FirstOrDefault(f => f.Role == AccentForegroundRole.SelectedItem)?.Foreground
                ?? (light ? SrgbColor.Black : SrgbColor.White);
            outline = outline.Over(page);
            double? againstFill = colors.ContainsKey(ThemeColor.AccentPrimary)
                ? WcagContrast.Ratio(outline, primary.Over(page))
                : null;
            itemOutline = new SelectionOutline(outline, againstFill, ContrastShade.Lowest(outline, surfaces));
        }

        return new AccentContrastPlan(AccentContrastMode.Applied, foregrounds, shades, perimeter, true, itemOutline, selectedItemFillRatio);

        SrgbColor? Convention(TextConvention convention) => convention switch
        {
            TextConvention.OnAccent => light ? SrgbColor.White : SrgbColor.Black,
            TextConvention.Body => colors.GetValueOrDefault(ThemeColor.BodyText, light ? LightBodyText : DarkBodyText),
            TextConvention.BodySecondary => colors.GetValueOrDefault(ThemeColor.BodyTextSecondary, light ? LightBodyTextSecondary : DarkBodyTextSecondary),
            _ => colors.TryGetValue(ThemeColor.ControlText, out var drawn) ? drawn : null,
        };

        bool TryResolve(Fill[] fills, out List<SrgbColor> resolved)
        {
            resolved = new List<SrgbColor>(fills.Length * surfaces.Count);
            foreach (var fill in fills)
            {
                SrgbColor color;
                if (fill.Shade is { } shadeRole)
                {
                    if (shades.FirstOrDefault(s => s.Role == shadeRole) is not { } shade)
                    {
                        return false;
                    }

                    color = shade.Color;
                }
                else if (colors.TryGetValue(fill.Color, out var read))
                {
                    color = read.WithOpacity(fill.Opacity);
                }
                else
                {
                    return false;
                }

                // An opaque fill is itself on every surface; a translucent one is measured on each.
                foreach (var surface in surfaces)
                {
                    var drawn = color.Over(surface);
                    if (!resolved.Contains(drawn))
                    {
                        resolved.Add(drawn);
                    }
                }
            }

            return true;
        }
    }

    // Every surface accent-coloured text, a checked box or a switch is drawn on: the page (Mica's fallback), a window
    // without Mica, a card, a filled row and the Diagnostics panel, each translucent one over the page. Measuring on all
    // of them errs toward more contrast on the ones a given control is not on.
    private static List<SrgbColor> Surfaces(IReadOnlyDictionary<ThemeColor, SrgbColor> colors, SrgbColor page)
    {
        var surfaces = new List<SrgbColor> { page };
        foreach (var key in new[] { ThemeColor.WindowBackground, ThemeColor.CardBackground, ThemeColor.ControlFill, ThemeColor.ControlFillSecondary })
        {
            if (colors.TryGetValue(key, out var color) && !surfaces.Contains(color.Over(page)))
            {
                surfaces.Add(color.Over(page));
            }
        }

        return surfaces;
    }

    private static CheckBoxPerimeter? PlanPerimeter(
        IReadOnlyDictionary<ThemeColor, SrgbColor> colors,
        IReadOnlyList<SrgbColor> surfaces,
        IReadOnlyList<RoleForeground> foregrounds)
    {
        var fills = new List<SrgbColor>();
        foreach (var key in new[] { ThemeColor.AccentPrimary, ThemeColor.AccentFillHover, ThemeColor.AccentFillPressed })
        {
            if (colors.TryGetValue(key, out var color))
            {
                fills.Add(color);
            }
        }

        if (fills.Count == 0)
        {
            return null;
        }

        var fillRatio = fills.Min(fill => ContrastShade.Lowest(fill, surfaces));
        if (fillRatio >= WcagContrast.NonTextMinimum)
        {
            return null;
        }

        // The theme's own unchecked border keeps the box's outline where it always was, checked or not. CheckBox.xaml
        // draws that border inside the filled border, so it shows over the fill, and the fill over the surface under the
        // box. Where even that is too faint, the check's own colour, which reads on the fill by construction.
        if (colors.TryGetValue(ThemeColor.StrongStroke, out var stroke))
        {
            var strokeRatio = EdgeRatio(stroke);
            if (strokeRatio >= WcagContrast.NonTextMinimum)
            {
                return new CheckBoxPerimeter(stroke, true, fillRatio, strokeRatio);
            }
        }

        var glyph = foregrounds.FirstOrDefault(f => f.Role == AccentForegroundRole.CheckGlyph)?.Foreground
            ?? (ContrastShade.Lowest(SrgbColor.White, surfaces) > ContrastShade.Lowest(SrgbColor.Black, surfaces) ? SrgbColor.White : SrgbColor.Black);
        return new CheckBoxPerimeter(glyph, false, fillRatio, EdgeRatio(glyph));

        double EdgeRatio(SrgbColor edge) =>
            surfaces.Min(surface => fills.Min(fill => WcagContrast.Ratio(edge.Over(fill.Over(surface)), surface)));
    }

    // Which foreground a role prefers: the text-on-accent pair (white in the light theme, black in the dark one), the
    // ordinary text colour or its pressed tone, or what a pressed WPF-UI button actually draws today.
    private enum TextConvention
    {
        OnAccent,
        Body,
        BodySecondary,
        PressedAsDrawn,
    }

    private readonly record struct Fill(ThemeColor Color, AccentShadeRole? Shade, double Opacity)
    {
        public static Fill Of(ThemeColor color, double opacity = 1) => new(color, null, opacity);

        public static Fill OfShade(AccentShadeRole shade) => new(default, shade, 1);
    }

    private sealed record RoleSpec(
        AccentForegroundRole Role,
        ForegroundKind Kind,
        TextConvention[] Preferred,
        Fill[] Rest,
        Fill[] Interaction);

    private sealed record ShadeSpec(AccentShadeRole Role, ThemeColor Source, double Required);
}
