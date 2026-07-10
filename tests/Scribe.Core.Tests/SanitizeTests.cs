using Scribe.Core.Cleanup;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// Guards <see cref="TextCleanupService.TrySanitize"/> — the boundary that decides whether a model's
/// raw answer is usable. A rejected answer must report failure (so an all-rejected dictation falls
/// back to raw AND flashes the red "intelligence failed" overlay) rather than being silently logged
/// as an unchanged success.
/// </summary>
public sealed class SanitizeTests
{
    [Fact]
    public void Preserves_intentionally_quoted_text_and_code_identifiers()
    {
        const string quoted = "\"Use Console.WriteLine here.\"";

        Assert.True(TextCleanupService.TrySanitize(quoted, quoted, out var text));
        Assert.Equal(quoted, text);
    }

    [Fact]
    public void Legitimate_unchanged_output_is_accepted()
    {
        Assert.True(TextCleanupService.TrySanitize("Hello world.", "Hello world.", out var text));
        Assert.Equal("Hello world.", text);
    }

    [Fact]
    public void Wrapping_code_fence_is_stripped_and_accepted()
    {
        Assert.True(TextCleanupService.TrySanitize("```\nHello world.\n```", "Hello world.", out var text));
        Assert.Equal("Hello world.", text);
    }

    [Fact]
    public void Wrapping_quotes_are_stripped_and_accepted()
    {
        Assert.True(TextCleanupService.TrySanitize("\"Hello world.\"", "Hello world.", out var text));
        Assert.Equal("Hello world.", text);
    }

    [Fact]
    public void Null_or_whitespace_is_rejected()
    {
        Assert.False(TextCleanupService.TrySanitize(null, "raw", out var a));
        Assert.Equal("raw", a);
        Assert.False(TextCleanupService.TrySanitize("   ", "raw", out var b));
        Assert.Equal("raw", b);
    }

    [Fact]
    public void Think_only_answer_is_rejected()
    {
        Assert.False(TextCleanupService.TrySanitize("<think>let me reason</think>", "raw", out var text));
        Assert.Equal("raw", text);
    }

    [Fact]
    public void Empty_code_fence_is_rejected()
    {
        Assert.False(TextCleanupService.TrySanitize("```\n```", "raw", out var text));
        Assert.Equal("raw", text);
    }

    [Fact]
    public void Overlong_ramble_is_rejected()
    {
        var ramble = new string('a', 200); // far exceeds original.Length * 2.5 + 80
        Assert.False(TextCleanupService.TrySanitize(ramble, "hi", out var text));
        Assert.Equal("hi", text);
    }

    [Theory]
    [InlineData("I'm sorry, but I cannot assist with that request.")]
    [InlineData("I am sorry, but I can't help with that.")]
    [InlineData("Sorry, I can't assist with this.")]
    [InlineData("I cannot comply with that request.")]
    [InlineData("As an AI language model, I cannot fulfill this.")]
    [InlineData("I'm unable to help with that.")]
    public void Model_refusal_is_rejected_and_falls_back_to_raw(string refusal)
    {
        // The raw transcription is ordinary speech; the model answered with a canned refusal instead
        // of cleaning it. TrySanitize must reject the refusal so the pipeline injects the raw text.
        const string raw = "please schedule the meeting for tomorrow morning";
        Assert.False(TextCleanupService.TrySanitize(refusal, raw, out var text));
        Assert.Equal(raw, text);
    }

    [Fact]
    public void Refusal_wrapped_in_quotes_is_rejected()
    {
        const string raw = "book the flight for next week";
        Assert.False(
            TextCleanupService.TrySanitize("\"I'm sorry, but I cannot assist with that request.\"", raw, out var text));
        Assert.Equal(raw, text);
    }

    [Fact]
    public void Genuinely_dictated_refusal_phrasing_is_preserved()
    {
        // The user actually said it, so the raw input is phrased the same way. Cleanup must keep the
        // user's words rather than mistaking their sentence for a model refusal.
        var raw = "I'm sorry, but I cannot assist with that request.";
        Assert.True(TextCleanupService.TrySanitize(raw, raw, out var text));
        Assert.Equal(raw, text);
    }

    [Fact]
    public void Legitimate_text_containing_cannot_assist_is_preserved()
    {
        // "cannot assist" appears in the raw input, so a cleaned copy that keeps it is not a refusal.
        var raw = "The new policy means staff cannot assist customers after five.";
        Assert.True(TextCleanupService.TrySanitize(raw, raw, out var text));
        Assert.Equal(raw, text);
    }

    [Fact]
    public void Echoed_transcript_tags_are_stripped_and_accepted()
    {
        // A literal-minded model sometimes mirrors the delimiters it saw around the user message.
        Assert.True(TextCleanupService.TrySanitize(
            "<transcript>\nHello world.\n</transcript>", "Hello world.", out var text));
        Assert.Equal("Hello world.", text);
    }

    [Fact]
    public void Tag_only_answer_is_rejected()
    {
        Assert.False(TextCleanupService.TrySanitize("<transcript></transcript>", "raw", out var text));
        Assert.Equal("raw", text);
    }

    [Fact]
    public void User_message_wraps_the_chunk_in_transcript_tags()
    {
        var message = TextCleanupService.BuildUserMessage("hey can you make sure the CLI is installed");

        Assert.StartsWith("<transcript>", message);
        Assert.EndsWith("</transcript>", message);
        Assert.Contains("hey can you make sure the CLI is installed", message);
    }

    [Fact]
    public void Sanitizer_does_not_guess_at_missing_sentence_boundaries()
    {
        var raw = "I went to the store.The next day I left.It was fine.";
        Assert.True(TextCleanupService.TrySanitize(raw, raw, out var text));
        Assert.Equal(raw, text);
    }

    [Fact]
    public void Missing_sentence_space_fix_does_not_split_clear_code_member_access()
    {
        const string raw = "Call Console.WriteLine(value) next.";
        Assert.True(TextCleanupService.TrySanitize(raw, raw, out var text));
        Assert.Equal(raw, text);
    }

    [Theory]
    [InlineData("The value is 3.5 today.")]   // decimal must not be split
    [InlineData("Ship version 2.5 tomorrow.")] // version number must not be split
    [InlineData("The server is at 192.168.0.1 now.")] // IP address must not be split
    [InlineData("I live in the U.S.A now.")]  // acronym must not be split
    [InlineData("Visit example.com for more.")] // lowercase domain must not be split
    [InlineData("Well spaced. Sentences here.")] // already correct
    public void Sentence_space_fix_leaves_valid_text_untouched(string raw)
    {
        Assert.True(TextCleanupService.TrySanitize(raw, raw, out var text));
        Assert.Equal(raw, text);
    }

    // ---- Invented-reply guard ---------------------------------------------------------------
    // A weaker model (e.g. a small Foundry Local model) sometimes ANSWERS a dictated question or
    // acknowledges a request instead of cleaning it — the exact bug behind "Can you hear me now?"
    // producing "Yeah." The guard must reject a reply the speaker never dictated so the chunk falls
    // back to the raw transcription (and the pipeline flashes "intelligence failed").

    [Theory]
    [InlineData("Yeah.")]
    [InlineData("Yes.")]
    [InlineData("No.")]
    [InlineData("Sure.")]
    [InlineData("Nope.")]
    [InlineData("Of course.")]
    [InlineData("I think so.")]
    [InlineData("Yes, I can.")]
    public void Terse_answer_to_a_dictated_question_is_rejected(string answer)
    {
        const string raw = "Can you hear me now?";
        Assert.False(TextCleanupService.TrySanitize(answer, raw, out var text));
        Assert.Equal(raw, text);
    }

    [Fact]
    public void Answer_that_echoes_the_question_as_a_statement_is_rejected()
    {
        // The model converted the question into an affirmed statement — still an answer, not a clean-up.
        const string raw = "can you hear me now";
        Assert.False(TextCleanupService.TrySanitize("Yes, I can hear you now.", raw, out var text));
        Assert.Equal(raw, text);
    }

    [Fact]
    public void Offer_to_help_instead_of_cleaning_a_request_is_rejected()
    {
        // The prompt's canonical failure: a request phrased in the dictation gets answered with an
        // offer to help rather than transcribed. "make sure" contains "sure", so only the offer
        // signal (not the affirmation opener) may fire here.
        const string raw = "hey can you make sure the CLI is installed";
        Assert.False(TextCleanupService.TrySanitize("Sure, I can help with that.", raw, out var text));
        Assert.Equal(raw, text);
    }

    [Fact]
    public void Greeting_and_offer_of_assistance_is_rejected()
    {
        const string raw = "can you hear me";
        Assert.False(TextCleanupService.TrySanitize("Hello! How can I help you today?", raw, out var text));
        Assert.Equal(raw, text);
    }

    [Fact]
    public void Genuinely_dictated_affirmation_is_preserved()
    {
        // The speaker actually opened with "yes", so a cleaned copy that keeps it is not an invented
        // reply. A declarative statement (not a question) must never be mistaken for an answer.
        const string raw = "so yes we should ship it on thursday";
        Assert.True(TextCleanupService.TrySanitize("Yes, we should ship it on Thursday.", raw, out var text));
        Assert.Equal("Yes, we should ship it on Thursday.", text);
    }

    [Fact]
    public void Acknowledgement_to_a_dictated_request_is_rejected()
    {
        // A weak model acknowledges an imperative ("Will do.", "Sure, I'll do that.") instead of
        // cleaning it. The request contains no affirmation, so an invented reply opener must be rejected.
        const string raw = "please update the ticket before the standup";
        Assert.False(TextCleanupService.TrySanitize("Will do.", raw, out var a));
        Assert.Equal(raw, a);
        Assert.False(TextCleanupService.TrySanitize("Sure, I'll do that.", raw, out var b));
        Assert.Equal(raw, b);
    }

    [Fact]
    public void Genuinely_dictated_acknowledgement_is_preserved()
    {
        // The speaker actually opened with "will do", so keeping it is a clean-up, not an invented reply.
        const string raw = "will do that first thing tomorrow morning";
        Assert.True(TextCleanupService.TrySanitize("Will do that first thing tomorrow morning.", raw, out var text));
        Assert.Equal("Will do that first thing tomorrow morning.", text);
    }

    [Fact]
    public void Correctly_cleaned_question_is_preserved()
    {
        // Cleaning a question keeps it a question; that must always be accepted.
        const string raw = "can you hear me now";
        Assert.True(TextCleanupService.TrySanitize("Can you hear me now?", raw, out var text));
        Assert.Equal("Can you hear me now?", text);
    }

    [Fact]
    public void Genuinely_dictated_offer_to_help_is_preserved()
    {
        const string raw = "let me help you with the report";
        Assert.True(TextCleanupService.TrySanitize("Let me help you with the report.", raw, out var text));
        Assert.Equal("Let me help you with the report.", text);
    }

    [Fact]
    public void Short_self_correction_that_keeps_a_dictated_word_is_preserved()
    {
        // A four-word capture collapsing to one word is short, but the surviving word came from the
        // input (a resolved self-correction), so it is a clean-up, not an answer.
        const string raw = "monday no wait tuesday";
        Assert.True(TextCleanupService.TrySanitize("Tuesday.", raw, out var text));
        Assert.Equal("Tuesday.", text);
    }

    [Fact]
    public void Short_number_reformat_is_preserved()
    {
        // Reformatting spoken numbers legitimately replaces words with digits (zero word overlap),
        // so a numeric result must never be mistaken for a terse answer.
        const string raw = "the budget is nine hundred fifty";
        Assert.True(TextCleanupService.TrySanitize("$950.", raw, out var text));
        Assert.Equal("$950.", text);
    }
}
