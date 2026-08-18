using Scribe.Core.PostProcessing;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// Guards the boundary that keeps auto-learning honest: a generated pattern must be something the
/// recognizer was actually observed to produce, and must never be able to rewrite ordinary prose.
/// </summary>
public sealed class DictionaryTermVariantsTests
{
    [Theory]
    [InlineData("CSU", "c s u")]
    [InlineData("ATU", "a t u")]
    [InlineData("MCAP", "m c a p")]
    [InlineData(".NET", "n e t")]
    public void Acronyms_are_spelled_out(string term, string expected) =>
        Assert.Contains(expected, DictionaryTermVariants.For(term));

    [Theory]
    [InlineData("AI")]
    [InlineData("IQ")]
    [InlineData("PR")]
    public void Two_letter_acronyms_are_skipped(string term) =>
        // Their spelled form is two single letters, which collides with "a", "I" and friends.
        Assert.Empty(DictionaryTermVariants.For(term));

    [Theory]
    [InlineData("WebIQ", "web iq")]
    [InlineData("GitHub", "git hub")]
    [InlineData("JavaScript", "java script")]
    [InlineData("DeepSeek", "deep seek")]
    public void Compounds_are_split(string term, string expected) =>
        Assert.Contains(expected, DictionaryTermVariants.For(term));

    [Theory]
    [InlineData("AndThen")]     // "and then" is prose, not a rendering fix
    [InlineData("TheOther")]
    [InlineData("IfNot")]
    [InlineData("LinkedIn")]    // "the issue linked in the PR" must survive untouched
    public void Compounds_made_of_everyday_words_are_rejected(string term) =>
        Assert.Empty(DictionaryTermVariants.For(term));

    [Fact]
    public void A_generated_pattern_never_equals_its_replacement()
    {
        string[] terms = ["CSU", "WebIQ", "GitHub", "MCAP", "JavaScript", "ReBAC"];

        foreach (var term in terms)
        {
            Assert.DoesNotContain(
                DictionaryTermVariants.For(term),
                pattern => string.Equals(pattern, term, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void A_generated_pattern_differs_from_the_term_only_in_case_and_spacing()
    {
        string[] terms = ["CSU", "ATU", "WebIQ", "GitHub", "JavaScript", "MCAP"];

        foreach (var term in terms)
        {
            var squashedTerm = Squash(term);
            foreach (var pattern in DictionaryTermVariants.For(term))
            {
                // This is what makes a generated rule safe: it can only ever restore rendering,
                // never substitute a different word.
                Assert.Equal(squashedTerm, Squash(pattern));
            }
        }
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("Hello")]
    [InlineData("42")]
    [InlineData("")]
    [InlineData("   ")]
    public void Ordinary_tokens_yield_nothing(string term) =>
        Assert.Empty(DictionaryTermVariants.For(term));

    private static string Squash(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
