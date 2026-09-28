using System.Text.RegularExpressions;

namespace Scribe.Core.Tests;

/// <summary>
/// Colours in the app follow the theme while a window stays open. A brush read once in code, with FindResource or
/// TryFindResource, keeps the theme it was read in, and so does a brush a row or view model holds: in 0.5.0 the Word packs
/// list read its text brush into every row, so once Windows switched between light and dark the list drew dark text on
/// the dark theme's rows, or light text on the light theme's, until it was rebuilt. SystemColors' Win32 brushes ignore the
/// app's dark theme altogether (the App profiles menu drew its descriptions in the Win32 grey). So app code takes theme
/// colours by resource reference (DynamicResource in XAML, SetResourceReference in code), and these scans keep it so.
/// </summary>
public sealed partial class ThemeColorSourceTests
{
    // Files that may hold brushes, with why. Paths are relative to src\Scribe.App.
    private static readonly IReadOnlyDictionary<string, string> AllowedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [@"Infrastructure\AccentContrastResources.cs"] =
            "Plans and writes the accent colours for the theme it has just read, again after every theme or accent change, " +
            "and falls back to WPF's own system brushes in a contrast theme.",
    };

    [Fact]
    public void App_code_never_reads_a_theme_brush_into_a_property_or_keeps_one()
    {
        var violations = new List<string>();
        foreach (var (path, code) in AppCode())
        {
            if (AllowedFiles.ContainsKey(path))
            {
                continue;
            }

            foreach (var (pattern, what) in CodePatterns)
            {
                foreach (Match match in pattern.Matches(code))
                {
                    violations.Add($"{path}:{LineOf(code, match.Index)}: {what}: {match.Value.Trim()}");
                }
            }
        }

        Assert.True(violations.Count == 0, "Take theme colours by resource reference instead:\n" + string.Join('\n', violations));
    }

    [Fact]
    public void App_xaml_never_binds_a_colour_to_data_or_reads_a_theme_brush_once()
    {
        var violations = new List<string>();
        foreach (var file in AppFiles("*.xaml"))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (var (pattern, what) in XamlPatterns)
                {
                    if (pattern.IsMatch(lines[i]))
                    {
                        violations.Add($"{Path.GetRelativePath(AppRoot, file)}:{i + 1}: {what}: {lines[i].Trim()}");
                    }
                }
            }
        }

        Assert.True(violations.Count == 0, "Use a DynamicResource, or a trigger on a flag the row exposes:\n" + string.Join('\n', violations));
    }

    [Theory]
    [InlineData("row.TextBrush = TryFindResource(\"TextFillColorPrimaryBrush\") as Brush;")]
    [InlineData("icon.Foreground = TryFindResource(\"SystemFillColorCriticalBrush\") as Brush ?? Brushes.Red;")]
    [InlineData("TryDictationSummaryIcon.Foreground = (Brush)FindResource(brush);")]
    [InlineData("var tint = (Brush)Application.Current.FindResource(\"TextFillColorPrimaryBrush\");")]
    [InlineData("var tint = Resources[\"TextFillColorPrimaryBrush\"] as SolidColorBrush;")]
    [InlineData("var tint = (Brush)Application.Current.Resources[\"TextFillColorPrimaryBrush\"];")]
    [InlineData("Foreground = System.Windows.SystemColors.GrayTextBrush,")]
    [InlineData("private Brush? _textBrush;")]
    [InlineData("public Brush? TextBrush { get; set; }")]
    [InlineData("public SolidColorBrush Tint => _tint;")]
    public void The_code_scan_catches_what_0_5_0_shipped(string line)
    {
        Assert.Contains(CodePatterns, entry => entry.Pattern.IsMatch(StripComments(line)));
    }

    [Theory]
    [InlineData("<TextBlock Text=\"{Binding Name}\" Foreground=\"{Binding TextBrush}\"/>")]
    [InlineData("<TextBlock Foreground = '{Binding TextBrush}'/>")]
    [InlineData("<Setter Property=\"Foreground\" Value=\"{Binding TextBrush}\"/>")]
    [InlineData("<Border Background=\"{StaticResource CardBackgroundFillColorDefaultBrush}\"/>")]
    [InlineData("<TextBlock Foreground=\"{StaticResource TabViewItemForegroundSelected}\"/>")]
    [InlineData("<Setter TargetName=\"Border\" Property=\"Background\" Value=\"{StaticResource TabViewItemHeaderBackgroundSelected}\"/>")]
    public void The_xaml_scan_catches_a_colour_that_cannot_follow_the_theme(string line)
    {
        Assert.Contains(XamlPatterns, entry => entry.Pattern.IsMatch(line));
    }

    [Theory]
    [InlineData("icon.SetResourceReference(ForegroundProperty, \"SystemFillColorCriticalBrush\");")]
    [InlineData("Fill=\"{DynamicResource {x:Static SystemColors.HighlightBrushKey}}\"")]
    [InlineData("Style = (Style)FindResource(\"CardTitle\"),")]
    [InlineData("// SystemColors.GrayTextBrush, which this used, ignores dark mode.")]
    public void The_code_scan_lets_resource_references_styles_and_comments_through(string line)
    {
        Assert.DoesNotContain(CodePatterns, entry => entry.Pattern.IsMatch(StripComments(line)));
    }

    [Theory]
    [InlineData("<Setter Property=\"Foreground\" Value=\"{DynamicResource TextFillColorPrimaryBrush}\"/>")]
    [InlineData("Background=\"{Binding RowBackground, RelativeSource={RelativeSource AncestorType={x:Type DataGrid}}}\"")]
    [InlineData("BorderBrush=\"{TemplateBinding BorderBrush}\"")]
    [InlineData("<DataTrigger Binding=\"{Binding Dimmed}\" Value=\"True\">")]
    public void The_xaml_scan_lets_resource_references_and_template_bindings_through(string line)
    {
        Assert.DoesNotContain(XamlPatterns, entry => entry.Pattern.IsMatch(line));
    }

    [Fact]
    public void Every_allowed_file_exists_and_says_why()
    {
        foreach (var (path, why) in AllowedFiles)
        {
            Assert.True(File.Exists(Path.Combine(AppRoot, path)), $"{path} is allowed but does not exist.");
            Assert.False(string.IsNullOrWhiteSpace(why));
        }
    }

    private static readonly (Regex Pattern, string What)[] CodePatterns =
    [
        (BrushFromFindResource(), "a brush read once with FindResource"),
        (CastBrushFromFindResource(), "a brush read once with FindResource"),
        (BrushFromResourceIndexer(), "a brush read once from a resource dictionary"),
        (CastBrushFromResourceIndexer(), "a brush read once from a resource dictionary"),
        (Win32SystemBrush(), "a Win32 system brush, which ignores the app's theme"),
        (LiteralBrush(), "a fixed brush, which ignores the app's theme"),
        (BrushMember(), "a brush kept in a field or property, which keeps the theme it was read in"),
    ];

    private static readonly (Regex Pattern, string What)[] XamlPatterns =
    [
        (ColourBoundToData(), "a colour bound to data"),
        (ColourSetterBoundToData(), "a colour bound to data"),
        (StaticThemeBrush(), "a theme brush read once with StaticResource"),
        (ColourFromStaticResource(), "a colour read once with StaticResource"),
        (ColourSetterFromStaticResource(), "a colour read once with StaticResource"),
    ];

    private static readonly string AppRoot = Path.Combine(FindRepositoryRoot(), "src", "Scribe.App");

    private static IEnumerable<(string Path, string Code)> AppCode() =>
        AppFiles("*.cs").Select(file => (Path.GetRelativePath(AppRoot, file), StripComments(File.ReadAllText(file))));

    private static IEnumerable<string> AppFiles(string pattern) =>
        Directory.EnumerateFiles(AppRoot, pattern, SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    // Comments may name what the code must not do; line comments and block comments both go, and line breaks stay so a
    // violation's line number is still right.
    private static string StripComments(string code) =>
        BlockComment().Replace(LineComment().Replace(code, string.Empty), match => new string('\n', match.Value.Count(c => c == '\n')));

    private static int LineOf(string code, int index) => code.AsSpan(0, index).Count('\n') + 1;

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

    [GeneratedRegex(@"\b(?:Try)?FindResource\s*\([^;]*?\)\s*as\s+(?:\w+\.)*(?:Solid(?:Color)?)?Brush\b")]
    private static partial Regex BrushFromFindResource();

    [GeneratedRegex(@"\(\s*(?:\w+\.)*(?:Solid(?:Color)?)?Brush\s*\)\s*(?:\w+\s*\.\s*)*(?:Try)?FindResource\s*\(")]
    private static partial Regex CastBrushFromFindResource();

    [GeneratedRegex(@"\bResources\s*\[[^\]]+\]\s*as\s+(?:\w+\.)*(?:Solid(?:Color)?)?Brush\b")]
    private static partial Regex BrushFromResourceIndexer();

    [GeneratedRegex(@"\(\s*(?:\w+\.)*(?:Solid(?:Color)?)?Brush\s*\)\s*(?:\w+\s*\.\s*)*Resources\s*\[")]
    private static partial Regex CastBrushFromResourceIndexer();

    [GeneratedRegex(@"\bSystemColors\.\w+Brush\b")]
    private static partial Regex Win32SystemBrush();

    [GeneratedRegex(@"\bBrushes\.[A-Z]\w*")]
    private static partial Regex LiteralBrush();

    [GeneratedRegex(@"\b(?:public|internal|private|protected)\s+(?:static\s+)?(?:readonly\s+)?(?:\w+\.)*(?:Solid(?:Color)?)?Brush\??\s+\w+\s*(?:\{|;|=)")]
    private static partial Regex BrushMember();

    [GeneratedRegex(@"\b(?:Foreground|Background|BorderBrush|Fill|Stroke|CaretBrush|SelectionBrush)\s*=\s*[""']\{Binding(?![^""']*(?:RelativeSource|ElementName))[^""']*[""']")]
    private static partial Regex ColourBoundToData();

    [GeneratedRegex(@"<Setter\s+(?:TargetName\s*=\s*[""']\w+[""']\s+)?Property\s*=\s*[""'](?:\w+\.)?(?:Foreground|Background|BorderBrush|Fill|Stroke)[""']\s+Value\s*=\s*[""']\{Binding(?![^""']*(?:RelativeSource|ElementName))")]
    private static partial Regex ColourSetterBoundToData();

    [GeneratedRegex(@"\{StaticResource\s+[\w.]*(?:Brush|Color)\}")]
    private static partial Regex StaticThemeBrush();

    // A theme key need not end in Brush or Color (TabViewItemForegroundSelected), so any StaticResource a colour property
    // takes is a snapshot.
    [GeneratedRegex(@"\b(?:Foreground|Background|BorderBrush|Fill|Stroke|CaretBrush|SelectionBrush)\s*=\s*[""']\{StaticResource\b")]
    private static partial Regex ColourFromStaticResource();

    [GeneratedRegex(@"<Setter\s+(?:TargetName\s*=\s*[""']\w+[""']\s+)?Property\s*=\s*[""'](?:\w+\.)?(?:Foreground|Background|BorderBrush|Fill|Stroke)[""']\s+Value\s*=\s*[""']\{StaticResource\b")]
    private static partial Regex ColourSetterFromStaticResource();
}
