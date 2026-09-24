using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Cleanup;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests.Libraries;

/// <summary>
/// Renders what the libraries decide, over <see cref="LibraryFixture"/>, as plain text: the order the service returns
/// libraries in, the winners dictation applies, the AI cleanup glossary, the Dictionary page's library badges, the
/// Save prompt about entries a library already covers, and finished text from the real post-processor.
/// </summary>
/// <remarks>
/// The badge and prompt inputs are passed in because they come from the Settings window's own code in 0.4.3: the
/// golden file was rendered from verbatim copies of those two loops, and the test renders it again from the Core calls
/// that replaced them, so any difference between the two is a behaviour change.
/// </remarks>
internal static class LibraryGolden
{
    /// <summary>The spoken forms the enabled libraries cover, each with the entry that wins and its library's name.</summary>
    public delegate IReadOnlyDictionary<string, (DictionaryEntry Entry, string LibraryName)> CoverageMap(
        IReadOnlyList<DictionaryLibrary> libraries, IReadOnlyCollection<string> enabledIds);

    /// <summary>The report the Save prompt is built from, for the personal dictionary against the enabled libraries.</summary>
    public delegate DictionaryOverlapReport OverlapPrompt(
        IReadOnlyList<DictionaryEntry> personal, IReadOnlyList<DictionaryLibrary> libraries, IReadOnlyCollection<string> enabledIds);

    public const string UpdateVariable = "SCRIBE_WRITE_LIBRARY_GOLDEN";

    public static string GoldenPath =>
        Path.Combine(RepositoryRoot(), "tests", "fixtures", "libraries", "composition-golden.txt");

    private const string Header =
        "# What the dictionary libraries decide for the fixture in tests/Scribe.Core.Tests/Libraries/LibraryFixture.cs.\n" +
        "# Rendered by LibraryGolden. First captured from d42d683, the 0.4.3 behaviour (built-in libraries composed in\n" +
        "# category then name order, custom files in file-name order), before the Libraries list became alphabetical, so a\n" +
        "# difference here is a change to what dictation writes, the Dictionary page's badges or the Save prompt.\n" +
        "# Regenerate only for a change you mean: set SCRIBE_WRITE_LIBRARY_GOLDEN=1, run LibraryCompositionGoldenTests, and\n" +
        "# review the diff.\n";

    public static string Render(LibraryFixture fixture, CoverageMap coverage, OverlapPrompt prompt)
    {
        var text = new StringBuilder(Header);
        var libraries = fixture.Service.GetLibraries();
        Section(text, "libraries, in the order GetLibraries returns them");
        foreach (var library in libraries)
        {
            Line(text, $"{library.Id} ({(library.BuiltIn ? "built-in" : "custom")}) \"{library.Name}\"");
        }

        foreach (var (name, enabledIds) in LibraryFixture.Scenarios)
        {
            fixture.Enable(enabledIds);
            var enabled = new HashSet<string>(enabledIds, StringComparer.OrdinalIgnoreCase);
            var enabledLibraries = libraries.Where(l => enabled.Contains(l.Id)).ToList();
            var personalEnabled = fixture.Dictionary.GetEnabled();
            var personalAll = fixture.Dictionary.GetAll();
            var libraryEntries = fixture.Service.GetEnabledLibraryEntries();
            var effective = DictionaryLibraryComposer.Merge(personalEnabled, libraryEntries);
            string Source(DictionaryEntry entry) => SourceOf(entry, personalEnabled, enabledLibraries);

            Section(text, $"{name}: enabled libraries in composition order");
            Line(text, string.Join(", ", enabledLibraries.Select(l => l.Id)));

            // The library layer on its own, before the personal dictionary is merged on top: which library wins each
            // spoken form, in the order of the list the glossary and the post-processor walk. Positions are shown only
            // where every rule is the fixture's, so an edit to a shipped CSV never moves this file.
            var numbered = enabledLibraries.All(l => !l.BuiltIn);
            Section(text, $"{name}: library winners for the fixture's spoken forms, in library composition order");
            for (var i = 0; i < libraryEntries.Count; i++)
            {
                if (LibraryFixture.FixtureKeys.Contains(libraryEntries[i].Pattern.Trim()))
                {
                    Line(text, (numbered ? $"#{i + 1} " : string.Empty) + Rule(libraryEntries[i], Source(libraryEntries[i])));
                }
            }

            Section(text, $"{name}: effective winners for the fixture's spoken forms, in effective order");
            foreach (var entry in effective.Where(e => LibraryFixture.FixtureKeys.Contains(e.Pattern.Trim())))
            {
                Line(text, Rule(entry, Source(entry)));
            }

            if (name == "custom only")
            {
                Section(text, $"{name}: every effective rule");
                foreach (var entry in effective)
                {
                    Line(text, Rule(entry, Source(entry)));
                }

                Glossary(text, name, "on-device", CleanupPrompt.BuildGlossary(effective, CleanupPrompt.MaxGlossaryTermsLocal));
                Glossary(text, name, "cloud", CleanupPrompt.BuildGlossary(effective, CleanupPrompt.MaxGlossaryTermsCloud));
                Glossary(text, name, "cut at 6 terms", CleanupPrompt.BuildGlossary(effective, 6));
            }

            if (name is "custom only" or "default install")
            {
                // Which library the on-device glossary's terms come from: the order decides who fills the 80 slots.
                Section(text, $"{name}: sources of the first {CleanupPrompt.MaxGlossaryTermsLocal} effective rules");
                Line(text, RunLengths(effective.Take(CleanupPrompt.MaxGlossaryTermsLocal).Select(Source)));
            }

            Section(text, $"{name}: Dictionary page library badges (what covers each personal entry)");
            var covering = coverage(libraries, enabledIds);
            foreach (var entry in personalAll)
            {
                var key = entry.Pattern.Trim();
                Line(text, covering.TryGetValue(key, out var hit)
                    ? $"{key}: \"{hit.LibraryName}\" writes \"{hit.Entry.Replacement}\"{(hit.Entry.WholeWord ? string.Empty : " (substring)")}"
                    : $"{key}: not covered");
            }

            Section(text, $"{name}: Save prompt report");
            var report = prompt(personalAll, libraries, enabledIds);
            Line(text, $"{report.RedundantCount} redundant, {report.OverrideCount} override");
            foreach (var overlap in report.Overlaps)
            {
                Line(text, $"{overlap.Kind} {overlap.Pattern} -> \"{overlap.Replacement}\", library writes " +
                           $"\"{overlap.LibraryReplacement}\", named \"{overlap.LibraryId}\"");
            }

            Section(text, $"{name}: finished text from the post-processor");
            var processor = new TextPostProcessor(
                fixture.Dictionary, NullLogger<TextPostProcessor>.Instance, snippets: null, libraries: fixture.Service);
            foreach (var sentence in LibraryFixture.Sentences)
            {
                Line(text, $"{sentence} => {processor.Process(sentence)}");
            }
        }

        return text.ToString();
    }

    /// <summary>Compares line by line so a failure names the first difference instead of two walls of text.</summary>
    public static void AssertMatchesGolden(string actual)
    {
        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            File.WriteAllText(GoldenPath, actual);
            return;
        }

        var expectedLines = File.ReadAllText(GoldenPath).ReplaceLineEndings("\n").Split('\n');
        var actualLines = actual.ReplaceLineEndings("\n").Split('\n');
        for (var i = 0; i < Math.Max(expectedLines.Length, actualLines.Length); i++)
        {
            var expected = i < expectedLines.Length ? expectedLines[i] : "(end of file)";
            var got = i < actualLines.Length ? actualLines[i] : "(end of output)";
            if (!string.Equals(expected, got, StringComparison.Ordinal))
            {
                Assert.Fail(
                    $"The library outputs differ from {GoldenPath} at line {i + 1}.\n  expected: {expected}\n  actual:   {got}\n" +
                    $"If the change is intended, set {UpdateVariable}=1, run the test again and review the diff.");
            }
        }
    }

    private static string SourceOf(
        DictionaryEntry entry, IReadOnlyList<DictionaryEntry> personal, IReadOnlyList<DictionaryLibrary> enabledLibraries)
    {
        // Records compare by value, and the first library in composition order that holds an equal entry is the one it
        // came from: an earlier library holding the same value would itself have supplied the rule.
        if (personal.Contains(entry))
        {
            return "your dictionary";
        }

        return enabledLibraries.FirstOrDefault(l => l.Entries.Contains(entry))?.Id ?? "(unknown)";
    }

    private static string Rule(DictionaryEntry entry, string source) =>
        $"{entry.Pattern} => {entry.Replacement}{(entry.WholeWord ? string.Empty : " (substring)")} [{source}]";

    private static string RunLengths(IEnumerable<string> sources)
    {
        var runs = new List<(string Source, int Count)>();
        foreach (var source in sources)
        {
            if (runs.Count > 0 && runs[^1].Source == source)
            {
                runs[^1] = (source, runs[^1].Count + 1);
            }
            else
            {
                runs.Add((source, 1));
            }
        }

        return string.Join(", ", runs.Select(r => $"{r.Source} x{r.Count}"));
    }

    private static void Glossary(StringBuilder text, string scenario, string label, string glossary)
    {
        Section(text, $"{scenario}: AI cleanup glossary, {label}");
        foreach (var line in glossary.Split('\n'))
        {
            Line(text, line);
        }
    }

    private static void Section(StringBuilder text, string title) => text.Append('\n').Append("[").Append(title).Append("]\n");

    private static void Line(StringBuilder text, string line) => text.Append(line).Append('\n');

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
