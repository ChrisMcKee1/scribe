using Scribe.Core.Infrastructure;

namespace Scribe.Core.Tests;

public sealed class LatestRequestCoalescerTests
{
    [Fact]
    public void First_request_starts_immediately_with_a_live_token()
    {
        var coalescer = new LatestRequestCoalescer<string>();

        var work = coalescer.Submit("7 days");

        Assert.NotNull(work);
        Assert.Equal("7 days", work.Value);
        Assert.False(work.Cancellation.IsCancellationRequested);
        Assert.True(coalescer.IsRunning);
        Assert.True(coalescer.IsCurrent(work));
    }

    [Fact]
    public void Request_while_one_runs_waits_and_cancels_the_stale_computation()
    {
        var coalescer = new LatestRequestCoalescer<string>();
        var running = coalescer.Submit("7 days")!;

        Assert.Null(coalescer.Submit("30 days"));

        Assert.True(running.Cancellation.IsCancellationRequested);
        Assert.False(coalescer.IsCurrent(running));
        Assert.True(coalescer.HasPending);
        Assert.True(coalescer.IsRunning);
    }

    [Fact]
    public void Intermediate_requests_are_coalesced_into_the_latest_one()
    {
        var coalescer = new LatestRequestCoalescer<string>();
        var running = coalescer.Submit("7 days")!;
        Assert.Null(coalescer.Submit("30 days"));
        Assert.Null(coalescer.Submit("90 days"));
        Assert.Null(coalescer.Submit("All"));

        var next = coalescer.Complete(running);

        Assert.NotNull(next);
        Assert.Equal("All", next.Value);
        Assert.False(next.Cancellation.IsCancellationRequested);
        Assert.True(coalescer.IsCurrent(next));
        Assert.False(coalescer.HasPending);
        Assert.True(coalescer.IsRunning);

        Assert.Null(coalescer.Complete(next));
        Assert.False(coalescer.IsRunning);
    }

    [Fact]
    public void Pending_request_never_starts_before_the_running_one_has_finished()
    {
        var coalescer = new LatestRequestCoalescer<int>();
        var running = coalescer.Submit(1)!;
        coalescer.Submit(2);

        // Cancellation cannot interrupt a synchronous SQLite call, so "cancelled" is not "finished".
        Assert.True(running.Cancellation.IsCancellationRequested);
        Assert.True(coalescer.IsRunning);
        Assert.Null(coalescer.Submit(3));

        var next = coalescer.Complete(running);
        Assert.Equal(3, next!.Value);
    }

    [Fact]
    public void Finishing_with_nothing_pending_goes_idle_and_the_next_request_starts_at_once()
    {
        var coalescer = new LatestRequestCoalescer<int>();
        var first = coalescer.Submit(1)!;

        Assert.Null(coalescer.Complete(first));
        Assert.False(coalescer.IsRunning);
        Assert.True(coalescer.IsCurrent(first));

        var second = coalescer.Submit(2);
        Assert.NotNull(second);
        Assert.False(coalescer.IsCurrent(first));
        Assert.True(coalescer.IsCurrent(second));
    }

    [Fact]
    public void Only_the_newest_request_may_publish_success_or_failure()
    {
        var coalescer = new LatestRequestCoalescer<int>();
        var old = coalescer.Submit(1)!;
        coalescer.Submit(2);
        var newest = coalescer.Complete(old)!;

        Assert.False(coalescer.IsCurrent(old));
        Assert.True(coalescer.IsCurrent(newest));

        coalescer.Submit(3);
        Assert.False(coalescer.IsCurrent(newest));
    }

    [Fact]
    public void Close_cancels_the_running_work_drops_the_pending_one_and_blocks_publication()
    {
        var coalescer = new LatestRequestCoalescer<int>();
        var running = coalescer.Submit(1)!;
        coalescer.Submit(2);

        coalescer.Close();

        Assert.True(running.Cancellation.IsCancellationRequested);
        Assert.False(coalescer.IsCurrent(running));
        Assert.False(coalescer.HasPending);
        Assert.Null(coalescer.Complete(running));
        Assert.False(coalescer.IsRunning);
        Assert.Null(coalescer.Submit(3));
    }

    [Fact]
    public void Close_while_idle_blocks_later_requests()
    {
        var coalescer = new LatestRequestCoalescer<int>();
        var done = coalescer.Submit(1)!;
        Assert.Null(coalescer.Complete(done));

        coalescer.Close();

        Assert.False(coalescer.IsCurrent(done));
        Assert.Null(coalescer.Submit(2));
    }

    [Fact]
    public void Completing_something_other_than_the_running_request_changes_nothing()
    {
        var coalescer = new LatestRequestCoalescer<int>();
        var running = coalescer.Submit(1)!;
        coalescer.Submit(2);
        var stranger = new CoalescedRequest<int>(1, running.Version, CancellationToken.None);

        Assert.Null(coalescer.Complete(stranger));
        Assert.True(coalescer.IsRunning);
        Assert.True(coalescer.HasPending);
        Assert.Equal(2, coalescer.Complete(running)!.Value);
    }
}
