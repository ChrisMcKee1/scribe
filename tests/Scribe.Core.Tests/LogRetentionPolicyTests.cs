using Scribe.Core.Diagnostics;

namespace Scribe.Core.Tests;

public class LogRetentionPolicyTests
{
    private static readonly DateOnly Today = new(2026, 8, 20);

    private static LogFileEntry Day(int daysAgo, long bytes = 1024) =>
        new($"scribe-{Today.AddDays(-daysAgo):yyyyMMdd}.log", Today.AddDays(-daysAgo), bytes);

    [Fact]
    public void Keeps_a_full_week_counting_today()
    {
        var files = Enumerable.Range(0, 7).Select(d => Day(d)).ToList();

        var doomed = LogRetentionPolicy.SelectForDeletion(files, Today);

        Assert.Empty(doomed);
    }

    [Fact]
    public void Deletes_anything_older_than_the_window()
    {
        var files = Enumerable.Range(0, 10).Select(d => Day(d)).ToList();

        var doomed = LogRetentionPolicy.SelectForDeletion(files, Today);

        // Days 0..6 are the week; 7, 8 and 9 fall outside it.
        Assert.Equal(3, doomed.Count);
        Assert.All(doomed, f => Assert.True(f.Day < Today.AddDays(-6)));
    }

    [Fact]
    public void Drops_oldest_first_when_the_folder_exceeds_its_budget()
    {
        // Every file is inside the retention window, so only the size rule can act.
        var files = Enumerable.Range(0, 5).Select(d => Day(d, 10)).ToList();

        var doomed = LogRetentionPolicy.SelectForDeletion(files, Today, retentionDays: 7, totalBudgetBytes: 25);

        Assert.Equal([Today.AddDays(-4), Today.AddDays(-3), Today.AddDays(-2)], doomed.Select(f => f.Day));
    }

    [Fact]
    public void Never_deletes_todays_file_however_large_it_is()
    {
        // The one file a user reporting a problem right now actually needs. A budget sweep that can
        // reach it would delete the evidence at exactly the wrong moment.
        var files = new[] { Day(0, 5_000_000), Day(1, 10) };

        var doomed = LogRetentionPolicy.SelectForDeletion(files, Today, retentionDays: 7, totalBudgetBytes: 100);

        Assert.DoesNotContain(doomed, f => f.Day == Today);
    }

    [Fact]
    public void Keeps_a_future_dated_file()
    {
        // Clock skew, a timezone move, or a restored backup. Such a file is newer than the window,
        // not older than it, and deleting it would discard the newest logs on the machine.
        var files = new[] { new LogFileEntry("future.log", Today.AddDays(2), 10), Day(0) };

        var doomed = LogRetentionPolicy.SelectForDeletion(files, Today);

        Assert.Empty(doomed);
    }

    [Fact]
    public void A_zero_budget_disables_the_size_sweep_but_not_the_age_sweep()
    {
        var files = new[] { Day(0, 999_999), Day(1, 999_999), Day(30, 10) };

        var doomed = LogRetentionPolicy.SelectForDeletion(files, Today, retentionDays: 7, totalBudgetBytes: 0);

        Assert.Single(doomed);
        Assert.Equal(Today.AddDays(-30), doomed[0].Day);
    }

    [Fact]
    public void Retention_below_one_day_still_keeps_today()
    {
        var files = new[] { Day(0), Day(1) };

        var doomed = LogRetentionPolicy.SelectForDeletion(files, Today, retentionDays: 0);

        Assert.Single(doomed);
        Assert.Equal(Today.AddDays(-1), doomed[0].Day);
    }

    [Fact]
    public void Defaults_bound_the_folder_to_a_week_and_a_readable_size()
    {
        // These are the numbers quoted to users in the About page and in the session banner. If one
        // moves, the other has to move with it.
        Assert.Equal(7, LogRetentionPolicy.DefaultRetentionDays);
        Assert.Equal(64L * 1024 * 1024, LogRetentionPolicy.DefaultTotalBudgetBytes);
        Assert.True(
            LogRetentionPolicy.DefaultDailyBudgetBytes < LogRetentionPolicy.DefaultTotalBudgetBytes,
            "A single day must not be allowed to fill the whole folder budget.");
    }
}
