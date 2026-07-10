using Scribe.Core.Cleanup;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// Proves the long-buffer chunking (Feature B) splits arbitrarily long dictation into bounded
/// segments on sentence/word boundaries, never loses or duplicates text, and degrades gracefully
/// for unpunctuated speech — so a long hold is polished rather than skipped or truncated.
/// </summary>
public sealed class TextChunkingTests
{
    [Fact]
    public void Short_text_is_a_single_chunk()
    {
        var chunks = TextCleanupService.ChunkForCleanup("Just a short sentence.", 2400);

        Assert.Single(chunks);
        Assert.Equal("Just a short sentence.", chunks[0]);
    }

    [Fact]
    public void Long_text_splits_into_multiple_bounded_chunks()
    {
        var sentence = "This is a complete sentence that carries some weight. ";
        var text = string.Concat(System.Linq.Enumerable.Repeat(sentence, 200)); // ~10.8k chars

        var chunks = TextCleanupService.ChunkForCleanup(text, 2400);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.Length <= 2400, $"chunk len {c.Length} exceeds target"));
    }

    [Fact]
    public void Chunks_preserve_all_words_in_order()
    {
        var sentence = "Alpha bravo charlie delta echo foxtrot golf hotel. ";
        var text = string.Concat(System.Linq.Enumerable.Repeat(sentence, 120));

        var chunks = TextCleanupService.ChunkForCleanup(text, 2400);
        var rejoined = string.Join(' ', chunks);

        // Word sequence is identical once whitespace is normalised — nothing dropped or reordered.
        Assert.Equal(Words(text), Words(rejoined));
    }

    [Fact]
    public void Prefers_sentence_boundaries_so_chunks_end_on_punctuation()
    {
        var sentence = "Here is a sentence of a reasonable length to force boundary selection. ";
        var text = string.Concat(System.Linq.Enumerable.Repeat(sentence, 100));

        var chunks = TextCleanupService.ChunkForCleanup(text, 2400);

        // All but the final chunk should land on a sentence terminator rather than mid-word.
        for (var i = 0; i < chunks.Count - 1; i++)
        {
            var last = chunks[i][^1];
            Assert.True(last is '.' or '!' or '?', $"chunk {i} ended with '{last}', not sentence punctuation");
        }
    }

    [Fact]
    public void Unpunctuated_speech_still_splits_on_whitespace_without_breaking_words()
    {
        // Raw ASR with no sentence punctuation — the whitespace fallback must still bound the chunks.
        var text = string.Join(' ', System.Linq.Enumerable.Repeat("word", 2000)); // ~9999 chars

        var chunks = TextCleanupService.ChunkForCleanup(text, 2400);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.Length <= 2400));
        // Every token is intact (no mid-word hard split for normal-length words).
        Assert.All(chunks, c => Assert.All(c.Split(' '), t => Assert.Equal("word", t)));
        Assert.Equal(Words(text), Words(string.Join(' ', chunks)));
    }

    [Fact]
    public void A_single_unbroken_run_is_hard_split_rather_than_looping_forever()
    {
        var text = new string('x', 6000); // no spaces or punctuation at all

        var chunks = TextCleanupService.ChunkForCleanup(text, 2400);

        Assert.True(chunks.Count >= 3);
        Assert.All(chunks, c => Assert.True(c.Length <= 2400));
        Assert.Equal(6000, chunks.Sum(c => c.Length));
    }

    [Fact]
    public void Version_decimal_is_not_treated_as_a_sentence_boundary()
    {
        var prefix = new string('x', 1500) + " ";
        var text = prefix + "Use GPT-5.6 for this work. " + new string('y', 1200);

        var chunks = TextCleanupService.ChunkForCleanup(text, 1600);

        Assert.DoesNotContain(chunks, chunk => chunk.EndsWith("GPT-5.", StringComparison.Ordinal));
        Assert.Contains(chunks, chunk => chunk.Contains("GPT-5.6", StringComparison.Ordinal));
    }

    [Fact]
    public void Frontier_prompt_keeps_the_complete_transcript_in_one_request()
    {
        var text = string.Join(' ', Enumerable.Repeat("paragraph", 1000));
        var options = CleanupOptions.Disabled with
        {
            Enabled = true,
            Provider = CleanupProvider.AzureFoundry,
            PromptStyle = CleanupPromptStyle.Auto,
        };

        var chunks = TextCleanupService.PrepareChunks(text, options);

        Assert.Single(chunks);
        Assert.Equal(text, chunks[0]);
    }

    [Fact]
    public void Local_prompt_keeps_bounded_chunking()
    {
        var text = string.Join(' ', Enumerable.Repeat("paragraph", 1000));
        var options = CleanupOptions.Disabled with
        {
            Enabled = true,
            Provider = CleanupProvider.FoundryLocal,
            PromptStyle = CleanupPromptStyle.Auto,
        };

        var chunks = TextCleanupService.PrepareChunks(text, options);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.True(chunk.Length <= 2400));
    }

    private static string[] Words(string s) =>
        s.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries);
}
