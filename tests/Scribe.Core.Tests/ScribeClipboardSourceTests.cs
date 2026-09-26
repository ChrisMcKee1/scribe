namespace Scribe.Core.Tests;

public sealed class ScribeClipboardSourceTests
{
    [Fact]
    public void App_clipboard_writes_go_through_ScribeClipboard()
    {
        var root = RepositoryRoot();
        var appRoot = Path.Combine(root, "src", "Scribe.App");
        var allowed = Path.Combine("src", "Scribe.App", "Infrastructure", "ScribeClipboard.cs");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(appRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(file))
            {
                continue;
            }

            var relative = Path.GetRelativePath(root, file);
            if (relative.Equals(allowed, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var source = StripCommentsAndStrings(File.ReadAllText(file));
            if (FindClipboardWrites(source).Count > 0)
            {
                offenders.Add(relative);
            }
        }

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    [Theory]
    [InlineData("Clipboard.SetText\r\n(\"x\");")]
    [InlineData("Clipboard.SetDataObject (data);")]
    [InlineData("using Clip = System.Windows.Clipboard; class C { void M() { Clip.SetImage(image); } }")]
    [InlineData("var url = \"https://example.test/Clipboard.SetText(\"; System.Windows.Clipboard.SetFileDropList(files);")]
    [InlineData("using static System.Windows.Clipboard; class C { void M() { SetText(\"x\"); } }")]
    [InlineData("var c = '\"'; Clipboard.SetAudio(stream);")]
    [InlineData("var s = $$$\"\"\"Clipboard.SetText(\"x\")\"\"\"; Clipboard.SetData(\"f\", data);")]
    public void Detector_flags_clipboard_bypasses(string source)
    {
        Assert.NotEmpty(FindClipboardWrites(StripCommentsAndStrings(source)));
    }

    [Fact]
    public void Detector_allows_helper_itself()
    {
        var root = RepositoryRoot();
        var helper = Path.Combine(root, "src", "Scribe.App", "Infrastructure", "ScribeClipboard.cs");
        Assert.NotEmpty(FindClipboardWrites(StripCommentsAndStrings(File.ReadAllText(helper))));
    }

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

    private static bool IsBuildOutput(string file)
    {
        var separator = Path.DirectorySeparatorChar;
        return file.Contains($"{separator}obj{separator}", StringComparison.OrdinalIgnoreCase) ||
            file.Contains($"{separator}bin{separator}", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> FindClipboardWrites(string source)
    {
        var aliases = System.Text.RegularExpressions.Regex.Matches(
                source,
                @"using\s+(?<name>[A-Za-z_]\w*)\s*=\s*[^;]*\bClipboard\s*;",
                System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(match => match.Groups["name"].Value)
            .ToList();
        var usingStatic = System.Text.RegularExpressions.Regex.IsMatch(
            source,
            @"using\s+static\s+[^;]*\bClipboard\s*;",
            System.Text.RegularExpressions.RegexOptions.Multiline);
        var targets = new[] { "Clipboard", "System\\s*\\.\\s*Windows\\s*\\.\\s*Clipboard" }
            .Concat(aliases.Select(System.Text.RegularExpressions.Regex.Escape));
        var pattern = $@"(?<![A-Za-z0-9_])(?:{string.Join("|", targets)})\s*\.\s*Set\w+\s*\(";
        var matches = System.Text.RegularExpressions.Regex.Matches(source, pattern).Select(match => match.Value).ToList();
        if (usingStatic)
        {
            matches.AddRange(System.Text.RegularExpressions.Regex.Matches(source, @"(?<![A-Za-z0-9_\.])Set\w+\s*\(").Select(match => match.Value));
        }

        return matches;
    }

    private static string StripCommentsAndStrings(string source)
    {
        var result = new System.Text.StringBuilder(source.Length);
        for (var i = 0; i < source.Length; i++)
        {
            if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                i += 2;
                while (i < source.Length && source[i] != '\r' && source[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && (source[i] != '*' || source[i + 1] != '/'))
                {
                    i++;
                }

                i++;
                continue;
            }

            if (source[i] == '@' && i + 1 < source.Length && source[i + 1] == '"')
            {
                result.Append("\"\"");
                i += 2;
                while (i < source.Length)
                {
                    if (source[i] == '"' && i + 1 < source.Length && source[i + 1] == '"')
                    {
                        i += 2;
                        continue;
                    }

                    if (source[i] == '"')
                    {
                        break;
                    }

                    i++;
                }

                continue;
            }

            if (source[i] == '$' && i + 1 < source.Length && source[i + 1] == '"')
            {
                var start = i;
                while (i < source.Length && source[i] == '$')
                {
                    i++;
                }

                var quoteCount = CountQuotes(source, i);
                if (quoteCount >= 3)
                {
                    result.Append("\"\"");
                    i += quoteCount;
                    SkipRawString(source, ref i, quoteCount);
                    continue;
                }

                i = start + 1;
                result.Append("\"\"");
                SkipRegularString(source, ref i);
                continue;
            }

            if ((source[i] == '$' || source[i] == '@') && i + 1 < source.Length && source[i + 1] == '$')
            {
                while (i < source.Length && (source[i] == '$' || source[i] == '@'))
                {
                    i++;
                }
            }

            if (source[i] == '"')
            {
                var quoteCount = CountQuotes(source, i);
                if (quoteCount >= 3)
                {
                    result.Append("\"\"");
                    i += quoteCount;
                    while (i < source.Length)
                    {
                        var closing = CountQuotes(source, i);
                        if (closing >= quoteCount)
                        {
                            i += closing - 1;
                            break;
                        }

                        i++;
                    }

                    continue;
                }

                result.Append("\"\"");
                SkipRegularString(source, ref i);
                continue;
            }

            if (source[i] == '\'')
            {
                result.Append("''");
                SkipCharLiteral(source, ref i);
                continue;
            }

            result.Append(source[i]);
        }

        return result.ToString();
    }

    private static void SkipRegularString(string source, ref int i)
    {
        for (i++; i < source.Length; i++)
        {
            if (source[i] == '\\')
            {
                i++;
                continue;
            }

            if (source[i] == '"')
            {
                return;
            }
        }
    }

    private static void SkipRawString(string source, ref int i, int quoteCount)
    {
        while (i < source.Length)
        {
            var closing = CountQuotes(source, i);
            if (closing >= quoteCount)
            {
                i += closing - 1;
                return;
            }

            i++;
        }
    }

    private static void SkipCharLiteral(string source, ref int i)
    {
        for (i++; i < source.Length; i++)
        {
            if (source[i] == '\\')
            {
                i++;
                continue;
            }

            if (source[i] == '\'')
            {
                return;
            }
        }
    }

    private static int CountQuotes(string source, int index)
    {
        var count = 0;
        while (index + count < source.Length && source[index + count] == '"')
        {
            count++;
        }

        return count;
    }
}
