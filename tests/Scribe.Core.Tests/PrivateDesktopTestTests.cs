using System.Windows.Threading;

namespace Scribe.Core.Tests;

public sealed class PrivateDesktopTestTests
{
    [Fact]
    public void Render_checkpoint_waits_for_earlier_render_work_but_not_work_it_queues_later() =>
        PrivateDesktopTest.Run(typeof(PrivateDesktopTestTests), () =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var earlierRan = false;
            var laterRan = false;
            DispatcherOperation? later = null;
            var earlier = dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                earlierRan = true;
                later = dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => laterRan = true));
            }));

            try
            {
                PrivateDesktopTest.RenderCheckpoint("finite render marker");

                Assert.True(earlierRan);
                Assert.False(laterRan);
                Assert.Equal("finite render marker: complete", PrivateDesktopTest.Phase);
            }
            finally
            {
                earlier.Abort();
                later?.Abort();
            }
        });

    [Fact]
    public void A_checkpoint_failure_names_the_stage_and_keeps_its_original_exception() =>
        PrivateDesktopTest.Run(typeof(PrivateDesktopTestTests), () =>
        {
            var original = new TimeoutException("scripted layout timeout");

            var error = Assert.Throws<TimeoutException>(() =>
                PrivateDesktopTest.RenderCheckpoint("validation layout", () => throw original));

            Assert.Contains("phase=validation layout: render", error.Message, StringComparison.Ordinal);
            Assert.Same(original, error.InnerException);
        });

    // The texts hosted runners reported (Store run 41734441492, CI runs 39816892845 and 37319425517), so rewording a
    // message without the classifier fails here.
    [Theory]
    [InlineData("The private-desktop WPF check did not finish. phase=duplicate: add second way: queued; limit=60s", true)]
    [InlineData("The private-desktop WPF check did not finish. phase=grid: show; limit=30s", true)]
    [InlineData("The private-desktop WPF render checkpoint timed out. phase=dialog: initial render: queued", true)]
    [InlineData("The private-desktop test did not finish: wait result 258.", true)]
    [InlineData("Assert.Same() Failure: Values are not the same instance", false)]
    [InlineData("The private-desktop test failed with exit code 1.", false)]
    [InlineData("", false)]
    public void Only_a_stall_counts_as_one(string errors, bool stalled) =>
        Assert.Equal(stalled, PrivateDesktopTest.IsStall(errors));

    [Fact]
    public void A_stall_gets_a_fresh_attempt_and_a_pass_after_it_counts_the_stall()
    {
        var attempts = 0;

        var recovered = PrivateDesktopTest.RunWithStallRetry(() =>
            ++attempts == 1 ? $"{PrivateDesktopTest.WatchdogMessage} phase=body; limit=60s" : null);

        Assert.Equal(2, attempts);
        Assert.Equal(1, recovered);
    }

    [Fact]
    public void A_failed_assertion_fails_on_its_first_attempt()
    {
        var attempts = 0;

        var error = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            PrivateDesktopTest.RunWithStallRetry(() =>
            {
                attempts++;
                return "Assert.Equal() Failure: Values differ";
            }));

        Assert.Equal(1, attempts);
        Assert.Equal("Assert.Equal() Failure: Values differ", error.Message);
    }

    [Fact]
    public void Stalls_that_use_up_the_attempts_fail_with_every_attempt_s_report()
    {
        var attempts = 0;

        var error = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            PrivateDesktopTest.RunWithStallRetry(() => $"{PrivateDesktopTest.ChildWaitMessage} wait result {258 + ++attempts}."));

        Assert.Equal(PrivateDesktopTest.StallAttempts, attempts);
        Assert.Contains("wait result 259.", error.Message, StringComparison.Ordinal);
        Assert.Contains("wait result 260.", error.Message, StringComparison.Ordinal);
        Assert.Contains("Earlier attempts stalled:", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pass_on_the_first_attempt_counts_no_stall() =>
        Assert.Equal(0, PrivateDesktopTest.RunWithStallRetry(() => null));
}
