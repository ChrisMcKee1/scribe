using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Scribe.Core.Overlay;

namespace Scribe.Core.Tests;

/// <summary>
/// The overlay scales the whole pill with Windows text size, as one unit. No test can load the overlay (a WinUI 3 window)
/// and it has no reference to Scribe.Core, so this pins its source: no text on the pill scales by itself, the content keeps
/// its 100% layout inside a Viewbox, and the window's code sizes, anchors and re-reads the scale through
/// <see cref="PillGeometry"/> and <see cref="PillTextScale"/>, which it compiles itself.
/// </summary>
public sealed class OverlayTextScaleSourceTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void No_text_on_the_pill_scales_by_itself()
    {
        // WinUI scales a TextBlock's text with Windows text size unless told not to; inside the scaled content that would
        // scale it twice and trim every line. Each TextBlock says so itself, and nothing turns it back on.
        var blocks = 0;
        foreach (var file in OverlayFiles("*.xaml"))
        {
            var document = XDocument.Load(file);
            foreach (var block in document.Descendants(Presentation + "TextBlock"))
            {
                blocks++;
                Assert.Equal("False", (string?)block.Attribute("IsTextScaleFactorEnabled"));
            }

            Assert.DoesNotContain(document.Descendants(), e => e.Name.LocalName is "RichTextBlock" or "TextBox" or "RichEditBox");
            Assert.DoesNotContain(
                document.Descendants(Presentation + "Setter"), s => (string?)s.Attribute("Property") == "IsTextScaleFactorEnabled");
        }

        Assert.True(blocks >= 5, "The pill's text blocks were not found.");
        foreach (var file in OverlayFiles("*.cs"))
        {
            var code = StripComments(File.ReadAllText(file));
            Assert.DoesNotContain("IsTextScaleFactorEnabled", code, StringComparison.Ordinal);
            Assert.DoesNotContain("TextBlock(", code, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_content_keeps_its_100_percent_layout_and_is_drawn_scaled_to_fill_the_window()
    {
        var window = XDocument.Load(OverlayFile("OverlayWindow.xaml"));
        var content = Assert.Single(window.Root!.Elements());
        Assert.Equal("Viewbox", content.Name.LocalName);
        Assert.Equal("Uniform", (string?)content.Attribute("Stretch"));
        Assert.Single(window.Descendants(Presentation + "Viewbox"));

        var root = Assert.Single(content.Elements());
        Assert.Equal("RootGrid", (string?)root.Attribute(Xaml + "Name"));
        Assert.Equal(PillGeometry.LogicalWidth, double.Parse((string)root.Attribute("Width")!, CultureInfo.InvariantCulture));
        Assert.Equal(PillGeometry.LogicalHeight, double.Parse((string)root.Attribute("Height")!, CultureInfo.InvariantCulture));

        var pill = root.Descendants().Single(e => (string?)e.Attribute(Xaml + "Name") == "Pill");
        Assert.Equal("27", (string?)pill.Attribute("Margin"));
    }

    [Fact]
    public void The_window_is_sized_and_anchored_by_PillGeometry_at_the_scale_PillTextScale_holds()
    {
        var code = WindowCode();
        Assert.DoesNotMatch(new Regex(@"const double Logical(Width|Height)"), code); // the 100% size lives in PillGeometry
        Assert.DoesNotContain("_appWindow.Resize(", code, StringComparison.Ordinal);
        Assert.DoesNotContain("_appWindow.Move(", code, StringComparison.Ordinal);

        var size = Body(code, "private void SizeAndPosition()");
        Assert.Contains("var textScale = _textScale.Current;", size, StringComparison.Ordinal);
        Assert.Matches(
            new Regex(
                @"var place = PillGeometry\.Place\(\s*\(PillAnchor\)\(int\)_anchor,\s*new PillRect\(work\.X, work\.Y, work\.Width, work\.Height\),\s*textScale,\s*scale\);"),
            size);
        Assert.Contains("_appWindow.MoveAndResize(new RectInt32(place.X, place.Y, place.Width, place.Height));", size, StringComparison.Ordinal);
        Assert.Contains("size={place.Width}x{place.Height}", size, StringComparison.Ordinal);
        Assert.Contains("clamped={place.Clamped}", size, StringComparison.Ordinal);
    }

    [Fact]
    public void The_scale_is_read_at_each_show_and_again_on_the_UI_thread_when_Windows_changes_it()
    {
        var code = WindowCode();
        var read = Body(code, "private double ReadTextScale()");
        Assert.Contains("return PillGeometry.TextScale(_uiSettings.TextScaleFactor);", read, StringComparison.Ordinal);
        Assert.Contains("return PillGeometry.MinTextScale;", read, StringComparison.Ordinal); // unreadable: the 100% size
        Assert.Contains("_textScaleRead = ReadTextScale();", Body(code, "private void ReadDisplaySettings()"), StringComparison.Ordinal);

        Assert.Contains(
            "settings.TextScaleFactorChanged += OnTextScaleFactorChanged;",
            Body(code, "private UISettings CreateUiSettings()"),
            StringComparison.Ordinal);
        Assert.DoesNotContain("new UISettings()", code.Replace("var settings = new UISettings();", string.Empty), StringComparison.Ordinal);

        // Windows raises the change off the UI thread; the handler reads the scale again there, and resizes and re-anchors
        // when PillTextScale says so.
        var changed = Body(code, "private void OnTextScaleFactorChanged(UISettings sender, object args)");
        Assert.Contains("=> RunOnUi(() =>", changed, StringComparison.Ordinal);
        Assert.Matches(
            new Regex(@"var textScale = ReadTextScale\(\);\s*var resize = _textScale\.OnChanged\(textScale, _appWindow\.IsVisible, _fadingIn, _fadingOut\);"),
            changed);
        Assert.Matches(new Regex(@"if \(resize\)\s*\{\s*SizeAndPosition\(\);"), changed);
        Assert.Contains("_uiSettings.TextScaleFactorChanged -= OnTextScaleFactorChanged;", Body(code, "private void OnClosed("), StringComparison.Ordinal);
    }

    [Fact]
    public void A_new_scale_never_resizes_the_pill_mid_fade()
    {
        var code = WindowCode();
        var show = Body(code, "public void ShowState(OverlayState state)");
        var onShow = show.IndexOf("_textScale.OnShow(_textScaleRead, appearing, _fadingIn);", StringComparison.Ordinal);
        Assert.True(onShow >= 0, "ShowState does not hand the scale it read to PillTextScale.");
        Assert.True(show.IndexOf("ReadDisplaySettings();", StringComparison.Ordinal) < onShow, "It is read first.");
        Assert.True(onShow < show.IndexOf("StartStoryboard(FadeInStoryboardKey);", StringComparison.Ordinal), "Before the fade in starts.");
        Assert.True(onShow < show.IndexOf("EnsureShown();", StringComparison.Ordinal), "Before the window is sized and shown.");
        Assert.Matches(new Regex(@"StartStoryboard\(FadeInStoryboardKey\);[^\n]*\n\s*_fadingIn = true;"), show);

        var hide = Body(code, "private void HidePill(OverlayState previous)");
        Assert.Matches(new Regex(@"StopStoryboard\(FadeInStoryboardKey\);\s*_fadingIn = false;\s*_textScale\.OnHidden\(\);"), hide);

        Assert.Contains("fadeIn.Completed += OnFadeInCompleted;", code, StringComparison.Ordinal);
        var faded = Body(code, "private void OnFadeInCompleted(object? sender, object e)");
        Assert.Matches(
            new Regex(@"_fadingIn = false;\s*if \(_textScale\.OnFadeInCompleted\(_appWindow\.IsVisible, _fadingOut\)\)\s*\{\s*SizeAndPosition\(\);"),
            faded);
    }

    [Fact]
    public void The_logs_carry_the_text_scale_as_a_number()
    {
        var code = WindowCode();
        Assert.Contains("textScale={textScale:0.##}", Body(code, "private void SizeAndPosition()"), StringComparison.Ordinal);
        Assert.Contains("textScale={_textScale.Current:0.##}", Body(code, "private void LogState(string phase)"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_overlay_compiles_the_geometry_itself_and_still_has_no_reference_to_Core()
    {
        var project = XDocument.Load(OverlayFile("Scribe.Overlay.csproj"));
        var compiled = project.Descendants("Compile").Select(c => (string?)c.Attribute("Include")).ToArray();
        Assert.Contains(@"..\Scribe.Core\Overlay\PillGeometry.cs", compiled);
        Assert.Contains(@"..\Scribe.Core\Overlay\PillTextScale.cs", compiled);
        Assert.Empty(project.Descendants("ProjectReference"));

        // Both files must build in the overlay on its own implicit usings, with nothing else from Scribe.Core.
        foreach (var name in new[] { "PillGeometry.cs", "PillTextScale.cs" })
        {
            var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Scribe.Core", "Overlay", name));
            Assert.DoesNotMatch(new Regex(@"^using ", RegexOptions.Multiline), source);
        }
    }

    private static string WindowCode() =>
        StripComments(File.ReadAllText(OverlayFile("OverlayWindow.xaml.cs"))).ReplaceLineEndings("\n");

    // The text of a member from its signature to the brace that closes its first block.
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

    private static string StripComments(string source) => Regex.Replace(source, @"//[^\n]*", string.Empty);

    private static IEnumerable<string> OverlayFiles(string pattern) =>
        Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "src", "Scribe.Overlay"), pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string OverlayFile(string name) => Path.Combine(RepositoryRoot(), "src", "Scribe.Overlay", name);

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
