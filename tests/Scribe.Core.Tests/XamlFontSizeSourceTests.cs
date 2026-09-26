using System.Text.RegularExpressions;

namespace Scribe.Core.Tests;

public sealed partial class XamlFontSizeSourceTests
{
    [Fact]
    public void Scribe_app_xaml_has_no_inline_text_font_sizes()
    {
        var root = FindRepositoryRoot();
        var files = Directory.EnumerateFiles(Path.Combine(root, "src", "Scribe.App"), "*.xaml", SearchOption.AllDirectories);
        var violations = new List<string>();
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (InlineFontSize().IsMatch(lines[i]) && !AllowedInlineIconSize(lines[i]))
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
}
