using Scribe.Core.Hotkeys;
using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

/// <summary>
/// Review round 5. A5 (continuing): a reinstall hands its sealed debts on as they are, and the replacement asks Windows
/// about them only once its own mouse hook exists, because until then a release and a new press can still reach Windows
/// unseen. A6: a reading of up counts only when GetAsyncKeyState could reach the foreground thread; one UIPI may have
/// denied is unknown, which drops the debt.
/// </summary>
public sealed class MouseButtonRound5Tests
{
    private const uint Middle = MouseButtons.Middle;
    private const uint Back = MouseButtons.Back;

    private static HotkeyBinding Bare(uint button) => HotkeyCaptureSession.Build([button], HotkeyMode.Hold);

    // A5: Middle swallowed; the reinstall retires the old engine while Windows still shows Middle up (the press was
    // swallowed); before the replacement's mouse hook exists the user lets go and presses Middle again, both reaching
    // Windows; the hook thread registers the hook and only then asks. The real release of that second press must reach
    // the app.
    [Fact]
    public void A_debt_handed_on_by_a_reinstall_is_judged_when_the_replacement_s_hook_exists_not_before()
    {
        var windows = new HashSet<uint>();
        using var h = new HotkeyEngineHarness(Bare(Middle), buttonHeldInWindows: button => windows.Contains(button));
        Assert.True(h.ButtonDown(Middle).Suppress);

        var (replacement, _) = h.Router.BeginEngine(h.Transitions);
        Assert.Equal(1 << (int)Middle, replacement.OwedButtonReleases);
        windows.Add(Middle);

        replacement.ReconcileOwedReleases(); // what the new hook thread does right after its first registration

        Assert.Equal(0, replacement.OwedButtonReleases);
        Assert.False(replacement.OnMouseButtonEvent(Middle, isDown: false).Suppress);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public void A_reinstall_hands_on_every_sealed_debt_and_the_replacement_keeps_only_what_windows_shows_up(bool? view)
    {
        var asked = 0;
        using var h = new HotkeyEngineHarness(Bare(Back), buttonHeldInWindows: _ =>
        {
            asked++;
            return view;
        });
        Assert.True(h.ButtonDown(Back).Suppress);

        var (replacement, _) = h.Router.BeginEngine(h.Transitions);

        Assert.Equal(0, asked); // nothing is decided before the replacement's hook exists
        Assert.Equal(1 << (int)Back, replacement.OwedButtonReleases);

        replacement.ReconcileOwedReleases();

        Assert.Equal(1, asked);
        Assert.Equal(view == false ? 1 << (int)Back : 0, replacement.OwedButtonReleases);
        Assert.Equal(1, replacement.OwedReleaseReconciliations);
    }

    [Fact]
    public void A_retired_engine_reconciles_nothing()
    {
        using var h = new HotkeyEngineHarness(Bare(Back), buttonHeldInWindows: _ => true);
        Assert.True(h.ButtonDown(Back).Suppress);
        h.Router.BeginEngine(h.Transitions);

        h.Engine.ReconcileOwedReleases();

        Assert.Equal(1 << (int)Back, h.Engine.OwedButtonReleases); // sealed and handed on; the old engine changes nothing
        Assert.Equal(0, h.Engine.OwedReleaseReconciliations);
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

    // A6, end to end in memory: a leaked Middle press (Windows received it); an elevated window takes the foreground, so
    // GetAsyncKeyState reads zero; the recovery must not take that for up, or the real release, made once a normal app
    // is back in front, is swallowed and Windows keeps Middle down.
    [Fact]
    public void A_recovery_while_an_elevated_window_is_in_front_drops_the_debt()
    {
        const nint Elevated = 42;
        var foreground = Elevated;
        bool? View(uint button) => NativeMethods.ReadMouseButtonState(
            button, () => true, () => foreground, _ => false, window => window == Elevated ? false : true);
        using var h = new HotkeyEngineHarness(Bare(Middle), buttonHeldInWindows: View);
        Assert.True(h.ButtonDown(Middle).Suppress);

        h.Engine.OnMouseHookLost();
        foreground = 7; // a normal app is back in front

        Assert.Equal(0, h.Engine.OwedButtonReleases);
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
