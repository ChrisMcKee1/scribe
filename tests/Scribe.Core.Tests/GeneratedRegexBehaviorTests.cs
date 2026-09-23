using Scribe.Core.Cleanup;
using Scribe.Core.Models;
using Scribe.Core.Settings;
using Scribe.Core.TextInjection;

namespace Scribe.Core.Tests;

/// <summary>
/// P5: the regexes moved from RegexOptions.Compiled to [GeneratedRegex] keep their behavior. Each is
/// pinned through the code that uses it, on inputs that exercise its options (case-insensitivity,
/// single-line dot, lazy matching) and its word boundaries, where existing tests did not already.
/// </summary>
public sealed class GeneratedRegexBehaviorTests
{
    [Theory]
    [InlineData("<think>plan</think>Hello there.", "Hello there.")]
    [InlineData("<THINK>\nstep one\nstep two\n</Think>\nHello there.", "Hello there.")]
    [InlineData("<think>a</think>Hello<think>b</think> there.", "Hello there.")]
    public void Think_blocks_are_removed_case_insensitively_across_lines_and_one_at_a_time(string candidate, string expected)
    {
        Assert.True(TextCleanupService.TrySanitize(candidate, "hello there", out var text));
        Assert.Equal(expected, text);
        Assert.Equal(expected, TextCleanupService.SanitizeAuxiliaryCompletion(candidate));
    }

    [Theory]
    [InlineData("I'm sorry, but that is not possible.", true)]
    [InlineData("  i AM afraid that is not possible.", true)]
    [InlineData("I apologize for the confusion.", true)]
    [InlineData("I apologise for the confusion.", true)]
    [InlineData("My apologies, here it is.", true)]
    [InlineData("As an AI, I have no opinion.", true)]
    [InlineData("As a language model I will not.", true)]
    [InlineData("Well, I'm sorry to hear that.", false)]
    [InlineData("As an aide to the minister.", false)]
    [InlineData("I can't help with that.", true)]
    [InlineData("We cant assist today.", true)]
    [InlineData("They couldnt comply.", true)]
    [InlineData("I am unable to complete it.", true)]
    [InlineData("That will not continue.", true)]
    [InlineData("I cannot assistance.", false)]
    [InlineData("I can help with that.", false)]
    public void Refusals_are_recognized_by_their_preamble_or_an_inability_to_help(string text, bool expected)
    {
        Assert.Equal(expected, TextCleanupService.LooksLikeRefusal(text));
    }

    [Theory]
    [InlineData("\"Sure thing, the report is attached.", "the report is attached now", true)]
    [InlineData("No, the report went out on Friday.", "the report went out on friday", true)]
    [InlineData("My pleasure, the report is attached.", "the report is attached now", true)]
    [InlineData("Nobody knows where the report went.", "nobody knows where the report went", false)]
    [InlineData("Yes, it is definitely done now.", "it is definitely done now yes", false)]
    [InlineData("Yes, we met yesterday afternoon.", "we met yesterday afternoon", true)]
    [InlineData("I can help with the build tonight.", "fix the build tonight please", true)]
    [InlineData("I would be glad to review it later.", "review it later please", true)]
    [InlineData("Let me help you move the boxes.", "let me help you move the boxes", false)]
    public void Replies_are_recognized_by_openers_and_offers_the_speaker_never_said(
        string candidate, string original, bool expected)
    {
        Assert.Equal(expected, TextCleanupService.LooksLikeInventedReply(candidate, original));
    }

    [Theory]
    [InlineData("Is it done", true)]
    [InlineData("   what's next", true)]
    [InlineData("hows it going", true)]
    [InlineData("How's it going", true)]
    [InlineData("Whom should I call", true)]
    [InlineData("Isabel arrived early", false)]
    [InlineData("Island hopping", false)]
    [InlineData("Whomever you like", false)]
    [InlineData("Mightily done", false)]
    [InlineData("The meeting moved", false)]
    [InlineData("The meeting moved?", true)]
    public void Questions_are_recognized_by_a_leading_question_word_or_a_question_mark(string text, bool expected)
    {
        Assert.Equal(expected, TextCleanupService.LooksLikeQuestion(text));
    }

    [Fact]
    public void A_contraction_counts_as_one_word_when_measuring_overlap_with_the_dictation()
    {
        // Two words, "it's" and "fine", half of them shared with the dictation, so it is kept. Split at
        // the apostrophe it would be three words with one shared, and rejected as an invented reply.
        Assert.False(TextCleanupService.LooksLikeInventedReply("It's fine.", "its fine now thanks"));
    }

    [Theory]
    [InlineData("one \r\n\t two", "one two")]
    [InlineData("one\r\rtwo", "one two")]
    [InlineData("one\n\n\ntwo", "one two")]
    [InlineData(" \r\n", "")]
    public void Flattening_turns_each_run_of_line_breaks_and_the_blanks_around_it_into_one_space(string text, string expected)
    {
        Assert.Equal(expected, InjectionTextFormatter.Apply(text, NewlineInjectionMode.AlwaysFlatten, targetProcessName: null));
    }

    [Fact]
    public void Dictionary_usage_counts_a_word_with_inner_apostrophes_or_hyphens_once()
    {
        // "it's", "a", "well-known", "3rd-party", "tool’s", "API", then "42" and "ok": a word starts at a
        // letter or digit, so the leading dashes and the lone apostrophe count for nothing.
        var report = DictionaryUsageAnalyzer.Analyze(["it's a well-known 3rd-party tool’s API", "-- ' 42 ok"], [], []);

        Assert.Equal(8, report.WordsScanned);
    }
}
