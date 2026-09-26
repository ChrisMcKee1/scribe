using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Infrastructure;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;

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
        WaitUntil(() => seen.Count == 1);
        Assert.Equal(HistoryDeletionKind.Entry, seen.Single().Kind);
        Assert.Equal("one", seen.Single().Entry!.Text);
        Assert.Equal(1, seen.Single().Revision);

        repo.Delete(999999);
        Assert.Single(seen);

        var cutoff = DateTimeOffset.UtcNow.AddDays(-5);
        Assert.Equal(1, repo.DeleteEntriesOlderThan(cutoff));
        WaitUntil(() => seen.Count == 2);
        Assert.Equal(HistoryDeletionKind.OlderThan, seen[1].Kind);
        Assert.Equal(cutoff, seen[1].CutoffUtc);

        repo.Add(Entry("clear", DateTimeOffset.UtcNow));
        repo.Clear();
        WaitUntil(() => seen.Count == 3);
        Assert.Equal(HistoryDeletionKind.Clear, seen[2].Kind);
        Assert.Equal(3, seen.Count);
        _ = old;
    }

    [Fact]
    public void Delayed_clear_notice_keeps_later_dictations()
    {
        using var folder = new TempDirectory();
        using var database = new ScribeDatabase(new AppPaths(folder.Path), NullLogger<ScribeDatabase>.Instance);
        var notifier = new HistoryDeletionNotifier();
        var repo = new HistoryRepository(database, notifier);
        var store = new LastTranscriptStore(notifier);
        store.SeedHistory([repo.Add(Entry("before", DateTimeOffset.UtcNow))], notifier.Revision, () => notifier.Revision);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var delivered = new ManualResetEventSlim();
        notifier.Deleted += deletion =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            store.ApplyDeletion(deletion);
            delivered.Set();
        };

        repo.Clear();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        store.Set("after");
        release.Set();
        Assert.True(delivered.Wait(TimeSpan.FromSeconds(5)));

        Assert.Equal(["after"], store.GetRecent());
    }

    [Fact]
    public void Subscriber_failure_does_not_escape_repository()
    {
        using var folder = new TempDirectory();
        using var database = new ScribeDatabase(new AppPaths(folder.Path), NullLogger<ScribeDatabase>.Instance);
        var notifier = new HistoryDeletionNotifier(new ThrowingLogger<HistoryDeletionNotifier>());
        var called = false;
        notifier.Deleted += _ => throw new InvalidOperationException("boom");
        notifier.Deleted += _ => called = true;
        var repo = new HistoryRepository(database, notifier);
        var entry = repo.Add(Entry("delete", DateTimeOffset.UtcNow));

        repo.Delete(entry.Id);

        WaitUntil(() => called);
    }

    [Fact]
    public void Subscriber_observes_write_gate_free_after_retention_delete()
    {
        using var folder = new TempDirectory();
        using var database = new ScribeDatabase(new AppPaths(folder.Path), NullLogger<ScribeDatabase>.Instance);
        var notifier = new HistoryDeletionNotifier();
        var repo = new HistoryRepository(database, notifier);
        repo.Add(Entry("old", DateTimeOffset.UtcNow.AddDays(-10)));
        var gateFree = false;
        notifier.Deleted += _ =>
        {
            using var scope = database.TryEnterWriteScope();
            gateFree = scope.Held;
        };

        Assert.Equal(1, repo.DeleteEntriesOlderThan(DateTimeOffset.UtcNow.AddDays(-5)));

        WaitUntil(() => gateFree);
    }

    private static HistoryEntry Entry(string text, DateTimeOffset timestamp) =>
        new(0, timestamp, text, AudioMilliseconds: 100, DecodeMilliseconds: 10);

    private static void WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.True(condition());
            }

            Thread.Sleep(10);
        }
    }

    private sealed class ThrowingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            throw new InvalidOperationException("log failure");
    }
}
