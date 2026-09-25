using Scribe.Core.Hotkeys;
using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

/// <summary>
/// Review round 6: the release owed to a swallowed button press is decided when it is made, on Windows' view at that
/// moment, not on a snapshot taken when the hook came back. A press the hook never swallowed shows in Windows' view as
/// down, and its release must reach the app; a release a gap has made uncertain is swallowed only on an up the read can
/// vouch for, and let through on any doubt (fail-open: at worst one stray release, never a button left down in Windows);
/// a release no gap has touched needs no such trust. Windows is scripted: the buttons it holds, the window in front, and
/// whether a window of a higher integrity level (which makes GetAsyncKeyState return zero) was in front during a read.
/// </summary>
public sealed class MouseButtonRound6Tests
{
    private const uint Middle = MouseButtons.Middle;
    private const uint Back = MouseButtons.Back;

    private static HotkeyBinding Bare(uint button) => HotkeyCaptureSession.Build([button], HotkeyMode.Hold);

    // A5, Astra's ordering: Middle swallowed and owed; while no hook sees the mouse the user lets go and presses Middle
    // again, and that press is still inside another program's hook when Scribe's hook comes back, so Windows shows Middle
    // up at the recovery; the older hook then passes the press on and Windows holds Middle. Its real release must reach
    // the app. Here the gap is a lost hook; below, a reinstall.
    [Fact]
    public void A_press_that_reaches_windows_after_the_recovery_gets_its_release()
    {
        var windows = new ScriptedWindows();
        using var h = new HotkeyEngineHarness(Bare(Middle), windowsView: windows.View);
        Assert.True(h.ButtonDown(Middle).Suppress);

        h.Engine.OnMouseHookLost();
        windows.Held.Add(Middle);

        Assert.False(h.ButtonUp(Middle).Suppress, "The release of a press Windows holds was swallowed: Middle stays down.");
    }

    [Fact]
    public void A_press_that_reaches_windows_after_a_reinstall_gets_its_release()
    {
        var windows = new ScriptedWindows();
        using var h = new HotkeyEngineHarness(Bare(Middle), windowsView: windows.View);
        Assert.True(h.ButtonDown(Middle).Suppress);

        var (replacement, _) = h.Router.BeginEngine(h.Transitions);
        windows.Held.Add(Middle);

        Assert.False(replacement.OnMouseButtonEvent(Middle, isDown: false).Suppress);
    }

    // A6, Astra's ordering: a leaked Middle press (Windows holds it); the read at the recovery is taken while the foreground
    // flips to an elevated window and back, so UIPI makes it zero while both foreground samples name the normal window.
    // Nothing may be decided on it: the later release, made with the normal window in front, must reach the app.
    [Fact]
    public void A_recovery_read_uipi_denied_between_two_equal_samples_decides_nothing()
    {
        var windows = new ScriptedWindows();
        using var h = new HotkeyEngineHarness(Bare(Middle), windowsView: windows.View);
        Assert.True(h.ButtonDown(Middle).Suppress);
        windows.Held.Add(Middle); // the callback missed its deadline, so Windows got the press

        windows.FlipToElevatedDuringRead = true;
        h.Engine.OnMouseHookLost();
        windows.FlipToElevatedDuringRead = false;

        Assert.False(h.ButtonUp(Middle).Suppress);
    }

    // Normal, elevated, normal, at the release itself: the press was genuinely swallowed and the hook was lost and came
    // back; the release is made while an elevated window is in front, so the read's zero cannot be trusted and the release
    // gets through (the cost of fail-open: this one release). With the normal window back in front the next click is a
    // dictation again, press and release swallowed.
    [Fact]
    public void A_release_made_while_an_elevated_window_is_in_front_after_a_gap_gets_through()
    {
        var windows = new ScriptedWindows();
        using var h = new HotkeyEngineHarness(Bare(Back), windowsView: windows.View);
        Assert.True(h.ButtonDown(Back).Suppress);
        h.Engine.OnMouseHookLost();

        windows.Foreground = ScriptedWindows.Elevated;
        Assert.False(h.ButtonUp(Back).Suppress);
        Assert.Equal(0, h.Engine.OwedButtonReleases);
        Assert.Equal(0, h.Engine.UncertainButtonReleases);

        windows.Foreground = ScriptedWindows.Normal;
        var (down, up) = h.Click(Back);
        Assert.True(down.Suppress);
        Assert.True(up.Suppress);
    }

    // The control: a genuinely swallowed press held across the recovery and let go with the normal window in front, where
    // the read is an up it can vouch for, keeps its release from the app.
    [Fact]
    public void A_genuinely_swallowed_press_held_across_a_recovery_keeps_its_release_from_the_app()
    {
        var windows = new ScriptedWindows();
        using var h = new HotkeyEngineHarness(Bare(Middle), windowsView: windows.View);
        Assert.True(h.ButtonDown(Middle).Suppress);

        h.Engine.OnMouseHookLost();

        Assert.True(h.ButtonUp(Middle).Suppress);
    }

    // A release no gap has touched needs no trust in a zero: every press since went through this hook, so Windows can hold
    // the button only if a press reached it some other way. Let go after a UAC prompt with the elevated app in front, it
    // stays swallowed...
    [Fact]
    public void A_release_owed_without_a_gap_stays_swallowed_whoever_is_in_front()
    {
        var windows = new ScriptedWindows();
        using var h = new HotkeyEngineHarness(Bare(Back), windowsView: windows.View);
        Assert.True(h.ButtonDown(Back).Suppress);
        h.Engine.OnDesktopSwitchNotice(() => false); // the UAC prompt: the machines forget Back, the debt stays

        windows.Foreground = ScriptedWindows.Elevated;

        Assert.True(h.ButtonUp(Back).Suppress);
    }

    // ... and one Windows does hold (a press on the secure desktop, or from a second mouse) gets through: a button Windows
    // holds is never left down.
    [Fact]
    public void A_release_owed_without_a_gap_gets_through_when_windows_holds_the_button()
    {
        var windows = new ScriptedWindows();
        using var h = new HotkeyEngineHarness(Bare(Back), windowsView: windows.View);
        Assert.True(h.ButtonDown(Back).Suppress);

        windows.Held.Add(Back);

        Assert.False(h.ButtonUp(Back).Suppress);
        Assert.Equal(0, h.Engine.OwedButtonReleases);
    }

    // Each owed button by its own reading.
    [Theory]
    [InlineData(MouseButtons.Middle)]
    [InlineData(MouseButtons.Back)]
    [InlineData(MouseButtons.Forward)]
    public void Each_button_s_release_is_judged_by_that_button_s_reading(uint button)
    {
        var windows = new ScriptedWindows();
        using var h = new HotkeyEngineHarness(Bare(button), windowsView: windows.View);
        Assert.True(h.ButtonDown(button).Suppress);
        h.Engine.OnMouseHookLost();
        foreach (var other in new[] { Middle, Back, MouseButtons.Forward })
        {
            if (other != button)
            {
                windows.Held.Add(other); // held by Windows, but not the button whose release this is
            }
        }

        Assert.True(h.ButtonUp(button).Suppress);
    }

    // Which gaps make a debt uncertain: a lost hook and a reinstall; not a desktop switch, capture or new bindings. A
    // press swallowed after the gap is certain again, and a new press of the button forgives the old debt either way.
    [Fact]
    public void Only_a_time_no_hook_saw_the_mouse_makes_an_owed_release_uncertain()
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
        Assert.Equal(0, h.Engine.UncertainButtonReleases);

        h.Engine.OnMouseHookLost();
        Assert.Equal(BackBit, h.Engine.UncertainButtonReleases);

        var (replacement, _) = h.Router.BeginEngine(h.Transitions);
        Assert.Equal(BackBit, replacement.OwedButtonReleases);
        Assert.Equal(BackBit, replacement.UncertainButtonReleases);

        Assert.True(replacement.OnMouseButtonEvent(Back, isDown: true).Suppress);
        Assert.Equal(BackBit, replacement.OwedButtonReleases);
        Assert.Equal(0, replacement.UncertainButtonReleases);
    }

    // A reading taken while this desktop stops receiving input (a UAC prompt arriving mid-read) cannot be trusted either.
    [Fact]
    public void A_reading_whose_desktop_stops_receiving_input_during_it_cannot_be_trusted()
    {
        var desktop = new Queue<bool?>([true, false]);

        var reading = NativeMethods.ReadMouseButtonState(
            Middle, () => desktop.Count > 0 ? desktop.Dequeue() : false, () => ScriptedWindows.Normal, _ => false, _ => true);

        Assert.Null(reading);
    }

    // The decision in the callback allocates nothing, for a certain and an uncertain debt, with a view that allocates
    // nothing itself (the real one is measured below).
    [Fact]
    public void Deciding_an_owed_release_allocates_nothing()
    {
        var view = new WindowsMouseView(static _ => false, static _ => false);
        using var h = new HotkeyEngineHarness(Bare(Back), windowsView: view);
        for (var i = 0; i < 3; i++)
        {
            Assert.True(h.ButtonDown(Back).Suppress);
            h.Engine.OnDesktopSwitchNotice(() => false);
            Assert.True(h.ButtonUp(Back).Suppress);
            Assert.True(h.ButtonDown(Back).Suppress);
            h.Engine.OnMouseHookLost();
            Assert.True(h.ButtonUp(Back).Suppress);
            h.TakeTransitions();
        }

        Assert.True(h.ButtonDown(Back).Suppress);
        h.Engine.OnDesktopSwitchNotice(() => false);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var certain = h.ButtonUp(Back);
        var afterCertain = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(h.ButtonDown(Back).Suppress);
        h.Engine.OnMouseHookLost();
        var beforeUncertain = GC.GetAllocatedBytesForCurrentThread();
        var uncertain = h.ButtonUp(Back);
        var afterUncertain = GC.GetAllocatedBytesForCurrentThread();

        Assert.True(certain.Suppress);
        Assert.True(uncertain.Suppress);
        Assert.Equal(0, afterCertain - before);
        Assert.Equal(0, afterUncertain - beforeUncertain);
    }

    // The real reading, as the callback calls it: whatever this machine's desktop and foreground are, it allocates nothing
    // on the managed heap.
    [Fact]
    public void The_real_reading_allocates_nothing()
    {
        _ = NativeMethods.MouseButtonStateInWindows(Middle);
        _ = WindowsMouseView.Native.IsDown(Middle);

        var before = GC.GetAllocatedBytesForCurrentThread();
        _ = NativeMethods.MouseButtonStateInWindows(Middle);
        _ = WindowsMouseView.Native.IsDown(Middle);
        _ = NativeMethods.ProcessIntegrityLevel((uint)Environment.ProcessId);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void The_service_gives_its_engines_the_real_view()
    {
        using var service = new HotkeyService(Microsoft.Extensions.Logging.Abstractions.NullLogger<HotkeyService>.Instance);

        Assert.Same(WindowsMouseView.Native, service.WindowsButtonView);
    }

    /// <summary>
    /// A scripted Windows: the buttons its own state holds, the window in front, and an optional flip of the foreground to
    /// an elevated window during a read, which neither foreground sample around it sees. While an elevated window is in
    /// front, GetAsyncKeyState fails under UIPI and returns zero.
    /// </summary>
    private sealed class ScriptedWindows
    {
        public const nint Normal = 7;
        public const nint Elevated = 42;

        public HashSet<uint> Held { get; } = [];

        public nint Foreground { get; set; } = Normal;

        public bool FlipToElevatedDuringRead { get; set; }

        public WindowsMouseView View => new(Read, button => NativeMethods.ReadMouseButtonState(
            button, () => true, () => Foreground, Read, window => window == 0 ? null : window != Elevated));

        private bool Read(uint button)
        {
            var inFront = FlipToElevatedDuringRead ? Elevated : Foreground;
            return inFront != Elevated && Held.Contains(button);
        }
    }
}
