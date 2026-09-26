using System.IO;
using Scribe.Core.Appearance;
using Xunit;

namespace Scribe.Core.Tests;

// The palette ("Signal On") was decided with these exact values and ratios; a change to any of them is a design change,
// not a refactor, and has to come with the decision that made it.
public sealed class ScribeBrandTests
{
    [Fact]
    public void The_brand_colours_are_the_ones_sampled_from_the_icon()
    {
        Assert.Equal("#07142F", Hex(ScribeBrand.Ink));
        Assert.Equal("#FCFCFC", Hex(ScribeBrand.Paper));
        Assert.Equal("#1C83FE", Hex(ScribeBrand.Signal));
        Assert.Equal("#6B7689", Hex(ScribeBrand.Slate));
        Assert.Equal("#82B6FF", Hex(ScribeBrand.ProcessingDots));
    }

    [Fact]
    public void Scribe_blue_is_the_approved_four_colour_set_for_each_theme()
    {
        Assert.Equal(
            ["#2461E9", "#0C48CF", "#0035B1", "#00298E"],
            Hexes(ScribeBrand.LightAccent));
        Assert.Equal(
            ["#2461E9", "#72A0FF", "#88B0FE", "#A1C1FE"],
            Hexes(ScribeBrand.DarkAccent));
    }

    [Fact]
    public void The_recording_indicator_colours_are_the_approved_ones()
    {
        Assert.Equal("#1A2744", Hex(ScribeBrand.PillFaceTop));
        Assert.Equal("#0E1830", Hex(ScribeBrand.PillFaceBottom));
        Assert.Equal("#71ADFF", Hex(ScribeBrand.ListeningEdge));
        Assert.Equal("#8A9BB5", Hex(ScribeBrand.NeutralEdge));
        Assert.Equal("#FF99A4", Hex(ScribeBrand.ErrorEdge));
        Assert.Equal("#93C0FE", Hex(ScribeBrand.LevelTip));
        Assert.Equal("#549DFF", Hex(ScribeBrand.LevelBase));
    }

    [Fact]
    public void App_xaml_brand_brushes_match_the_core_brand_colours()
    {
        var app = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Scribe.App", "App.xaml"));

        Assert.Contains("x:Key=\"ScribePillFaceBrush\"", app);
        Assert.Contains($"Color=\"{Hex(ScribeBrand.PillFaceTop)}\" Offset=\"0\"", app);
        Assert.Contains($"Color=\"{Hex(ScribeBrand.PillFaceBottom)}\" Offset=\"1\"", app);
        Assert.Contains($"x:Key=\"ScribePillListeningEdgeBrush\" Color=\"{Hex(ScribeBrand.ListeningEdge)}\"", app);
        Assert.Contains($"x:Key=\"ScribeLevelTipBrush\" Color=\"{Hex(ScribeBrand.LevelTip)}\"", app);
        Assert.Contains($"x:Key=\"ScribeSignalBrush\" Color=\"{Hex(ScribeBrand.Signal)}\"", app);
    }

    [Fact]
    public void Position_picker_no_longer_uses_the_critical_system_fill()
    {
        var appRoot = Path.Combine(RepositoryRoot(), "src", "Scribe.App");
        foreach (var file in Directory.EnumerateFiles(appRoot, "*.xaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("SystemFillColorCriticalBrush", text);
        }
    }

    [Fact]
    public void Welcome_bars_are_gated_by_animation_and_contrast_settings()
    {
        var welcome = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Scribe.App", "Onboarding", "WelcomeWindow.xaml.cs"));
        var animationCheck = welcome.IndexOf("SystemParameters.ClientAreaAnimation", StringComparison.Ordinal);
        var contrastCheck = welcome.IndexOf("SystemParameters.HighContrast", StringComparison.Ordinal);
        var startAnimation = welcome.IndexOf("BeginAnimation", StringComparison.Ordinal);

        Assert.True(animationCheck >= 0, "Welcome must check Windows animation effects before animating bars.");
        Assert.True(contrastCheck >= 0, "Welcome must check contrast mode before animating bars.");
        Assert.True(animationCheck < startAnimation, "The animation-effects check must run before the bars animate.");
        Assert.True(contrastCheck < startAnimation, "The contrast check must run before the bars animate.");
    }

    [Theory]
    // Save at rest: white on the light accent button, black on the dark one.
    [InlineData("#FFFFFF", "#0C48CF", 7.39)]
    [InlineData("#000000", "#88B0FE", 9.69)]
    // The text selection background, with white selected text, in both themes.
    [InlineData("#FFFFFF", "#2461E9", 5.30)]
    // The tray: the capsule on the recording tile, the dots on Ink, the pause bars on Slate.
    [InlineData("#FCFCFC", "#1C83FE", 3.57)]
    [InlineData("#82B6FF", "#07142F", 8.77)]
    [InlineData("#FCFCFC", "#6B7689", 4.47)]
    public void The_measured_ratios_of_the_decision_hold(string foreground, string background, double expected)
    {
        var ratio = WcagContrast.Ratio(SrgbColor.Parse(foreground), SrgbColor.Parse(background));
        Assert.Equal(expected, Math.Round(ratio, 2), 2);
    }

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        return root?.FullName ?? throw new InvalidOperationException("Could not find the repository root.");
    }

    private static string Hex(SrgbColor color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static string[] Hexes(AccentSet set) => [Hex(set.System), Hex(set.Primary), Hex(set.Secondary), Hex(set.Tertiary)];
}
