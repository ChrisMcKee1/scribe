using Scribe.Core.Hotkeys;
using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

/// <summary>
/// Review round 3, item 1 (Astra's A6). Whether a key-down is uncertain after the hook became the newest registration was
/// judged against the key view before the commands the engine applies at the start of the event, while the machines then
/// processed it against the view after them. A command that clears the machines' key state (a capture start and end, new
/// bindings) queued between two repeats of a key whose press a hook ahead of Scribe's may have kept turned its next repeat
/// into a fresh press judged certain: swallowed, and its release with it, although that hook forwarded the press. The
/// engine now applies the pending commands once, first, and judges on the view the machines use
/// (<see cref="HotkeyEngine.OnKeyEvent"/>). Pause and a toggle's cancel clear no key state; they are guards here.
/// </summary>
public sealed class KeyboardHookUncertaintyOrderTests
{
    private const uint RightCtrl = 0xA3;
    private const uint LeftCtrl = 0xA2;
    private const uint PageDown = 0x22;
    private const uint Moved = 10_000;
    private const int Window = 875;

    public enum ClearingCommand
    {
        CaptureStartedAndEnded,
        NewBindings,
        PausedAndResumed,
        ToggleCancelled,
    }

    [Theory]
    [InlineData(ClearingCommand.CaptureStartedAndEnded)] // Astra's sequence
    [InlineData(ClearingCommand.NewBindings)]
    [InlineData(ClearingCommand.PausedAndResumed)] // clears no key state: a guard
    [InlineData(ClearingCommand.ToggleCancelled)] // clears no key state: a guard
    public void A_command_queued_between_two_repeats_inside_the_window_leaves_the_uncertain_keystroke_unswallowed(
        ClearingCommand command)
    {
        using var h = Armed(HotkeyBinding.Legacy);
        Assert.False(h.Engine.OnKeyEvent(RightCtrl, isDown: true, eventTime: Moved + 100).Suppress);
        var activation = h.TakeTransitions().Single().Activation;

        Queue(h, command, activation);

        Assert.False(h.Engine.OnKeyEvent(RightCtrl, isDown: true, eventTime: Moved + 130).Suppress);
        Assert.False(h.Engine.OnKeyEvent(RightCtrl, isDown: false, eventTime: Moved + 160).Suppress);
        Assert.False(h.Engine.HoldsSwallowedKey);

        // The next press, after a release seen since the move, is swallowed as ever.
        Assert.True(h.Engine.OnKeyEvent(RightCtrl, isDown: true, eventTime: Moved + 190).Suppress);
        Assert.True(h.Engine.OnKeyEvent(RightCtrl, isDown: false, eventTime: Moved + 220).Suppress);
    }

    [Fact]
    public void A_key_seen_down_before_the_move_is_judged_on_the_view_the_commands_leave()
    {
        // The ordering alone. Ctrl+Page Down passed as another command before the move (a bare Page Down binding), so Page
        // Down's press went on to every hook behind Scribe's and to the app. Ctrl is let go after the move with Page Down still
        // held, and a capture start and end are queued. Judged before they apply, Page Down's next repeat was held, so certain;
        // processed after them, it was a fresh bare Page Down with no modifier: swallowed, and its release too, and the app
        // (or the remote session) kept Page Down down. Judged after them, the key is one the view does not hold and the
        // window is open: uncertain, so nothing of it is swallowed.
        using var h = new HotkeyEngineHarness(HotkeyBinding.DefaultDictation);
        h.Engine.SetUncertaintyWindow(Window);
        Assert.False(h.Engine.OnKeyEvent(LeftCtrl, isDown: true, eventTime: Moved - 600).Suppress);
        Assert.False(h.Engine.OnKeyEvent(PageDown, isDown: true, eventTime: Moved - 500).Suppress);
        h.Engine.OnRegisteredAhead(Moved);

        Assert.False(h.Engine.OnKeyEvent(PageDown, isDown: true, eventTime: Moved + 40).Suppress);
        Assert.False(h.Engine.OnKeyEvent(LeftCtrl, isDown: false, eventTime: Moved + 60).Suppress);
        h.Router.SetCaptureMode(true);
        h.Router.SetCaptureMode(false);

        Assert.False(h.Engine.OnKeyEvent(PageDown, isDown: true, eventTime: Moved + 80).Suppress);
        Assert.False(h.Engine.OnKeyEvent(PageDown, isDown: false, eventTime: Moved + 110).Suppress);
        Assert.Equal(1, h.Engine.UncertainPresses);
    }

    [Fact]
    public void Without_a_move_no_keystroke_is_passing_and_every_rule_is_as_before()
    {
        using var h = new HotkeyEngineHarness(HotkeyBinding.Legacy);
        h.Engine.SetUncertaintyWindow(Window);

        Assert.True(h.Engine.OnKeyEvent(RightCtrl, isDown: true, eventTime: 100).Suppress);
        h.Router.SetCaptureMode(true);
        h.Router.SetCaptureMode(false);
        Assert.True(h.Engine.OnKeyEvent(RightCtrl, isDown: true, eventTime: 130).Suppress); // a fresh press after the clear
        Assert.True(h.Engine.OnKeyEvent(RightCtrl, isDown: false, eventTime: 160).Suppress);
        Assert.Equal(0, h.Engine.UncertainPresses);
    }

    private static void Queue(HotkeyEngineHarness h, ClearingCommand command, long activation)
    {
        switch (command)
        {
            case ClearingCommand.CaptureStartedAndEnded:
                h.Router.SetCaptureMode(true);
                h.Router.SetCaptureMode(false);
                break;
            case ClearingCommand.NewBindings:
                h.Router.UpdateBindings(HotkeyBinding.Legacy, HotkeyBinding.DefaultDictationOnly);
                break;
            case ClearingCommand.PausedAndResumed:
                h.Router.SetPaused(true);
                h.Router.SetPaused(false);
                break;
            case ClearingCommand.ToggleCancelled:
                h.Router.CancelToggle(activation);
                break;
        }
    }

    private static HotkeyEngineHarness Armed(HotkeyBinding binding)
    {
        var h = new HotkeyEngineHarness(binding);
        h.Engine.SetUncertaintyWindow(Window);
        h.Engine.OnRegisteredAhead(Moved);
        return h;
    }
}
