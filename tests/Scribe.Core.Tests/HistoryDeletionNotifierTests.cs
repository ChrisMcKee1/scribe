using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Infrastructure;
using Scribe.Core.Models;
using Scribe.Core.Persistence;

namespace Scribe.Core.Tests;

public sealed class HistoryDeletionNotifierTests
{
    [Fact]
    public void Delete_clear_and_retention_raise_after_successful_deletes()
    {
        using var folder = new TempDirectory();
        using var database = new ScribeDatabase(new AppPaths(folder.Path), NullLogger<ScribeDatabase>.Instance);
        var notifier = new HistoryDeletionNotifier();
        var seen = new List<HistoryDeletion>();
        notifier.Deleted += seen.Add;
        var repo = new HistoryRepository(database, notifier);
        var old = repo.Add(Entry("old", DateTimeOffset.UtcNow.AddDays(-10)));
        var one = repo.Add(Entry("one", DateTimeOffset.UtcNow.AddDays(-3)));

        repo.Delete(one.Id);
        Assert.Equal(HistoryDeletionKind.Entry, seen.Single().Kind);
        Assert.Equal("one", seen.Single().Entry!.Text);

        repo.Delete(999999);
        Assert.Single(seen);

        var cutoff = DateTimeOffset.UtcNow.AddDays(-5);
        Assert.Equal(1, repo.DeleteEntriesOlderThan(cutoff));
        Assert.Equal(HistoryDeletionKind.OlderThan, seen[1].Kind);
        Assert.Equal(cutoff, seen[1].CutoffUtc);

        repo.Add(Entry("clear", DateTimeOffset.UtcNow));
        repo.Clear();
        Assert.Equal(HistoryDeletionKind.Clear, seen[2].Kind);
        Assert.Equal(3, seen.Count);
        _ = old;
    }

    [Fact]
    public void Subscriber_failure_does_not_escape_repository()
    {
        using var folder = new TempDirectory();
        using var database = new ScribeDatabase(new AppPaths(folder.Path), NullLogger<ScribeDatabase>.Instance);
        var notifier = new HistoryDeletionNotifier(NullLogger<HistoryDeletionNotifier>.Instance);
        var called = false;
        notifier.Deleted += _ => throw new InvalidOperationException("boom");
        notifier.Deleted += _ => called = true;
        var repo = new HistoryRepository(database, notifier);
        var entry = repo.Add(Entry("delete", DateTimeOffset.UtcNow));

        repo.Delete(entry.Id);

        Assert.True(called);
    }

    private static HistoryEntry Entry(string text, DateTimeOffset timestamp) =>
        new(0, timestamp, text, AudioMilliseconds: 100, DecodeMilliseconds: 10);
}
