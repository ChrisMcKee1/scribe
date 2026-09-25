using Scribe.Core.Appearance;
using Xunit;

namespace Scribe.Core.Tests;

public sealed class AccentContrastPlannerTests
{
    // The roles drawn on the accent itself, at rest (the pressed label is checked on its own below).
    private static readonly AccentForegroundRole[] AccentRoles =
    [
        AccentForegroundRole.AccentButton,
        AccentForegroundRole.AccentFill,
        AccentForegroundRole.SelectedItem,
        AccentForegroundRole.CheckGlyph,
    ];

    private static readonly AccentForegroundRole[] BadgeRoles =
    [
        AccentForegroundRole.CautionBadge,
        AccentForegroundRole.InfoBadge,
        AccentForegroundRole.DangerBadge,
        AccentForegroundRole.SuccessBadge,
    ];

    private static readonly AccentShadeRole[] AccentTextShades =
    [
        AccentShadeRole.AccentTextPrimary,
        AccentShadeRole.AccentTextSecondary,
        AccentShadeRole.AccentTextTertiary,
    ];

    private static readonly AccentShadeRole[] TrackShades =
    [
        AccentShadeRole.SwitchTrack,
        AccentShadeRole.SwitchTrackHover,
        AccentShadeRole.SwitchTrackPressed,
    ];

    private static SrgbColor C(string hex) => SrgbColor.Parse(hex);

    // What WPF-UI 4.3.0 has for an accent (ApplicationAccentColorManager.Apply, as Scribe calls it through
    // ApplicationThemeManager.Apply): three shades, the theme's accent fill and that fill at alpha 229 and 204 for hover
    // and pressed, the accent text brushes it writes from the shades, and the theme dictionaries' surfaces and text. The
    // offscreen renders read the same values back from the live resources. The values the tests pin were recomputed by
    // an independent implementation of the same formulas, not read back from this one.
    private static Dictionary<ThemeColor, SrgbColor> Theme(bool light, string primary, string secondary, string tertiary)
    {
        var fill = C(light ? primary : secondary);
        return new Dictionary<ThemeColor, SrgbColor>
        {
            [ThemeColor.Surface] = C(light ? "#F3F3F3" : "#202020"),
            [ThemeColor.WindowBackground] = C(light ? "#FAFAFA" : "#202020"),
            [ThemeColor.CardBackground] = C(light ? "#B3FFFFFF" : "#0DFFFFFF"),
            [ThemeColor.ControlFill] = C(light ? "#B3FFFFFF" : "#0FFFFFFF"),
            [ThemeColor.ControlFillSecondary] = C(light ? "#80F9F9F9" : "#15FFFFFF"),
            [ThemeColor.StrongStroke] = C(light ? "#72000000" : "#8BFFFFFF"),
            [ThemeColor.BodyText] = C(light ? "#E4000000" : "#FFFFFF"),
            [ThemeColor.BodyTextSecondary] = C(light ? "#9E000000" : "#C5FFFFFF"),
            [ThemeColor.ControlText] = SrgbColor.Black,
            [ThemeColor.ButtonPressedFill] = C(light ? "#4DF9F9F9" : "#08FFFFFF"),
            [ThemeColor.AccentPrimary] = C(primary),
            [ThemeColor.AccentFill] = fill,
            [ThemeColor.AccentFillHover] = fill.WithAlpha(229),
            [ThemeColor.AccentFillPressed] = fill.WithAlpha(204),
            [ThemeColor.AccentTextPrimary] = C(secondary),
            [ThemeColor.AccentTextSecondary] = C(tertiary),
            [ThemeColor.AccentTextTertiary] = C(primary),
            [ThemeColor.Hyperlink] = C("#0066CC"),
            [ThemeColor.HyperlinkHover] = C("#FF0000"),
            [ThemeColor.PaletteOrange] = C("#FF9800"),
            [ThemeColor.PaletteLightBlue] = C("#03A9F4"),
            [ThemeColor.PaletteRed] = C("#F44336"),
            [ThemeColor.PaletteGreen] = C("#4CAF50"),
        };
    }

    // The maintainer's accent, #0E0E70.
    private static Dictionary<ThemeColor, SrgbColor> NavyDark() => Theme(false, "#42429B", "#59599B", "#78789B");

    private static Dictionary<ThemeColor, SrgbColor> NavyLight() => Theme(true, "#0B0B57", "#060630", "#01010A");

    // A light accent: Windows' Gold, #FFB900.
    private static Dictionary<ThemeColor, SrgbColor> GoldDark() => Theme(false, "#FFCE4D", "#FFD973", "#FFE7A6");

    private static Dictionary<ThemeColor, SrgbColor> GoldLight() => Theme(true, "#E6A700", "#BF8B00", "#996F00");

    // Windows' default accent, #0078D4.
    private static Dictionary<ThemeColor, SrgbColor> BlueDark() => Theme(false, "#4DB2FF", "#73C2FF", "#A6D8FF");

    private static Dictionary<ThemeColor, SrgbColor> BlueLight() => Theme(true, "#006ABB", "#005494", "#003E6E");

    private static Dictionary<ThemeColor, SrgbColor> Colours(string accent, bool light) => (accent, light) switch
    {
        ("navy", true) => NavyLight(),
        ("navy", false) => NavyDark(),
        ("gold", true) => GoldLight(),
        ("gold", false) => GoldDark(),
        ("blue", true) => BlueLight(),
        _ => BlueDark(),
    };

    private static AccentContrastPlan Plan(string accent, bool light) =>
        AccentContrastPlanner.Plan(light ? AppearanceTheme.Light : AppearanceTheme.Dark, false, Colours(accent, light));

    private static SrgbColor[] Surfaces(bool light) =>
        light ? [C("#F3F3F3"), C("#FAFAFA"), C("#FBFBFB"), C("#F6F6F6")] : [C("#202020"), C("#2B2B2B"), C("#2D2D2D"), C("#323232")];

    private static RoleForeground Role(AccentContrastPlan plan, AccentForegroundRole role) =>
        plan.For(role) ?? throw new Xunit.Sdk.XunitException($"no foreground planned for {role}");

    private static ShadeCorrection Shade(AccentContrastPlan plan, AccentShadeRole role) =>
        plan.For(role) ?? throw new Xunit.Sdk.XunitException($"no shade planned for {role}");

    [Fact]
    public void A_dark_accent_in_the_dark_theme_gets_white_text_and_glyphs_on_every_accent_fill()
    {
        var plan = Plan("navy", light: false);

        Assert.Equal(AccentContrastMode.Applied, plan.Mode);
        Assert.True(plan.Applies);
        foreach (var role in AccentRoles.Append(AccentForegroundRole.AccentButtonPressed))
        {
            var planned = Role(plan, role);
            Assert.Equal(SrgbColor.White, planned.Foreground);
            Assert.False(planned.Choice.IsThemeForeground);
            Assert.True(planned.Choice.MeetsInEveryState, $"{role} at {planned.Choice.AllStatesRatio:F2}:1");
        }

        // Save and the other accent buttons sit on the secondary shade, selected items and checked boxes on the primary.
        Assert.Equal(6.31, Math.Round(Role(plan, AccentForegroundRole.AccentButton).Choice.RestRatio, 2));
        Assert.Equal(8.45, Math.Round(Role(plan, AccentForegroundRole.SelectedItem).Choice.RestRatio, 2));
    }

    [Fact]
    public void A_dark_accent_in_the_light_theme_keeps_the_themes_white()
    {
        var plan = Plan("navy", light: true);

        foreach (var role in AccentRoles.Concat([AccentForegroundRole.SwitchKnob, AccentForegroundRole.SwitchKnobHover, AccentForegroundRole.SwitchKnobPressed]))
        {
            var planned = Role(plan, role);
            Assert.Equal(SrgbColor.White, planned.Foreground);
            Assert.True(planned.Choice.IsThemeForeground);
            Assert.True(planned.Choice.MeetsInEveryState);
        }
    }

    [Fact]
    public void A_light_accent_in_the_light_theme_gets_black_on_every_accent_fill()
    {
        var plan = Plan("gold", light: true);

        foreach (var role in AccentRoles)
        {
            var planned = Role(plan, role);
            Assert.Equal(SrgbColor.Black, planned.Foreground);
            Assert.False(planned.Choice.IsThemeForeground);
            Assert.True(planned.Choice.MeetsInEveryState, $"{role} at {planned.Choice.AllStatesRatio:F2}:1");
        }
    }

    [Fact]
    public void A_light_accent_in_the_dark_theme_keeps_the_themes_black()
    {
        var plan = Plan("gold", light: false);

        foreach (var role in AccentRoles.Append(AccentForegroundRole.AccentButtonPressed))
        {
            Assert.Equal(SrgbColor.Black, Role(plan, role).Foreground);
            Assert.True(Role(plan, role).Choice.IsThemeForeground);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_default_accent_keeps_every_foreground_shade_and_cue_it_has(bool light)
    {
        var plan = Plan("blue", light);

        foreach (var role in AccentRoles.Concat([
                     AccentForegroundRole.AccentButtonPressed,
                     AccentForegroundRole.SwitchKnob,
                     AccentForegroundRole.SwitchKnobHover,
                     AccentForegroundRole.SwitchKnobPressed]))
        {
            Assert.True(Role(plan, role).Choice.IsThemeForeground, role.ToString());
            Assert.True(Role(plan, role).Choice.MeetsInEveryState, role.ToString());
        }

        foreach (var shade in AccentTextShades.Concat(TrackShades))
        {
            Assert.False(Shade(plan, shade).Changed, shade.ToString());
        }

        Assert.Null(plan.CheckBoxPerimeter);
        Assert.Null(plan.SelectedItemOutline);
    }

    [Fact]
    public void What_changes_for_the_default_accent_is_what_fails_whatever_the_accent()
    {
        var dark = Plan("blue", light: false);
        var light = Plan("blue", light: true);

        // WPF's own link colour in the dark theme (2.93:1 on the page, 2.30:1 on the Diagnostics panel), and its red for a
        // hovered link in both themes (4.07:1 and 3.60:1 on the page).
        Assert.True(Shade(dark, AccentShadeRole.Hyperlink).Changed);
        Assert.False(Shade(light, AccentShadeRole.Hyperlink).Changed);
        Assert.True(Shade(dark, AccentShadeRole.HyperlinkHover).Changed);
        Assert.True(Shade(light, AccentShadeRole.HyperlinkHover).Changed);

        // The danger button, and a pressed standard, transparent or danger button, in the dark theme only.
        Assert.False(Role(dark, AccentForegroundRole.DangerButton).Choice.IsThemeForeground);
        Assert.False(Role(dark, AccentForegroundRole.DangerButtonPressed).Choice.IsThemeForeground);
        Assert.False(Role(dark, AccentForegroundRole.SecondaryButtonPressed).Choice.IsThemeForeground);
        Assert.True(Role(light, AccentForegroundRole.DangerButton).Choice.IsThemeForeground);
        Assert.True(Role(light, AccentForegroundRole.DangerButtonPressed).Choice.IsThemeForeground);
        Assert.True(Role(light, AccentForegroundRole.SecondaryButtonPressed).Choice.IsThemeForeground);
    }

    [Fact]
    public void A_pressed_accent_button_keeps_todays_black_where_it_reads_and_takes_the_intended_label_where_it_does_not()
    {
        // WPF-UI 4.3.0 draws every pressed ui:Button label in Control.Foreground's default, black: its template binds
        // the pressed label to PressedForeground through a TemplatedParent source the button itself does not have.
        var blueLight = Plan("blue", light: true);
        var bluePressedFill = C("#CC006ABB").Over(C("#F3F3F3"));

        // With the default blue in the light theme that black reads (5.37:1 on #3186C7) where the white the template
        // means to draw would not (3.91:1), so the pressed label keeps it, while the label at rest and hovered stays white.
        Assert.Equal(SrgbColor.Black, Role(blueLight, AccentForegroundRole.AccentButtonPressed).Foreground);
        Assert.Equal(5.37, Math.Round(Role(blueLight, AccentForegroundRole.AccentButtonPressed).Choice.RestRatio, 2));
        Assert.Equal(3.91, Math.Round(WcagContrast.Ratio(SrgbColor.White, bluePressedFill), 2));
        Assert.Equal(SrgbColor.White, Role(blueLight, AccentForegroundRole.AccentButton).Foreground);
        Assert.True(Role(blueLight, AccentForegroundRole.AccentButton).Choice.MeetsInEveryState);

        // With the maintainer's accent that black is 2.05:1 in the light theme and 2.70:1 in the dark one; white reads.
        var navyLight = Role(Plan("navy", light: true), AccentForegroundRole.AccentButtonPressed);
        var navyDark = Role(Plan("navy", light: false), AccentForegroundRole.AccentButtonPressed);
        Assert.Equal(2.05, Math.Round(WcagContrast.Ratio(SrgbColor.Black, C("#CC0B0B57").Over(C("#F3F3F3"))), 2));
        Assert.Equal(SrgbColor.White, navyLight.Foreground);
        Assert.Equal(SrgbColor.White, navyDark.Foreground);
        Assert.True(navyLight.Choice.MeetsInEveryState);
        Assert.True(navyDark.Choice.MeetsInEveryState);
    }

    [Fact]
    public void A_danger_button_gets_a_legible_label_at_rest_hovered_and_pressed()
    {
        var dark = Plan("navy", light: false);
        var light = Plan("navy", light: true);

        // Dark: white, the button text, is 3.68:1 on the red, so black at rest and hovered (4.90:1 on the 0.9 tone);
        // pressed, on the 0.7 tone, what shows today is black (3.59:1 on the page) and the style's #C5FFFFFF would be
        // 4.11:1 there and 3.89:1 over the Diagnostics panel, so white (5.49:1 at worst).
        Assert.Equal(SrgbColor.Black, Role(dark, AccentForegroundRole.DangerButton).Foreground);
        Assert.Equal(5.70, Math.Round(Role(dark, AccentForegroundRole.DangerButton).Choice.RestRatio, 2));
        Assert.Equal(4.90, Math.Round(Role(dark, AccentForegroundRole.DangerButton).Choice.AllStatesRatio, 2));
        Assert.Equal(SrgbColor.White, Role(dark, AccentForegroundRole.DangerButtonPressed).Foreground);
        Assert.Equal(5.49, Math.Round(Role(dark, AccentForegroundRole.DangerButtonPressed).Choice.RestRatio, 2));

        // Light: the theme's text reads at rest (5.30:1), and today's pressed black reads too.
        Assert.Equal(C("#E4000000"), Role(light, AccentForegroundRole.DangerButton).Foreground);
        Assert.True(Role(light, AccentForegroundRole.DangerButton).Choice.IsThemeForeground);
        Assert.Equal(SrgbColor.Black, Role(light, AccentForegroundRole.DangerButtonPressed).Foreground);
        Assert.True(Role(light, AccentForegroundRole.DangerButtonPressed).Choice.IsThemeForeground);
        Assert.All(new[] { dark, light }, plan =>
        {
            Assert.True(Role(plan, AccentForegroundRole.DangerButton).Choice.MeetsInEveryState);
            Assert.True(Role(plan, AccentForegroundRole.DangerButtonPressed).Choice.MeetsInEveryState);
        });
    }

    [Fact]
    public void A_pressed_standard_button_in_the_dark_theme_takes_the_pressed_tone_instead_of_black()
    {
        // Today's black is 1.41:1 on a pressed standard button on the dark page; the theme's own pressed tone, with its
        // alpha, reads on every surface the button can sit on (7.76:1 at worst). In the light theme today's black reads.
        var dark = Role(Plan("navy", light: false), AccentForegroundRole.SecondaryButtonPressed);
        var light = Role(Plan("navy", light: true), AccentForegroundRole.SecondaryButtonPressed);

        Assert.Equal(1.41, Math.Round(WcagContrast.Ratio(SrgbColor.Black, C("#08FFFFFF").Over(C("#202020"))), 2));
        Assert.Equal(C("#C5FFFFFF"), dark.Foreground);
        Assert.Equal(7.76, Math.Round(dark.Choice.RestRatio, 2));
        Assert.Equal(SrgbColor.Black, light.Foreground);
        Assert.True(light.Choice.IsThemeForeground);
    }

    [Fact]
    public void A_pressed_role_without_todays_colour_prefers_the_label_the_template_means()
    {
        var colours = NavyLight();
        colours.Remove(ThemeColor.ControlText);

        var pressed = Role(AccentContrastPlanner.Plan(AppearanceTheme.Light, false, colours), AccentForegroundRole.AccentButtonPressed);

        Assert.Equal(SrgbColor.White, pressed.Foreground);
        Assert.True(pressed.Choice.IsThemeForeground);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Palette_badges_get_black_in_both_themes(bool light)
    {
        var plan = Plan("navy", light);

        foreach (var role in BadgeRoles)
        {
            var planned = Role(plan, role);
            Assert.Equal(SrgbColor.Black, planned.Foreground);
            Assert.True(planned.Choice.MeetsAtRest, $"{role} at {planned.Choice.RestRatio:F2}:1");

            // The light theme's white was the defect: 2.16:1 on the orange caution badge, 2.63:1 on the blue one.
            Assert.Equal(!light, planned.Choice.IsThemeForeground);
        }

        Assert.Equal(9.74, Math.Round(Role(plan, AccentForegroundRole.CautionBadge).Choice.RestRatio, 2));
        Assert.Equal(7.99, Math.Round(Role(plan, AccentForegroundRole.InfoBadge).Choice.RestRatio, 2));
    }

    [Theory]
    // The maintainer's accent in the dark theme: WPF-UI's accent text brushes are 2.58:1, 3.85:1 and 1.93:1 on the page
    // and 2.03:1, 3.03:1 and 1.52:1 on the lightest surface they are drawn on, the Diagnostics panel.
    [InlineData("navy", false, "#9696C2", "#9797B2", "#9494D1")]
    // Gold in the light theme: 2.74:1, 4.09:1 and 1.91:1 on the page, the darkest light surface.
    [InlineData("gold", true, "#8F6800", "#906800", "#906800")]
    public void Accent_text_that_does_not_read_is_moved_in_lightness_until_it_reads_on_every_surface(
        string accent, bool light, string primary, string secondary, string tertiary)
    {
        var plan = Plan(accent, light);
        var expected = new[] { C(primary), C(secondary), C(tertiary) };

        for (var i = 0; i < AccentTextShades.Length; i++)
        {
            var shade = Shade(plan, AccentTextShades[i]);
            Assert.True(shade.Changed, shade.Role.ToString());
            Assert.Equal(expected[i], shade.Color);
            Assert.True(shade.Meets);
            Assert.All(Surfaces(light), surface => Assert.True(WcagContrast.Ratio(shade.Color, surface) >= WcagContrast.TextMinimum));
            Assert.True(Math.Abs(shade.Color.ToHsl().Hue - shade.Original.ToHsl().Hue) < 3, shade.Role.ToString());
        }
    }

    [Theory]
    [InlineData("navy", true)]
    [InlineData("gold", false)]
    [InlineData("blue", true)]
    [InlineData("blue", false)]
    public void Accent_text_that_reads_is_left_exactly_as_the_theme_has_it(string accent, bool light)
    {
        var plan = Plan(accent, light);
        var colours = Colours(accent, light);

        Assert.Equal(colours[ThemeColor.AccentTextPrimary], Shade(plan, AccentShadeRole.AccentTextPrimary).Color);
        Assert.Equal(colours[ThemeColor.AccentTextSecondary], Shade(plan, AccentShadeRole.AccentTextSecondary).Color);
        Assert.Equal(colours[ThemeColor.AccentTextTertiary], Shade(plan, AccentShadeRole.AccentTextTertiary).Color);
    }

    [Fact]
    public void Links_follow_the_same_rule_from_the_colours_WPF_draws_them_in()
    {
        var dark = Plan("navy", light: false);
        var light = Plan("navy", light: true);

        // SystemColors.HotTrackColor, #0066CC: 2.93:1 on the dark page (2.30:1 on the Diagnostics panel), 5.02:1 on the
        // light page.
        Assert.Equal(C("#399CFF"), Shade(dark, AccentShadeRole.Hyperlink).Color);
        Assert.False(Shade(light, AccentShadeRole.Hyperlink).Changed);

        // Hovered, WPF's red: 3.21:1 dark and 3.60:1 light, at worst.
        Assert.Equal(C("#FF6767"), Shade(dark, AccentShadeRole.HyperlinkHover).Color);
        Assert.Equal(C("#E10000"), Shade(light, AccentShadeRole.HyperlinkHover).Color);
        Assert.All(new[] { dark, light }, plan => Assert.All(
            plan.Shades.Where(s => s.Role is AccentShadeRole.Hyperlink or AccentShadeRole.HyperlinkHover),
            shade => Assert.True(shade.Meets)));
    }

    [Theory]
    [InlineData("navy", false, "#7373C3", "#000000")]
    [InlineData("gold", true, "#B68400", "#FFFFFF")]
    public void A_switch_that_is_on_gets_a_track_that_stands_out_and_knobs_chosen_on_it(string accent, bool light, string track, string knob)
    {
        var plan = Plan(accent, light);

        // The on track is 1.52:1 (navy, dark) and 1.91:1 (gold, light) on the surface it stands out from least, and its
        // hovered and pressed tones lower still: each is moved to the first shade that stands out at 3:1.
        Assert.Equal(C(track), Shade(plan, AccentShadeRole.SwitchTrack).Color);
        foreach (var shade in TrackShades)
        {
            Assert.True(Shade(plan, shade).Changed, shade.ToString());
            Assert.True(Shade(plan, shade).Meets, shade.ToString());
            Assert.Equal(WcagContrast.NonTextMinimum, Shade(plan, shade).Required);
        }

        // Each knob is chosen on its own corrected track; the theme's own reads on all of them here (4.92:1 and 3.34:1
        // at worst), so the knob keeps the pairing Windows draws: black on a light track, white on a dark one.
        foreach (var role in new[] { AccentForegroundRole.SwitchKnob, AccentForegroundRole.SwitchKnobHover, AccentForegroundRole.SwitchKnobPressed })
        {
            Assert.True(Role(plan, role).Choice.MeetsInEveryState, role.ToString());
            Assert.True(Role(plan, role).Choice.IsThemeForeground, role.ToString());
            Assert.Equal(C(knob), Role(plan, role).Foreground);
        }
    }

    [Fact]
    public void Glyphs_are_held_to_the_non_text_minimum_and_text_to_the_text_one()
    {
        var plan = Plan("blue", light: true);

        foreach (var role in new[] { AccentForegroundRole.CheckGlyph, AccentForegroundRole.SwitchKnob, AccentForegroundRole.SwitchKnobPressed })
        {
            Assert.Equal(ForegroundKind.Glyph, Role(plan, role).Kind);
            Assert.Equal(WcagContrast.NonTextMinimum, Role(plan, role).Choice.Required);
        }

        Assert.Equal(WcagContrast.TextMinimum, Role(plan, AccentForegroundRole.AccentButton).Choice.Required);

        // So the default blue's pressed knob keeps its white (3.86:1) rather than turning black only while pressed.
        Assert.Equal(SrgbColor.White, Role(plan, AccentForegroundRole.SwitchKnobPressed).Foreground);
        Assert.Equal(3.86, Math.Round(Role(plan, AccentForegroundRole.SwitchKnobPressed).Choice.RestRatio, 2));
    }

    [Theory]
    [InlineData("navy", false, "#8BFFFFFF", 1.52, 5.67)]
    [InlineData("gold", true, "#72000000", 1.68, 4.97)]
    public void A_checked_box_whose_fill_does_not_stand_out_gets_the_themes_own_border(
        string accent, bool light, string stroke, double fillRatio, double borderRatio)
    {
        var perimeter = Plan(accent, light).CheckBoxPerimeter;

        Assert.NotNull(perimeter);
        Assert.Equal(C(stroke), perimeter.Color);
        Assert.True(perimeter.IsStrongStroke);
        Assert.Equal(fillRatio, Math.Round(perimeter.FillRatio, 2));
        Assert.Equal(borderRatio, Math.Round(perimeter.Ratio, 2));
    }

    [Theory]
    [InlineData("navy", true)]
    [InlineData("gold", false)]
    [InlineData("blue", true)]
    [InlineData("blue", false)]
    public void A_checked_box_whose_fill_stands_out_keeps_the_themes_border(string accent, bool light)
    {
        Assert.Null(Plan(accent, light).CheckBoxPerimeter);
    }

    [Fact]
    public void Where_even_the_themes_border_would_not_show_the_box_is_edged_in_its_checks_colour()
    {
        var colours = NavyDark();
        colours[ThemeColor.StrongStroke] = C("#10FFFFFF");

        var perimeter = AccentContrastPlanner.Plan(AppearanceTheme.Dark, false, colours).CheckBoxPerimeter;

        Assert.NotNull(perimeter);
        Assert.False(perimeter.IsStrongStroke);
        Assert.Equal(SrgbColor.White, perimeter.Color);
        Assert.True(perimeter.Ratio >= WcagContrast.NonTextMinimum);
    }

    [Theory]
    // The maintainer's accent in the dark theme: the selected fill is 1.52:1 on the lightest surface.
    [InlineData("navy", false, "#FFFFFF", 1.52)]
    [InlineData("navy", true, null, 15.77)]
    // Gold in the light theme: 1.91:1.
    [InlineData("gold", true, "#000000", 1.91)]
    [InlineData("gold", false, null, 8.66)]
    [InlineData("blue", true, null, 5.01)]
    [InlineData("blue", false, null, 5.57)]
    public void A_selected_list_item_gets_an_outline_in_its_own_text_colour_only_where_its_fill_is_under_3_to_1(
        string accent, bool light, string? outline, double fillRatio)
    {
        var plan = Plan(accent, light);

        Assert.Equal(fillRatio, Math.Round(plan.SelectedItemFillRatio!.Value, 2));
        if (outline is null)
        {
            Assert.Null(plan.SelectedItemOutline);
            return;
        }

        Assert.NotNull(plan.SelectedItemOutline);
        Assert.Equal(C(outline), plan.SelectedItemOutline.Color);
        Assert.Equal(Role(plan, AccentForegroundRole.SelectedItem).Foreground, plan.SelectedItemOutline.Color);
        Assert.True(plan.SelectedItemOutline.AgainstFill >= WcagContrast.NonTextMinimum);
        Assert.True(plan.SelectedItemOutline.AgainstSurfaces >= WcagContrast.NonTextMinimum);
    }

    [Fact]
    public void An_unreadable_selection_fill_errs_toward_the_outline()
    {
        var colours = NavyLight();
        colours.Remove(ThemeColor.AccentPrimary);

        var plan = AccentContrastPlanner.Plan(AppearanceTheme.Light, false, colours);

        Assert.NotNull(plan.SelectedItemOutline);
        Assert.Equal(SrgbColor.Black, plan.SelectedItemOutline.Color);
        Assert.Null(plan.SelectedItemOutline.AgainstFill);
        Assert.Null(plan.SelectedItemFillRatio);
    }

    [Fact]
    public void Hover_and_pressed_fills_are_measured_on_every_surface_they_are_drawn_on()
    {
        // A checked box at rest is on the primary shade (8.45:1 with white). Hovered or pressed it is the accent fill at
        // alpha 229 or 204, measured as it shows on each surface: the lowest, pressed on a filled row, is 6.80:1.
        var glyph = Role(Plan("navy", light: false), AccentForegroundRole.CheckGlyph);

        Assert.Equal(8.45, Math.Round(glyph.Choice.RestRatio, 2));
        Assert.Equal(6.80, Math.Round(glyph.Choice.AllStatesRatio, 2));
    }

    [Fact]
    public void Without_a_readable_page_colour_the_themes_base_is_used()
    {
        var colours = NavyDark();
        var withPage = AccentContrastPlanner.Plan(AppearanceTheme.Dark, false, colours);
        colours.Remove(ThemeColor.Surface);
        var withoutPage = AccentContrastPlanner.Plan(AppearanceTheme.Dark, false, colours);

        Assert.Equal(withPage.Foregrounds, withoutPage.Foregrounds);
        Assert.Equal(withPage.Shades, withoutPage.Shades);
        Assert.Equal(withPage.SelectedItemFillRatio, withoutPage.SelectedItemFillRatio);
    }

    [Fact]
    public void Every_role_and_shade_is_planned_once_when_every_colour_can_be_read()
    {
        var plan = Plan("gold", light: true);

        Assert.Equal(Enum.GetValues<AccentForegroundRole>().Order(), plan.Foregrounds.Select(f => f.Role).Order());
        Assert.Equal(Enum.GetValues<AccentShadeRole>().Order(), plan.Shades.Select(s => s.Role).Order());
        Assert.Equal(AccentContrastPlanner.AllRoles.Order(), plan.Foregrounds.Select(f => f.Role).Order());
        Assert.Equal(AccentContrastPlanner.AllShades.Order(), plan.Shades.Select(s => s.Role).Order());
    }

    [Fact]
    public void A_role_whose_fill_cannot_be_read_is_left_to_the_theme()
    {
        var colours = NavyDark();
        colours.Remove(ThemeColor.PaletteOrange);
        colours.Remove(ThemeColor.AccentFillPressed);

        var plan = AccentContrastPlanner.Plan(AppearanceTheme.Dark, false, colours);

        Assert.Null(plan.For(AccentForegroundRole.CautionBadge));
        Assert.Null(plan.For(AccentForegroundRole.AccentButtonPressed));
        Assert.Null(plan.For(AccentForegroundRole.CheckGlyph));
        Assert.Null(plan.For(AccentShadeRole.SwitchTrackPressed));
        Assert.Null(plan.For(AccentForegroundRole.SwitchKnobPressed));
        Assert.NotNull(plan.For(AccentForegroundRole.AccentButton));
        Assert.NotNull(plan.For(AccentForegroundRole.SelectedItem));
        Assert.NotNull(plan.For(AccentForegroundRole.InfoBadge));
        Assert.NotNull(plan.For(AccentShadeRole.SwitchTrack));
    }

    [Theory]
    [InlineData(AppearanceTheme.HighContrast, false)]
    [InlineData(AppearanceTheme.Dark, true)]
    [InlineData(AppearanceTheme.Light, true)]
    public void A_contrast_theme_keeps_its_own_colours(AppearanceTheme theme, bool systemHighContrast)
    {
        // Windows can be in a contrast theme while WPF-UI still has the light or dark dictionary loaded.
        var plan = AccentContrastPlanner.Plan(theme, systemHighContrast, NavyDark());

        Assert.Equal(AccentContrastMode.ContrastTheme, plan.Mode);
        Assert.False(plan.Applies);
        Assert.Empty(plan.Foregrounds);
        Assert.Empty(plan.Shades);
        Assert.Null(plan.CheckBoxPerimeter);
        Assert.Null(plan.SelectedItemOutline);
        Assert.False(plan.SelectedRowCue);
    }

    [Fact]
    public void An_unknown_theme_plans_nothing()
    {
        var plan = AccentContrastPlanner.Plan(AppearanceTheme.Unknown, false, NavyDark());

        Assert.Equal(AccentContrastMode.UnknownTheme, plan.Mode);
        Assert.False(plan.Applies);
        Assert.Empty(plan.Foregrounds);
        Assert.Empty(plan.Shades);
        Assert.False(plan.SelectedRowCue);
    }

    [Fact]
    public void Selected_library_rows_are_always_marked_beyond_their_fill_in_light_and_dark()
    {
        Assert.True(Plan("blue", light: false).SelectedRowCue);
        Assert.True(Plan("blue", light: true).SelectedRowCue);
    }
}
