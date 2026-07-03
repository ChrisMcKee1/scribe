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
