using Scribe.Core.Cleanup;

namespace Scribe.Core.Tests;

/// <summary>
/// The writing style asks the model not to emit em/en dashes, but that is advisory. These lock in the
/// deterministic backstop that makes the house style hold no matter which model answered.
/// </summary>
public class DashNormalizerTests
{
    private const string Em = "\u2014";
    private const string En = "\u2013";

    [Fact]
    public void Leaves_text_without_dashes_untouched()
    {
        const string input = "A normal sentence, with a comma; and a semicolon.";
        Assert.Same(input, DashNormalizer.Normalize(input));
    }

    [Fact]
    public void Hyphens_and_product_names_survive()
    {
        const string input = "Use GPT-5.6-Terra with Qwen3-14B on a well-structured run-on sentence.";
        Assert.Equal(input, DashNormalizer.Normalize(input));
    }

    [Fact]
    public void Spaced_em_dash_becomes_a_comma()
    {
        Assert.Equal(
            "The pill shows Scribe is listening, and where it lives.",
            DashNormalizer.Normalize($"The pill shows Scribe is listening {Em} and where it lives."));
    }

    [Fact]
    public void Unspaced_em_dash_becomes_a_comma_and_a_space()
    {
        Assert.Equal(
            "It works, mostly.",
            DashNormalizer.Normalize($"It works{Em}mostly."));
    }

    [Fact]
    public void Paired_em_dashes_both_become_commas()
    {
        Assert.Equal(
            "Say a phrase, like this one, and Scribe types it.",
            DashNormalizer.Normalize($"Say a phrase {Em} like this one {Em} and Scribe types it."));
    }

    [Fact]
    public void En_dash_between_numbers_becomes_a_range_word()
    {
        Assert.Equal("about 1 to 2 GB", DashNormalizer.Normalize($"about 1{En}2 GB"));
        Assert.Equal("pages 3 to 7", DashNormalizer.Normalize($"pages 3 {En} 7"));
    }

    [Fact]
    public void Does_not_double_up_existing_punctuation()
    {
        Assert.Equal("Wait, I mean the park.", DashNormalizer.Normalize($"Wait, {Em} I mean the park."));
        Assert.Equal("Done. Next up.", DashNormalizer.Normalize($"Done. {Em} Next up."));
    }

    [Fact]
    public void Line_leading_dash_is_dropped_rather_than_turned_into_a_comma()
    {
        Assert.Equal(
            "Items:\nfirst\nsecond",
            DashNormalizer.Normalize($"Items:\n{Em} first\n{Em} second"));
    }

    [Fact]
    public void Trailing_dash_is_removed_without_adding_punctuation()
    {
        Assert.Equal("An unfinished thought", DashNormalizer.Normalize($"An unfinished thought {Em}"));
    }

    [Fact]
    public void Dash_before_a_closing_bracket_or_quote_is_dropped_not_comma_ed()
    {
        Assert.Equal("(an aside)", DashNormalizer.Normalize($"(an aside {Em})"));
        Assert.Equal("he said \"fine\"", DashNormalizer.Normalize($"he said \"fine{Em}\""));
        Assert.Equal("[note]", DashNormalizer.Normalize($"[note{Em}]"));
    }

    [Fact]
    public void Dash_at_end_of_a_line_does_not_leave_a_dangling_comma()
    {
        Assert.Equal("First thought\nSecond thought", DashNormalizer.Normalize($"First thought {Em}\nSecond thought"));
        Assert.Equal("First\r\nSecond", DashNormalizer.Normalize($"First{Em}\r\nSecond"));
    }

    [Fact]
    public void Newlines_are_preserved()
    {
        Assert.Equal(
            "First para, with an aside.\n\nSecond para.",
            DashNormalizer.Normalize($"First para {Em} with an aside.\n\nSecond para."));
    }

    [Fact]
    public void Runs_of_dashes_collapse_to_one_replacement()
    {
        Assert.Equal("Yes, really.", DashNormalizer.Normalize($"Yes {Em}{Em} really."));
        Assert.Equal("Yes, really.", DashNormalizer.Normalize($"Yes {Em}{En} really."));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Null_and_empty_are_safe(string? input) =>
        Assert.Equal(string.Empty, DashNormalizer.Normalize(input));

    [Fact]
    public void Output_never_contains_a_dash()
    {
        string[] cases =
        [
            $"a {Em} b",
            $"a{Em}b",
            $"{Em}leading",
            $"trailing{Em}",
            $"1{En}2",
            $"one {Em} two {Em} three {Em} four",
            $"{Em}",
            $" {Em} ",
            $"(aside {Em})",
            $"line {Em}\nnext",
            $"quote{Em}\"",
        ];

        foreach (var c in cases)
        {
            Assert.False(DashNormalizer.ContainsDash(DashNormalizer.Normalize(c)), c);
        }
    }

    [Fact]
    public void Sanitized_cleanup_output_is_dash_free()
    {
        // The real guarantee: whatever the model answered, what reaches the document has no dashes.
        Assert.True(TextCleanupService.TrySanitize(
            $"The defaults are right for most people {Em} changes take effect after restart.",
            "the defaults are right for most people changes take effect after restart",
            out var text));

        Assert.Equal("The defaults are right for most people, changes take effect after restart.", text);
        Assert.False(DashNormalizer.ContainsDash(text));
    }

    [Fact]
    public void Default_writing_style_and_frontier_prompt_are_themselves_dash_free()
    {
        // A prompt that models em-dash punctuation teaches the model to imitate it.
        Assert.False(DashNormalizer.ContainsDash(CleanupPrompt.DefaultWritingStyle));
        Assert.False(DashNormalizer.ContainsDash(CleanupPrompt.DefaultFrontierPrompt));
        Assert.False(DashNormalizer.ContainsDash(CleanupPrompt.SingleLineWritingStyle));
    }
}
