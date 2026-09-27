using Scribe.Core.Hotkeys;
using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

/// <summary>
/// Review round 5, as decided since round 7. A5 (continuing): a reinstall hands nothing on, and asks Windows nothing, so
/// the release of a button held through it reaches the app. A6: no reading of Windows is trusted for a release a gap has
/// touched; the recovery reads nothing and the release passes. Round 5's foreground and integrity reading, and its tests,
/// went with round 7 (process inspection has no place inside a callback Windows removes on timeout).
/// </summary>
public sealed class MouseButtonRound5Tests
{
    private const uint Middle = MouseButtons.Middle;
    private const uint Back = MouseButtons.Back;

    private static HotkeyBinding Bare(uint button) => HotkeyCaptureSession.Build([button], HotkeyMode.Hold);

    // The reinstall asks Windows nothing, and neither does the replacement at the release: it owes nothing, whatever
    // Windows would say, so the release passes without a reading.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_reinstall_asks_windows_nothing_and_lets_the_release_through(bool windowsHoldsBack)
    {
        var asked = 0;
        using var h = new HotkeyEngineHarness(Bare(Back), buttonDownInWindows: _ =>
        {
            asked++;
            return windowsHoldsBack;
        });
        Assert.True(h.ButtonDown(Back).Suppress);

        var (replacement, _) = h.Router.BeginEngine(h.Transitions);

        Assert.Equal(0, replacement.OwedButtonReleases);
        Assert.False(replacement.OnMouseButtonEvent(Back, isDown: false).Suppress);
        Assert.Equal(0, asked);
    }

    // A retired engine judges nothing: its sealed debts stay as they were, and an event that reaches it after the
    // retirement passes without a reading.
    [Fact]
    public void A_retired_engine_judges_nothing()
    {
        var asked = 0;
        using var h = new HotkeyEngineHarness(Bare(Back), buttonDownInWindows: _ =>
        {
            asked++;
            return true;
        });
        Assert.True(h.ButtonDown(Back).Suppress);
        h.Router.BeginEngine(h.Transitions);

        h.Engine.OnMouseHookLost();
        Assert.False(h.Engine.OnMouseButtonEvent(Back, isDown: false).Suppress);

        Assert.Equal(1 << (int)Back, h.Engine.OwedButtonReleases); // sealed: the old engine changes nothing
        Assert.Equal(0, asked);
    }

    // A6, end to end in memory: a leaked Middle press (Windows received it); an elevated window is in front when the
    // hook comes back, where GetAsyncKeyState would read zero. Nothing is read or decided then; the release, made once a
    // normal app is back in front, reaches the app.
    [Fact]
    public void A_recovery_while_an_elevated_window_is_in_front_decides_nothing()
    {
        const nint Elevated = 42;
        var foreground = Elevated;
        var asked = 0;
        bool ReadsDown(uint button)
        {
            asked++;
            return button == Middle && foreground != Elevated; // UIPI makes it zero while the elevated window is in front
        }

        using var h = new HotkeyEngineHarness(Bare(Middle), buttonDownInWindows: ReadsDown);
        Assert.True(h.ButtonDown(Middle).Suppress);

        h.Engine.OnMouseHookLost();
        Assert.Equal(0, asked);
        foreground = 7; // a normal app is back in front

        Assert.False(h.ButtonUp(Middle).Suppress);
    }
}
