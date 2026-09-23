using Scribe.Core.Lifecycle;

namespace Scribe.Core.Tests;

/// <summary>
/// The app's exit path runs through StagedTeardown so one failing step can no longer skip the others: a single try
/// block used to abandon tray disposal, the theme watcher, host shutdown and host disposal together on the first throw.
/// </summary>
public sealed class StagedTeardownTests
{
    [Fact]
    public void A_failing_step_never_skips_the_steps_after_it()
    {
        var ran = new List<string>();
        var reported = new List<(string Step, Exception Error)>();
        var failure = new InvalidOperationException("boom");

        var failures = StagedTeardown.Run(
            [
                new("controller", () => ran.Add("controller")),
                new("tray", () =>
                {
                    ran.Add("tray");
                    throw failure;
                }),
                new("host stop", () => ran.Add("host stop")),
                new("host dispose", () => ran.Add("host dispose")),
            ],
            (step, error) => reported.Add((step, error)));

        Assert.Equal(1, failures);
        Assert.Equal(new[] { "controller", "tray", "host stop", "host dispose" }, ran);
        var report = Assert.Single(reported);
        Assert.Equal("tray", report.Step);
        Assert.Same(failure, report.Error);
    }

    [Fact]
    public void A_throwing_failure_report_does_not_stop_the_sequence()
    {
        var ran = new List<string>();

        var failures = StagedTeardown.Run(
            [
                new("first", () => throw new InvalidOperationException()),
                new("second", () => throw new InvalidOperationException()),
                new("third", () => ran.Add("third")),
            ],
            (_, _) => throw new IOException("the log is locked"));

        Assert.Equal(2, failures);
        Assert.Equal(new[] { "third" }, ran);
    }

    [Fact]
    public void Steps_run_strictly_in_the_given_order()
    {
        var ran = new List<int>();

        StagedTeardown.Run([.. Enumerable.Range(1, 5).Select(n => new TeardownStep($"step {n}", () => ran.Add(n)))]);

        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, ran);
    }
}
