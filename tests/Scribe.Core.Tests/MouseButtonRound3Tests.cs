using Scribe.Core.Hotkeys;
using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

/// <summary>
/// Review round 3 of the mouse button hotkeys, in the engine and the leak check: the leak check never injects a mouse
/// button event, and a press judged while a reinstall retires its engine keeps its press and release paired. Each
/// interleaving is driven through a real seam, Windows' view of a key or button, which the code under test asks in the
/// middle of its work. The drain-only mouse hook's tests install real hooks, so they are Start_ tests in
/// HotkeyServiceMouseTests.
/// </summary>
public sealed class MouseButtonRound3Tests
{
    private const uint Middle = MouseButtons.Middle;
    private const uint Back = MouseButtons.Back;
    private const uint LeftCtrl = 0xA2;

    private static HotkeyBinding Bare(uint button, HotkeyMode mode = HotkeyMode.Hold) =>
        HotkeyCaptureSession.Build([button], mode);

    // A3: Middle held and swallowed; Set begins, and Middle's owed release is swallowed during the capture; the leak check
    // starts; while it asks Windows about Middle, the user presses Middle again for the capture, which passes through, and
    // Windows now holds Middle for real. Nothing may be injected, in either order.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_leak_check_injects_no_mouse_button_event_whatever_interleaves_with_it(bool pressDuringTheCheck)
    {
        using var h = new HotkeyEngineHarness(Bare(Middle));
        var windows = new HashSet<uint>();
        var injected = new List<uint>();
        var reconciler = HotkeyService.CreateReconciler(
            h.Router,
            key =>
            {
                if (pressDuringTheCheck && key == Middle && !windows.Contains(Middle))
                {
                    Assert.False(h.ButtonDown(Middle).Suppress); // the capture's press passes through
                    windows.Add(Middle);
                }

                return windows.Contains(key);
            },
            key =>
            {
                injected.Add(key);
                return true;
            });
        h.ButtonDown(Middle);
        h.Router.SetCaptureMode(true);
        Assert.True(h.ButtonUp(Middle).Suppress); // owed, so swallowed even during the capture
        if (!pressDuringTheCheck)
        {
            Assert.False(h.ButtonDown(Middle).Suppress);
            windows.Add(Middle);
        }

        var result = reconciler.ReleaseLeakedKeys(Bare(Middle));

        Assert.Empty(result.Released);
        Assert.Empty(result.Failed);
        Assert.Empty(injected);
    }

    [Fact]
    public void The_leak_check_never_lists_a_mouse_button_and_still_lists_the_keys_held_with_one()
    {
        Assert.Empty(SuppressedKeyReconciler.CandidateKeys(Bare(Back)));
        Assert.Equal(
            new[] { LeftCtrl },
            SuppressedKeyReconciler.CandidateKeys(HotkeyCaptureSession.Build([LeftCtrl, Back], HotkeyMode.Hold)).ToArray());
        Assert.Equal(
            new[] { 0xA2u, 0xA3u },
            SuppressedKeyReconciler.CandidateKeys(
                HotkeyCaptureSession.Build([LeftCtrl, 0xA0, Middle], HotkeyMode.Hold))
                .Where(key => key is 0xA2 or 0xA3).OrderBy(key => key).ToArray());
    }

    // A4: the reinstall's retirement lands while the old engine's callback is judging a Back press (Windows is asked about
    // the Ctrl the hook's view holds, and the reinstall happens right there). The press lost its commit to the
    // retirement, so it must reach the app, and so must its release, which goes to the replacement.
    [Fact]
    public void A_press_judged_while_a_reinstall_retires_its_engine_reaches_the_app_with_its_release()
    {
        HotkeyEngineHarness? harness = null;
        HotkeyEngine? replacement = null;
        using var h = new HotkeyEngineHarness(Bare(Back), isLogicallyDown: _ =>
        {
            replacement ??= harness!.Router.BeginEngine(harness.Transitions).Engine;
            return false; // Ctrl is up in Windows' view, so the press completes the bare binding
        });
        harness = h;
        h.Down(LeftCtrl);

        var down = h.ButtonDown(Back);

        Assert.NotNull(replacement);
        Assert.True(h.Engine.IsRetired);
        Assert.False(down.Suppress);
        Assert.Equal(0, h.Engine.OwedButtonReleases);
        Assert.Equal(0, replacement!.OwedButtonReleases);
        Assert.False(replacement.OnMouseButtonEvent(Back, isDown: false).Suppress);
    }

    // The same seal when the reinstall's 2 s join times out and the old hook thread keeps running: nothing the old engine
    // sees afterwards, press or release, is swallowed or changes its sealed debt. Since round 7 the replacement starts
    // owing nothing (a reinstall is a gap in the hook's view), so that release reaches the app once.
    [Fact]
    public void A_retired_engine_whose_thread_outlived_its_join_swallows_nothing_after_and_its_release_reaches_the_app()
    {
        using var h = new HotkeyEngineHarness(Bare(Back));
        Assert.True(h.ButtonDown(Back).Suppress);
        var (replacement, _) = h.Router.BeginEngine(h.Transitions);

        Assert.Equal(0, replacement.OwedButtonReleases);
        Assert.False(h.Engine.OnMouseButtonEvent(Back, isDown: false).Suppress);
        Assert.False(h.Engine.OnMouseButtonEvent(Back, isDown: true).Suppress);
        Assert.False(h.Engine.OnMouseButtonEvent(Back, isDown: false).Suppress);
        Assert.Equal(1 << (int)Back, h.Engine.OwedButtonReleases); // sealed: the old thread changes nothing
        Assert.False(replacement.OnMouseButtonEvent(Back, isDown: false).Suppress);
    }
}
