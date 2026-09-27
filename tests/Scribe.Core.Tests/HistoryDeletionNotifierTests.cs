using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Infrastructure;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using ReleaseAtExit = Scribe.Core.Tests.Concurrency.ReleaseAtExit;

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
        notifier.Deleted += deletion =>
        {
            lock (seen)
            {
                seen.Add(deletion);
            }
        };
        var repo = new HistoryRepository(database, notifier);
        var old = repo.Add(Entry("old", DateTimeOffset.UtcNow.AddDays(-10)));
        var one = repo.Add(Entry("one", DateTimeOffset.UtcNow.AddDays(-3)));

        repo.Delete(one.Id);
        WaitUntil(() => Count(seen) == 1);
        Assert.Equal(HistoryDeletionKind.Entry, seen.Single().Kind);
        Assert.Equal("one", seen.Single().Entry!.Text);
        Assert.Equal(1, seen.Single().Revision);

        repo.Delete(999999);
        Assert.Single(seen);

        var cutoff = DateTimeOffset.UtcNow.AddDays(-5);
        Assert.Equal(1, repo.DeleteEntriesOlderThan(cutoff));
        WaitUntil(() => Count(seen) == 2);
        Assert.Equal(HistoryDeletionKind.OlderThan, seen[1].Kind);
        Assert.Equal(cutoff, seen[1].CutoffUtc);

        repo.Add(Entry("clear", DateTimeOffset.UtcNow));
        repo.Clear();
        WaitUntil(() => Count(seen) == 3);
        Assert.Equal(HistoryDeletionKind.Clear, seen[2].Kind);
        Assert.Equal(3, seen.Count);
        _ = old;
    }

    [Fact]
    public async Task Clear_publishes_revision_before_storage_callbacks_run()
    {
        using var folder = new TempDirectory();
        using var database = new ScribeDatabase(new AppPaths(folder.Path), NullLogger<ScribeDatabase>.Instance);
        var notifier = new HistoryDeletionNotifier();
        var repo = new HistoryRepository(database, notifier);
        var store = new LastTranscriptStore(notifier);
        store.SeedHistory([repo.Add(Entry("before", DateTimeOffset.UtcNow))], notifier.Revision, () => notifier.Revision);
        using var storageCallbackEntered = new ManualResetEventSlim();
        using var releaseStorageCallback = new ManualResetEventSlim();
        using var releaseDeletionDelivery = new ManualResetEventSlim();
        using var deletionDelivered = new ManualResetEventSlim();

        // Both callbacks hold their thread until the test lets it go, and for nothing else, so neither can let go by itself
        // while the test still judges the order; declared after the database and the gates, so a failure first releases
        // both before either is disposed (stream TR round 7).
        using var releaseAtExit = new ReleaseAtExit(releaseStorageCallback, releaseDeletionDelivery);
        database.StorageChanged += change =>
        {
            if (change == StorageChange.HistoryCleared)
            {
                storageCallbackEntered.Set();
                releaseStorageCallback.Wait();
            }
        };
        notifier.Deleted += deletion =>
        {
            releaseDeletionDelivery.Wait();
            store.ApplyDeletion(deletion);
            deletionDelivered.Set();
        };

        var clearTask = Task.Run(repo.Clear);
        Assert.True(storageCallbackEntered.Wait(Bound));
        store.Set("after");
        releaseDeletionDelivery.Set();
        Assert.True(deletionDelivered.Wait(Bound));
        releaseStorageCallback.Set();
        await clearTask.WaitAsync(Bound);

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
    public void Retention_delivery_runs_off_the_committing_thread_and_can_take_the_write_gate_once_the_delete_returns()
    {
        using var folder = new TempDirectory();
        using var database = new ScribeDatabase(new AppPaths(folder.Path), NullLogger<ScribeDatabase>.Instance);
        var notifier = new HistoryDeletionNotifier();
        var repo = new HistoryRepository(database, notifier);
        repo.Add(Entry("old", DateTimeOffset.UtcNow.AddDays(-10)));
        var committer = Environment.CurrentManagedThreadId;
        using var deleteReturned = new ManualResetEventSlim();
        int? deliveryThread = null;
        bool? gateFree = null;
        notifier.Deleted += _ =>
        {
            deliveryThread = Environment.CurrentManagedThreadId;

            // The deletion is published before the committing thread leaves its write scope, so the gate is only certain
            // to be free once the delete has returned. A delivery run synchronously under the writer would never see that
            // and leave gateFree unset.
            if (deleteReturned.Wait(Bound))
            {
                using var scope = database.TryEnterWriteScope();
                gateFree = scope.Held;
            }
        };

        Assert.Equal(1, repo.DeleteEntriesOlderThan(DateTimeOffset.UtcNow.AddDays(-5)));
        deleteReturned.Set();

        WaitUntil(() => gateFree is not null);
        Assert.NotEqual(committer, deliveryThread);
        Assert.True(gateFree);
    }

    private static int Count(List<HistoryDeletion> seen)
    {
        lock (seen)
        {
            return seen.Count;
        }
    }

    private static HistoryEntry Entry(string text, DateTimeOffset timestamp) =>
        new(0, timestamp, text, AudioMilliseconds: 100, DecodeMilliseconds: 10);

    // Delivery runs on a background queue, so these waits only bound a failure: a machine busy with parallel builds took
    // longer than 5 seconds to schedule it, and a passing run never waits this long.
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static void WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Bound;
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
