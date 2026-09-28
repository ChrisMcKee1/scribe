using System.Text.RegularExpressions;

namespace Scribe.Core.Tests;

/// <summary>
/// The regular expression analyzer policy (combined.md row 16): SYSLIB1045 (a static pattern is source-generated) and
/// CA1875 (count matches with Regex.Count) are warnings for the product code under src, and only there.
/// </summary>
public sealed partial class AnalyzerPolicyTests
{
    private static readonly string[] Rules = ["SYSLIB1045", "CA1875"];

    [Fact]
    public void Regex_analyzer_policy_covers_src_only()
    {
        var root = RepositoryRoot();

        // src's own .editorconfig raises both rules for C# files, without claiming to be the root, so the repository's
        // other settings still apply there.
        var src = File.ReadAllLines(Path.Combine(root, "src", ".editorconfig"));
        Assert.DoesNotContain(src, line => line.Trim().StartsWith("root", StringComparison.OrdinalIgnoreCase));
        var csSection = SectionOf(src, "[*.cs]");
        foreach (var rule in Rules)
        {
            Assert.Contains($"dotnet_diagnostic.{rule}.severity = warning", csSection);
        }

        // No other .editorconfig, and no project or props file, sets either rule: tests and tools keep the defaults, and
        // nothing turns the rules off again for part of src.
        var others = ConfigurationFiles(root)
            .Where(path => !string.Equals(path, Path.Combine(root, "src", ".editorconfig"), StringComparison.OrdinalIgnoreCase));
        foreach (var path in others)
        {
            var text = File.ReadAllText(path);
            foreach (var rule in Rules)
            {
                Assert.False(text.Contains(rule, StringComparison.OrdinalIgnoreCase), $"{Path.GetRelativePath(root, path)} mentions {rule}.");
            }
        }
    }

    [Fact]
    public void No_product_code_counts_matches_through_a_match_collection()
    {
        // A backstop for CA1875 that runs without an analyzer build: the product code never takes .Count of Matches(...).
        var root = RepositoryRoot();
        var offenders = SourceFiles(Path.Combine(root, "src"), "*.cs")
            .SelectMany(path => File.ReadLines(path).Select((line, index) => (path, index, line)))
            .Where(item => MatchesCount().IsMatch(item.line) && !item.line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .Select(item => $"{Path.GetRelativePath(root, item.path)}:{item.index + 1}")
            .ToList();
        Assert.Empty(offenders);
    }

    private static List<string> SectionOf(IReadOnlyList<string> lines, string header)
    {
        var section = new List<string>();
        var inside = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inside = string.Equals(line, header, StringComparison.Ordinal);
                continue;
            }

            if (inside && line.Length > 0 && !line.StartsWith('#'))
            {
                section.Add(line);
            }
        }

        return section;
    }

    // Every analyzer configuration file and every project or props file in the repository, build output skipped.
    private static IEnumerable<string> ConfigurationFiles(string root) =>
        SourceFiles(root, "*").Where(path =>
            Path.GetFileName(path) is ".editorconfig" or ".globalconfig" ||
            path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".props", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".targets", StringComparison.OrdinalIgnoreCase));

    // The files under a folder, never descending into build output, tool state or the downloaded models.
    private static IEnumerable<string> SourceFiles(string folder, string pattern)
    {
        var pending = new Stack<string>();
        pending.Push(folder);
        while (pending.TryPop(out var current))
        {
            foreach (var file in Directory.EnumerateFiles(current, pattern))
            {
                yield return file;
            }

            foreach (var child in Directory.EnumerateDirectories(current))
            {
                if (Path.GetFileName(child) is not ("bin" or "obj" or ".git" or ".vs" or "node_modules" or "releases" or "publish" or "TestResults" or "models"))
                {
                    pending.Push(child);
                }
            }
        }
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

    [GeneratedRegex(@"\.Matches\([^;]*\)\s*\.Count\b")]
    private static partial Regex MatchesCount();
}
