using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests.Libraries;

/// <summary>
/// Pins what the libraries decide for dictation, the Dictionary page's badges and the Save prompt against golden
/// outputs captured before the Libraries list became alphabetical (see <see cref="LibraryGolden"/>).
/// </summary>
/// <remarks>
/// The golden file was rendered at d42d683 with the badge and prompt inputs taken from verbatim copies of the two loops
/// the Settings window ran then (<c>UpdateDictionaryCoverage</c> and <c>ConfirmDictionaryOverlapAsync</c>). It is
/// rendered here from <see cref="DictionaryLibraryOverlapAnalyzer.Coverage"/> and
/// <see cref="DictionaryLibraryOverlapAnalyzer.AnalyzeEnabledLibraries"/>, which replaced them, so the same file proves
/// the move changed nothing.
/// </remarks>
public sealed class LibraryCompositionGoldenTests
{
    /// <summary>The shipped spoken forms the fixture uses on purpose, so the golden reads those shipped rows.</summary>
    private static readonly string[] ChosenShippedForms = ["azure", "get hub", "gpt five six terra", "llm"];

    /// <summary>
    /// Every shipped rule the real matcher finds in a fixture sentence today, as "library: spoken form". The chosen forms
    /// win their spans. The two other llm rows lose to ai-terminology's, and the shorter GPT rules lose to the longer
    /// "gpt five six terra" that starts at the same place, so they leave the golden alone now, but they would decide if
    /// the rule that beats them changed.
    /// </summary>
    private static readonly string[] KnownSentenceMatches =
    [
        "ai-terminology: llm", "data-and-ai: llm", "data-science-machine-learning: llm",
        "ai-model-names: gpt five six terra", "ai-model-names: gpt five six", "ai-model-names: gpt five", "data-and-ai: gpt",
        "github: get hub", "microsoft-azure: azure",
    ];

    [Fact]
    public void Dictation_badges_and_prompts_match_the_outputs_captured_before_the_list_was_sorted()
    {
        using var fixture = new LibraryFixture();

        var actual = LibraryGolden.Render(
            fixture,
            (libraries, enabledIds) => DictionaryLibraryOverlapAnalyzer.Coverage(libraries, enabledIds)
                .ToDictionary(pair => pair.Key, pair => (pair.Value.Entry, pair.Value.LibraryName), StringComparer.OrdinalIgnoreCase),
            DictionaryLibraryOverlapAnalyzer.AnalyzeEnabledLibraries);

        LibraryGolden.AssertMatchesGolden(actual);
    }

    [Fact]
    public void The_fixture_shares_only_its_chosen_spoken_forms_with_the_shipped_libraries()
    {
        // The golden depends on shipped data in several ways (the failure message in LibraryGolden lists them); this checks
        // one: the rows for these four spoken forms, and no shipped library having any other spoken form the fixture uses.
        // A shipped CSV that gains one (say "kube" or "sprint") would move the golden for a reason that has nothing to do
        // with library order, so this names the word to change.
        var shared = BuiltInDictionaryLibraries.All
            .SelectMany(library => library.Entries.Select(entry => (Form: entry.Pattern.Trim(), Library: library.Id)))
            .Where(pair => LibraryFixture.FixtureKeys.Contains(pair.Form))
            .ToList();

        var unexpected = shared
            .Where(pair => !ChosenShippedForms.Contains(pair.Form, StringComparer.OrdinalIgnoreCase))
            .Select(pair => $"'{pair.Form}' in {pair.Library}")
            .ToList();
        var gone = ChosenShippedForms
            .Where(form => !shared.Any(pair => string.Equals(pair.Form, form, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.True(unexpected.Count == 0,
            "A shipped library now has a spoken form LibraryFixture uses for its own libraries: " + string.Join(", ", unexpected) +
            ". Change that word in the fixture and regenerate the golden, or regenerate it if the new winner is meant.");
        Assert.True(gone.Count == 0,
            "The golden reads these shipped rows, and no shipped library has them any more: " + string.Join(", ", gone) +
            ". Pick another shipped spoken form for the fixture and regenerate the golden.");
    }

    [Fact]
    public void No_shipped_rule_matches_a_fixture_sentence_beyond_the_ones_the_golden_already_reads()
    {
        // The finished-text sections run whole sentences through every shipped rule that is on, so a shipped rule for any
        // part of a sentence moves the golden, not only one for a spoken form the fixture defines ("fix" or "the pipeline"
        // would). Judged by the real matcher, which is what writes the finished text.
        var matches = BuiltInDictionaryLibraries.All
            .SelectMany(library => library.EnabledEntries
                .Where(entry => TextPostProcessor.TryCompile(entry, out _) is { } rule &&
                    LibraryFixture.Sentences.Any(sentence => rule.Matches(TextPostProcessor.NormalizeDictated(sentence))))
                .Select(entry => $"{library.Id}: {entry.Pattern.Trim()}"))
            .ToList();

        var unexpected = matches.Except(KnownSentenceMatches, StringComparer.Ordinal).ToList();

        Assert.True(unexpected.Count == 0,
            "A shipped rule now matches part of a LibraryFixture sentence: " + string.Join(", ", unexpected) +
            ". Reword that sentence and regenerate the golden, or regenerate it if the new text is meant, and list the " +
            "match in KnownSentenceMatches.");
    }
}
