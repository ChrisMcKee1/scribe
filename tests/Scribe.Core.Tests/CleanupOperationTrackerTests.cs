using Scribe.Core.Cleanup;

namespace Scribe.Core.Tests;

/// <summary>
/// The admission counter disposal waits on. An operation is either admitted before close, and so
/// waited for, or refused, and so never touches anything shared.
/// </summary>
public sealed class CleanupOperationTrackerTests
{
    // A hang guard, never the verdict: every wait below is for something certain to happen.
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [Fact]
    public void Leases_count_and_release_exactly_once()
    {
        var tracker = new CleanupOperationTracker();

        var first = tracker.TryEnter();
        var second = tracker.TryEnter();
        Assert.Equal(2, tracker.ActiveCount);

        first!.Dispose();
        first.Dispose();
        Assert.Equal(1, tracker.ActiveCount);

        second!.Dispose();
        Assert.Equal(0, tracker.ActiveCount);
    }

    [Fact]
    public void Closing_with_nothing_in_flight_completes_at_once_and_refuses_admission()
    {
        var tracker = new CleanupOperationTracker();

        var drained = tracker.Close();

        Assert.True(drained.IsCompletedSuccessfully);
        Assert.True(tracker.IsClosed);
        Assert.Null(tracker.TryEnter());
    }

    [Fact]
    public async Task Closing_waits_for_every_admitted_operation()
    {
        var tracker = new CleanupOperationTracker();
        var first = tracker.TryEnter()!;
        var second = tracker.TryEnter()!;

        var drained = tracker.Close();
        Assert.False(drained.IsCompleted);
        Assert.Null(tracker.TryEnter());
        Assert.Same(drained, tracker.Close());

        first.Dispose();
        Assert.False(drained.IsCompleted);

        second.Dispose();
        await drained.WaitAsync(Bound);
    }

    [Fact]
    public async Task The_drain_never_runs_its_waiter_inline_on_the_thread_releasing_the_last_lease()
    {
        // The last release happens on whatever thread finished the last operation, often while it
        // still holds its own state. The disposer's continuation must not run inside that.
        var tracker = new CleanupOperationTracker();
        var lease = tracker.TryEnter()!;
        var drained = tracker.Close();

        var releaseReturned = false;
        var releasingThread = Environment.CurrentManagedThreadId;
        var ranInline = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = drained.ContinueWith(
            _ => ranInline.TrySetResult(
                Environment.CurrentManagedThreadId == releasingThread && !Volatile.Read(ref releaseReturned)),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        lease.Dispose();
        Volatile.Write(ref releaseReturned, true);

        Assert.False(await ranInline.Task.WaitAsync(Bound));
    }

    [Fact]
    public async Task Racing_admission_against_close_never_loses_an_operation()
    {
        for (var round = 0; round < 50; round++)
        {
            var tracker = new CleanupOperationTracker();
            const int Workers = 8;
            using var start = new Barrier(Workers + 1);
            var admitted = new CleanupOperationTracker.Lease?[Workers];

            var workers = Enumerable.Range(0, Workers).Select(i => Task.Run(() =>
            {
                start.SignalAndWait();
                admitted[i] = tracker.TryEnter();
            })).ToArray();

            start.SignalAndWait();
            var drained = tracker.Close();
            await Task.WhenAll(workers);

            // Whatever was admitted is still outstanding, so the drain cannot have completed unless
            // nothing was admitted at all.
            var outstanding = admitted.Count(lease => lease is not null);
            Assert.Equal(outstanding, tracker.ActiveCount);
            Assert.Equal(outstanding == 0, drained.IsCompleted);
            Assert.Null(tracker.TryEnter());

            foreach (var lease in admitted)
            {
                lease?.Dispose();
            }

            await drained.WaitAsync(Bound);
        }
    }
}
