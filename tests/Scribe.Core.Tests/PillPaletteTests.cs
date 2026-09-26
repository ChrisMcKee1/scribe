using System.Text.RegularExpressions;
using Scribe.Core.Appearance;

namespace Scribe.Core.Tests;

// The recording pill's colours ("Signal On", palette decision section 5). The brand values are ScribeBrand's own; the
// pill adds only the sheen, its text tones and the two status icons Fluent defines. The ratios are the ones the decision
// measured, every one against the worst surface the colour sits on: the top of the face under the full sheen.
public sealed class PillPaletteTests
{
    private static readonly SrgbColor FaceUnderSheen = PillPalette.Sheen.Over(PillPalette.FaceTop);

    [Fact]
    public void The_pill_s_own_colours_are_the_approved_ones()
    {
        Assert.Equal("#14FFFFFF", PillPalette.Sheen.ToString());
        Assert.Equal("#00FFFFFF", PillPalette.SheenEnd.ToString());
        Assert.Equal("#FFFFFF", PillPalette.Text.ToString());
        Assert.Equal("#C5FFFFFF", PillPalette.SecondaryText.ToString());
        Assert.Equal("#6CCB5F", PillPalette.SuccessIcon.ToString());
        Assert.Equal("#FCE100", PillPalette.CautionIcon.ToString());
        Assert.Equal(0.45, PillPalette.SheenFraction);
    }

    [Fact]
    public void Every_brand_colour_on_the_pill_is_ScribeBrand_s()
    {
        Assert.Equal(ScribeBrand.PillFaceTop, PillPalette.FaceTop);
        Assert.Equal(ScribeBrand.PillFaceBottom, PillPalette.FaceBottom);
        Assert.Equal(ScribeBrand.ListeningEdge, PillPalette.ListeningEdge);
        Assert.Equal(ScribeBrand.NeutralEdge, PillPalette.NeutralEdge);
        Assert.Equal(ScribeBrand.ErrorEdge, PillPalette.ErrorEdge);
        Assert.Equal(ScribeBrand.LevelTip, PillPalette.LevelTip);
        Assert.Equal(ScribeBrand.LevelBase, PillPalette.LevelBase);
        Assert.Equal(ScribeBrand.LevelTip, PillPalette.ProcessingDots);
        Assert.Equal(ScribeBrand.ErrorEdge, PillPalette.ErrorIcon);
    }

    [Fact]
    public void The_palette_writes_a_literal_only_for_a_colour_the_brand_does_not_have()
    {
        // A literal equal to a brand value would be a second source of it, free to drift from ScribeBrand.
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Scribe.Core", "Appearance", "PillPalette.cs"));
        var literals = Regex.Matches(source, "\"(#[0-9A-Fa-f]{6}(?:[0-9A-Fa-f]{2})?)\"")
            .Select(match => SrgbColor.Parse(match.Groups[1].Value))
            .ToArray();

        Assert.Equal(6, literals.Length);
        Assert.All(literals, literal => Assert.DoesNotContain(literal, BrandColours()));
        Assert.Equal(
            new[] { PillPalette.Sheen, PillPalette.SheenEnd, PillPalette.Text, PillPalette.SecondaryText, PillPalette.SuccessIcon, PillPalette.CautionIcon }
                .OrderBy(c => c.ToString()),
            literals.OrderBy(c => c.ToString()));
    }

    [Fact]
    public void All_lists_every_colour_once_with_its_role()
    {
        var roles = PillPalette.All.Select(c => c.Role).ToArray();
        Assert.Equal(roles.Distinct().Count(), roles.Length);
        Assert.Equal(
            ["face top", "face bottom", "sheen", "sheen end", "listening edge", "neutral edge", "error edge", "text",
             "secondary text", "level tip", "level base", "processing dots", "success icon", "caution icon", "error icon"],
            roles);
        Assert.Equal(PillPalette.FaceTop, PillPalette.All.Single(c => c.Role == "face top").Color);
        Assert.Equal(PillPalette.CautionIcon, PillPalette.All.Single(c => c.Role == "caution icon").Color);
        Assert.Equal(PillPalette.ErrorIcon, PillPalette.All.Single(c => c.Role == "error icon").Color);
    }

    [Theory]
    // Text and the icons, on the face's top under the full sheen (#2C3853), its lightest point.
    [InlineData("text", 11.68)]
    [InlineData("level tip", 6.24)]
    [InlineData("level base", 4.25)]
    [InlineData("processing dots", 6.24)]
    [InlineData("success icon", 5.75)]
    [InlineData("caution icon", 8.85)]
    [InlineData("error icon", 5.75)]
    public void The_measured_ratios_on_the_face_hold(string role, double expected)
    {
        Assert.Equal("#2C3853", FaceUnderSheen.ToString());
        var color = PillPalette.All.Single(c => c.Role == role).Color;
        Assert.Equal(expected, Math.Round(WcagContrast.Ratio(color, FaceUnderSheen), 2), 2);
    }

    [Fact]
    public void Secondary_text_is_readable_where_it_is_faintest()
    {
        var drawn = PillPalette.SecondaryText.Over(FaceUnderSheen);
        Assert.Equal(7.71, Math.Round(WcagContrast.Ratio(drawn, FaceUnderSheen), 2), 2);
    }

    [Theory]
    // The edge is the pill's boundary on a dark window: the face bottom alone is 1.11 against #0C0C0C.
    [InlineData("listening edge", "#0C0C0C", 8.50)]
    [InlineData("listening edge", "#1E1E1E", 7.24)]
    [InlineData("neutral edge", "#0C0C0C", 6.93)]
    [InlineData("neutral edge", "#1E1E1E", 5.90)]
    [InlineData("error edge", "#0C0C0C", 9.64)]
    [InlineData("error edge", "#1E1E1E", 8.21)]
    [InlineData("face bottom", "#0C0C0C", 1.11)]
    public void The_edges_mark_the_pill_on_a_dark_window(string role, string window, double expected)
    {
        var color = PillPalette.All.Single(c => c.Role == role).Color;
        Assert.Equal(expected, Math.Round(WcagContrast.Ratio(color, SrgbColor.Parse(window)), 2), 2);
    }

    [Fact]
    public void The_face_stands_out_on_a_white_page()
    {
        Assert.Equal(11.68, Math.Round(WcagContrast.Ratio(FaceUnderSheen, SrgbColor.White), 2), 2);
    }

    private static SrgbColor[] BrandColours() =>
    [
        ScribeBrand.Ink, ScribeBrand.Paper, ScribeBrand.Signal, ScribeBrand.Slate, ScribeBrand.ProcessingDots,
        ScribeBrand.LightAccent.System, ScribeBrand.LightAccent.Primary, ScribeBrand.LightAccent.Secondary, ScribeBrand.LightAccent.Tertiary,
        ScribeBrand.DarkAccent.System, ScribeBrand.DarkAccent.Primary, ScribeBrand.DarkAccent.Secondary, ScribeBrand.DarkAccent.Tertiary,
        ScribeBrand.PillFaceTop, ScribeBrand.PillFaceBottom, ScribeBrand.ListeningEdge, ScribeBrand.NeutralEdge,
        ScribeBrand.ErrorEdge, ScribeBrand.LevelTip, ScribeBrand.LevelBase,
    ];

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return root.FullName;
    }
}
