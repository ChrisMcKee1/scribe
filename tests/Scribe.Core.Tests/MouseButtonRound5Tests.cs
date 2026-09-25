using Scribe.Core.Hotkeys;
using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

/// <summary>
/// Review round 5, as decided since round 6. A5 (continuing): a reinstall hands its sealed debts on as they are, and
/// nothing is decided about them until their release is made, on Windows' view at that moment. A6: a reading of up counts
/// only when GetAsyncKeyState could reach the foreground thread; one UIPI may have denied is unknown, and an unknown
/// release is let through.
/// </summary>
public sealed class MouseButtonRound5Tests
{
    private const uint Middle = MouseButtons.Middle;
    private const uint Back = MouseButtons.Back;

    private static HotkeyBinding Bare(uint button) => HotkeyCaptureSession.Build([button], HotkeyMode.Hold);

    // The reinstall asks Windows nothing: the replacement takes every sealed debt, and its release asks once and follows
    // the answer.
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(null, false)]
    public void A_reinstall_asks_windows_nothing_and_the_release_follows_one_answer(bool? view, bool swallowed)
    {
        var asked = 0;
        using var h = new HotkeyEngineHarness(Bare(Back), windowsView: new WindowsMouseView(_ => view == true, _ =>
        {
            asked++;
            return view;
        }));
        Assert.True(h.ButtonDown(Back).Suppress);

        var (replacement, _) = h.Router.BeginEngine(h.Transitions);

        Assert.Equal(0, asked);
        Assert.Equal(1 << (int)Back, replacement.OwedButtonReleases);

        Assert.Equal(swallowed, replacement.OnMouseButtonEvent(Back, isDown: false).Suppress);
        Assert.Equal(1, asked);
    }

    // A retired engine judges nothing: its sealed debts stay as they were, handed on, and an event that reaches it after
    // the retirement passes without a reading.
    [Fact]
    public void A_retired_engine_judges_nothing()
    {
        var asked = 0;
        using var h = new HotkeyEngineHarness(Bare(Back), windowsView: new WindowsMouseView(_ =>
        {
            asked++;
            return true;
        }, _ =>
        {
            asked++;
            return true;
        }));
        Assert.True(h.ButtonDown(Back).Suppress);
        h.Router.BeginEngine(h.Transitions);

        h.Engine.OnMouseHookLost();
        Assert.False(h.Engine.OnMouseButtonEvent(Back, isDown: false).Suppress);

        Assert.Equal(1 << (int)Back, h.Engine.OwedButtonReleases); // sealed and handed on; the old engine changes nothing
        Assert.Equal(0, h.Engine.UncertainButtonReleases);
        Assert.Equal(0, asked);
    }

    // A6: the reading, over its scripted parts.
    public static TheoryData<bool?, nint, nint, bool, bool?, bool?> Readings => new()
    {
        // desktop receives input, foreground before, foreground after, GetAsyncKeyState's high bit, window allows, result
        { true, 10, 10, true, false, true },   // down is Windows' own, whoever is in front: a failed call reads zero
        { true, 10, 10, false, true, false },  // up, and the call could reach the foreground thread
        { true, 10, 10, false, false, null },  // up, but an elevated window is in front: UIPI may have denied the call
        { true, 10, 10, false, null, null },   // up, and the foreground's integrity cannot be read
        { true, 0, 0, false, true, null },     // no foreground window to judge
        { true, 10, 11, false, true, null },   // the foreground changed during the reading
        { false, 10, 10, false, true, null },  // not the active desktop
        { null, 10, 10, true, true, null },    // the desktop cannot be told
    };

    [Theory]
    [MemberData(nameof(Readings))]
    public void A_reading_of_up_counts_only_when_the_call_could_reach_the_foreground_thread(
        bool? desktop, nint before, nint after, bool down, bool? allows, bool? expected)
    {
        var windows = new Queue<nint>([before, after]);

        var reading = NativeMethods.ReadMouseButtonState(
            Middle, () => desktop, () => windows.Dequeue(), _ => down, _ => allows);

        Assert.Equal(expected, reading);
    }

    // A6, end to end in memory: a leaked Middle press (Windows received it); an elevated window is in front when the
    // hook comes back, where GetAsyncKeyState reads zero. Nothing is decided then; the release, made once a normal app is
    // back in front, reads Windows holding Middle and reaches the app.
    [Fact]
    public void A_recovery_while_an_elevated_window_is_in_front_decides_nothing()
    {
        const nint Elevated = 42;
        var foreground = Elevated;
        bool ReadsDown(uint button) => button == Middle && foreground != Elevated; // UIPI zero while elevated
        bool? View(uint button) => NativeMethods.ReadMouseButtonState(
            button, () => true, () => foreground, ReadsDown, window => window == Elevated ? false : true);
        using var h = new HotkeyEngineHarness(Bare(Middle), windowsView: new WindowsMouseView(ReadsDown, View));
        Assert.True(h.ButtonDown(Middle).Suppress);

        h.Engine.OnMouseHookLost();
        foreground = 7; // a normal app is back in front

        Assert.False(h.ButtonUp(Middle).Suppress);
    }

    [Fact]
    public void The_integrity_reading_works_for_this_process_and_refuses_no_window()
    {
        Assert.NotNull(NativeMethods.ProcessIntegrityLevel((uint)Environment.ProcessId));
        Assert.Null(NativeMethods.WindowAllowsKeyStateReads(0));
        Assert.Null(NativeMethods.ProcessIntegrityLevel(0));
    }

    // UIPI's direction: a reading can reach a process of the same or a lower integrity level, never a higher one.
    [Theory]
    [InlineData(0x2000u, 0x2000u, true)]   // medium and medium: an ordinary app in front
    [InlineData(0x2000u, 0x1000u, true)]   // a low-integrity process in front, such as a sandboxed one
    [InlineData(0x2000u, 0x3000u, false)]  // an elevated app in front of a medium Scribe
    [InlineData(0x3000u, 0x3000u, true)]   // Scribe elevated too
    [InlineData(0x3000u, 0x4000u, false)]  // a system process's window in front of an elevated Scribe
    [InlineData(null, 0x2000u, null)]
    [InlineData(0x2000u, null, null)]
    public void A_reading_can_reach_the_foreground_only_at_the_same_or_a_lower_integrity_level(
        uint? own, uint? theirs, bool? expected)
    {
        Assert.Equal(expected, NativeMethods.IntegrityAllowsReads(own, theirs));
    }

    // The same rule on a real window no ordinary process owns: the desktop window belongs to the session's CSRSS, whose
    // integrity level is System, so the reading either cannot read its level (a medium Scribe) or finds it higher (an
    // elevated one); it never calls that window readable.
    [Fact]
    public void A_system_process_s_window_is_never_taken_as_readable()
    {
        Assert.NotEqual(true, NativeMethods.WindowAllowsKeyStateReads(GetDesktopWindow()));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetDesktopWindow();
}
