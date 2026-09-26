using System.Text.RegularExpressions;

namespace Scribe.Core.Tests;

public sealed partial class XamlFontSizeSourceTests
{
    [Fact]
    public void Scribe_app_xaml_has_no_inline_text_font_sizes_or_literal_font_size_setters()
    {
        var root = FindRepositoryRoot();
        var files = Directory.EnumerateFiles(Path.Combine(root, "src", "Scribe.App"), "*.xaml", SearchOption.AllDirectories);
        var violations = new List<string>();
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if ((InlineFontSize().IsMatch(lines[i]) && !AllowedInlineIconSize(lines[i])) || LiteralFontSizeSetter().IsMatch(lines[i]))
                {
                    violations.Add($"{Path.GetRelativePath(root, file)}:{i + 1}:{lines[i].Trim()}");
                }
            }
        }

        Assert.Empty(violations);
    }

    private static bool AllowedInlineIconSize(string line) =>
        line.Contains("<ui:SymbolIcon", StringComparison.Ordinal);

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

    [GeneratedRegex("FontSize=\\\"[0-9]")]
    private static partial Regex InlineFontSize();

    // A style setter with a number stays at 100% whatever Windows' text size: a merge once put the literal values back in
    // every Settings text style while every inline size had already moved to the ScribeFont ramp.
    [GeneratedRegex("<Setter\\s+Property=\\\"(TextElement\\.)?FontSize\\\"\\s+Value=\\\"[0-9]")]
    private static partial Regex LiteralFontSizeSetter();
}
