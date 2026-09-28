using Scribe.Core.Lifecycle;

namespace Scribe.Core.Tests;

/// <summary>
/// PillBeforeTray orders the two views of a dictation state, the tray icon and the recording pill. In either order each view
/// receives the state exactly once, and a view that throws never keeps the other from receiving it (UI-IR-03): the first
/// view's exception goes on only after the second ran, and the second's goes on after both ran.
/// </summary>
public sealed class DictationStateViewsTests
{
    private sealed class FakeViews(bool trayThrows, bool pillThrows)
    {
        public List<string> Calls { get; } = [];

        public void Tray()
        {
            Calls.Add("tray");
            if (trayThrows)
            {
                throw new TrayFailed();
            }
        }

        public void Pill()
        {
            Calls.Add("pill");
            if (pillThrows)
            {
                throw new PillFailed();
            }
        }
    }

    private sealed class TrayFailed : Exception;

    private sealed class PillFailed : Exception;

    [Theory]
    [InlineData(false, false, false, "tray,pill", null)]
    [InlineData(true, false, false, "pill,tray", null)]
    [InlineData(false, true, false, "tray,pill", typeof(TrayFailed))]
    [InlineData(true, true, false, "pill,tray", typeof(TrayFailed))]
    [InlineData(false, false, true, "tray,pill", typeof(PillFailed))]
    [InlineData(true, false, true, "pill,tray", typeof(PillFailed))]
    [InlineData(false, true, true, "tray,pill", typeof(PillFailed))]
    [InlineData(true, true, true, "pill,tray", typeof(TrayFailed))]
    public void Each_view_receives_the_state_once_in_order_whatever_the_other_does(
        bool pillFirst, bool trayThrows, bool pillThrows, string expectedCalls, Type? expectedFailure)
    {
        var views = new FakeViews(trayThrows, pillThrows);

        var failure = Record.Exception(() => DictationStateViews.Show(pillFirst, views.Tray, views.Pill));

        Assert.Equal(expectedCalls, string.Join(",", views.Calls));
        if (expectedFailure is null)
        {
            Assert.Null(failure);
        }
        else
        {
            Assert.IsType(expectedFailure, failure);
        }
    }

    [Fact]
    public void The_first_view_s_failure_goes_on_only_after_the_second_view_ran()
    {
        var order = new List<string>();

        var failure = Record.Exception(() => DictationStateViews.Show(
            pillFirst: true,
            tray: () => order.Add("tray"),
            pill: () =>
            {
                order.Add("pill");
                throw new PillFailed();
            }));
        order.Add("caught");

        Assert.IsType<PillFailed>(failure);
        Assert.Equal(["pill", "tray", "caught"], order);
    }

    [Fact]
    public void Both_views_run_on_the_caller_s_thread_before_it_returns()
    {
        var caller = Environment.CurrentManagedThreadId;
        var threads = new List<int>();

        DictationStateViews.Show(false, () => threads.Add(Environment.CurrentManagedThreadId), () => threads.Add(Environment.CurrentManagedThreadId));

        Assert.Equal([caller, caller], threads);
    }

    [Fact]
    public void Both_views_must_be_given()
    {
        Assert.Throws<ArgumentNullException>(() => DictationStateViews.Show(true, null!, () => { }));
        Assert.Throws<ArgumentNullException>(() => DictationStateViews.Show(true, () => { }, null!));
    }
}
