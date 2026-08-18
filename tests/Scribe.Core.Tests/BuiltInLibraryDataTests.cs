using Scribe.Core.Models;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

/// <summary>
/// Guardrails on the CSV libraries that ship in the box. Two of them are enabled on every new
/// install, and every rule is a whole-word, case-insensitive rewrite applied to raw dictation, so a
/// single careless row silently corrupts text for people who never opened the dictionary page.
/// These assertions are cheap and catch the failure modes at the point a library is edited rather
/// than in a bug report about "Scribe changed a word I didn't say".
/// </summary>
public class BuiltInLibraryDataTests
{
    // Enabled by default in AppSettings.CreateDefault, so their content is the highest-risk data
    // in the repository.
    private static readonly string[] DefaultOn = ["ai-model-names", "ai-terminology"];

    /// <summary>
    /// Ordinary words in languages the bundled Parakeet model transcribes. A whole-word rule on any
    /// of these fires mid-sentence for a speaker of that language. "il" (French/Italian) and "di"
    /// (Italian) were both shipped as bare acronym rules and are the reason this test exists.
    /// </summary>
    private static readonly HashSet<string> CommonWordsInSupportedLanguages =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // French / Italian / Spanish / Portuguese / German function words.
            "il", "di", "la", "le", "les", "de", "du", "des", "un", "une", "et", "en", "au", "ce",
            "se", "si", "su", "da", "del", "che", "non", "per", "con", "una", "el", "los", "las",
            "es", "als", "das", "der", "die", "den", "und", "ist", "im", "am", "an", "zu", "so",
            "no", "na", "os", "as", "em", "ao", "ou", "je", "tu", "me", "te", "ne", "on", "ma",
            // English words short enough to be mistaken for an acronym.
            "a", "i", "an", "as", "at", "be", "by", "do", "go", "he", "if", "in", "is", "it", "me",
            "my", "no", "of", "on", "or", "so", "to", "up", "us", "we",
        };

    private static IReadOnlyList<DictionaryLibrary> Libraries => BuiltInDictionaryLibraries.All;

    private static DictionaryLibrary Get(string id) =>
        Libraries.Single(l => string.Equals(l.Id, id, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void No_rule_rewrites_a_common_word_in_a_supported_language()
    {
        var offenders = Libraries
            .SelectMany(lib => lib.Entries.Select(e => (Library: lib.Id, e.Pattern)))
            .Where(x => !x.Pattern.Contains(' ', StringComparison.Ordinal))
            .Where(x => CommonWordsInSupportedLanguages.Contains(x.Pattern.Trim()))
            .Select(x => $"{x.Library}: '{x.Pattern}'")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "These rules would rewrite ordinary words for speakers of languages the model supports. " +
            "Spell the acronym out ('d i' rather than 'di') instead: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Every_rule_is_whole_word()
    {
        // A substring rule in a shipped library is unbounded damage: "sol" inside "solution".
        // DictionaryCsv defaults whole_word to true, so this asserts nobody overrode it to false.
        var offenders = Libraries
            .SelectMany(lib => lib.Entries.Where(e => !e.WholeWord).Select(e => $"{lib.Id}: '{e.Pattern}'"))
            .ToList();

        Assert.True(offenders.Count == 0, "Substring rules found: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// Names that really are always lowercase, so forcing them down is the intended behaviour.
    /// Everything else that maps a word to its own lowercase form is a bug: matching is
    /// case-insensitive and nothing re-capitalises afterwards, so "Distillation reduces size"
    /// came out as "distillation reduces size". Four such rows shipped enabled by default.
    /// </summary>
    private static readonly HashSet<string> AlwaysLowercaseNames =
        new(StringComparer.Ordinal)
        {
            "npm", "pnpm", "kubectl", "webpack", "pandas", "conda", "dbt", "htmx",
            "statsmodels", "torchvision", "torchaudio",
        };

    [Fact]
    public void No_rule_forces_an_ordinary_word_to_lowercase()
    {
        var offenders = Libraries
            .SelectMany(lib => lib.Entries.Select(e => (Library: lib.Id, e.Pattern, e.Replacement)))
            // Only casing differs, and the canonical form is entirely lowercase.
            .Where(x => x.Pattern.Equals(x.Replacement, StringComparison.OrdinalIgnoreCase))
            .Where(x => x.Replacement == x.Replacement.ToLowerInvariant())
            .Where(x => !AlwaysLowercaseNames.Contains(x.Replacement.Trim()))
            .Select(x => $"{x.Library}: '{x.Pattern}'")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "These rules lowercase a word wherever it appears, including the start of a sentence. " +
            "Add genuinely all-lowercase names to AlwaysLowercaseNames; otherwise delete the row: " +
            string.Join(", ", offenders));
    }

    [Fact]
    public void No_library_contradicts_itself()
    {
        // The composer de-duplicates first-wins, so a second row for the same spoken form is dead
        // code that reads as a working rule.
        foreach (var library in Libraries)
        {
            var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in library.Entries)
            {
                var key = entry.Pattern.Trim();
                if (seen.TryGetValue(key, out var first))
                {
                    Assert.Fail(
                        $"{library.Id} defines '{key}' twice: '{first}' then '{entry.Replacement}'. " +
                        "The first wins, so the second never applies.");
                }

                seen[key] = entry.Replacement;
            }
        }
    }

    [Fact]
    public void No_two_libraries_disagree_about_the_same_spoken_form()
    {
        // Any two libraries can be switched on together, and the composer resolves a clash by load
        // order rather than by intent, so a disagreement anywhere in the shipped set is a coin flip
        // the user never sees. Checking only the default-on pair missed the other 53 combinations.
        var all = Libraries.ToList();
        for (var i = 0; i < all.Count; i++)
        {
            var byPattern = all[i].Entries.ToDictionary(
                e => e.Pattern.Trim(), e => e.Replacement, StringComparer.OrdinalIgnoreCase);

            for (var j = i + 1; j < all.Count; j++)
            {
                foreach (var entry in all[j].Entries)
                {
                    if (byPattern.TryGetValue(entry.Pattern.Trim(), out var other))
                    {
                        Assert.True(
                            string.Equals(other, entry.Replacement, StringComparison.Ordinal),
                            $"'{entry.Pattern}' is '{other}' in {all[i].Id} but '{entry.Replacement}' " +
                            $"in {all[j].Id}. Whichever loads first would silently win.");
                    }
                }
            }
        }
    }

    [Fact]
    public void The_default_on_libraries_are_present_and_populated()
    {
        foreach (var id in DefaultOn)
        {
            var library = Get(id);
            Assert.True(library.EnabledEntryCount > 0, $"{id} shipped with no enabled entries.");
        }

        // The opt-in default is what makes the data above high risk; if this ever stops matching,
        // the risk profile of the assertions in this file changes with it.
        Assert.Equal(DefaultOn.OrderBy(x => x), AppSettings.DefaultLibraryIds.OrderBy(x => x));
    }

    [Fact]
    public void A_redundant_personal_entry_is_only_flagged_when_the_library_truly_covers_it()
    {
        // Ties the analyzer to real shipped data rather than hand-built fixtures: a false Redundant
        // verdict makes the settings window offer to delete an entry whose removal changes output.
        var library = Get("ai-model-names");
        var libraryEntries = library.EnabledEntries.ToList();
        var sample = libraryEntries[0];

        var identical = new DictionaryEntry(1, sample.Pattern, sample.Replacement, sample.WholeWord, true);
        var different = new DictionaryEntry(2, sample.Pattern, sample.Replacement + " Preview", sample.WholeWord, true);

        var report = DictionaryLibraryOverlapAnalyzer.Analyze(
            [identical, different], libraryEntries);

        // Both personal rows share one spoken form, so only the first survives de-duplication in the
        // grid; the analyzer still classifies each row it is handed.
        Assert.Contains(report.Overlaps, o => o.Kind == DictionaryOverlapKind.Redundant);
        Assert.Contains(report.Overlaps, o => o.Kind == DictionaryOverlapKind.Override);

        // The override must survive even though it shares a spoken form with the redundant row.
        // Keying the removal on the pattern alone would delete both.
        var kept = DictionaryLibraryOverlapAnalyzer.RemoveRedundant([identical, different], report);
        Assert.Single(kept);
        Assert.Equal(different.Replacement, kept[0].Replacement);
    }
}
