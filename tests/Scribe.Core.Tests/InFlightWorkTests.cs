using Scribe.Core.Lifecycle;
using Scribe.Core.Tests.Concurrency;

namespace Scribe.Core.Tests;

/// <summary>
/// InFlightWork keeps processing observable until the work itself says it is done, independent of any UI state the
/// work publishes on its way out. The dictation controller used to clear its record of the processing task in the same
/// step that published Idle, so a Dispose racing that step saw nothing to wait for, and the processing then re-armed a
/// timer Dispose had already disposed.
/// </summary>
public sealed class InFlightWorkTests
{
    [Fact]
    public void Closing_stops_admission_but_work_already_admitted_is_still_observed()
    {
        var work = new InFlightWork();
        var lease = work.TryBegin()!;

        work.Close();

        Assert.Null(work.TryBegin());
        var pending = work.WaitForDrain(TimeSpan.Zero);
        Assert.False(pending.Drained);
        Assert.Equal(1, pending.StillRunning);

        lease.Complete();

        Assert.True(work.WaitForDrain(TimeSpan.Zero).Drained);
    }

    [Fact]
    public void A_drain_blocks_until_the_last_lease_completes()
    {
        var work = new InFlightWork();
        var first = work.TryBegin()!;
        var second = work.TryBegin()!;
        work.Close();

        WorkDrainResult? drain = null;
        var waiter = BlockedThreads.Start(() => drain = work.WaitForDrain(BlockedThreads.SafetyTimeout));
        BlockedThreads.WaitUntilBlocked(waiter);

        first.Complete();
        Assert.Equal(1, work.Running);
        second.Complete();
        BlockedThreads.Join(waiter);

        Assert.True(drain!.Value.Drained);
        Assert.Equal(0, drain.Value.StillRunning);
    }

    /// <summary>
    /// The confirmed shutdown defect, replayed with the real helpers in the controller's order: close admission, close
    /// the timers, then wait. Only after that does the dictation's terminal cleanup run, publishing Idle and re-arming
    /// the idle timer. That re-arm used to throw ObjectDisposedException out of a disposed System.Threading.Timer and
    /// fault the processing task, which then threw out of Dispose and skipped the rest of the app's teardown.
    /// </summary>
    [Fact]
    public void Completion_that_publishes_idle_after_closing_began_is_still_waited_for_and_stays_safe()
    {
        var provider = new ManualTimeProvider();
        var idleTimer = new ClosableTimer(() => { }, provider);
        var work = new InFlightWork();
        var lease = work.TryBegin()!;
        using var closed = new ManualResetEventSlim();

        WorkDrainResult? drain = null;
        var shutdown = BlockedThreads.Start(() =>
        {
            work.Close();
            idleTimer.Close();
            closed.Set();
            drain = work.WaitForDrain(BlockedThreads.SafetyTimeout);
        });
        Assert.True(closed.Wait(BlockedThreads.SafetyTimeout));
        BlockedThreads.WaitUntilBlocked(shutdown);

        // The dictation's cleanup, now: its idle reset re-arms the timer. Nothing throws and nothing is armed.
        Assert.False(idleTimer.Schedule(TimeSpan.FromMinutes(10)));
        Assert.Null(drain); // publishing idle did not end the tracked work
        lease.Complete();

        BlockedThreads.Join(shutdown);
        Assert.True(drain!.Value.Drained);
        Assert.Equal(0, drain.Value.Faulted);
        Assert.Equal(new[] { "dispose" }, provider.Timers[0].Events);
    }

    [Fact]
    public void A_fault_is_counted_rather_than_thrown()
    {
        var work = new InFlightWork();
        work.TryBegin()!.Complete(faulted: true);
        work.TryBegin()!.Complete();

        var drain = work.WaitForDrain(TimeSpan.Zero);

        Assert.True(drain.Drained);
        Assert.Equal(1, drain.Faulted);
    }

    [Fact]
    public void Only_the_first_completion_of_a_lease_counts()
    {
        var work = new InFlightWork();
        var lease = work.TryBegin()!;
        var other = work.TryBegin()!;

        lease.Complete();
        lease.Complete(faulted: true);

        Assert.Equal(1, work.Running);
        Assert.Equal(0, work.WaitForDrain(TimeSpan.Zero).Faulted);
        other.Complete();
        Assert.Equal(0, work.Running);
    }
}
