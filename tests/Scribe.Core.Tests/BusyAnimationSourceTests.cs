using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Scribe.Core.Tests;

/// <summary>
/// StopInactiveProgress's wiring in the WPF shell, which the tests cannot load: the flag gates everything (off keeps WPF-UI's
/// and WPF's own animations exactly), every busy indicator in Settings is covered, the window closes them, and the bar's
/// style is WPF-UI's own with only the glow renamed. The behaviour itself is in <see cref="Scribe.Core.Settings.BusyAnimationLifecycle"/>
/// and <see cref="Scribe.Core.Settings.BusyBarSweep"/>.
/// </summary>
public sealed class BusyAnimationSourceTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Nothing_attaches_unless_the_flag_is_on_and_the_window_closes_what_it_attached()
    {
        var partial = Read("src", "Scribe.App", "Settings", "SettingsWindow.BusyAnimation.cs");
        var init = Slice(partial, "private void InitializeBusyAnimations()", "private static ProgressBar BusyBarIn(");
        Assert.Matches(
            new Regex(@"^\s*\{\s*if \(!_perfFlags\.IsOn\(PerfFlags\.StopInactiveProgress\)\)\s*\{\s*return;\s*\}", RegexOptions.Singleline),
            init[init.IndexOf('{', StringComparison.Ordinal)..]);

        var window = Read("src", "Scribe.App", "Settings", "SettingsWindow.xaml.cs");
        var constructor = Slice(window, "public SettingsWindow(", "RefreshAiStatus();");
        Assert.Contains("_perfFlags = perfFlags ?? PerfFlags.None;", constructor, StringComparison.Ordinal);
        Assert.True(
            constructor.IndexOf("InitializeComponent();", StringComparison.Ordinal) <
            constructor.IndexOf("InitializeBusyAnimations();", StringComparison.Ordinal),
            "The busy animations attach after the controls exist.");

        var onClosed = Slice(window, "private void OnClosed(object? sender, EventArgs e)", "// --- Themed dialogs");
        Assert.Contains("CloseBusyAnimations();", onClosed, StringComparison.Ordinal);

        var app = Read("src", "Scribe.App", "App.xaml.cs");
        Assert.Contains("perfFlags: services.GetRequiredService<PerfFlags>()", app, StringComparison.Ordinal);
    }

    [Fact]
    public void Off_the_status_row_runs_exactly_the_code_it_ran_before()
    {
        var row = Read("src", "Scribe.App", "Settings", "SettingsStatusRow.xaml.cs");
        var show = Slice(row, "public void Show(AiCleanupStatusRow? row)", "internal void UseBusyAnimationLifecycle()");

        // Show still only sets the spinner's visibility; the lifecycle, when it exists, follows that visibility.
        Assert.DoesNotContain("_busyAnimation", show, StringComparison.Ordinal);
        Assert.Contains("BusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;", show, StringComparison.Ordinal);

        var ring = XDocument.Parse(Read("src", "Scribe.App", "Settings", "SettingsStatusRow.xaml"))
            .Descendants().Single(e => e.Name.LocalName == "ProgressRing");
        Assert.Equal("True", (string?)ring.Attribute("IsIndeterminate"));
        Assert.Equal("Collapsed", (string?)ring.Attribute("Visibility"));
    }

    [Fact]
    public void Every_status_row_in_Settings_has_its_spinner_covered()
    {
        var xaml = XDocument.Parse(Read("src", "Scribe.App", "Settings", "SettingsWindow.xaml"));
        var rows = xaml.Descendants()
            .Where(e => e.Name.LocalName == "SettingsStatusRow")
            .Select(e => (string?)e.Attribute(Xaml + "Name"))
            .ToList();
        Assert.NotEmpty(rows);
        Assert.DoesNotContain(null, rows);

        var partial = Read("src", "Scribe.App", "Settings", "SettingsWindow.BusyAnimation.cs");
        var list = Slice(partial, "private SettingsStatusRow[] BusyStatusRows =>", ";");
        var covered = Regex.Matches(list, @"\b(\w+StatusRow)\b").Select(m => m.Groups[1].Value)
            .Where(name => name != "SettingsStatusRow")
            .ToList();
        Assert.Equal(rows.Order(StringComparer.Ordinal), covered.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Every_animated_progress_indicator_in_the_shell_is_one_the_flag_covers()
    {
        // A new spinner or indeterminate bar anywhere in the WPF shell must join StopInactiveProgress, or it brings the idle
        // frames back once it has been shown.
        var app = Path.Combine(RepositoryRoot(), "src", "Scribe.App");
        var rings = new List<string>();
        var bars = new List<string>();
        foreach (var file in Directory.EnumerateFiles(app, "*.xaml", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(app, file);
            foreach (var element in XDocument.Load(file).Descendants())
            {
                if (element.Name.LocalName == "ProgressRing")
                {
                    rings.Add($"{relative}:{(string?)element.Attribute(Xaml + "Name")}");
                }
                else if (element.Name == Presentation + "ProgressBar" && (string?)element.Attribute("IsIndeterminate") == "True")
                {
                    bars.Add($"{relative}:{(string?)element.Parent?.Attribute(Xaml + "Name")}");
                }
            }
        }

        Assert.Equal([Path.Combine("Settings", "SettingsStatusRow.xaml") + ":BusyRing"], rings);
        Assert.Equal(
            [
                Path.Combine("Settings", "SettingsWindow.xaml") + ":DictionarySuggestBusy",
                Path.Combine("Settings", "SettingsWindow.xaml") + ":DictionaryCleanupBusy",
            ],
            bars);

        // Each busy panel holds exactly the one bar BusyBarIn finds, and nothing sets a bar's Foreground (the glow brush
        // ProgressBar would have made only differs from the plain one for a Foreground that is not a solid brush).
        var window = XDocument.Parse(Read("src", "Scribe.App", "Settings", "SettingsWindow.xaml"));
        foreach (var panel in new[] { "DictionarySuggestBusy", "DictionaryCleanupBusy" })
        {
            var element = window.Descendants().Single(e => (string?)e.Attribute(Xaml + "Name") == panel);
            var bar = Assert.Single(element.Elements(Presentation + "ProgressBar"));
            Assert.Null(bar.Attribute("Foreground"));
            Assert.Null(bar.Attribute("Style"));
        }

        var partial = Read("src", "Scribe.App", "Settings", "SettingsWindow.BusyAnimation.cs");
        Assert.Contains("BusyBarIn(DictionarySuggestBusy)", partial, StringComparison.Ordinal);
        Assert.Contains("BusyBarIn(DictionaryCleanupBusy)", partial, StringComparison.Ordinal);
    }

    [Fact]
    public void The_busy_bar_style_is_WPF_UI_s_with_only_the_glow_renamed()
    {
        var source = Read("src", "Scribe.App", "Settings", "BusyBarStyle.xaml");
        var style = Assert.Single(XDocument.Parse(source).Root!.Elements(Presentation + "Style"));
        Assert.Equal("ScribeBusyBarStyle", (string?)style.Attribute(Xaml + "Key"));
        Assert.Equal("{x:Type ProgressBar}", (string?)style.Attribute("TargetType"));
        Assert.Null(style.Attribute("BasedOn"));

        // ProgressBar animates a template part named PART_GlowRect by itself; the copy must not have one.
        Assert.DoesNotContain("PART_GlowRect", style.ToString(), StringComparison.Ordinal);
        var named = style.Descendants().Where(e => e.Attribute("Name") is not null)
            .Select(e => $"{e.Name.LocalName}:{(string?)e.Attribute("Name")}")
            .ToList();
        Assert.Equal(
            [
                "Grid:TemplateRoot", "Rectangle:PART_Track", "Border:PART_Indicator",
                "Grid:TemplateRoot", "Rectangle:PART_Track", "Decorator:PART_Indicator", "Grid:Animation", "Border:ScribeBusyGlow",
            ],
            named);

        var glow = style.Descendants().Single(e => (string?)e.Attribute("Name") == "ScribeBusyGlow");
        Assert.Equal("200", (string?)glow.Attribute("Width"));
        Assert.Equal("0,0,0,0", (string?)glow.Attribute("Margin"));
        Assert.Equal("Left", (string?)glow.Attribute("HorizontalAlignment"));
        Assert.Equal("{TemplateBinding Foreground}", (string?)glow.Attribute("Background"));
        Assert.Equal("2", (string?)glow.Attribute("CornerRadius"));

        // The same theme brushes WPF-UI's style draws with, so every theme (contrast included) colours it the same.
        var keys = Regex.Matches(source, @"\{DynamicResource (\w+)\}").Select(m => m.Groups[1].Value).Distinct().Order().ToList();
        Assert.Equal(
            ["ProgressBarBackground", "ProgressBarBorderBrush", "ProgressBarForeground", "ProgressBarIndeterminateBackground"],
            keys);

        var code = Read("src", "Scribe.App", "Settings", "BusyAnimation.cs");
        Assert.Contains("internal const string StyleKey = \"ScribeBusyBarStyle\";", code, StringComparison.Ordinal);
        Assert.Contains("internal const string GlowName = \"ScribeBusyGlow\";", code, StringComparison.Ordinal);
        Assert.Contains("internal const string IndicatorName = \"PART_Indicator\";", code, StringComparison.Ordinal);
        Assert.Contains("\"/Scribe;component/Settings/BusyBarStyle.xaml\"", code, StringComparison.Ordinal);
    }

    [Fact]
    public void The_spinner_leaves_a_shown_but_disabled_ring_to_WPF_UI_and_stops_a_hidden_one()
    {
        var code = Read("src", "Scribe.App", "Settings", "BusyAnimation.cs");
        var ring = Slice(code, "internal sealed class BusyRingAnimation", "internal sealed class BusyBarAnimation");

        // Shown means IsVisible only; WPF-UI's own IsEnabled trigger keeps pausing a disabled ring that is still on screen.
        Assert.DoesNotContain("IsEnabled", ring, StringComparison.Ordinal);
        Assert.Contains("_lifecycle.Update(_ring.IsVisible)", ring, StringComparison.Ordinal);
        Assert.Contains("storyboard.Stop(_ring);", ring, StringComparison.Ordinal);
        Assert.Contains("storyboard.Remove(_ring);", ring, StringComparison.Ordinal);
        Assert.Contains("_ring.IsIndeterminate = false;", ring, StringComparison.Ordinal);

        // The bar stops the clock it owns, never pauses it.
        var bar = Slice(code, "internal sealed class BusyBarAnimation", "internal static class BusyAnimationTemplates");
        Assert.Contains("clock.Controller?.Stop();", bar, StringComparison.Ordinal);
        Assert.DoesNotContain("Pause(", bar, StringComparison.Ordinal);
        Assert.DoesNotContain("BeginAnimation(", bar, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_owners_change_a_busy_indicator_s_indeterminate_state()
    {
        // BusyBarAnimation finds its template parts on each start and relies on the bars' template never changing after it
        // attaches; BusyRingAnimation owns the rings' IsIndeterminate. Anything else writing it would undo the lifecycle.
        var app = Path.Combine(RepositoryRoot(), "src", "Scribe.App");
        var writers = Directory.EnumerateFiles(app, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => Regex.IsMatch(File.ReadAllText(file), @"\.IsIndeterminate\s*=(?!=)"))
            .Select(file => Path.GetRelativePath(app, file))
            .ToList();

        Assert.Equal([Path.Combine("Settings", "BusyAnimation.cs")], writers);
        var window = string.Concat(Directory.EnumerateFiles(Path.Combine(app, "Settings"), "SettingsWindow*.cs").Select(File.ReadAllText));
        Assert.DoesNotContain("DictionarySuggestBusy.Children.", window.Replace("DictionarySuggestBusy.Children.OfType", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain("DictionaryCleanupBusy.Children.", window.Replace("DictionaryCleanupBusy.Children.OfType", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([RepositoryRoot(), .. parts]));

    private static string Slice(string source, string start, string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Missing start marker {start}.");
        var endIndex = source.IndexOf(end, startIndex + start.Length, StringComparison.Ordinal);
        Assert.True(endIndex > startIndex, $"Missing end marker {end}.");
        return source[startIndex..endIndex];
    }

    private static string RepositoryRoot()
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
}
