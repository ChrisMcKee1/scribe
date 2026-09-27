using System.Text.RegularExpressions;

namespace Scribe.Core.Tests;

/// <summary>
/// Words in the app's code-behind that the 0.5.0 redesign renamed on the page, pinned by source because the app has no
/// tests of its own. Each of these once kept a name the page no longer showed.
/// </summary>
public sealed class SettingsCopySourceTests
{
    private static readonly string Root = FindRoot();

    [Fact]
    public void Learn_from_history_puts_back_the_tooltip_the_page_gave_it()
    {
        // The tooltip is cleared while a run is busy. Putting back a literal restored the 0.4.4 wording after the first run.
        var code = Read("SettingsWindow.YourWords.cs");
        var run = code[code.IndexOf("private async Task RunDictionarySuggestionAsync(", StringComparison.Ordinal)..];
        run = run[..run.IndexOf("\n    }", StringComparison.Ordinal)];

        Assert.Contains("var toolTip = DictionarySuggestButton.ToolTip;", run, StringComparison.Ordinal);
        Assert.Contains("DictionarySuggestButton.ToolTip = toolTip;", run, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"DictionarySuggestButton\.ToolTip\s*=\s*""", code);
    }

    [Fact]
    public void The_clean_up_window_calls_them_word_packs()
    {
        var literals = UserFacingLiterals(Read("DictionaryCleanupWindow.xaml.cs"));

        Assert.Contains(literals, text => text.Contains("word pack", StringComparison.Ordinal));
        Assert.DoesNotContain(literals, text => Regex.IsMatch(text, @"\b[Ll]ibrar(y|ies)\b"));
    }

    [Fact]
    public void Restoring_the_ai_instructions_uses_the_names_on_the_page()
    {
        // The page shows "Detailed instructions" and "Short instructions"; the confirmations said frontier and local prompt.
        var literals = UserFacingLiterals(Read("SettingsWindow.xaml.cs"));

        Assert.Contains("Restore Scribe's detailed instructions?", literals);
        Assert.Contains("Restore Scribe's short instructions?", literals);
        Assert.DoesNotContain(literals, text => Regex.IsMatch(text, @"\b(frontier|local) prompt\b", RegexOptions.IgnoreCase));
    }

    private static string Read(string file) =>
        File.ReadAllText(Path.Combine(Root, "src", "Scribe.App", "Settings", file));

    // String literals outside comments and log calls, which keep their internal names.
    private static List<string> UserFacingLiterals(string code) =>
        code.Split('\n')
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal) && !line.Contains("_log.Log", StringComparison.Ordinal))
            .SelectMany(line => Regex.Matches(line, @"""((?:[^""\\]|\\.)*)""").Select(match => match.Groups[1].Value))
            .ToList();

    private static string FindRoot()
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
