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
}
