using Scribe.Core.Models;
using Scribe.Core.PostProcessing;
using Xunit;

namespace Scribe.Core.Tests;

public sealed class LastTranscriptStoreTests
{
    [Fact]
    public void Set_preserves_exact_finalized_text_and_last_write_wins()
    {
        var store = new LastTranscriptStore();

        store.Set("first");
        store.Set("Second line\r\nwith spacing.  ");

        Assert.Equal("Second line\r\nwith spacing.  ", store.Get());
    }

    [Fact]
    public void Empty_updates_do_not_erase_recoverable_text()
    {
        var store = new LastTranscriptStore();
        store.Set("recover me");

        store.Set("  ");

        Assert.Equal("recover me", store.Get());
    }

    [Fact]
    public void Get_uses_latest_nonempty_history_only_when_memory_is_empty()
    {
        var store = new LastTranscriptStore();
        var history = new[]
        {
            new HistoryEntry(2, DateTimeOffset.UtcNow, "", 1000, 100),
            new HistoryEntry(1, DateTimeOffset.UtcNow.AddMinutes(-1), "restart fallback", 1000, 100),
        };

        Assert.Equal("restart fallback", store.Get(history));

        store.Set("failed injection text");
        Assert.Equal("failed injection text", store.Get(history));
    }

    [Fact]
    public void GetRecent_returns_most_recent_first()
    {
        var store = new LastTranscriptStore();
        store.Set("first");
        store.Set("second");
        store.Set("third");

        Assert.Equal(new[] { "third", "second", "first" }, store.GetRecent());
    }

    [Fact]
    public void GetRecent_evicts_oldest_beyond_capacity()
    {
        var store = new LastTranscriptStore();
        for (var i = 1; i <= LastTranscriptStore.Capacity + 2; i++)
        {
            store.Set($"dictation {i}");
        }

        var recent = store.GetRecent();
        Assert.Equal(LastTranscriptStore.Capacity, recent.Count);
        Assert.Equal("dictation 7", recent[0]);
        Assert.Equal("dictation 3", recent[^1]);
    }

    [Fact]
    public void Consecutive_duplicate_text_occupies_a_single_slot()
    {
        var store = new LastTranscriptStore();
        store.Set("repeat me");
        store.Set("repeat me");

        Assert.Equal(new[] { "repeat me" }, store.GetRecent());
    }

    [Fact]
    public void Nonadjacent_duplicate_text_is_kept_as_a_distinct_entry()
    {
        var store = new LastTranscriptStore();
        store.Set("alpha");
        store.Set("beta");
        store.Set("alpha");

        Assert.Equal(new[] { "alpha", "beta", "alpha" }, store.GetRecent());
    }

    [Fact]
    public void GetRecent_snapshot_is_unaffected_by_later_writes()
    {
        var store = new LastTranscriptStore();
        store.Set("original");

        var snapshot = store.GetRecent();
        store.Set("newer");

        Assert.Equal(new[] { "original" }, snapshot);
        Assert.Equal(new[] { "newer", "original" }, store.GetRecent());
    }

    [Fact]
    public void GetRecent_is_empty_before_any_dictation()
    {
        Assert.Empty(new LastTranscriptStore().GetRecent());
    }

    [Fact]
    public void FormatPreview_returns_short_text_unchanged()
    {
        Assert.Equal("Hello there.", LastTranscriptStore.FormatPreview("Hello there."));
    }

    [Fact]
    public void FormatPreview_keeps_text_exactly_at_the_cap()
    {
        var exact = new string('a', LastTranscriptStore.PreviewLength);

        Assert.Equal(exact, LastTranscriptStore.FormatPreview(exact));
    }

    [Fact]
    public void FormatPreview_truncates_over_cap_text_with_an_ellipsis_inside_the_budget()
    {
        var over = new string('a', LastTranscriptStore.PreviewLength + 1);

        var preview = LastTranscriptStore.FormatPreview(over);

        Assert.Equal(LastTranscriptStore.PreviewLength, preview.Length);
        Assert.EndsWith("…", preview);
        Assert.Equal(new string('a', LastTranscriptStore.PreviewLength - 1), preview[..^1]);
    }

    [Fact]
    public void FormatPreview_collapses_multiline_and_repeated_whitespace()
    {
        Assert.Equal(
            "First line second line.",
            LastTranscriptStore.FormatPreview("  First line\r\n\r\n\tsecond   line. "));
    }

    [Fact]
    public void FormatPreview_never_splits_a_surrogate_pair_at_the_cut()
    {
        // 25 emoji (2 UTF-16 units each, 50 total) put a pair boundary exactly astride the
        // default 42-char budget: a naive cut at 41 would leave a lone high surrogate.
        var emoji = string.Concat(Enumerable.Repeat("\U0001F9D1", 25));

        var preview = LastTranscriptStore.FormatPreview(emoji);

        Assert.EndsWith("…", preview);
        Assert.False(char.IsHighSurrogate(preview[^2]), "preview ends with a lone high surrogate");
        Assert.Equal(20, preview.Count(char.IsHighSurrogate));
        Assert.Equal(LastTranscriptStore.PreviewLength - 1, preview.Length);
    }

    [Fact]
    public void FormatPreview_renders_null_or_whitespace_as_empty()
    {
        Assert.Equal(string.Empty, LastTranscriptStore.FormatPreview(null));
        Assert.Equal(string.Empty, LastTranscriptStore.FormatPreview("  \r\n "));
    }

    // A correction saved from the quick add popup rewrites the transcript it came from, so that
    // "copy last dictation" hands back the fixed wording. The ring must stay the same size doing it:
    // spending a recovery slot on a spelling fix would evict a dictation the user might still need.

    [Fact]
    public void Update_rewrites_in_place_without_reordering_or_growing_the_ring()
    {
        var store = new LastTranscriptStore();
        store.Set("one");
        store.Set("two");
        store.Set("three");

        Assert.True(store.Update("two", "TWO"));

        Assert.Equal(new[] { "three", "TWO", "one" }, store.GetRecent());
    }

    [Fact]
    public void Update_of_the_newest_entry_is_reflected_by_Get()
    {
        var store = new LastTranscriptStore();
        store.Set("teh quick fox");

        Assert.True(store.Update("teh quick fox", "the quick fox"));

        Assert.Equal("the quick fox", store.Get());
        Assert.Single(store.GetRecent());
    }

    [Fact]
    public void Update_ignores_a_transcript_that_has_already_been_evicted()
    {
        var store = new LastTranscriptStore();
        for (var i = 0; i < LastTranscriptStore.Capacity + 1; i++)
        {
            store.Set($"entry {i}");
        }

        Assert.False(store.Update("entry 0", "corrected"));
        Assert.DoesNotContain("corrected", store.GetRecent());
        Assert.Equal(LastTranscriptStore.Capacity, store.GetRecent().Count);
    }

    // The popup is modeless, so a dictation can land while it is open and shift every index in the
    // ring. Matching on content rather than position is what stops the correction being written over
    // the wrong transcript.
    [Fact]
    public void Update_follows_the_transcript_after_a_new_dictation_shifts_the_ring()
    {
        var store = new LastTranscriptStore();
        store.Set("target text");
        store.Set("arrived while the popup was open");

        Assert.True(store.Update("target text", "corrected text"));

        Assert.Equal(new[] { "arrived while the popup was open", "corrected text" }, store.GetRecent());
    }

    [Fact]
    public void Update_is_a_no_op_when_nothing_actually_changes()
    {
        var store = new LastTranscriptStore();
        store.Set("same");

        Assert.False(store.Update("same", "same"));
        Assert.False(store.Update(null, "x"));
        Assert.False(store.Update("same", null));
        Assert.False(store.Update("same", "   "));
        Assert.Equal(new[] { "same" }, store.GetRecent());
    }

    // A correction can make one transcript identical to another already in the ring. The ring must
    // still not shrink: losing a recovery slot as the price of a spelling fix is exactly what the
    // "update, not insert" rule exists to prevent.
    [Fact]
    public void Update_keeps_the_ring_intact_when_the_correction_duplicates_another_entry()
    {
        var store = new LastTranscriptStore();
        store.Set("say hello");
        store.Set("say helo");

        Assert.True(store.Update("say helo", "say hello"));

        Assert.Equal(new[] { "say hello", "say hello" }, store.GetRecent());
    }

    // The ring can hold the same text twice, because Set only collapses an immediate repeat. Fixing
    // one and leaving its twin stale would look broken, and picking "the first match" would rewrite
    // an entry the user did not select.
    [Fact]
    public void Update_rewrites_every_slot_holding_that_exact_transcript()
    {
        var store = new LastTranscriptStore();
        store.Set("okay thanks");
        store.Set("something else");
        store.Set("okay thanks");

        Assert.True(store.Update("okay thanks", "OK, thanks"));

        Assert.Equal(new[] { "OK, thanks", "something else", "OK, thanks" }, store.GetRecent());
    }

    // Everything shown after a restart comes from history. Without seeding, a correction saved
    // against one of those transcripts would find nothing in the ring to repair, so the fix would
    // report success while "copy last dictation" still returned the mistake.
    [Fact]
    public void Seed_fills_an_empty_ring_so_history_backed_transcripts_can_be_repaired()
    {
        var store = new LastTranscriptStore();
        store.Seed(["newest", "older"]);

        Assert.Equal(new[] { "newest", "older" }, store.GetRecent());
        Assert.True(store.Update("newest", "newest, corrected"));
        Assert.Equal("newest, corrected", store.Get());
    }

    [Fact]
    public void Seed_never_displaces_live_dictations_or_overflows_the_ring()
    {
        var store = new LastTranscriptStore();
        store.Set("live");
        store.Seed(["from history"]);

        Assert.Equal(new[] { "live" }, store.GetRecent());

        var empty = new LastTranscriptStore();
        empty.Seed(Enumerable.Range(0, LastTranscriptStore.Capacity + 3).Select(i => $"h{i}"));

        Assert.Equal(LastTranscriptStore.Capacity, empty.GetRecent().Count);
        Assert.Null(Record.Exception(() => empty.Seed(null)));
    }

    [Fact]
    public void Update_matches_case_sensitively_so_a_casing_fix_is_not_mistaken_for_a_no_op()
    {
        var store = new LastTranscriptStore();
        store.Set("aspire is great");

        Assert.False(store.Update("Aspire is great", "Aspire is great!"));
        Assert.True(store.Update("aspire is great", "Aspire is great"));
        Assert.Equal("Aspire is great", store.Get());
    }}
