using Scribe.Core.Models;
using Scribe.Core.Overlay;

namespace Scribe.App.Overlay;

/// <summary>
/// Abstraction over the recording pill so the WPF host can drive it without caring whether it is the
/// in-process WPF window or the out-of-process WinUI 3 overlay. The WinUI overlay
/// (<see cref="OverlayProcessClient"/>) is the default: it renders the pill in a separate kept-warm
/// process via DWM composition, sidestepping the WPF <c>AllowsTransparency</c>/layered-window path
/// that produced the recurring "black box". The implementation owns the helper's idle lifetime itself,
/// independently of the speech models: callers push the keep-warm period and whether the helper is kept resident (the pill
/// is on and dictation is not paused), and may ask for a release on pause or for a warmup, but never end the helper directly.
/// </summary>
public interface IOverlayController
{
    /// <summary>
    /// Pre-warms the overlay (launches the helper process / presents the surface) before first use. A
    /// warmed helper that is never shown follows the idle deadline like any other: trimmed and kept while it is kept
    /// resident, ended otherwise. A warmup on a running helper is only a no-op write.
    /// </summary>
    void Warmup();

    /// <summary>Listening: the listening edge and the live level bars.</summary>
    void ShowRecording();

    /// <summary>Brief warning text while the recording indicator and live level bars remain active.</summary>
    void ShowRecordingWarning(string? reason);

    /// <summary>
    /// Processing: three dots, and words that say whether it is transcribing, running AI cleanup, or waiting for AI
    /// cleanup's model on this PC to start (<paramref name="startingLocalModel"/>, with "This can take time").
    /// </summary>
    void ShowProcessing(bool aiPolishing, bool startingLocalModel = false);

    /// <summary>
    /// A finished dictation's outcome (<see cref="PillOutcome"/>): a check and "Typed" briefly, or a notice with its
    /// reason or next step, held on screen for its hold and then hidden by the overlay itself. Shown with the dictation's
    /// return to idle, in place of the hide, so it rides that change's presentation revision and a late outcome never
    /// covers a newer recording; a new recording replaces it at once. The helper is kept while it is on screen and is
    /// never relaunched for it later (see <see cref="OverlayHelperLifetime"/>).
    /// </summary>
    void ShowOutcome(PillOutcome outcome);

    /// <summary>Hides the pill (ignored while an outcome is still holding on screen).</summary>
    void HideOverlay();

    /// <summary>Anchors the pill at the given screen position, now and across overlay relaunches.</summary>
    void SetPosition(OverlayPosition position);

    /// <summary>
    /// Sets the keep-warm period and what it does, together, so the two are never applied out of step. A helper left
    /// idle for <paramref name="minutes"/> is trimmed and kept running while <paramref name="keepResident"/> (the pill is
    /// turned on and dictation is not paused, <c>OverlayWarmup.KeepResident</c>), so the next pill shows at once;
    /// otherwise it is ended to reclaim its memory and relaunches on the next show. Zero or less keeps it resident
    /// untouched. The period mirrors <see cref="AppSettings.ReleaseModelsAfterIdleMinutes"/>, the speech models' keep-warm
    /// setting, and also applies to an idle period already running. Until the first call the helper is never trimmed or
    /// ended for being idle.
    /// </summary>
    void SetKeepWarm(int minutes, bool keepResident);

    /// <summary>
    /// Ends the helper now instead of after the keep-warm period, if nothing needs it (dictation was
    /// paused), whether or not the pill is turned on. A command sent after this call, or a state that keeps the pill on
    /// screen, vetoes it, so it can never end the pill of a recording started later; the next show, or the warmup the
    /// resume brings, relaunches the helper.
    /// </summary>
    void ReleaseWhenIdle();

    /// <summary>
    /// Briefly shows the pill at a candidate position, with a synthetic level-meter sweep so it
    /// looks alive, then hides it (or returns it to a recording or processing state it covered) and
    /// restores the applied position. Lets the settings window demonstrate a position before the user
    /// saves it. Anything newer than the preview supersedes it, and the pill never stays at the
    /// candidate position. A helper launched for a preview then follows the idle deadline like any other.
    /// </summary>
    void Preview(OverlayPosition position);

    /// <summary>Permanently tears the overlay down during application shutdown.</summary>
    void CloseOverlay();
}
