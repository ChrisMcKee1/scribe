using System.Text.RegularExpressions;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

// The dictionary cleanup's merge gate of the language work (combined.md row 4; sign-off SIG-LANG-01): its evidence count,
// WordLike.Count(t), against the old WordLike.Matches(t).Count with the same generated pattern.
public partial class DictionaryUsageAnalyzerTests
{
    [Fact]
    public void Evidence_count_matches_legacy_regex_for_seeded_unicode()
    {
        // The copy below is the production pattern, options included, so only the counting API differs.
        var production = (Regex)typeof(DictionaryUsageAnalyzer)
            .GetProperty("WordLike", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null)!;
        Assert.Equal(LegacyWordLike().ToString(), production.ToString());
        Assert.Equal(LegacyWordLike().Options, production.Options);

        var generated = UsageEquivalenceCorpus.UnicodeTexts(seed: 4_052, count: 2_000);
        foreach (var culture in UsageEquivalenceCorpus.Cultures)
        {
            UsageEquivalenceCorpus.InCulture(culture, () =>
            {
                foreach (var text in generated.Concat([null, string.Empty, " ", "\t\r\n"]))
                {
                    List<string?> one = [text];
                    Assert.Equal(LegacyEvidence(one), Scanned(one));
                }

                Assert.Equal(LegacyEvidence(generated), Scanned(generated));
            });
        }
    }

    private static int Scanned(IReadOnlyList<string?> transcripts) =>
        DictionaryUsageAnalyzer.Analyze(transcripts!, Array.Empty<DictionaryEntry>(), Array.Empty<DictionaryLibrary>()).WordsScanned;

    // The count as it was: every usable transcript's matches, one Match each.
    private static int LegacyEvidence(IReadOnlyList<string?> transcripts) =>
        transcripts.Where(t => !string.IsNullOrWhiteSpace(t)).Sum(t => LegacyWordLike().Matches(t!).Count);

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}'’\-]*")]
    private static partial Regex LegacyWordLike();
}
