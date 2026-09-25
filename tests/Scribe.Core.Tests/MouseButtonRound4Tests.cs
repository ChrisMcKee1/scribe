using Scribe.Core.Hotkeys;
using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

/// <summary>
/// Review round 4 (A5), as decided since round 6: a press the hook swallowed never reaches Windows, so Windows holding a
/// button means it received a press the hook did not swallow (a callback that missed its deadline passes its message on
/// and the hook is removed, or a press made while no hook existed), and that press's release must reach the app, or the
/// app keeps the button down. The lost-hook recovery and a reinstall no longer ask Windows anything; they make the owed
/// releases uncertain, and each release is judged by Windows' view when it is made. Windows' view is scripted through the
/// engine's seam: the buttons it holds, and whether a reading can vouch for an up.
/// </summary>
public sealed class MouseButtonRound4Tests
{
    private const uint Middle = MouseButtons.Middle;
    private const uint Back = MouseButtons.Back;

    private static HotkeyBinding Bare(uint button) => HotkeyCaptureSession.Build([button], HotkeyMode.Hold);

    // A view where every up can be trusted: down for the buttons Windows holds, up for the rest.
    private static WindowsMouseView Holding(HashSet<uint> held) => new(held.Contains, button => held.Contains(button));

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
        using var h = new HotkeyEngineHarness(Bare(Middle), windowsView: Holding(windows));
        Assert.True(h.ButtonDown(Middle).Suppress);
        windows.Add(Middle); // Windows received a Middle press the hook did not swallow
        h.TakeTransitions();

        h.Engine.OnMouseHookLost();

        Assert.Equal(1 << (int)Middle, h.Engine.UncertainButtonReleases); // still owed, decided when released
        Assert.False(h.ButtonUp(Middle).Suppress, $"The release of {which} was swallowed, so the app keeps Middle down.");
        windows.Remove(Middle);

        // The next click is a dictation of its own, swallowed press and release alike, as for any bound button.
        var (down, up) = h.Click(Middle);
        Assert.True(down.Suppress);
        Assert.True(up.Suppress);
    }

    [Fact]
    public void A_swallowed_press_still_held_at_the_renewal_keeps_its_release_from_the_app()
    {
        // The control: Windows never saw the press, so it reports Middle up, and the release stays swallowed.
        var windows = new HashSet<uint>();
        using var h = new HotkeyEngineHarness(Bare(Middle), windowsView: Holding(windows));
        Assert.True(h.ButtonDown(Middle).Suppress);

        h.Engine.OnMouseHookLost();

        Assert.Equal(1 << (int)Middle, h.Engine.OwedButtonReleases);
        Assert.True(h.ButtonUp(Middle).Suppress);
    }

    [Fact]
    public void A_release_windows_cannot_vouch_for_after_a_lost_hook_gets_through()
    {
        using var h = new HotkeyEngineHarness(Bare(Back), windowsView: new WindowsMouseView(_ => false, _ => null));
        Assert.True(h.ButtonDown(Back).Suppress);

        h.Engine.OnMouseHookLost();

        Assert.False(h.ButtonUp(Back).Suppress); // at worst this one release reaches the app; no button is left down
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(null, false)]
    public void A_reinstall_s_replacement_swallows_only_a_release_windows_shows_up(bool? windowsHoldsBack, bool swallowed)
    {
        using var h = new HotkeyEngineHarness(
            Bare(Back), windowsView: new WindowsMouseView(_ => windowsHoldsBack == true, _ => windowsHoldsBack));
        Assert.True(h.ButtonDown(Back).Suppress);

        var (replacement, _) = h.Router.BeginEngine(h.Transitions);

        Assert.Equal(1 << (int)Back, replacement.OwedButtonReleases);
        Assert.Equal(swallowed, replacement.OnMouseButtonEvent(Back, isDown: false).Suppress);
    }
}
