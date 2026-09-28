using System.Text.RegularExpressions;
using Scribe.Core.Appearance;

namespace Scribe.Core.Tests;

/// <summary>
/// WPF-UI 4.3.0's contrast dictionaries leave 101 resources as the placeholder <c>#FF0000</c>, so in a contrast theme
/// every status text and icon Scribe draws with them was red, and so were disabled menu items and menu separators.
/// <see cref="ContrastPlaceholderRepairs"/> names the ones Scribe draws on the contrast dictionary's own surfaces and the
/// system colour WinUI uses there, and AccentContrastResources writes them. These tests pin the table, that every such
/// key the app draws is repaired or says why not, and that the tray menu's copy of the resources follows the removals.
/// </summary>
public sealed partial class ContrastPlaceholderRepairsTests
{
    // Every key WPF-UI 4.3.0's HC1, HC2, HCBlack and HCWhite dictionaries set to #FF0000 (the four agree), with every
    // brush those dictionaries build on one. Re-derive it on a WPF-UI upgrade (Resources/Theme/HC*.xaml).
    private static readonly string[] WpfUiContrastPlaceholders =
    [
        "AccentFillColorDisabled", "AccentTextFillColorDisabled", "AcrylicBackgroundFillColorDefault",
        "CardBackgroundFillColorDefault", "CardBackgroundFillColorSecondary", "CardStrokeColorDefault",
        "CardStrokeColorDefaultSolid", "ControlAltFillColorDisabled", "ControlAltFillColorQuarternary",
        "ControlAltFillColorSecondary", "ControlAltFillColorTertiary", "ControlAltFillColorTransparent",
        "ControlFillColorDefault", "ControlFillColorDisabled", "ControlFillColorInputActive",
        "ControlFillColorSecondary", "ControlFillColorTertiary", "ControlFillColorTransparent",
        "ControlOnImageFillColorDefault", "ControlOnImageFillColorDisabled", "ControlOnImageFillColorSecondary",
        "ControlOnImageFillColorTertiary", "ControlSolidFillColorDefault", "ControlStrokeColorDefault",
        "ControlStrokeColorForStrongFillWhenOnImage", "ControlStrokeColorOnAccentDefault",
        "ControlStrokeColorOnAccentDisabled", "ControlStrokeColorOnAccentSecondary",
        "ControlStrokeColorOnAccentTertiary", "ControlStrokeColorSecondary", "ControlStrokeColorTertiary",
        "ControlStrongFillColorDefault", "ControlStrongFillColorDisabled", "ControlStrongStrokeColorDefault",
        "ControlStrongStrokeColorDisabled", "DividerStrokeColorDefault", "FocusStrokeColorInner",
        "FocusStrokeColorOuter", "LayerFillColorAlt", "LayerFillColorDefault",
        "LayerOnAccentAcrylicFillColorDefault", "LayerOnAcrylicFillColorDefault",
        "LayerOnMicaBaseAltFillColorDefault", "LayerOnMicaBaseAltFillColorSecondary",
        "LayerOnMicaBaseAltFillColorTertiary", "LayerOnMicaBaseAltFillColorTransparent", "MenuBarBackground",
        "MenuBarItemBackgroundPressed", "MenuBarItemBackgroundSelected", "MenuBarItemBorderBrush",
        "MenuBarItemTextForegroundPressed", "SmokeFillColorDefault", "SolidBackgroundFillColorBase",
        "SolidBackgroundFillColorBaseAlt", "SolidBackgroundFillColorQuarternary",
        "SolidBackgroundFillColorSecondary", "SolidBackgroundFillColorTertiary",
        "SolidBackgroundFillColorTransparent", "SubtleFillColorDisabled", "SubtleFillColorSecondary",
        "SubtleFillColorTertiary", "SubtleFillColorTransparent", "SurfaceStrokeColorDefault",
        "SurfaceStrokeColorFlyout", "SurfaceStrokeColorInverse", "SystemFillColorAttention",
        "SystemFillColorAttentionBackground", "SystemFillColorAttentionBackgroundBrush", "SystemFillColorCaution",
        "SystemFillColorCautionBackground", "SystemFillColorCautionBackgroundBrush", "SystemFillColorCautionBrush",
        "SystemFillColorCritical", "SystemFillColorCriticalBackground", "SystemFillColorCriticalBackgroundBrush",
        "SystemFillColorCriticalBrush", "SystemFillColorInformational", "SystemFillColorNeutral",
        "SystemFillColorNeutralBackground", "SystemFillColorNeutralBackgroundBrush", "SystemFillColorNeutralBrush",
        "SystemFillColorSolidAttentionBackground", "SystemFillColorSolidAttentionBackgroundBrush",
        "SystemFillColorSolidNeutral", "SystemFillColorSolidNeutralBackground",
        "SystemFillColorSolidNeutralBackgroundBrush", "SystemFillColorSolidNeutralBrush", "SystemFillColorSuccess",
        "SystemFillColorSuccessBackground", "SystemFillColorSuccessBackgroundBrush", "SystemFillColorSuccessBrush",
        "TextFillColorDisabled", "TextFillColorInverse", "TextFillColorPrimary", "TextFillColorSecondary",
        "TextFillColorTertiary", "TextOnAccentFillColorDisabled", "TextOnAccentFillColorPrimary",
        "TextOnAccentFillColorSecondary", "TextOnAccentFillColorSelectedText", "TextPlaceholderColor",
    ];

    // Files whose references to these keys are not drawing, with why. Paths are relative to src\Scribe.App.
    private static readonly IReadOnlyDictionary<string, string> NotDrawing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [@"Infrastructure\AccentContrastResources.cs"] =
            "Reads the light and dark themes' colours to plan from; in a contrast theme the plan is empty and nothing it reads is drawn.",
    };

    // Placeholder keys app code draws without a repair, with why (see ContrastPlaceholderRepairs' remarks).
    private static readonly IReadOnlyDictionary<string, string> DrawnUnrepaired = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["MenuBarItemBackgroundSelected"] =
            "The tray menu's template draws it only as a light or dark theme's highlight: while the flag is off its own later triggers draw the system Highlight over it.",
        ["MenuBarItemBackgroundPressed"] =
            "The tray menu's template draws it only as a light or dark theme's pressed fill: while the flag is off its own later triggers draw the system Highlight over it.",
        ["MenuBarItemTextForegroundPressed"] =
            "The tray menu's template draws it only as a light or dark theme's pressed label: while the flag is off its own later triggers draw HighlightText over it.",
    };

    [Fact]
    public void Every_repair_names_a_wpf_ui_contrast_placeholder_once()
    {
        Assert.Equal(101, WpfUiContrastPlaceholders.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(ContrastPlaceholderRepairs.All.Count, ContrastPlaceholderRepairs.All.Select(r => r.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.All(ContrastPlaceholderRepairs.All, repair => Assert.Contains(repair.Key, WpfUiContrastPlaceholders));
        Assert.All(DrawnUnrepaired.Keys, key => Assert.Contains(key, WpfUiContrastPlaceholders));
    }

    [Fact]
    public void The_placeholder_is_opaque_red()
    {
        Assert.Equal(new SrgbColor(255, 255, 0, 0), ContrastPlaceholderRepairs.Placeholder);
    }

    [Theory]
    [InlineData("MenuBarItemBorderBrush", ContrastSystemColor.WindowText)]
    [InlineData("TextFillColorDisabled", ContrastSystemColor.GrayText)]
    [InlineData("SystemFillColorSuccessBrush", ContrastSystemColor.WindowText)]
    [InlineData("SystemFillColorCautionBrush", ContrastSystemColor.WindowText)]
    [InlineData("SystemFillColorCriticalBrush", ContrastSystemColor.WindowText)]
    public void Each_key_takes_the_system_colour_winui_uses_for_its_role(string key, ContrastSystemColor expected)
    {
        Assert.Equal(expected, Assert.Single(ContrastPlaceholderRepairs.All, r => r.Key == key).Color);
    }

    [Fact]
    public void Only_text_fill_color_disabled_is_a_colour_resource()
    {
        Assert.Equal("TextFillColorDisabled", Assert.Single(ContrastPlaceholderRepairs.All, r => r.Kind == ContrastResourceKind.Color).Key);
    }

    [Fact]
    public void The_menu_highlight_is_not_repaired()
    {
        // A Highlight fill needs HighlightText on every label drawn over it and on none drawn elsewhere, which WPF-UI's
        // MenuItem templates cannot give it: the label is the item's inherited Foreground (so a submenu's items would
        // inherit it too), and the same fill is WPF-UI's ListBoxItem hover.
        Assert.DoesNotContain(ContrastPlaceholderRepairs.All, r => r.Key.StartsWith("MenuBarItemBackground", StringComparison.Ordinal));
        Assert.DoesNotContain(ContrastPlaceholderRepairs.All, r => r.Key == "MenuBarItemTextForegroundPressed");
    }

    [Fact]
    public void Every_placeholder_the_app_draws_is_repaired_or_says_why_not()
    {
        var repaired = ContrastPlaceholderRepairs.All.Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        var pattern = new Regex(@"\b(?:" + string.Join('|', WpfUiContrastPlaceholders) + @")\b", RegexOptions.CultureInvariant);
        var violations = new List<string>();
        foreach (var file in AppFiles())
        {
            var path = Path.GetRelativePath(AppRoot, file);
            if (NotDrawing.ContainsKey(path))
            {
                continue;
            }

            var text = StripComments(File.ReadAllText(file), file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase));
            foreach (Match match in pattern.Matches(text))
            {
                if (!repaired.Contains(match.Value) && !DrawnUnrepaired.ContainsKey(match.Value))
                {
                    violations.Add($"{path}:{text.AsSpan(0, match.Index).Count('\n') + 1}: {match.Value}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "WPF-UI's contrast dictionaries leave these as the placeholder red; add each to ContrastPlaceholderRepairs with the " +
            "system colour WinUI uses for its role, or draw a key that has one:\n" + string.Join('\n', violations));
    }

    [Fact]
    public void Every_file_that_does_not_draw_exists_and_says_why()
    {
        Assert.All(NotDrawing, entry =>
        {
            Assert.True(File.Exists(Path.Combine(AppRoot, entry.Key)), entry.Key);
            Assert.False(string.IsNullOrWhiteSpace(entry.Value));
        });
        Assert.All(DrawnUnrepaired, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Value)));
    }

    [Fact]
    public void The_app_writes_the_repairs_only_with_the_contrast_dictionary_and_before_the_flag_turns_on()
    {
        var code = File.ReadAllText(Path.Combine(AppRoot, "Infrastructure", "AccentContrastResources.cs"));
        var repair = code.IndexOf("RepairContrastPlaceholders(resources, wpfTheme == ApplicationTheme.HighContrast);", StringComparison.Ordinal);
        var flagOn = code.IndexOf("WriteScribe(resources, AccentContrastKeys.Applies, true);", StringComparison.Ordinal);

        Assert.True(repair > 0, "Apply must write the contrast repairs.");
        Assert.True(flagOn > repair, "The repairs are written before the flag turns Scribe's triggers on.");
        Assert.Contains("foreach (var repair in ContrastPlaceholderRepairs.All)", code, StringComparison.Ordinal);
        Assert.Contains("WriteOverride(resources, repair.Key, null);", code, StringComparison.Ordinal);
    }

    [Fact]
    public void The_tray_menu_takes_back_what_the_application_took_back()
    {
        // WPF-UI's ApplicationThemeManager.Apply(element) copies every application-level entry and removes none, so the
        // long-lived tray menu kept a contrast repair after the contrast theme ended. The reconcile must compare with the
        // application's own keys: ResourceDictionary.Contains also searches the merged theme dictionaries, which define
        // every key the application overrides, so it kept every stale copy (Astra's round 2).
        var code = StripComments(File.ReadAllText(Path.Combine(AppRoot, "Tray", "TrayIconHost.cs")), xaml: false);
        var method = code.IndexOf("private void ApplyMenuTheme()", StringComparison.Ordinal);
        Assert.True(method > 0);
        var body = code[method..code.IndexOf("private void RebuildMenu()", method, StringComparison.Ordinal)];
        var reconcile = body.IndexOf("CopiedResourceKeys.Reconcile(_copiedApplicationKeys, app.Resources.Keys.Cast<object>(), key => _menu.Resources.Remove(key));", StringComparison.Ordinal);
        var copy = body.IndexOf("ApplicationThemeManager.Apply(_menu);", StringComparison.Ordinal);

        Assert.True(reconcile > 0 && copy > reconcile, "Stale copies are removed, by the application's own keys, before the menu copies again.");
        Assert.DoesNotContain(".Contains(key)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_copy_the_application_no_longer_has_is_removed_and_everything_else_stays()
    {
        var copied = new HashSet<object> { "MenuBarItemBorderBrush", "TextFillColorDisabled", "ScribeAccentContrastApplies", typeof(string) };
        var removed = new List<object>();

        // The contrast theme ended: the repairs are gone from the application's own entries (the theme still defines
        // them, which is why only the primary keys may be compared), the flag stays, and a new key arrived.
        var count = CopiedResourceKeys.Reconcile(copied, ["ScribeAccentContrastApplies", typeof(string), "ScribeChartBarBrush"], removed.Add);

        Assert.Equal(2, count);
        Assert.Equal(["MenuBarItemBorderBrush", "TextFillColorDisabled"], removed.Cast<string>().Order(StringComparer.Ordinal));
        Assert.Equal(3, copied.Count);
        Assert.Contains("ScribeChartBarBrush", copied);
        Assert.DoesNotContain("MenuBarItemBorderBrush", copied);
    }

    [Fact]
    public void A_key_the_menu_never_copied_is_never_removed()
    {
        var copied = new HashSet<object>();
        var removed = new List<object>();

        Assert.Equal(0, CopiedResourceKeys.Reconcile(copied, ["ScribeAccentContrastApplies"], removed.Add));
        Assert.Empty(removed);

        // A second pass after the application dropped the flag removes only what the first pass recorded as copied.
        Assert.Equal(1, CopiedResourceKeys.Reconcile(copied, [], removed.Add));
        Assert.Equal(["ScribeAccentContrastApplies"], removed.Cast<string>());
        Assert.Empty(copied);
    }
    private static readonly string AppRoot = Path.Combine(FindRepositoryRoot(), "src", "Scribe.App");

    private static IEnumerable<string> AppFiles() =>
        Directory.EnumerateFiles(AppRoot, "*.*", SearchOption.AllDirectories)
            .Where(file => file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    // Comments may name these keys (several explain why a template avoids them); line breaks stay so line numbers hold.
    private static string StripComments(string text, bool xaml) => xaml
        ? XmlComment().Replace(text, KeepLineBreaks)
        : BlockComment().Replace(LineComment().Replace(text, string.Empty), KeepLineBreaks);

    private static string KeepLineBreaks(Match match) => new('\n', match.Value.Count(c => c == '\n'));

    private static string FindRepositoryRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Scribe.slnx")))
            {
                return dir;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException("Repository root not found.");
    }

    [GeneratedRegex(@"(?<![:""])//[^\n]*")]
    private static partial Regex LineComment();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockComment();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex XmlComment();
}
