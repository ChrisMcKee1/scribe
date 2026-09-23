using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

public sealed class PendingRowUpdatesTests
{
    [Fact]
    public void Second_click_on_a_row_with_a_write_running_is_refused()
    {
        var updates = new PendingRowUpdates<long, AiRating>();

        Assert.True(updates.TryBegin(7, AiRating.Unrated, AiRating.Useful));
        Assert.False(updates.TryBegin(7, AiRating.Useful, AiRating.Unrated));
        Assert.True(updates.IsPending(7));

        // Other rows are independent.
        Assert.True(updates.TryBegin(8, AiRating.Unrated, AiRating.NotUseful));
        Assert.Equal(2, updates.Count);
    }

    [Fact]
    public void Reload_during_a_write_shows_the_pending_value_not_the_stale_stored_one()
    {
        var updates = new PendingRowUpdates<long, AiRating>();
        updates.TryBegin(7, AiRating.Unrated, AiRating.Useful);

        Assert.Equal(AiRating.Useful, updates.Resolve(7, AiRating.Unrated));
        Assert.Equal(AiRating.NotUseful, updates.Resolve(9, AiRating.NotUseful));
    }

    [Fact]
    public void Successful_write_keeps_the_new_value()
    {
        var updates = new PendingRowUpdates<long, AiRating>();
        updates.TryBegin(7, AiRating.Unrated, AiRating.Useful);

        Assert.Equal(AiRating.Useful, updates.Complete(7, succeeded: true));
        Assert.False(updates.IsPending(7));
        Assert.True(updates.TryBegin(7, AiRating.Useful, AiRating.Unrated));
    }

    [Fact]
    public void Failed_write_reverts_to_the_value_from_before_the_click()
    {
        var updates = new PendingRowUpdates<long, AiRating>();
        updates.TryBegin(7, AiRating.NotUseful, AiRating.Unrated);

        Assert.Equal(AiRating.NotUseful, updates.Complete(7, succeeded: false));
        Assert.False(updates.IsPending(7));
        Assert.Equal(AiRating.Unrated, updates.Resolve(7, AiRating.Unrated));
    }

    [Fact]
    public void Completing_a_row_with_nothing_pending_is_a_bug_not_a_silent_success()
    {
        var updates = new PendingRowUpdates<long, AiRating>();

        Assert.Throws<InvalidOperationException>(() => updates.Complete(7, succeeded: true));
    }
}
