using Scribe.Core.Models;
using Scribe.Core.PostProcessing;
using Xunit;

namespace Scribe.Core.Tests;

public sealed class DictionarySuggestionMinerTests
{
    private static HistoryEntry H(string text) =>
        new(0, DateTimeOffset.UtcNow, text, 1000, 100);

    [Theory]
    [InlineData("ReBAC", true)]     // camel hump
    [InlineData("GitHub", true)]
    [InlineData("ASR", true)]       // acronym
    [InlineData(".NET", true)]      // leading dot + caps
    [InlineData("K8s", true)]       // letter+digit
    [InlineData("net10", true)]
    [InlineData("hello", false)]    // plain word
    [InlineData("Hello", false)]    // sentence-case word
    [InlineData("WORD-SALAD", false)] // hyphen breaks the acronym shape
    [InlineData("42", false)]       // pure number
    public void Jargon_shape_detection(string token, bool expected) =>
        Assert.Equal(expected, DictionarySuggestionMiner.IsJargonShaped(token));

    [Fact]
    public void Recurring_terms_across_distinct_dictations_are_suggested()
    {
        var history = new[]
        {
            H("deploy the ReBAC rules to K8s"),
            H("ReBAC needs a review before the demo"),
            H("I updated ReBAC and the K8s manifests"),
            H("lunch at noon works for me"),
        };

        var suggestions = DictionarySuggestionMiner.Mine(history, [], minDictations: 3);

        var term = Assert.Single(suggestions); // K8s only hit 2 dictations
        Assert.Equal("ReBAC", term.Term);
        Assert.Equal(3, term.Dictations);
    }

    [Fact]
    public void Repeats_within_one_dictation_count_once()
    {
        var history = new[] { H("ASR ASR ASR ASR ASR") };

        Assert.Empty(DictionarySuggestionMiner.Mine(history, [], minDictations: 2));
    }

    [Fact]
    public void Terms_already_in_the_dictionary_are_not_suggested()
    {
        var history = new[] { H("use ReBAC"), H("check ReBAC"), H("love ReBAC") };
        var existing = new[] { DictionaryEntry.New("rebac", "ReBAC") };

        Assert.Empty(DictionarySuggestionMiner.Mine(history, existing, minDictations: 3));
    }

    [Fact]
    public void Trailing_punctuation_is_stripped_and_stoplist_filtered()
    {
        var history = new[] { H("migrate to K8s."), H("K8s, right?"), H("OK K8s it is OK") };

        var suggestions = DictionarySuggestionMiner.Mine(history, [], minDictations: 3);

        var term = Assert.Single(suggestions);
        Assert.Equal("K8s", term.Term); // punctuation gone, OK never suggested
    }

    [Fact]
    public void Most_common_surface_form_wins()
    {
        var history = new[] { H("use GitHub"), H("on GitHub today"), H("github is down"), H("GitHub again") };

        var suggestions = DictionarySuggestionMiner.Mine(history, [], minDictations: 3);

        // "github" (lowercase) isn't jargon-shaped, so only the cased form is counted anyway —
        // and the suggestion carries the shape that actually appeared.
        var term = Assert.Single(suggestions);
        Assert.Equal("GitHub", term.Term);
    }

    [Fact]
    public void Results_are_capped_and_ordered_by_frequency()
    {
        var history = new List<HistoryEntry>();
        for (var i = 0; i < 5; i++) history.Add(H("ReBAC and ASR"));
        for (var i = 0; i < 3; i++) history.Add(H("just K8s"));

        var suggestions = DictionarySuggestionMiner.Mine(history, [], minDictations: 3, maxSuggestions: 2);

        Assert.Equal(2, suggestions.Count);
        Assert.Equal(5, suggestions[0].Dictations);
        Assert.Contains(suggestions, s => s.Term == "K8s" || s.Term == "ASR" || s.Term == "ReBAC");
    }
}
