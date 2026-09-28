using System.Text;
using System.Text.RegularExpressions;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

// The dictionary cleanup scan counts a term that an ordinal ignore-case search cannot find as 0 without building its regex
// (HANDOFF 9.3: the static Regex.Count cache holds 15 patterns, so a scan of every word pack built about 3,000). The analyzer
// as it was at 77b22af, copied below, decides every report: every field, every term's hits, every library's kept terms.
public partial class DictionaryUsageAnalyzerTests
{
    [Fact]
    public void Term_counts_match_legacy_regex_for_seeded_corpora()
    {
        var shipped = BuiltInDictionaryLibraries.All;
        DictionaryEntry[] adversarial =
        [
            new(1, "kube", "Kube"), new(2, "\u212Aube ctl", "Kubectl"), new(3, "caf\u00e9", "Caf\u00e9"), new(4, "c#", "C#"),
            new(5, ".net", ".NET"), new(6, "a+b", "A+B"), new(7, "x(y)", "X(Y)"), new(8, "istanbul", "Istanbul"),
            new(9, "k8s", "Kubernetes", WholeWord: false), new(10, "sig", "Best,\nChris"), new(11, "um", " "),
            new(12, "stra\u00dfe", "Stra\u00dfe"), new(13, "  padded  ", "Padded"), new(14, "never said", "NeverWritten"),
            new(15, "i", "I"), new(16, "\u0131i", "Dotless"), new(17, KelvinOnly, "Kelvinator"),
        ];
        var custom = new DictionaryLibrary("custom-adversarial", "Adversarial", "Custom", null, BuiltIn: false,
            [.. adversarial.Select(entry => entry with { Id = 0 })]);
        var random = new Random(20_261_103);
        var (reports, kelvinOnlyUsed) = (0, 0);
        foreach (var culture in UsageEquivalenceCorpus.Cultures)
        {
            UsageEquivalenceCorpus.InCulture(culture, () =>
            {
                for (var round = 0; round < 6; round++)
                {
                    var transcripts = ScanCorpus(random, shipped, adversarial, withKelvin: round % 3 == 2);
                    IReadOnlyList<DictionaryLibrary> libraries = round % 2 == 0 ? [.. shipped, custom] : [custom, shipped[round % shipped.Count]];
                    var excluded = new HashSet<string>([shipped[0].Id, custom.Id], StringComparer.OrdinalIgnoreCase);

                    var expected = LegacyDictionaryUsageAnalyzer.Analyze(transcripts, adversarial, libraries, excluded, 1, 1);
                    var actual = DictionaryUsageAnalyzer.Analyze(transcripts, adversarial, libraries, excluded, 1, 1);
                    AssertSameReport(expected, actual, $"{culture} round {round}");
                    if (!expected.UnusedEntries.Any(usage => usage.Entry.Pattern == KelvinOnly))
                    {
                        kelvinOnlyUsed++;
                    }

                    Assert.True(expected.HasEnoughEvidence);
                    reports++;
                }
            });
        }

        Assert.Equal(18, reports);

        // Every Kelvin round holds the term only through the Kelvin sign, which the regex takes for a K and an ordinal search
        // does not, so the guard is what keeps it counted.
        Assert.Equal(6, kelvinOnlyUsed);
    }

    [Fact]
    public void A_single_term_is_scored_as_the_legacy_regex_scores_it()
    {
        // The public Score, for every shipped row and the tricky ones, in corpora with and without the Kelvin sign, a null or
        // empty corpus included.
        var entries = BuiltInDictionaryLibraries.All.SelectMany(library => library.Entries)
            .Concat([new DictionaryEntry(1, "\u212Aube", "Kube"), new(2, "kube", "\u212Aube"), new(3, "caf\u00e9", "CAF\u00c9"), new(4, " ", "x"), new(5, "kelvin", "Kelvin")])
            .ToList();
        string?[] corpora =
        [
            null, string.Empty, "kube KUBE \u212Aube caf\u00e9 CAF\u00c9", "only the \u212Aelvin sign",
            string.Join(' ', entries.Take(400).Select(entry => entry.Pattern)),
            string.Join(' ', entries.Skip(400).Take(400).Select(entry => entry.Replacement.ToUpperInvariant())) + " \u212A",
        ];
        foreach (var corpus in corpora)
        {
            foreach (var entry in entries)
            {
                Assert.Equal(LegacyDictionaryUsageAnalyzer.Score(corpus!, entry), DictionaryUsageAnalyzer.Score(corpus!, entry));
            }
        }
    }

    // An ASCII term the corpus holds only with a Kelvin sign for its K.
    private const string KelvinOnly = "kelvinator";

    private static void AssertSameReport(DictionaryUsageReport expected, DictionaryUsageReport actual, string context)
    {
        Assert.Equal(expected.HasEnoughEvidence, actual.HasEnoughEvidence);
        Assert.Equal(expected.TranscriptsScanned, actual.TranscriptsScanned);
        Assert.Equal(expected.WordsScanned, actual.WordsScanned);
        Assert.Equal(expected.TermsExamined, actual.TermsExamined);
        Assert.True(expected.UnusedEntries.SequenceEqual(actual.UnusedEntries), $"{context}: the unused entries differ.");
        Assert.Equal(expected.Libraries.Count, actual.Libraries.Count);
        for (var i = 0; i < expected.Libraries.Count; i++)
        {
            var (e, a) = (expected.Libraries[i], actual.Libraries[i]);
            Assert.Equal((e.Id, e.Name, e.UnusedCount, e.BuiltIn, e.FileName, e.AiExcluded), (a.Id, a.Name, a.UnusedCount, a.BuiltIn, a.FileName, a.AiExcluded));
            Assert.True(e.KeepTerms.SequenceEqual(a.KeepTerms), $"{context}: {e.Id} keeps different terms.");
        }

        Assert.Equal(expected.Summary, actual.Summary);
    }

    // Dictations holding some shipped and adversarial terms in any case, prose, and, when asked, the Kelvin sign.
    private static List<string> ScanCorpus(Random random, IReadOnlyList<DictionaryLibrary> shipped, IReadOnlyList<DictionaryEntry> adversarial, bool withKelvin)
    {
        var terms = shipped.SelectMany(library => library.Entries).Concat(adversarial.Where(entry => entry.Pattern != KelvinOnly)).ToList();
        string[] prose = ["the", "and", "please", "review", "meeting", "\u0130stanbul", "\u017Fign", "na\u00efve", "C#;", ".NET:"];
        var transcripts = new List<string>();
        for (var i = 0; i < 120; i++)
        {
            var text = new StringBuilder();
            for (var w = random.Next(6, 30); w > 0; w--)
            {
                if (random.Next(5) == 0)
                {
                    var term = terms[random.Next(terms.Count)];
                    var form = random.Next(2) == 0 ? term.Pattern : term.Replacement;
                    text.Append(random.Next(3) switch { 0 => form.ToUpperInvariant(), 1 => form.ToLowerInvariant(), _ => form });
                }
                else
                {
                    text.Append(prose[random.Next(prose.Length)]);
                }

                text.Append(random.Next(6) == 0 ? ", " : " ");
            }

            if (withKelvin && random.Next(10) == 0)
            {
                text.Append(random.Next(2) == 0 ? "\u212Aube" : "\u212A" + KelvinOnly[1..]);
            }

            transcripts.Add(text.ToString());
        }

        return transcripts;
    }

    // DictionaryUsageAnalyzer's scan as it was at 77b22af, verbatim apart from the class name and its XML documentation.
    private static partial class LegacyDictionaryUsageAnalyzer
    {
        [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}'’\-]*")]
        private static partial Regex WordLike { get; }

        public static DictionaryUsageReport Analyze(
            IReadOnlyList<string> transcripts,
            IReadOnlyList<DictionaryEntry> baseEntries,
            IReadOnlyList<DictionaryLibrary> enabledLibraries,
            IReadOnlySet<string>? aiExcludedLibraryIds,
            int minimumTranscripts,
            int minimumWords)
        {
            ArgumentNullException.ThrowIfNull(transcripts);
            ArgumentNullException.ThrowIfNull(baseEntries);
            ArgumentNullException.ThrowIfNull(enabledLibraries);

            var usable = transcripts.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            var words = usable.Sum(t => WordLike.Count(t));

            var candidates = baseEntries.Where(e => e.Id != 0 && DictionaryUsageAnalyzer.IsMeasurable(e)).ToList();
            var examined = candidates.Count
                + enabledLibraries.Sum(l => l.EnabledEntries.Count(DictionaryUsageAnalyzer.IsMeasurable));

            if (usable.Count < minimumTranscripts || words < minimumWords)
            {
                return new DictionaryUsageReport(
                    HasEnoughEvidence: false,
                    TranscriptsScanned: usable.Count,
                    WordsScanned: words,
                    TermsExamined: examined,
                    UnusedEntries: [],
                    Libraries: [],
                    Summary: "Not enough dictation history yet to safely recommend a cleanup. You have "
                        + $"{usable.Count:N0} of {minimumTranscripts:N0} dictations and about {words:N0} of "
                        + $"{minimumWords:N0} words. Keep dictating and run this again. You can still turn "
                        + "word packs off by hand on the Word packs tab.");
            }

            var corpus = string.Join('\n', usable);

            var unused = candidates
                .Select(entry => Score(corpus, entry))
                .Where(u => u.Unused)
                .OrderBy(u => u.Entry.Pattern, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            var excluded = new HashSet<string>(aiExcludedLibraryIds ?? new HashSet<string>(), StringComparer.OrdinalIgnoreCase);
            var libraries = enabledLibraries
                .Select(library => ScoreLibrary(corpus, library, excluded.Contains(library.Id)))
                .Where(l => l.Actionable)
                .OrderByDescending(l => l.UnusedCount)
                .ToList();

            return new DictionaryUsageReport(
                HasEnoughEvidence: true,
                TranscriptsScanned: usable.Count,
                WordsScanned: words,
                TermsExamined: examined,
                UnusedEntries: unused,
                Libraries: libraries,
                Summary: Describe(unused.Count, libraries, usable.Count, examined));
        }

        public static TermUsage Score(string corpus, DictionaryEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);
            var patternHits = Count(corpus, entry.Pattern, entry.WholeWord);
            var replacementHits = string.IsNullOrWhiteSpace(entry.Replacement)
                ? 0
                : Count(corpus, entry.Replacement, wholeWord: false);

            return new TermUsage(entry, patternHits, replacementHits);
        }

        private static LibraryUsage ScoreLibrary(string corpus, DictionaryLibrary library, bool aiExcluded)
        {
            var keep = new List<DictionaryEntry>();
            var unused = 0;

            foreach (var term in library.EnabledEntries.Where(e => !string.IsNullOrWhiteSpace(e.Pattern)))
            {
                if (!DictionaryUsageAnalyzer.IsMeasurable(term) || !Score(corpus, term).Unused)
                {
                    keep.Add(term);
                }
                else
                {
                    unused++;
                }
            }

            return new LibraryUsage(library.Id, library.Name, keep, unused, library.BuiltIn)
            {
                FileName = library.BuiltIn ? null : library.FileName,
                AiExcluded = aiExcluded,
            };
        }

        private static int Count(string corpus, string term, bool wholeWord)
        {
            var trimmed = (term ?? string.Empty).Trim();
            if (trimmed.Length == 0 || string.IsNullOrEmpty(corpus))
            {
                return 0;
            }

            var escaped = Regex.Escape(trimmed);
            var pattern = wholeWord ? $@"(?<!\w){escaped}(?!\w)" : escaped;
            return Regex.Count(corpus, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static string Describe(
            int unusedCount,
            IReadOnlyList<LibraryUsage> libraries,
            int transcripts,
            int examined)
        {
            var libraryTerms = libraries.Sum(l => l.UnusedCount);
            if (unusedCount == 0 && libraryTerms == 0)
            {
                return $"Every word in your dictionary turned up in your last {transcripts:N0} dictations. "
                    + "Nothing to clean up.";
            }

            var parts = new List<string>();
            if (unusedCount > 0)
            {
                parts.Add($"{unusedCount:N0} of your own words");
            }

            if (libraries.Count > 0)
            {
                parts.Add($"{libraryTerms:N0} {(libraryTerms == 1 ? "word" : "words")} across "
                    + $"{libraries.Count:N0} {(libraries.Count == 1 ? "word pack" : "word packs")}");
            }

            var headline = $"Checked {examined:N0} {(examined == 1 ? "word" : "words")} against your last "
                + $"{transcripts:N0} dictations. {string.Join(" and ", parts)} did not appear.";

            var summary = examined > Cleanup.CleanupPrompt.MaxGlossaryTermsLocal
                ? headline + " Turning them off frees room in the vocabulary list Scribe sends to a local "
                    + $"AI model, which fits {Cleanup.CleanupPrompt.MaxGlossaryTermsLocal} words."
                : headline;

            var excluded = libraries.Count(l => l.AiExcluded && l.KeepTerms.Count > 0);
            return excluded == 0
                ? summary
                : summary + $" {excluded:N0} {(excluded == 1 ? "word pack is" : "word packs are")} kept from AI cleanup, so the "
                    + $"words {(excluded == 1 ? "it" : "they")} still use{(excluded == 1 ? "s" : string.Empty)} are not copied "
                    + "into your dictionary, which AI cleanup always receives. Turning "
                    + $"{(excluded == 1 ? "it" : "them")} off stops applying those words.";
        }
    }
}
