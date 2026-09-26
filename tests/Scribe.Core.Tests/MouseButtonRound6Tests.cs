using Scribe.Core.Hotkeys;
using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

/// <summary>
/// Review round 6, as decided since round 7: the release owed to a swallowed button press is decided when it is made, on
/// Windows' view at that moment, never on a snapshot taken when the hook came back. A press the hook never swallowed
/// shows in Windows' view as down, and its release must reach the app. A time no hook saw the mouse (a lost hook, a
/// reinstall) drops what the engine owed, so those releases pass (fail-open: at worst one stray release, never a button
/// left down in Windows); a debt no gap has touched is swallowed unless Windows shows the button down. Windows is
/// scripted: the buttons it holds, the window in front, and whether a window of a higher integrity level (which makes
/// GetAsyncKeyState return zero) was in front during a read.
/// </summary>
public sealed class MouseButtonRound6Tests
{
    private const uint Middle = MouseButtons.Middle;
    private const uint Back = MouseButtons.Back;

    private static HotkeyBinding Bare(uint button) => HotkeyCaptureSession.Build([button], HotkeyMode.Hold);

    // A5, Astra's ordering: Middle swallowed and owed; while no hook sees the mouse the user lets go and presses Middle
    // again, and that press is still inside another program's hook when Scribe's hook comes back; the older hook then
    // passes the press on and Windows holds Middle. Its real release must reach the app. Here the gap is a lost hook;
    // below, a reinstall.
    [Fact]
    public void A_press_that_reaches_windows_after_the_recovery_gets_its_release()
    {
        var windows = new ScriptedWindows();
        using var h = new HotkeyEngineHarness(Bare(Middle), buttonDownInWindows: windows.View);
        Assert.True(h.ButtonDown(Middle).Suppress);

        h.Engine.OnMouseHookLost();
        windows.Held.Add(Middle);

        Assert.False(h.ButtonUp(Middle).Suppress, "The release of a press Windows holds was swallowed: Middle stays down.");
    }

    [Fact]
    public void A_press_that_reaches_windows_after_a_reinstall_gets_its_release()
    {
        var windows = new ScriptedWindows();
        using var h = new HotkeyEngineHarness(Bare(Middle), buttonDownInWindows: windows.View);
        Assert.True(h.ButtonDown(Middle).Suppress);

        var (replacement, _) = h.Router.BeginEngine(h.Transitions);
        windows.Held.Add(Middle);

        Assert.False(replacement.OnMouseButtonEvent(Middle, isDown: false).Suppress);
    }

    // A6, Astra's ordering: a leaked Middle press (Windows holds it), and the foreground flips to an elevated window and
    // back around the recovery, when any read UIPI denies would be zero. Nothing is read or decided then: the later
    // release, made with the normal window in front, reaches the app.
    [Fact]
    public void A_recovery_while_the_foreground_flips_to_an_elevated_window_decides_nothing()
    {
        var windows = new ScriptedWindows();
        using var h = new HotkeyEngineHarness(Bare(Middle), buttonDownInWindows: windows.View);
        Assert.True(h.ButtonDown(Middle).Suppress);
        windows.Held.Add(Middle); // the callback missed its deadline, so Windows got the press

        windows.FlipToElevatedDuringRead = true;
        h.Engine.OnMouseHookLost();
        windows.FlipToElevatedDuringRead = false;

        Assert.Equal(0, windows.Reads);
        Assert.False(h.ButtonUp(Middle).Suppress);
    }

    // Normal, elevated, normal: the press was genuinely swallowed and the hook was lost and came back; the release is made
    // while an elevated window is in front, where a read would be zero whatever Windows holds. The gap dropped the debt,
    // so the release gets through without a read (the cost of fail-open: this one release). With the normal window back
    // in front the next click is a dictation again, press and release swallowed.
    [Fact]
    public void A_release_made_while_an_elevated_window_is_in_front_after_a_gap_gets_through()
    {
        var windows = new ScriptedWindows();
        using var h = new HotkeyEngineHarness(Bare(Back), buttonDownInWindows: windows.View);
        Assert.True(h.ButtonDown(Back).Suppress);
        h.Engine.OnMouseHookLost();

        windows.Foreground = ScriptedWindows.Elevated;
        Assert.False(h.ButtonUp(Back).Suppress);
        Assert.Equal(0, h.Engine.OwedButtonReleases);
        Assert.Equal(0, windows.Reads);

        windows.Foreground = ScriptedWindows.Normal;
        var (down, up) = h.Click(Back);
        Assert.True(down.Suppress);
        Assert.True(up.Suppress);
    }

    // Round 7's trade, where round 6 kept the control: a genuinely swallowed press held across the recovery and let go
    // with the normal window in front. After a gap no reading in the callback can be trusted to tell that press from one
    // Windows did get, so the gap drops the debt and the release reaches the app once: at worst one stray release.
    [Fact]
    public void A_genuinely_swallowed_press_held_across_a_recovery_gets_its_release_through()
    {
        var windows = new ScriptedWindows();
        using var h = new HotkeyEngineHarness(Bare(Middle), buttonDownInWindows: windows.View);
        Assert.True(h.ButtonDown(Middle).Suppress);

        h.Engine.OnMouseHookLost();

        Assert.False(h.ButtonUp(Middle).Suppress);
    }

    // A release no gap has touched needs no trust in a zero: every press since went through this hook, so Windows can hold
    // the button only if a press reached it some other way. Let go after a UAC prompt with the elevated app in front, it
    // stays swallowed...
    [Fact]
    public void A_release_owed_without_a_gap_stays_swallowed_whoever_is_in_front()
    {
        var windows = new ScriptedWindows();
        using var h = new HotkeyEngineHarness(Bare(Back), buttonDownInWindows: windows.View);
        Assert.True(h.ButtonDown(Back).Suppress);
        h.Engine.OnDesktopSwitchNotice(() => false); // the UAC prompt: the machines forget Back, the debt stays

        windows.Foreground = ScriptedWindows.Elevated;

        Assert.True(h.ButtonUp(Back).Suppress);
    }

    // ... and one Windows does hold (a press on the secure desktop, or from a second mouse) gets through: a button Windows
    // holds is never kept down by a release swallowed on a reading of down.
    [Fact]
    public void A_release_owed_without_a_gap_gets_through_when_windows_holds_the_button()
    {
        var windows = new ScriptedWindows();
        using var h = new HotkeyEngineHarness(Bare(Back), buttonDownInWindows: windows.View);
        Assert.True(h.ButtonDown(Back).Suppress);

        windows.Held.Add(Back);

        Assert.False(h.ButtonUp(Back).Suppress);
        Assert.Equal(0, h.Engine.OwedButtonReleases);
    }

    // Each owed button by its own reading: Windows holds the other two, not this one, so this release stays swallowed.
    [Theory]
    [InlineData(MouseButtons.Middle)]
    [InlineData(MouseButtons.Back)]
    [InlineData(MouseButtons.Forward)]
    public void Each_button_s_release_is_judged_by_that_button_s_reading(uint button)
    {
        var windows = new ScriptedWindows();
        using var h = new HotkeyEngineHarness(Bare(button), buttonDownInWindows: windows.View);
        Assert.True(h.ButtonDown(button).Suppress);
        foreach (var other in new[] { Middle, Back, MouseButtons.Forward })
        {
            if (other != button)
            {
                windows.Held.Add(other); // held by Windows, but not the button whose release this is
            }
        }

        Assert.True(h.ButtonUp(button).Suppress);
    }

    // Which gaps drop what the engine owes: a lost hook and a reinstall; not a desktop switch, capture or new bindings.
    // A press swallowed after the gap owes its release again.
    [Fact]
    public void Only_a_time_no_hook_saw_the_mouse_drops_an_owed_release()
    {
        const int BackBit = 1 << (int)Back;
        using var h = new HotkeyEngineHarness(Bare(Back));
        Assert.True(h.ButtonDown(Back).Suppress);
        h.Engine.OnDesktopSwitchNotice(() => false);
        h.Router.SetCaptureMode(true);
        h.Engine.OnWake();
        h.Router.SetCaptureMode(false);
        h.Router.UpdateBindings(Bare(Back), null);
        h.Engine.OnWake();
        Assert.Equal(BackBit, h.Engine.OwedButtonReleases);

        h.Engine.OnMouseHookLost();
        Assert.Equal(0, h.Engine.OwedButtonReleases);

        Assert.True(h.ButtonDown(Back).Suppress);
        Assert.Equal(BackBit, h.Engine.OwedButtonReleases);
        var (replacement, _) = h.Router.BeginEngine(h.Transitions);
        Assert.Equal(0, replacement.OwedButtonReleases);

        Assert.True(replacement.OnMouseButtonEvent(Back, isDown: true).Suppress);
        Assert.Equal(BackBit, replacement.OwedButtonReleases);
    }

    // The decision in the callback allocates nothing, for a release still owed and for one a gap dropped, with a view that
    // allocates nothing itself (the production one, cold, is measured in MouseButtonRound7Tests).
    // In the collection that runs alone (stream TR, item 1): nothing else in the process runs while it measures.
    [Collection(AllocationMeasurementCollection.Name)]
    public sealed class Allocations
    {
        [Fact]
        public void Deciding_an_owed_release_allocates_nothing()
        {
            using var h = new HotkeyEngineHarness(Bare(Back), buttonDownInWindows: static _ => false);
            for (var i = 0; i < 3; i++)
            {
                Assert.True(h.ButtonDown(Back).Suppress);
                h.Engine.OnDesktopSwitchNotice(() => false);
                Assert.True(h.ButtonUp(Back).Suppress);
                Assert.True(h.ButtonDown(Back).Suppress);
                h.Engine.OnMouseHookLost();
                Assert.False(h.ButtonUp(Back).Suppress);
                h.TakeTransitions();
            }

            _ = RuntimeWork.Now().Since(RuntimeWork.Now());
            Assert.True(h.ButtonDown(Back).Suppress);
            h.Engine.OnDesktopSwitchNotice(() => false);
            var work = RuntimeWork.Now();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var owed = h.ButtonUp(Back);
            var afterOwed = GC.GetAllocatedBytesForCurrentThread();
            var owedWork = RuntimeWork.Now().Since(work);
            Assert.True(h.ButtonDown(Back).Suppress);
            h.Engine.OnMouseHookLost();
            work = RuntimeWork.Now();
            var beforeDropped = GC.GetAllocatedBytesForCurrentThread();
            var dropped = h.ButtonUp(Back);
            var afterDropped = GC.GetAllocatedBytesForCurrentThread();
            var droppedWork = RuntimeWork.Now().Since(work);

            Assert.True(owed.Suppress);
            Assert.False(dropped.Suppress);
            AllocationMeasurement.AssertZero(
                afterOwed - before,
                owedWork,
                "Deciding a release still owed",
                rerun: () => h.ButtonUp(Back),
                prepare: () =>
                {
                    h.TakeTransitions();
                    h.ButtonDown(Back);
                    h.Engine.OnDesktopSwitchNotice(() => false);
                });
            AllocationMeasurement.AssertZero(
                afterDropped - beforeDropped,
                droppedWork,
                "Deciding a release a gap dropped",
                rerun: () => h.ButtonUp(Back),
                prepare: () =>
                {
                    h.TakeTransitions();
                    h.ButtonDown(Back);
                    h.Engine.OnMouseHookLost();
                });
        }
    }

    [Fact]
    public void The_service_gives_its_engines_the_real_view()
    {
        using var service = new HotkeyService(Microsoft.Extensions.Logging.Abstractions.NullLogger<HotkeyService>.Instance);

        Assert.Equal((Func<uint, bool>)NativeMethods.IsKeyLogicallyDown, service.WindowsButtonState);
    }

    /// <summary>
    /// A scripted Windows: the buttons its own state holds, the window in front, and an optional flip of the foreground to
    /// an elevated window during a read. While an elevated window is in front, GetAsyncKeyState fails under UIPI and
    /// returns zero, whatever Windows holds.
    /// </summary>
    private sealed class ScriptedWindows
    {
        public const nint Normal = 7;
        public const nint Elevated = 42;

        public HashSet<uint> Held { get; } = [];

        public nint Foreground { get; set; } = Normal;

        public bool FlipToElevatedDuringRead { get; set; }

        public int Reads { get; private set; }

        public Func<uint, bool> View => Read;

        private bool Read(uint button)
        {
            Reads++;
            var inFront = FlipToElevatedDuringRead ? Elevated : Foreground;
            return inFront != Elevated && Held.Contains(button);
        }
    }
}
