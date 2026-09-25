using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
using Scribe.Core.Appearance;
using Scribe.Core.Diagnostics;
using Wpf.Ui.Appearance;

namespace Scribe.App.Infrastructure;

/// <summary>
/// The resource keys Scribe's own styles read for foregrounds on coloured fills and for its selection cues.
/// <see cref="AccentContrastResources"/> writes them after every theme change; App.xaml holds what they are before that.
/// </summary>
internal static class AccentContrastKeys
{
    /// <summary>A boxed bool: true in a light or dark theme once planned, false in a contrast theme and before.</summary>
    public const string Applies = "ScribeAccentContrastApplies";

    public const string CautionBadgeForeground = "ScribeCautionBadgeForeground";
    public const string InfoBadgeForeground = "ScribeInfoBadgeForeground";
    public const string DangerBadgeForeground = "ScribeDangerBadgeForeground";
    public const string SuccessBadgeForeground = "ScribeSuccessBadgeForeground";

    /// <summary>A danger button's label at rest and hovered.</summary>
    public const string DangerButtonForeground = "ScribeDangerButtonForeground";

    /// <summary>A pressed accent button's label.</summary>
    public const string PrimaryPressedForeground = "ScribePrimaryPressedForeground";

    /// <summary>A pressed danger button's label.</summary>
    public const string DangerPressedForeground = "ScribeDangerPressedForeground";

    /// <summary>A pressed standard or transparent button's label or icon.</summary>
    public const string SecondaryPressedForeground = "ScribeSecondaryPressedForeground";

    /// <summary>A link at rest.</summary>
    public const string HyperlinkForeground = "ScribeHyperlinkForeground";

    /// <summary>A link while hovered.</summary>
    public const string HyperlinkHoverForeground = "ScribeHyperlinkHoverForeground";

    /// <summary>The outline of the selected library row: the strong control stroke, or transparent.</summary>
    public const string SelectedRowOutline = "ScribeSelectedRowOutline";

    /// <summary>The weight of the selected library row's name: SemiBold, or Normal.</summary>
    public const string SelectedRowNameWeight = "ScribeSelectedRowNameWeight";

    /// <summary>The outline of a selected list item where its accent fill is faint, else transparent.</summary>
    public const string SelectedItemOutline = "ScribeSelectedItemOutline";
}

/// <summary>
/// Whether Scribe's accent contrast overrides apply to an element. Scribe's styles set it from
/// <see cref="AccentContrastKeys.Applies"/> and add it to the conditions of every trigger that replaces a colour WPF-UI
/// or WPF draws, so in a contrast theme none of those triggers is active and the theme draws exactly what it drew
/// before: a trigger that is not active sets nothing, whatever its precedence.
/// </summary>
internal static class AccentContrastFlag
{
    public static readonly DependencyProperty AppliesProperty = DependencyProperty.RegisterAttached(
        "Applies", typeof(bool), typeof(AccentContrastFlag), new FrameworkPropertyMetadata(false));

    public static bool GetApplies(DependencyObject element) => (bool)element.GetValue(AppliesProperty);

    public static void SetApplies(DependencyObject element, bool value) => element.SetValue(AppliesProperty, value);
}

/// <summary>
/// Keeps text, glyphs and state indicators legible whatever accent the user picked. After every theme or accent change
/// WPF-UI makes, and whenever Windows turns a contrast theme on or off, it reads the colours the theme now has, asks
/// <see cref="AccentContrastPlanner"/> what each needs, and writes application-level brushes, which take precedence
/// over the theme dictionary's.
/// </summary>
/// <remarks>
/// <para>
/// WPF-UI 4.3.0's theme dictionaries define every text-on-accent brush with a StaticResource to the theme's own colour,
/// black in the dark theme and white in the light one, so its accent manager, which does rewrite that colour for the
/// accent, never reaches the brushes the controls draw with: with the accent #0E0E70 the dark theme drew black on
/// #42429B (2.48:1) on selected items and checked boxes and on #59599B (3.33:1) on Save. Its accent text brushes are the
/// accent's own shades whatever the page (#59599B, 2.58:1 on the dark page), and WPF's link colours are fixed.
/// </para>
/// <para>
/// Only brushes are written, and only where the theme's own does not read: elsewhere the theme's brush stays, so an
/// accent that never needed this draws exactly as before. The colour keys stay WPF-UI's own. In a contrast theme every
/// brush this class set is taken back and <see cref="AccentContrastKeys.Applies"/> turns Scribe's own triggers off, so
/// the theme's system pairs are exactly what they were.
/// </para>
/// </remarks>
internal static class AccentContrastResources
{
    // Where each colour the plan reads comes from: WPF-UI 4.3.0's Color resources, written by its accent manager for
    // the accent (SystemAccentColor*, AccentFillColor*) or defined by its theme and palette dictionaries.
    private static readonly (ThemeColor Color, string Key)[] ColorKeys =
    [
        (ThemeColor.Surface, "SolidBackgroundFillColorBase"),
        (ThemeColor.WindowBackground, "ApplicationBackgroundColor"),
        (ThemeColor.CardBackground, "CardBackgroundFillColorDefault"),
        (ThemeColor.ControlFill, "ControlFillColorDefault"),
        (ThemeColor.ControlFillSecondary, "ControlFillColorSecondary"),
        (ThemeColor.StrongStroke, "ControlStrongStrokeColorDefault"),
        (ThemeColor.BodyText, "TextFillColorPrimary"),
        (ThemeColor.BodyTextSecondary, "TextFillColorSecondary"),
        (ThemeColor.AccentPrimary, "SystemAccentColorPrimary"),
        (ThemeColor.AccentFill, "AccentFillColorDefault"),
        (ThemeColor.AccentFillHover, "AccentFillColorSecondary"),
        (ThemeColor.AccentFillPressed, "AccentFillColorTertiary"),
        (ThemeColor.PaletteOrange, "PaletteOrangeColor"),
        (ThemeColor.PaletteLightBlue, "PaletteLightBlueColor"),
        (ThemeColor.PaletteRed, "PaletteRedColor"),
        (ThemeColor.PaletteGreen, "PaletteGreenColor"),
    ];

    // Brushes the theme dictionary owns, overridden at application level only while the plan changes them: the
    // foreground roles, then the switch tracks the plan moves in lightness.
    private static readonly (AccentForegroundRole Role, string Key)[] DictionaryForegrounds =
    [
        (AccentForegroundRole.AccentButton, "AccentButtonForeground"),
        (AccentForegroundRole.AccentFill, "TextOnAccentFillColorPrimaryBrush"),
        (AccentForegroundRole.SelectedItem, "ListBoxItemSelectedForegroundThemeBrush"),
        (AccentForegroundRole.CheckGlyph, "CheckBoxCheckGlyphForeground"),
        (AccentForegroundRole.SwitchKnob, "ToggleSwitchKnobFillOn"),
        (AccentForegroundRole.SwitchKnobHover, "ToggleSwitchKnobFillOnPointerOver"),
        (AccentForegroundRole.SwitchKnobPressed, "ToggleSwitchKnobFillOnPressed"),
    ];

    private static readonly (AccentShadeRole Role, string Key)[] DictionaryShades =
    [
        (AccentShadeRole.SwitchTrack, "ToggleSwitchFillOn"),
        (AccentShadeRole.SwitchTrackHover, "ToggleSwitchFillOnPointerOver"),
        (AccentShadeRole.SwitchTrackPressed, "ToggleSwitchFillOnPressed"),
    ];

    // CheckBox.xaml draws a checked box's border with this brush, transparent in the light and dark themes.
    private const string CheckedBorderKey = "CheckBoxCheckBorderBrush";

    // Brushes WPF-UI's accent manager writes into the application dictionary itself on every theme or accent change, so
    // they are read as they are now, and what it wrote is kept to put back.
    private static readonly (AccentShadeRole Role, ThemeColor Color, string Key)[] ManagerShades =
    [
        (AccentShadeRole.AccentTextPrimary, ThemeColor.AccentTextPrimary, "AccentTextFillColorPrimaryBrush"),
        (AccentShadeRole.AccentTextSecondary, ThemeColor.AccentTextSecondary, "AccentTextFillColorSecondaryBrush"),
        (AccentShadeRole.AccentTextTertiary, ThemeColor.AccentTextTertiary, "AccentTextFillColorTertiaryBrush"),
    ];

    // WPF-UI draws these roles with a brush it shares across fills (every badge appearance uses BadgeForeground, a
    // danger button the ordinary ButtonForeground), so Scribe's styles point them at keys of their own. Where the plan
    // keeps the theme's own, a key holds the theme's brush itself.
    private static readonly (AccentForegroundRole Role, string Key, string ThemeKey)[] ScribeForegrounds =
    [
        (AccentForegroundRole.CautionBadge, AccentContrastKeys.CautionBadgeForeground, "BadgeForeground"),
        (AccentForegroundRole.InfoBadge, AccentContrastKeys.InfoBadgeForeground, "BadgeForeground"),
        (AccentForegroundRole.DangerBadge, AccentContrastKeys.DangerBadgeForeground, "BadgeForeground"),
        (AccentForegroundRole.SuccessBadge, AccentContrastKeys.SuccessBadgeForeground, "BadgeForeground"),
        (AccentForegroundRole.DangerButton, AccentContrastKeys.DangerButtonForeground, "ButtonForeground"),
    ];

    // The pressed labels ButtonLabelContrast's triggers draw. Always the plan's choice, even where it is what shows
    // today, because what shows today is a binding that never resolves (see ThemeColor.ControlText).
    private static readonly (AccentForegroundRole Role, string Key)[] PressedForegrounds =
    [
        (AccentForegroundRole.AccentButtonPressed, AccentContrastKeys.PrimaryPressedForeground),
        (AccentForegroundRole.DangerButtonPressed, AccentContrastKeys.DangerPressedForeground),
        (AccentForegroundRole.SecondaryButtonPressed, AccentContrastKeys.SecondaryPressedForeground),
    ];

    private const string StrongStrokeKey = "ControlStrongStrokeColorDefaultBrush";
    private const string ButtonPressedFillKey = "ButtonBackgroundPressed";

    // What this class last wrote to each key it overrides, so it knows which of the values now there are its own.
    private static readonly Dictionary<string, object> Written = new(StringComparer.Ordinal);

    // What WPF-UI's accent manager last wrote to its accent text keys, to plan from and to put back.
    private static readonly Dictionary<string, object?> ManagerWritten = new(StringComparer.Ordinal);

    private static Application? _app;
    private static ILogger? _log;
    private static string? _lastOutcome;
    private static int _lastMissing = -1;

    /// <summary>
    /// Starts following WPF-UI's theme changes and Windows' contrast setting. Call once, before the first theme is
    /// applied: the first plan is made for that theme, when WPF-UI raises Changed for it, and until then Scribe's own
    /// triggers are off. Later calls only replace the logger.
    /// </summary>
    public static void Attach(Application app, ILogger? log)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (log is not null)
        {
            _log = log;
        }

        if (_app is null)
        {
            _app = app;
            ApplicationThemeManager.Changed += OnApplicationThemeChanged;
            SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
        }
    }

    /// <summary>Plans and writes the foregrounds for the theme as it is now. Never throws.</summary>
    public static AccentContrastPlan? Refresh()
    {
        if (_app is not { } app)
        {
            return null;
        }

        try
        {
            if (!app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.BeginInvoke(new Action(() => Refresh()));
                return null;
            }

            return Apply(app.Resources);
        }
        catch (Exception ex)
        {
            // This runs inside WPF-UI's Changed event, raised with a plain Invoke from its theme manager: a throw here
            // would stop the tray's own handler and fail the theme change that raised it.
            TryLogFailure(ex);
            return null;
        }
    }

    // WPF-UI raises Changed after its accent manager and the theme dictionary swap, so the fills read here are the
    // ones the theme now draws.
    private static void OnApplicationThemeChanged(ApplicationTheme theme, Color accent) => Refresh();

    // Windows can switch a contrast theme on or off without WPF-UI applying anything (no window watching, or the
    // light or dark theme Scribe applies itself), and the choices must be taken back or made again either way.
    private static void OnSystemParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
        {
            Refresh();
        }
    }

    private static AccentContrastPlan Apply(ResourceDictionary resources)
    {
        var theme = ApplicationThemeManager.GetAppTheme() switch
        {
            ApplicationTheme.Light => AppearanceTheme.Light,
            ApplicationTheme.Dark => AppearanceTheme.Dark,
            ApplicationTheme.HighContrast => AppearanceTheme.HighContrast,
            _ => AppearanceTheme.Unknown,
        };

        var colors = new Dictionary<ThemeColor, SrgbColor>();
        foreach (var (color, key) in ColorKeys)
        {
            if (resources[key] is Color value)
            {
                colors[color] = From(value);
            }
        }

        if (BrushColor(resources[ButtonPressedFillKey]) is { } pressedFill)
        {
            colors[ThemeColor.ButtonPressedFill] = pressedFill;
        }

        // WPF-UI 4.3.0's ui:Button template sets a pressed button's Foreground with a binding to PressedForeground whose
        // source is the button's TemplatedParent, which a button in a window does not have, so the binding fails and
        // the label takes Foreground's default: the colour a pressed label actually shows in today.
        if (BrushColor(Control.ForegroundProperty.GetMetadata(typeof(Wpf.Ui.Controls.Button)).DefaultValue) is { } drawn)
        {
            colors[ThemeColor.ControlText] = drawn;
        }

        // WPF's own Hyperlink style: HotTrackBrush at rest and a literal red while hovered.
        colors[ThemeColor.Hyperlink] = From(SystemColors.HotTrackColor);
        colors[ThemeColor.HyperlinkHover] = From(Colors.Red);

        foreach (var (_, color, key) in ManagerShades)
        {
            if (BrushColor(ManagerValue(resources, key)) is { } text)
            {
                colors[color] = text;
            }
        }

        var plan = AccentContrastPlanner.Plan(theme, SystemParameters.HighContrast, colors);

        // Off first when leaving a light or dark theme, so no trigger meets a value already taken back.
        if (!plan.Applies)
        {
            WriteScribe(resources, AccentContrastKeys.Applies, false);
        }

        foreach (var (role, key) in DictionaryForegrounds)
        {
            WriteOverride(resources, key, plan.For(role) is { Choice.IsThemeForeground: false } planned ? Frozen(planned.Foreground) : null);
        }

        foreach (var (role, key) in DictionaryShades)
        {
            WriteOverride(resources, key, plan.For(role) is { Changed: true } shade ? Frozen(shade.Color) : null);
        }

        WriteOverride(resources, CheckedBorderKey, plan.CheckBoxPerimeter is { } perimeter ? Frozen(perimeter.Color) : null);

        foreach (var (role, _, key) in ManagerShades)
        {
            WriteManaged(resources, key, plan.For(role) is { Changed: true } shade ? Frozen(shade.Color) : null);
        }

        foreach (var (role, key, themeKey) in ScribeForegrounds)
        {
            WriteScribe(resources, key, plan.For(role) is { Choice.IsThemeForeground: false } planned
                ? Frozen(planned.Foreground)
                : resources[themeKey] ?? Brushes.Black);
        }

        // Without a plan the triggers are off; the keys then hold what the failed binding draws, as a default.
        var controlTextDefault = Control.ForegroundProperty.GetMetadata(typeof(Wpf.Ui.Controls.Button)).DefaultValue ?? Brushes.Black;
        foreach (var (role, key) in PressedForegrounds)
        {
            WriteScribe(resources, key, plan.For(role) is { } planned ? Frozen(planned.Foreground) : controlTextDefault);
        }

        WriteScribe(resources, AccentContrastKeys.HyperlinkForeground,
            plan.For(AccentShadeRole.Hyperlink) is { Changed: true } link ? Frozen(link.Color) : SystemColors.HotTrackBrush);
        WriteScribe(resources, AccentContrastKeys.HyperlinkHoverForeground,
            plan.For(AccentShadeRole.HyperlinkHover) is { Changed: true } hover ? Frozen(hover.Color) : Brushes.Red);

        WriteScribe(resources, AccentContrastKeys.SelectedRowOutline,
            plan.SelectedRowCue ? resources[StrongStrokeKey] ?? Brushes.Transparent : Brushes.Transparent);
        WriteScribe(resources, AccentContrastKeys.SelectedRowNameWeight, plan.SelectedRowCue ? FontWeights.SemiBold : FontWeights.Normal);
        WriteScribe(resources, AccentContrastKeys.SelectedItemOutline,
            plan.SelectedItemOutline is { } outline ? Frozen(outline.Color) : Brushes.Transparent);

        // On last when entering one, once every value its triggers read is in place.
        if (plan.Applies)
        {
            WriteScribe(resources, AccentContrastKeys.Applies, true);
        }

        LogOutcome(plan, plan.Applies ? MissingWpfUiKeys(resources) : 0);
        return plan;
    }

    // A theme dictionary brush: overridden in the application dictionary while the plan changes it, and the override
    // removed when it does not, so the theme's own shows again. Compared with the value there now, not only with what
    // was last written, so nothing is written twice and nothing another writer put there is taken for Scribe's.
    private static void WriteOverride(ResourceDictionary resources, string key, SolidColorBrush? desired)
    {
        var current = resources[key];
        var mine = Written.TryGetValue(key, out var written) && ReferenceEquals(current, written);
        if (desired is not null)
        {
            if (!SameValue(current, desired))
            {
                resources[key] = desired;
                Written[key] = desired;
            }

            return;
        }

        if (mine)
        {
            resources.Remove(key);
        }

        Written.Remove(key);
    }

    // A brush WPF-UI's accent manager writes into the application dictionary on every Apply. An identical theme
    // applied again writes a new, uncorrected brush over Scribe's, so the value there now is compared with the one
    // wanted, never only the one last written; what the manager wrote is kept to plan from and to put back.
    private static void WriteManaged(ResourceDictionary resources, string key, SolidColorBrush? desired)
    {
        var current = resources[key];
        var mine = Written.TryGetValue(key, out var written) && ReferenceEquals(current, written);
        if (desired is not null)
        {
            if (!SameValue(current, desired))
            {
                resources[key] = desired;
                Written[key] = desired;
            }

            return;
        }

        if (mine && ManagerWritten.TryGetValue(key, out var managers) && managers is not null)
        {
            resources[key] = managers;
        }

        Written.Remove(key);
    }

    // The accent manager's value for a key: what is there now, unless that is Scribe's own correction, in which case
    // the manager has not written since, and what it wrote then still stands.
    private static object? ManagerValue(ResourceDictionary resources, string key)
    {
        var current = resources[key];
        if (Written.TryGetValue(key, out var written) && ReferenceEquals(current, written))
        {
            return ManagerWritten.GetValueOrDefault(key);
        }

        ManagerWritten[key] = current;
        return current;
    }

    // Scribe's own keys, always written, and only when the value there differs from the one wanted.
    private static void WriteScribe(ResourceDictionary resources, string key, object value)
    {
        if (!SameValue(resources[key], value))
        {
            resources[key] = value;
        }
    }

    private static bool SameValue(object? current, object value) => (current, value) switch
    {
        (SolidColorBrush a, SolidColorBrush b) => ReferenceEquals(a, b) || (a.IsFrozen && b.IsFrozen && a.Color == b.Color && a.Opacity == b.Opacity),
        _ => Equals(current, value),
    };

    private static SolidColorBrush Frozen(SrgbColor color)
    {
        var brush = new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    private static SrgbColor From(Color color) => new(color.A, color.R, color.G, color.B);

    // A solid brush's colour as it draws, its opacity folded into the alpha as WPF does.
    private static SrgbColor? BrushColor(object? value) =>
        value is SolidColorBrush brush ? From(brush.Color).WithOpacity(brush.Opacity) : null;

    // A WPF-UI upgrade that renames one of these brushes would quietly bring back its old foreground, so the theme
    // dictionary is checked for every key this class overrides or reads.
    private static int MissingWpfUiKeys(ResourceDictionary resources)
    {
        if (FindThemeDictionary(resources) is not { } theme)
        {
            return 0;
        }

        return DictionaryForegrounds.Select(entry => entry.Key)
            .Concat(DictionaryShades.Select(entry => entry.Key))
            .Append(CheckedBorderKey)
            .Append(ButtonPressedFillKey)
            .Count(key => !theme.Contains(key));
    }

    private static ResourceDictionary? FindThemeDictionary(ResourceDictionary resources)
    {
        foreach (var dictionary in resources.MergedDictionaries)
        {
            if (dictionary.Source?.OriginalString.Contains("/Resources/Theme/", StringComparison.OrdinalIgnoreCase) == true)
            {
                return dictionary;
            }

            if (FindThemeDictionary(dictionary) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    // Shapes only: the theme, how many roles and shades needed a colour of their own, and the lowest ratios, never a
    // colour of the user's personalisation. Logged once per distinct outcome, because Windows sends several messages
    // per change.
    private static void LogOutcome(AccentContrastPlan plan, int missing)
    {
        if (_log is not { } log)
        {
            return;
        }

        try
        {
            if (missing != _lastMissing)
            {
                _lastMissing = missing;
                if (missing > 0)
                {
                    log.LogWarning(
                        "{Missing} WPF-UI brush(es) for text and state on accent fills are missing from the theme dictionary; those controls keep WPF-UI's own colours.",
                        missing);
                }
            }

            var changed = plan.Foregrounds.Count(f => !f.Choice.IsThemeForeground);
            var shadesChanged = plan.Shades.Count(s => s.Changed);
            var belowMinimum = plan.Foregrounds.Count(f => !f.Choice.MeetsInEveryState) + plan.Shades.Count(s => !s.Meets);
            var lowest = plan.Foregrounds.Count == 0 ? 0 : plan.Foregrounds.Min(f => f.Choice.AllStatesRatio);
            var lowestShade = plan.Shades.Count == 0 ? 0 : plan.Shades.Min(s => s.Ratio);
            var outcome = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{plan.Mode}|{plan.Foregrounds.Count}|{changed}|{plan.Shades.Count}|{shadesChanged}|{belowMinimum}|{lowest:F2}|{lowestShade:F2}|{plan.CheckBoxPerimeter is not null}|{plan.SelectedItemOutline is not null}");
            if (outcome == _lastOutcome)
            {
                return;
            }

            _lastOutcome = outcome;
            if (!plan.Applies)
            {
                log.LogInformation("Colours on accent fills left to the theme ({Mode}).", plan.Mode);
                return;
            }

            log.LogInformation(
                "Colours on accent fills: {Changed} of {Planned} foregrounds and {ShadesChanged} of {Shades} shades differ from the theme's own, {Below} below their minimum; lowest {Lowest:F2}:1 for a foreground in any state and {LowestShade:F2}:1 for a shade; checked-box border {Perimeter}, selected-item outline {Outline} (fill {FillRatio:F2}:1).",
                changed,
                plan.Foregrounds.Count,
                shadesChanged,
                plan.Shades.Count,
                belowMinimum,
                lowest,
                lowestShade,
                plan.CheckBoxPerimeter is not null,
                plan.SelectedItemOutline is not null,
                plan.SelectedItemFillRatio ?? 0);
        }
        catch
        {
            // Logging is best effort and must never be why a theme change fails.
        }
    }

    private static void TryLogFailure(Exception ex)
    {
        try
        {
            _log?.LogWarning(
                "Could not choose the colours on accent fills; the theme's own stay ({Failure}).",
                FailureShape.Describe(ex));
        }
        catch
        {
            // As above.
        }
    }
}
