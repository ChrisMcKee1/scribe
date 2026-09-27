using Scribe.Core.Hotkeys;
using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

/// <summary>
/// Review round 4 (A5), as decided since round 7: a press the hook swallowed never reaches Windows, so Windows holding a
/// button means it received a press the hook did not swallow (a callback that missed its deadline passes its message on
/// and the hook is removed, or a press made while no hook existed), and that press's release must reach the app, or the
/// app keeps the button down. The lost-hook recovery and a reinstall no longer ask Windows anything: they drop what the
/// engine owed, so each such release reaches the app, and only a debt no gap has touched is judged, when its release is
/// made, by Windows' view, which is scripted through the engine's seam.
/// </summary>
public sealed class MouseButtonRound4Tests
{
    private const uint Middle = MouseButtons.Middle;
    private const uint Back = MouseButtons.Back;

    private static HotkeyBinding Bare(uint button) => HotkeyCaptureSession.Build([button], HotkeyMode.Hold);

    // Windows' view: down for the buttons it holds, up for the rest.
    private static Func<uint, bool> Holding(HashSet<uint> held) => held.Contains;

    // (1) Middle's press entered the engine and committed a debt, but its callback missed the deadline, so Windows got the
    // press and removed the hook; the renewal comes while Middle is still held. (2) A swallowed press was released while
    // the hook was gone, and another press reached Windows in the gap; the renewal comes while that one is held. Either
    // way Windows holds Middle when the release is made, and the real release must reach the app, as must later ones.
    [Theory]
    [InlineData("the press that missed its deadline")]
    [InlineData("a second press made while the hook was gone")]
    public void A_release_windows_is_waiting_for_reaches_the_app_after_a_lost_hook(string which)
    {
        var windows = new HashSet<uint>();
        using var h = new HotkeyEngineHarness(Bare(Middle), buttonDownInWindows: Holding(windows));
        Assert.True(h.ButtonDown(Middle).Suppress);
        windows.Add(Middle); // Windows received a Middle press the hook did not swallow
        h.TakeTransitions();

        h.Engine.OnMouseHookLost();

        Assert.Equal(0, h.Engine.OwedButtonReleases); // the gap dropped the debt (round 7)
        Assert.False(h.ButtonUp(Middle).Suppress, $"The release of {which} was swallowed, so the app keeps Middle down.");
        windows.Remove(Middle);

        // The next click is a dictation of its own, swallowed press and release alike, as for any bound button.
        var (down, up) = h.Click(Middle);
        Assert.True(down.Suppress);
        Assert.True(up.Suppress);
    }

    [Fact]
    public void A_swallowed_press_still_held_at_the_renewal_gets_its_release_through()
    {
        // Round 7's trade: Windows never saw the press and reports Middle up, but after a gap no reading in the callback
        // can be trusted to tell that press from one Windows did get, so the debt is dropped and the release passes.
        var windows = new HashSet<uint>();
        using var h = new HotkeyEngineHarness(Bare(Middle), buttonDownInWindows: Holding(windows));
        Assert.True(h.ButtonDown(Middle).Suppress);

        h.Engine.OnMouseHookLost();

        Assert.Equal(0, h.Engine.OwedButtonReleases);
        Assert.False(h.ButtonUp(Middle).Suppress);
    }

    [Fact]
    public void A_release_windows_cannot_vouch_for_after_a_lost_hook_gets_through()
    {
        using var h = new HotkeyEngineHarness(Bare(Back), buttonDownInWindows: _ => false);
        Assert.True(h.ButtonDown(Back).Suppress);

        h.Engine.OnMouseHookLost();

        Assert.False(h.ButtonUp(Back).Suppress); // at worst this one release reaches the app; no button is left down
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(null, false)]
    public void A_reinstall_s_replacement_lets_every_inherited_release_through(bool? windowsHoldsBack, bool swallowed)
    {
        using var h = new HotkeyEngineHarness(
            Bare(Back), buttonDownInWindows: _ => windowsHoldsBack == true);
        Assert.True(h.ButtonDown(Back).Suppress);

        var (replacement, _) = h.Router.BeginEngine(h.Transitions);

        Assert.Equal(0, replacement.OwedButtonReleases);
        Assert.Equal(swallowed, replacement.OnMouseButtonEvent(Back, isDown: false).Suppress);
    }
}
