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
}