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
        // Eight racers on threads of their own, as every other barrier race in this project uses, made once and met at a
        // barrier to start each round and another to finish it. On the thread pool a round could start only once eight
        // pool threads were free at the same time, and the racers already at the barrier held theirs until it did, so on
        // a loaded machine a round waited for as long as the pool took to grow, with every other test's pool work queued
        // behind it (stream TR round 4: a 37 s stall in a full run). Made once, not for each round: a thread's start is
        // slow to be scheduled on a loaded machine, and one just started meets Close less often than one parked at the
        // barrier (an admission check made outside the lock was caught 20 of 20 times with these racers, 17 of 20 with a
        // thread for each round, and 20 of 20 on the pool).
        const int Workers = 8;
        const int Rounds = 50;
        var trackers = new CleanupOperationTracker[Rounds];
        var admitted = new CleanupOperationTracker.Lease?[Rounds, Workers];
        using var start = new Barrier(Workers + 1);
        using var finish = new Barrier(Workers + 1);
        using var abandon = new CancellationTokenSource();
        var racers = Enumerable.Range(0, Workers).Select(i => new Thread(() =>
        {
            try
            {
                for (var round = 0; round < Rounds; round++)
                {
                    start.SignalAndWait(abandon.Token);
                    admitted[round, i] = trackers[round].TryEnter();
                    finish.SignalAndWait(abandon.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // The test failed and let the racers go.
            }
        })
        {
            IsBackground = true,
            Name = "test-admission-racer",
        }).ToArray();
        foreach (var racer in racers)
        {
            racer.Start();
        }

        try
        {
            for (var round = 0; round < Rounds; round++)
            {
                var tracker = trackers[round] = new CleanupOperationTracker();
                Assert.True(start.SignalAndWait(Bound), "The racers never reached the start.");
                var drained = tracker.Close();
                Assert.True(finish.SignalAndWait(Bound), "A racer never finished its round.");

                // Whatever was admitted is still outstanding, so the drain cannot have completed unless
                // nothing was admitted at all.
                var leases = Enumerable.Range(0, Workers).Select(i => admitted[round, i]).ToArray();
                var outstanding = leases.Count(lease => lease is not null);
                Assert.Equal(outstanding, tracker.ActiveCount);
                Assert.Equal(outstanding == 0, drained.IsCompleted);
                Assert.Null(tracker.TryEnter());

                foreach (var lease in leases)
                {
                    lease?.Dispose();
                }

                await drained.WaitAsync(Bound);
            }
        }
        finally
        {
            abandon.Cancel();
        }

        Assert.All(racers, racer => Assert.True(racer.Join(Bound), "A racer never ended."));
    }
}
