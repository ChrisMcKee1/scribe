using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Scribe.Core.Appearance;
using Scribe.Core.Overlay;

namespace Scribe.Core.Tests;

/// <summary>
/// The recording pill as the overlay draws it. The overlay has no reference to Scribe.Core and no test can load it (it is
/// a WinUI 3 window), so its XAML and code are pinned from source: every colour it draws is a
/// <see cref="PillPalette"/> colour in the role the palette gives it (decision PD15), a contrast theme draws system
/// colours only, every theme resource and storyboard target it names exists (a missing one throws when the window loads,
/// which no build catches), and its motion and timing follow the palette decision and <see cref="PillTiming"/>.
/// </summary>
public sealed class OverlayPillSourceTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    // The brushes the pill's XAML reads, each with the palette colours it must draw, top to bottom for a gradient.
    public static TheoryData<string, string[]> BrandBrushes => new()
    {
        { "PillFaceBrush", [Hex(PillPalette.FaceTop), Hex(PillPalette.FaceBottom)] },
        { "PillSheenBrush", [Hex(PillPalette.Sheen), Hex(PillPalette.SheenEnd)] },
        { "PillListeningEdgeBrush", [Hex(PillPalette.ListeningEdge)] },
        { "PillNeutralEdgeBrush", [Hex(PillPalette.NeutralEdge)] },
        { "PillErrorEdgeBrush", [Hex(PillPalette.ErrorEdge)] },
        { "PillTextBrush", [Hex(PillPalette.Text)] },
        { "PillSecondaryTextBrush", [Hex(PillPalette.SecondaryText)] },
        { "PillLevelBrush", [Hex(PillPalette.LevelTip), Hex(PillPalette.LevelBase)] },
        { "PillDotBrush", [Hex(PillPalette.ProcessingDots)] },
        { "PillSuccessBrush", [Hex(PillPalette.SuccessIcon)] },
        { "PillCautionBrush", [Hex(PillPalette.CautionIcon)] },
        { "PillErrorBrush", [Hex(PillPalette.ErrorIcon)] },
    };

    [Fact]
    public void Every_colour_the_overlay_s_XAML_writes_is_a_pill_palette_colour_or_Transparent()
    {
        // Transparent paints nothing: the window's see-through background, and the sheen in a contrast theme.
        var found = 0;
        foreach (var file in OverlayFiles("*.xaml"))
        {
            var colours = Colours(File.ReadAllText(file)).ToArray();
            Assert.All(colours, c => Assert.True(c.Approved, $"{Path.GetFileName(file)}: {c.Where} is {c.Value}, which PillPalette does not have."));
            found += colours.Count(c => c.Value.StartsWith('#'));
        }

        Assert.True(found >= 2 * 15, "The pill's colours were not found where the overlay keeps them.");
    }

    [Theory]
    // Astra's example: valid XAML, a visible colour, in single quotes.
    [InlineData("<Grid x:Name='NoticeContent' Background='Red'/>")]
    // Property-element syntax: as the property's text, as an object's attribute, and as an object's initialization text.
    [InlineData("<Border><Border.Background>Red</Border.Background></Border>")]
    [InlineData("<Border><Border.Background><SolidColorBrush Color='Red'/></Border.Background></Border>")]
    [InlineData("<Border><Border.Background><SolidColorBrush>Red</SolidColorBrush></Border.Background></Border>")]
    // A colour resource written as its element's text, and a colour property the old list did not name.
    [InlineData("<Grid.Resources><Color x:Key='Stray'>#123456</Color></Grid.Resources>")]
    [InlineData("<TextBox PlaceholderForeground=\"Red\"/>")]
    // Any case, and a short form no palette colour is written in.
    [InlineData("<Border Background=\"red\"/>")]
    [InlineData("<Border Background=\"#F00\"/>")]
    // A colour inside a markup extension, and the scRGB form.
    [InlineData("<TextBlock Foreground='{Binding Tint, FallbackValue=Red}'/>")]
    [InlineData("<Border Background='sc#1,1,0,0'/>")]
    // A markup extension's argument quoted either way (Grok's G3), a colour named as a static member, and one passed to a
    // function.
    [InlineData("<TextBlock Foreground='{Binding Tint, FallbackValue=\"Red\"}'/>")]
    [InlineData("<TextBlock Foreground='{Binding Tint, FallbackValue=\"#FF0000\"}'/>")]
    [InlineData("<TextBlock Foreground=\"{Binding Tint, FallbackValue='Red'}\"/>")]
    [InlineData("<Border><Border.Background><SolidColorBrush Color='{x:Bind ui:Colors.Red}'/></Border.Background></Border>")]
    [InlineData("<Border><Border.Background><SolidColorBrush Color='{x:Bind local:Tints.Of(ui:Colors.Red)}'/></Border.Background></Border>")]
    public void The_colour_scan_finds_a_stray_colour_however_XAML_writes_it(string fragment)
    {
        Assert.Contains(Colours(InGrid(fragment)), c => !c.Approved);
    }

    [Fact]
    public void The_colour_scan_passes_the_palette_Transparent_theme_resources_and_words_that_are_no_XAML_colour()
    {
        // Highlight and Window name system colours, which XAML does not accept as colour names; here they are a name and
        // a text.
        var colours = Colours(InGrid(
            "<Border Background='Transparent' BorderBrush='{ThemeResource PillErrorEdgeBrush}'>" +
            "<Border.Background><SolidColorBrush Color='#1A2744'/></Border.Background></Border>" +
            "<TextBlock x:Name='Highlight' Text='Window' TextTrimming='CharacterEllipsis' VerticalAlignment='Center'/>")).ToArray();

        Assert.Equal(["Transparent", "#1A2744"], colours.Select(c => c.Value));
        Assert.All(colours, c => Assert.True(c.Approved));
    }

    // A fragment inside a Grid that declares the XAML namespaces, so it parses as the overlay's own files do.
    private static string InGrid(string fragment) =>
        "<Grid xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
        "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>" + fragment + "</Grid>";

    // Every colour a XAML document writes, read from the parsed document rather than its source text, so the quote style
    // and the syntax do not matter: each attribute's value and each element's own text (property-element syntax, and an
    // object's initialization text such as a Color resource), with each term of a markup extension read on its own. A
    // value is a colour when it is written in hex, in scRGB, or as a colour's name in any case (the names XAML accepts are
    // the web colours System.Drawing knows; its system colours, such as Window and Menu, are not colour names in XAML).
    // Only a hex value PillPalette has, and Transparent, which paints nothing, are approved.
    private static IEnumerable<(string Where, string Value, bool Approved)> Colours(string xaml)
    {
        var palette = PillPalette.All.Select(c => c.Color).ToHashSet();
        foreach (var element in XDocument.Parse(xaml, LoadOptions.SetLineInfo).Descendants())
        {
            var values = element.Attributes()
                .Select(a => (Node: (XObject)a, Where: $"{element.Name.LocalName} {a.Name.LocalName}", a.Value))
                .Concat(element.Nodes().OfType<XText>().Select(t => (Node: (XObject)t, Where: $"{element.Name.LocalName}'s text", t.Value)));
            foreach (var (node, where, value) in values)
            {
                foreach (var term in Terms(value))
                {
                    if (Approval(term) is { } approved)
                    {
                        yield return ($"line {((System.Xml.IXmlLineInfo)node).LineNumber}, {where}", term, approved);
                    }
                }
            }
        }

        // Null when the term is not a colour at all.
        bool? Approval(string term)
        {
            if (term.StartsWith('#'))
            {
                return SrgbColor.TryParse(term, out var color) && palette.Contains(color);
            }

            if (term.StartsWith("sc#", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return IsColourName(term) ? string.Equals(term, "Transparent", StringComparison.OrdinalIgnoreCase) : null;
        }

        // A markup extension is read term by term: its argument quotes, either kind, are dropped, and a member is read apart
        // from what it belongs to (Colors.Red) and a call apart from its arguments.
        static IEnumerable<string> Terms(string value) =>
            value.TrimStart().StartsWith('{')
                ? value.Split(
                    [' ', '\t', '\r', '\n', '{', '}', ',', '=', '\'', '"', '.', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
                : value.Trim() is { Length: > 0 } whole ? [whole] : [];

        static bool IsColourName(string term) =>
            System.Drawing.Color.FromName(term) is { IsKnownColor: true, IsSystemColor: false };
    }

    [Fact]
    public void The_overlay_s_code_draws_no_colour()
    {
        foreach (var file in OverlayFiles("*.cs"))
        {
            var source = StripComments(File.ReadAllText(file));
            var name = Path.GetFileName(file);
            Assert.DoesNotMatch(new Regex("\"#[0-9A-Fa-f]{3,8}\""), source);
            Assert.False(source.Contains("Colors.", StringComparison.Ordinal), $"{name} names a colour.");
            Assert.False(source.Contains("SolidColorBrush", StringComparison.Ordinal), $"{name} makes a brush.");

            // The one colour in code is TransparentBackdrop's alpha-0 system backdrop: it paints nothing.
            foreach (Match call in Regex.Matches(source, @"Color\.FromArgb\((?<args>[^)]*)\)"))
            {
                Assert.Equal("TransparentBackdrop.cs", name);
                Assert.Equal("0, 0, 0, 0", call.Groups["args"].Value.Trim());
            }
        }
    }

    [Theory]
    [MemberData(nameof(BrandBrushes))]
    public void Each_brush_draws_its_palette_role_in_both_app_themes(string key, string[] colours)
    {
        foreach (var theme in new[] { "Default", "Light" })
        {
            var brush = ThemeDictionary(theme).Elements().Single(e => (string?)e.Attribute(Xaml + "Key") == key);
            string[] drawn = brush.Name.LocalName == "SolidColorBrush"
                ? [(string)brush.Attribute("Color")!]
                : [.. brush.Elements(Presentation + "GradientStop").Select(s => (string)s.Attribute("Color")!)];

            Assert.Equal(colours, drawn.Select(c => Hex(SrgbColor.Parse(c))).ToArray());
        }
    }

    [Fact]
    public void The_gradients_run_top_to_bottom_and_the_sheen_covers_the_top_45_percent()
    {
        foreach (var theme in new[] { "Default", "Light" })
        {
            foreach (var key in new[] { "PillFaceBrush", "PillSheenBrush", "PillLevelBrush" })
            {
                var brush = ThemeDictionary(theme).Elements().Single(e => (string?)e.Attribute(Xaml + "Key") == key);
                Assert.Equal("0,0", (string?)brush.Attribute("StartPoint"));
                Assert.Equal("0,1", (string?)brush.Attribute("EndPoint"));
            }

            var sheenEnd = ThemeDictionary(theme).Elements()
                .Single(e => (string?)e.Attribute(Xaml + "Key") == "PillSheenBrush")
                .Elements(Presentation + "GradientStop").Last();
            Assert.Equal(PillPalette.SheenFraction, double.Parse((string)sheenEnd.Attribute("Offset")!, CultureInfo.InvariantCulture));
        }
    }

    [Fact]
    public void The_edges_are_1_5_DIP_while_listening_and_1_DIP_otherwise()
    {
        foreach (var theme in new[] { "Default", "Light" })
        {
            Assert.Equal("1.5", Resource(theme, "PillListeningEdgeThickness").Value);
            Assert.Equal("1", Resource(theme, "PillQuietEdgeThickness").Value);
        }
    }

    [Fact]
    public void A_contrast_theme_draws_system_colours_only_with_a_2_DIP_WindowText_edge()
    {
        var contrast = ThemeDictionary("HighContrast");
        Assert.Equal(KeysOf(ThemeDictionary("Default")), KeysOf(contrast));
        Assert.DoesNotContain(contrast.Descendants(), e => e.Name.LocalName is "LinearGradientBrush" or "GradientStop");

        Assert.Equal("SystemColorWindowColor", SystemColourOf(contrast, "PillFaceBrush"));
        Assert.Equal("Transparent", (string?)Resource("HighContrast", "PillSheenBrush").Attribute("Color"));
        foreach (var key in new[] { "PillListeningEdgeBrush", "PillNeutralEdgeBrush", "PillErrorEdgeBrush", "PillTextBrush",
                     "PillSecondaryTextBrush", "PillSuccessBrush", "PillCautionBrush", "PillErrorBrush" })
        {
            Assert.Equal("SystemColorWindowTextColor", SystemColourOf(contrast, key));
        }

        Assert.Equal("SystemColorHighlightColor", SystemColourOf(contrast, "PillLevelBrush"));
        Assert.Equal("SystemColorHighlightColor", SystemColourOf(contrast, "PillDotBrush"));
        Assert.Equal("2", Resource("HighContrast", "PillListeningEdgeThickness").Value);
        Assert.Equal("2", Resource("HighContrast", "PillQuietEdgeThickness").Value);
    }

    [Fact]
    public void Every_theme_resource_the_pill_names_is_defined_in_every_theme()
    {
        var window = File.ReadAllText(OverlayFile("OverlayWindow.xaml"));
        var used = Regex.Matches(window, @"\{ThemeResource (?<key>\w+)\}").Select(m => m.Groups["key"].Value).ToHashSet();

        Assert.NotEmpty(used);
        foreach (var theme in new[] { "Default", "Light", "HighContrast" })
        {
            var keys = KeysOf(ThemeDictionary(theme));
            Assert.All(used, key => Assert.Contains(key, keys));
        }

        Assert.DoesNotContain("{StaticResource", window, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_storyboard_target_and_every_element_the_code_names_exists()
    {
        var window = XDocument.Load(OverlayFile("OverlayWindow.xaml"));
        var names = window.Descendants().Select(e => (string?)e.Attribute(Xaml + "Name")).OfType<string>().ToHashSet();

        var targets = window.Descendants()
            .Select(e => (string?)e.Attribute(Presentation + "Storyboard.TargetName") ?? (string?)e.Attribute("Storyboard.TargetName"))
            .OfType<string>()
            .ToArray();
        Assert.NotEmpty(targets);
        Assert.All(targets, target => Assert.Contains(target, names));

        foreach (var key in new[] { "ProcessingStoryboard", "FadeInStoryboard", "FadeOutStoryboard" })
        {
            Assert.Contains(window.Descendants(Presentation + "Storyboard"), s => (string?)s.Attribute(Xaml + "Key") == key);
        }
    }

    [Fact]
    public void The_icons_are_simple_figures_the_path_syntax_reads()
    {
        // A malformed Data string throws only when the window loads.
        var window = XDocument.Load(OverlayFile("OverlayWindow.xaml"));
        var data = window.Descendants(Presentation + "Path").Select(p => (string)p.Attribute("Data")!).ToArray();

        Assert.Equal(3, data.Length);
        const string point = @"-?\d+(?:\.\d+)?,-?\d+(?:\.\d+)?";
        var figure = new Regex($@"^M{point}(?: (?:L{point}|M{point}|Z))*$");
        Assert.All(data, d => Assert.Matches(figure, d));
    }

    [Fact]
    public void The_record_dot_its_pulse_the_meter_and_the_glow_are_gone()
    {
        var xaml = File.ReadAllText(OverlayFile("OverlayWindow.xaml"));
        var code = StripComments(File.ReadAllText(OverlayFile("OverlayWindow.xaml.cs")));
        foreach (var gone in new[] { "PulseStoryboard", "RecDot", "MeterFill", "OuterGlow", "FailedTint", "FailedContent" })
        {
            Assert.DoesNotContain(gone, xaml, StringComparison.Ordinal);
            Assert.DoesNotContain(gone, code, StringComparison.Ordinal);
        }

        // Only the processing dots repeat, and only while processing with Animation effects on.
        var window = XDocument.Load(OverlayFile("OverlayWindow.xaml"));
        var forever = window.Descendants().Where(e => (string?)e.Attribute("RepeatBehavior") == "Forever").ToArray();
        Assert.Equal(3, forever.Length);
        Assert.All(forever, e => Assert.Equal("ProcessingStoryboard", (string?)e.Parent!.Attribute(Xaml + "Key")));
    }

    [Fact]
    public void The_level_bars_are_the_icon_s_proportions_and_follow_the_level_by_render_transform()
    {
        var window = XDocument.Load(OverlayFile("OverlayWindow.xaml"));
        var bars = window.Descendants(Presentation + "ScaleTransform").ToArray();
        Assert.Equal(["Bar1Scale", "Bar2Scale", "Bar3Scale", "Bar4Scale", "Bar5Scale"], bars.Select(b => (string?)b.Attribute(Xaml + "Name")));
        foreach (var bar in bars)
        {
            Assert.Equal("0.25", (string?)bar.Attribute("ScaleY")); // the 4 DIP floor of a 16 DIP bar
            var rectangle = bar.Parent!.Parent!;
            Assert.Equal("Rectangle", rectangle.Name.LocalName);
            Assert.Equal("3", (string?)rectangle.Attribute("Width"));
            Assert.Equal("16", (string?)rectangle.Attribute("Height"));
            Assert.Equal("0.5,0.5", (string?)rectangle.Attribute("RenderTransformOrigin"));
            Assert.Equal("{ThemeResource PillLevelBrush}", (string?)rectangle.Attribute("Fill"));
        }

        var code = StripComments(File.ReadAllText(OverlayFile("OverlayWindow.xaml.cs")));
        Assert.Contains("BarProportions = [0.26, 0.56, 1.0, 0.56, 0.26];", code, StringComparison.Ordinal);
        Assert.Contains("BarFloor = 4.0 / 16.0;", code, StringComparison.Ordinal);
        var setBars = Body(code, "private void SetBars(double level)");
        Assert.Contains(".ScaleY = Math.Max(BarFloor, BarProportions[i] * level);", setBars, StringComparison.Ordinal);
        Assert.DoesNotContain(".Width =", code, StringComparison.Ordinal);
        Assert.DoesNotContain(".Height =", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Motion_is_read_at_each_show_and_gates_the_dots_and_the_fades()
    {
        var code = StripComments(File.ReadAllText(OverlayFile("OverlayWindow.xaml.cs")));
        var read = Body(code, "private void ReadDisplaySettings()");
        Assert.Contains("_animate = _uiSettings.AnimationsEnabled;", read, StringComparison.Ordinal);
        Assert.Contains("_contrast = _accessibility.HighContrast;", read, StringComparison.Ordinal);

        var show = Body(code, "public void ShowState(OverlayState state)");
        Assert.Contains("ReadDisplaySettings();", show, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"if \(appearing && _animate\)\s*\{\s*StartStoryboard\(FadeInStoryboardKey\);"), show);
        Assert.Matches(new Regex(@"if \(state == OverlayState\.Processing && _animate\)\s*\{\s*StartStoryboard\(ProcessingStoryboardKey\);"), show);

        var hide = Body(code, "private void HidePill(OverlayState previous)");
        Assert.Matches(new Regex(@"if \(_animate && _appWindow\.IsVisible"), hide);
        Assert.Contains("StopStoryboard(ProcessingStoryboardKey);", hide, StringComparison.Ordinal);
        Assert.Contains("_outcomeTimer?.Stop();", hide, StringComparison.Ordinal);

        // Every storyboard start is one of these three: nothing else moves.
        var starts = Regex.Matches(code, @"StartStoryboard\((?<key>\w+)\)").Select(m => m.Groups["key"].Value).Where(k => k != "string").ToHashSet();
        Assert.Equal(new HashSet<string> { "FadeInStoryboardKey", "FadeOutStoryboardKey", "ProcessingStoryboardKey" }, starts);
    }

    [Fact]
    public void The_overlay_holds_and_fades_for_as_long_as_PillTiming_says()
    {
        var code = StripComments(File.ReadAllText(OverlayFile("OverlayWindow.xaml.cs")));
        Assert.Equal(PillTiming.TypedHold, HoldIn(code, "TypedHold"));
        Assert.Equal(PillTiming.NoticeHold, HoldIn(code, "NoticeHold"));
        Assert.Equal(PillTiming.RecordingWarningHold, HoldIn(code, "RecordingWarningHold"));

        var window = XDocument.Load(OverlayFile("OverlayWindow.xaml"));
        Assert.Equal(PillTiming.FadeIn, DurationOf(window, "FadeInStoryboard"));
        Assert.Equal(PillTiming.FadeOut, DurationOf(window, "FadeOutStoryboard"));
    }

    private static TimeSpan HoldIn(string code, string name)
    {
        var match = Regex.Match(code, $@"TimeSpan {name} = TimeSpan\.FromMilliseconds\((?<ms>\d+)\);");
        Assert.True(match.Success, $"{name} was not found in the overlay.");
        return TimeSpan.FromMilliseconds(int.Parse(match.Groups["ms"].Value, CultureInfo.InvariantCulture));
    }

    private static TimeSpan DurationOf(XDocument window, string storyboard) =>
        TimeSpan.Parse(
            (string)window.Descendants(Presentation + "Storyboard")
                .Single(s => (string?)s.Attribute(Xaml + "Key") == storyboard)
                .Elements(Presentation + "DoubleAnimation").Single()
                .Attribute("Duration")!,
            CultureInfo.InvariantCulture);

    private static string SystemColourOf(XElement dictionary, string key)
    {
        var brush = dictionary.Elements().Single(e => (string?)e.Attribute(Xaml + "Key") == key);
        Assert.Equal("SolidColorBrush", brush.Name.LocalName);
        var match = Regex.Match((string)brush.Attribute("Color")!, @"^\{ThemeResource (?<key>SystemColor\w+Color)\}$");
        Assert.True(match.Success, $"{key} does not draw a system colour in a contrast theme.");
        return match.Groups["key"].Value;
    }

    private static XElement Resource(string theme, string key) =>
        ThemeDictionary(theme).Elements().Single(e => (string?)e.Attribute(Xaml + "Key") == key);

    private static string[] KeysOf(XElement dictionary) =>
        [.. dictionary.Elements().Select(e => (string)e.Attribute(Xaml + "Key")!).Order(StringComparer.Ordinal)];

    private static XElement ThemeDictionary(string theme)
    {
        var app = XDocument.Load(OverlayFile("App.xaml"));
        return app.Descendants(Presentation + "ResourceDictionary.ThemeDictionaries").Single()
            .Elements(Presentation + "ResourceDictionary")
            .Single(d => (string?)d.Attribute(Xaml + "Key") == theme);
    }

    // The text of a member from its signature to its matching closing brace.
    private static string Body(string code, string signature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' was not found in the overlay.");
        var open = code.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < code.Length; i++)
        {
            depth += code[i] == '{' ? 1 : code[i] == '}' ? -1 : 0;
            if (depth == 0)
            {
                return code[start..(i + 1)];
            }
        }

        throw new InvalidOperationException($"'{signature}' has no closing brace.");
    }

    private static string Hex(SrgbColor color) => color.ToString();

    private static string StripComments(string source) => Regex.Replace(source, @"//[^\n]*", string.Empty);

    private static IEnumerable<string> OverlayFiles(string pattern) =>
        Directory.EnumerateFiles(OverlaySourceFolder(), pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string OverlayFile(string name) => Path.Combine(OverlaySourceFolder(), name);

    private static string OverlaySourceFolder()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return Path.Combine(root.FullName, "src", "Scribe.Overlay");
    }
}
