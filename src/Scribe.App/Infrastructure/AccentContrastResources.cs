using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
using Scribe.Core.Appearance;
using Scribe.Core.Diagnostics;
using Wpf.Ui.Appearance;

namespace Scribe.App.Infrastructure;

/// <summary>
/// The resource keys Scribe's own XAML reads for foregrounds on coloured fills and for its selection cues.
/// <see cref="AccentContrastResources"/> writes them after every theme change; App.xaml holds what they are before that.
/// </summary>
internal static class AccentContrastKeys
{
    public const string CautionBadgeForeground = "ScribeCautionBadgeForeground";
    public const string InfoBadgeForeground = "ScribeInfoBadgeForeground";
    public const string DangerBadgeForeground = "ScribeDangerBadgeForeground";
    public const string SuccessBadgeForeground = "ScribeSuccessBadgeForeground";
    public const string DangerButtonForeground = "ScribeDangerButtonForeground";

    /// <summary>The outline of the selected library row: the strong control stroke, or transparent.</summary>
    public const string SelectedRowOutline = "ScribeSelectedRowOutline";

    /// <summary>The weight of the selected library row's name: SemiBold, or Normal.</summary>
    public const string SelectedRowNameWeight = "ScribeSelectedRowNameWeight";

    /// <summary>The weight of a selected list item's text: SemiBold where its accent fill is faint, else Normal.</summary>
    public const string SelectedItemWeight = "ScribeSelectedItemWeight";
}

/// <summary>
/// Keeps text and glyphs on accent and palette fills legible whatever accent the user picked. After every theme or
/// accent change WPF-UI makes, and whenever Windows turns a contrast theme on or off, it reads the fills the theme now
/// has, asks <see cref="AccentContrastPlanner"/> which foreground each needs, and writes application-level brushes,
/// which take precedence over the theme dictionary's.
/// </summary>
/// <remarks>
/// <para>
/// WPF-UI 4.3.0's theme dictionaries define every text-on-accent brush with a StaticResource to the theme's own colour,
/// black in the dark theme and white in the light one, so its accent manager, which does rewrite that colour for the
/// accent, never reaches the brushes the controls draw with: with the accent #0E0E70 the dark theme drew black on
/// #42429B (2.48:1) on selected items and checked boxes and on #59599B (3.33:1) on Save.
/// </para>
/// <para>
/// Only brushes are written, and only where the theme's own foreground does not read: elsewhere WPF-UI's brush stays,
/// so an accent that never needed this draws exactly as before. The colour keys stay WPF-UI's own (its accent manager
/// rewrites them on every change), and in a contrast theme every brush this class set is taken back, so the theme's
/// system pairs are exactly what they were. Scribe's own keys then show what their controls showed before the keys
/// existed.
/// </para>
/// </remarks>
internal static class AccentContrastResources
{
    // Where each colour the plan reads comes from: WPF-UI 4.3.0's Color resources, written by its accent manager for
    // the accent (SystemAccentColor*, AccentFillColor*) or defined by its theme and palette dictionaries.
    private static readonly (ThemeColor Color, string Key)[] ColorKeys =
    [
        (ThemeColor.Surface, "SolidBackgroundFillColorBase"),
        (ThemeColor.AccentPrimary, "SystemAccentColorPrimary"),
        (ThemeColor.AccentSecondary, "SystemAccentColorSecondary"),
        (ThemeColor.AccentTertiary, "SystemAccentColorTertiary"),
        (ThemeColor.AccentFill, "AccentFillColorDefault"),
        (ThemeColor.AccentFillHover, "AccentFillColorSecondary"),
        (ThemeColor.AccentFillPressed, "AccentFillColorTertiary"),
        (ThemeColor.PaletteOrange, "PaletteOrangeColor"),
        (ThemeColor.PaletteLightBlue, "PaletteLightBlueColor"),
        (ThemeColor.PaletteRed, "PaletteRedColor"),
        (ThemeColor.PaletteGreen, "PaletteGreenColor"),
    ];

    // The WPF-UI 4.3.0 brushes each role is drawn with, and whether a brush takes the fainter secondary tone.
    private static readonly (AccentForegroundRole Role, string Key, bool Secondary)[] WpfUiBrushes =
    [
        (AccentForegroundRole.AccentButton, "AccentButtonForeground", false),
        (AccentForegroundRole.AccentButton, "AccentButtonForegroundPointerOver", false),
        (AccentForegroundRole.AccentButton, "AccentButtonForegroundPressed", true),
        (AccentForegroundRole.AccentFill, "TextOnAccentFillColorPrimaryBrush", false),
        (AccentForegroundRole.AccentFill, "TextOnAccentFillColorSecondaryBrush", true),
        (AccentForegroundRole.SelectedItem, "ListBoxItemSelectedForegroundThemeBrush", false),
        (AccentForegroundRole.CheckedToggleButton, "ToggleButtonForegroundChecked", false),
        (AccentForegroundRole.CheckedToggleButton, "ToggleButtonForegroundCheckedPressed", true),
        (AccentForegroundRole.CalendarToday, "CalendarViewTodayForeground", false),
        (AccentForegroundRole.CheckGlyph, "CheckBoxCheckGlyphForeground", false),
        (AccentForegroundRole.RadioGlyph, "RadioButtonCheckGlyphFill", false),
        (AccentForegroundRole.SwitchKnob, "ToggleSwitchKnobFillOn", false),
        (AccentForegroundRole.SwitchKnob, "ToggleSwitchKnobFillOnPointerOver", false),
        (AccentForegroundRole.SwitchKnob, "ToggleSwitchKnobFillOnPressed", false),
    ];

    // WPF-UI draws these roles with a brush it shares across fills (every badge appearance uses BadgeForeground, a
    // danger button the ordinary ButtonForeground), so Scribe's styles point them at keys of their own. Without a
    // choice a key holds the brush the control used before, from the theme now loaded.
    private static readonly (AccentForegroundRole Role, string Key, string ThemeKey)[] ScribeBrushes =
    [
        (AccentForegroundRole.CautionBadge, AccentContrastKeys.CautionBadgeForeground, "BadgeForeground"),
        (AccentForegroundRole.InfoBadge, AccentContrastKeys.InfoBadgeForeground, "BadgeForeground"),
        (AccentForegroundRole.DangerBadge, AccentContrastKeys.DangerBadgeForeground, "BadgeForeground"),
        (AccentForegroundRole.SuccessBadge, AccentContrastKeys.SuccessBadgeForeground, "BadgeForeground"),
        (AccentForegroundRole.DangerButton, AccentContrastKeys.DangerButtonForeground, "ButtonForeground"),
    ];

    private const string StrongStrokeKey = "ControlStrongStrokeColorDefaultBrush";

    // What this class last wrote to each key, so an unchanged plan writes nothing (every write re-resolves every
    // DynamicResource in every window) and a contrast theme knows which WPF-UI brushes to take back.
    private static readonly Dictionary<string, object> Written = new(StringComparer.Ordinal);

    private static Application? _app;
    private static ILogger? _log;
    private static string? _lastOutcome;
    private static int _lastMissing = -1;

    /// <summary>
    /// Starts following WPF-UI's theme changes and Windows' contrast setting. Call once, before the first theme is
    /// applied: the first plan is made for that theme, when WPF-UI raises Changed for it, and until then Scribe's own
    /// keys hold App.xaml's defaults. Later calls only replace the logger.
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
                colors[color] = new SrgbColor(value.A, value.R, value.G, value.B);
            }
        }

        var plan = AccentContrastPlanner.Plan(theme, SystemParameters.HighContrast, colors);

        // A role whose theme foreground is legible keeps the theme's own brush, so an accent that never needed this
        // draws exactly as WPF-UI draws it.
        foreach (var (role, key, secondary) in WpfUiBrushes)
        {
            if (plan.For(role) is { Choice.IsThemeForeground: false } planned)
            {
                Write(resources, key, Frozen(secondary ? planned.SecondaryForeground : planned.Foreground));
            }
            else if (Written.Remove(key))
            {
                resources.Remove(key);
            }
        }

        foreach (var (role, key, themeKey) in ScribeBrushes)
        {
            object value = plan.For(role) is { Choice.IsThemeForeground: false } planned
                ? Frozen(planned.Foreground)
                : resources[themeKey] ?? Brushes.Black;
            Write(resources, key, value);
        }

        Write(resources, AccentContrastKeys.SelectedRowOutline,
            plan.SelectedRowCue ? resources[StrongStrokeKey] ?? Brushes.Transparent : Brushes.Transparent);
        Write(resources, AccentContrastKeys.SelectedRowNameWeight, plan.SelectedRowCue ? FontWeights.SemiBold : FontWeights.Normal);
        Write(resources, AccentContrastKeys.SelectedItemWeight, plan.SelectedItemWeightCue ? FontWeights.SemiBold : FontWeights.Normal);

        LogOutcome(plan, plan.Mode == AccentContrastMode.Applied ? MissingWpfUiKeys(resources) : 0);
        return plan;
    }

    private static void Write(ResourceDictionary resources, string key, object value)
    {
        if (Written.TryGetValue(key, out var previous) && SameValue(previous, value))
        {
            return;
        }

        resources[key] = value;
        Written[key] = value;
    }

    private static bool SameValue(object previous, object value) => (previous, value) switch
    {
        (SolidColorBrush a, SolidColorBrush b) => ReferenceEquals(a, b) || (a.IsFrozen && b.IsFrozen && a.Color == b.Color && a.Opacity == b.Opacity),
        _ => Equals(previous, value),
    };

    private static SolidColorBrush Frozen(SrgbColor color)
    {
        var brush = new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    // A WPF-UI upgrade that renames one of these brushes would quietly bring back its old foreground, so the theme
    // dictionary is checked for every key this class overrides.
    private static int MissingWpfUiKeys(ResourceDictionary resources)
    {
        if (FindThemeDictionary(resources) is not { } theme)
        {
            return 0;
        }

        return WpfUiBrushes.Count(entry => !theme.Contains(entry.Key));
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

    // Shapes only: the theme, how many roles needed the other foreground, and the lowest ratios, never a colour of
    // the user's personalisation. Logged once per distinct outcome, because Windows sends several messages per change.
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
                        "{Missing} WPF-UI brush(es) for text on accent fills are missing from the theme dictionary; those controls keep WPF-UI's own foreground.",
                        missing);
                }
            }

            var changed = plan.Foregrounds.Count(f => !f.Choice.IsThemeForeground);
            var restLowest = plan.Foregrounds.Count == 0 ? 0 : plan.Foregrounds.Min(f => f.Choice.RestRatio);
            var anyLowest = plan.Foregrounds.Count == 0 ? 0 : plan.Foregrounds.Min(f => f.Choice.AllStatesRatio);
            var belowAtRest = plan.Foregrounds.Count(f => !f.Choice.MeetsAtRest);
            var outcome = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{plan.Mode}|{plan.Foregrounds.Count}|{changed}|{belowAtRest}|{restLowest:F2}|{anyLowest:F2}|{plan.SelectedRowCue}|{plan.SelectedItemWeightCue}");
            if (outcome == _lastOutcome)
            {
                return;
            }

            _lastOutcome = outcome;
            if (plan.Mode != AccentContrastMode.Applied)
            {
                log.LogInformation("Foregrounds on accent fills left to the theme ({Mode}).", plan.Mode);
                return;
            }

            log.LogInformation(
                "Foregrounds on accent fills: {Changed} of {Planned} roles use the opposite of the theme's own, {BelowAtRest} below 4.5:1 at rest; lowest {RestLowest:F2}:1 at rest and {AnyLowest:F2}:1 hovered or pressed; selected items weighted {WeightCue} (fill {FillRatio:F2}:1).",
                changed,
                plan.Foregrounds.Count,
                belowAtRest,
                restLowest,
                anyLowest,
                plan.SelectedItemWeightCue,
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
                "Could not choose the foregrounds on accent fills; the theme's own stay ({Failure}).",
                FailureShape.Describe(ex));
        }
        catch
        {
            // As above.
        }
    }
}
