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
}