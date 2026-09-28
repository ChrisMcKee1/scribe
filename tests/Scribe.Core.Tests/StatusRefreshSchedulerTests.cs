using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

/// <summary>
/// CoalesceDictionaryStatus: the Dictionary page's status refresh runs once per burst of row notifications and once per
/// bulk edit, always after the last change, and never after the window closed.
/// </summary>
public sealed class StatusRefreshSchedulerTests
{
    private sealed class Harness
    {
        public Harness() => Scheduler = new StatusRefreshScheduler(Refresh, Posted.Enqueue);

        public StatusRefreshScheduler Scheduler { get; }

        public Queue<Action> Posted { get; } = new();

        public int Refreshes { get; private set; }

        public Action? DuringRefresh { get; set; }

        // What the refresh saw: the page's state when it ran.
        public List<int> Seen { get; } = [];

        public int State { get; set; }

        public void RunPosted()
        {
            while (Posted.TryDequeue(out var work))
            {
                work();
            }
        }

        private void Refresh()
        {
            Refreshes++;
            Seen.Add(State);
            DuringRefresh?.Invoke();
        }
    }

    [Fact]
    public void A_burst_of_requests_posts_one_refresh_that_sees_the_last_change()
    {
        var harness = new Harness();

        // Typing one character raises Pattern then DeleteName; a cell commit asks again.
        harness.State = 1;
        harness.Scheduler.Request();
        harness.Scheduler.Request();
        harness.State = 2;
        harness.Scheduler.Request();

        Assert.Single(harness.Posted);
        Assert.True(harness.Scheduler.IsPending);
        Assert.Equal(0, harness.Refreshes);

        harness.RunPosted();

        Assert.Equal(1, harness.Refreshes);
        Assert.Equal([2], harness.Seen);
        Assert.False(harness.Scheduler.IsPending);
    }

    [Fact]
    public void A_request_after_the_refresh_ran_posts_another()
    {
        var harness = new Harness();
        harness.Scheduler.Request();
        harness.RunPosted();
        harness.Scheduler.Request();

        Assert.Single(harness.Posted);
        harness.RunPosted();
        Assert.Equal(2, harness.Refreshes);
    }

    [Fact]
    public void A_request_made_while_the_refresh_runs_is_not_lost()
    {
        var harness = new Harness();
        var once = true;
        harness.DuringRefresh = () =>
        {
            if (once)
            {
                once = false;
                harness.State = 5;
                harness.Scheduler.Request();
            }
        };

        harness.Scheduler.Request();
        harness.RunPosted();

        Assert.Equal(2, harness.Refreshes);
        Assert.Equal([0, 5], harness.Seen);
    }

    [Fact]
    public void A_row_added_or_removed_refreshes_at_once_outside_a_batch()
    {
        var harness = new Harness();

        harness.Scheduler.RefreshNow();

        Assert.Equal(1, harness.Refreshes);
        Assert.Empty(harness.Posted);
    }

    [Fact]
    public void A_bulk_edit_refreshes_once_at_its_end_after_its_last_row()
    {
        var harness = new Harness();
        using (harness.Scheduler.Batch())
        {
            for (var row = 1; row <= 1_000; row++)
            {
                harness.State = row;
                harness.Scheduler.RefreshNow(); // each row's collection change
                harness.Scheduler.Request(); // each row's property change
            }

            Assert.Equal(0, harness.Refreshes);
            Assert.True(harness.Scheduler.IsBatching);
        }

        Assert.Equal(1, harness.Refreshes);
        Assert.Equal([1_000], harness.Seen);
        Assert.Empty(harness.Posted);
        Assert.False(harness.Scheduler.IsBatching);
    }

    [Fact]
    public void A_batch_nobody_asked_anything_of_refreshes_nothing()
    {
        var harness = new Harness();
        using (harness.Scheduler.Batch())
        {
        }

        Assert.Equal(0, harness.Refreshes);
    }

    [Fact]
    public void Nested_batches_refresh_once_at_the_outermost_end()
    {
        var harness = new Harness();
        var outer = harness.Scheduler.Batch();
        using (harness.Scheduler.Batch())
        {
            harness.Scheduler.RefreshNow();
        }

        Assert.Equal(0, harness.Refreshes);
        harness.Scheduler.RefreshNow();
        outer.Dispose();
        outer.Dispose(); // a second dispose ends nothing more

        Assert.Equal(1, harness.Refreshes);
        Assert.False(harness.Scheduler.IsBatching);
    }

    [Fact]
    public void A_refresh_posted_before_a_batch_still_runs_and_the_batch_adds_one()
    {
        var harness = new Harness();
        harness.Scheduler.Request();
        using (harness.Scheduler.Batch())
        {
            harness.Scheduler.Request();
        }

        Assert.Equal(1, harness.Refreshes); // the batch's end
        harness.RunPosted();
        Assert.Equal(2, harness.Refreshes); // the earlier post, which sees the final state as well
    }

    [Fact]
    public void Nothing_refreshes_after_the_window_closed_a_posted_refresh_included()
    {
        var harness = new Harness();
        harness.Scheduler.Request();
        var batch = harness.Scheduler.Batch();
        harness.Scheduler.RefreshNow();

        harness.Scheduler.Close();
        batch.Dispose();
        harness.RunPosted();
        harness.Scheduler.Request();
        harness.Scheduler.RefreshNow();
        harness.RunPosted();

        Assert.True(harness.Scheduler.IsClosed);
        Assert.Equal(0, harness.Refreshes);
        Assert.Empty(harness.Posted);
    }

    [Fact]
    public void A_null_refresh_or_poster_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() => new StatusRefreshScheduler(null!, _ => { }));
        Assert.Throws<ArgumentNullException>(() => new StatusRefreshScheduler(() => { }, null!));
    }
}
