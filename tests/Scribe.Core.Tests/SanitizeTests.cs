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
    public void Missing_space_between_sentences_is_inserted()
    {
        var raw = "I went to the store.The next day I left.It was fine.";
        Assert.True(TextCleanupService.TrySanitize(raw, raw, out var text));
        Assert.Equal("I went to the store. The next day I left. It was fine.", text);
    }

    [Theory]
    [InlineData("The value is 3.5 today.")]   // decimal must not be split
    [InlineData("I live in the U.S.A now.")]  // acronym must not be split
    [InlineData("Visit example.com for more.")] // lowercase domain must not be split
    [InlineData("Well spaced. Sentences here.")] // already correct
    public void Sentence_space_fix_leaves_valid_text_untouched(string raw)
    {
        Assert.True(TextCleanupService.TrySanitize(raw, raw, out var text));
        Assert.Equal(raw, text);
    }
}
