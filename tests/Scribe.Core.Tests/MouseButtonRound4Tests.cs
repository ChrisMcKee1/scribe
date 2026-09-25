using Scribe.Core.Hotkeys;
using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

/// <summary>
/// Review round 4 (A5): the recovery from a lost mouse hook, and a reinstall that hands on owed releases, asks Windows
/// about every button whose release is still owed. A press the hook swallowed never reaches Windows, so Windows holding
/// the button means it received a press the hook did not swallow (a callback that missed its deadline passes its message
/// on and the hook is removed, or a press made while no hook existed); that press's release must reach the app, or the
/// app keeps the button down. Windows' view is scripted through the engine's seam.
/// </summary>
public sealed class MouseButtonRound4Tests
{
    private const uint Middle = MouseButtons.Middle;
    private const uint Back = MouseButtons.Back;

    private static HotkeyBinding Bare(uint button) => HotkeyCaptureSession.Build([button], HotkeyMode.Hold);

    // (1) Middle's press entered the engine and committed a debt, but its callback missed the deadline, so Windows got the
    // press and removed the hook; the renewal comes while Middle is still held. (2) A swallowed press was released while
    // the hook was gone, and another press reached Windows in the gap; the renewal comes while that one is held. Either
    // way Windows holds Middle at the renewal, and the real release must reach the app, as must later ones.
    [Theory]
    [InlineData("the press that missed its deadline")]
    [InlineData("a second press made while the hook was gone")]
    public void A_release_windows_is_waiting_for_reaches_the_app_after_a_lost_hook(string which)
    {
        var windows = new HashSet<uint>();
        using var h = new HotkeyEngineHarness(Bare(Middle), buttonHeldInWindows: button => windows.Contains(button));
        Assert.True(h.ButtonDown(Middle).Suppress);
        windows.Add(Middle); // Windows received a Middle press the hook did not swallow
        h.TakeTransitions();

        h.Engine.OnMouseHookLost();

        Assert.Equal(0, h.Engine.OwedButtonReleases);
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
        // The control: Windows never saw the press, so it reports Middle up, and the release is still owed.
        var windows = new HashSet<uint>();
        using var h = new HotkeyEngineHarness(Bare(Middle), buttonHeldInWindows: button => windows.Contains(button));
        Assert.True(h.ButtonDown(Middle).Suppress);

        h.Engine.OnMouseHookLost();

        Assert.Equal(1 << (int)Middle, h.Engine.OwedButtonReleases);
        Assert.True(h.ButtonUp(Middle).Suppress);
    }

    [Fact]
    public void A_button_windows_cannot_tell_about_is_forgiven_rather_than_risk_a_stuck_press()
    {
        using var h = new HotkeyEngineHarness(Bare(Back), buttonHeldInWindows: _ => null);
        Assert.True(h.ButtonDown(Back).Suppress);

        h.Engine.OnMouseHookLost();

        Assert.Equal(0, h.Engine.OwedButtonReleases);
        Assert.False(h.ButtonUp(Back).Suppress); // at worst one release reaches the app; no press is stranded
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1 << (int)Back)]
    [InlineData(null, 0)]
    public void A_reinstall_hands_on_only_the_releases_windows_is_not_waiting_for(bool? windowsHoldsBack, int handedOn)
    {
        using var h = new HotkeyEngineHarness(Bare(Back), buttonHeldInWindows: _ => windowsHoldsBack);
        Assert.True(h.ButtonDown(Back).Suppress);

        var (replacement, _) = h.Router.BeginEngine(h.Transitions);

        Assert.Equal(handedOn, replacement.OwedButtonReleases);
        Assert.Equal(handedOn != 0, replacement.OnMouseButtonEvent(Back, isDown: false).Suppress);
    }

    [Fact]
    public void Only_the_owed_buttons_windows_holds_are_forgiven()
    {
        var owed = (1 << (int)Middle) | (1 << (int)Back) | (1 << (int)MouseButtons.Forward);
        var asked = new List<uint>();

        var kept = HotkeyEngine.ReleasesWindowsDoesNotHold(owed, button =>
        {
            asked.Add(button);
            return button switch { Middle => true, Back => false, _ => null };
        });

        Assert.Equal(1 << (int)Back, kept);
        Assert.Equal(new[] { Middle, Back, MouseButtons.Forward }, asked.ToArray());
        Assert.Equal(owed, HotkeyEngine.ReleasesWindowsDoesNotHold(owed, null)); // no view: everything stays owed
        Assert.Equal(0, HotkeyEngine.ReleasesWindowsDoesNotHold(0, _ => throw new InvalidOperationException("asked")));
    }
}
