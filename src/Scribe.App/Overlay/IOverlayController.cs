using Scribe.Core.Models;
using Scribe.Core.Overlay;

namespace Scribe.App.Overlay;

/// <summary>
/// Abstraction over the recording pill so the WPF host can drive it without caring whether it is the
/// in-process WPF window or the out-of-process WinUI 3 overlay. The WinUI overlay
/// (<see cref="OverlayProcessClient"/>) is the default: it renders the pill in a separate kept-warm
/// process via DWM composition, sidestepping the WPF <c>AllowsTransparency</c>/layered-window path
/// that produced the recurring "black box". The implementation owns the helper's idle lifetime itself,
/// independently of the speech models: callers push the keep-warm period and may ask for a release on
/// pause, but never end the helper directly.
/// </summary>
public interface IOverlayController
{
    /// <summary>
    /// Pre-warms the overlay (launches the helper process / presents the surface) before first use. A
    /// warmed helper that is never shown still follows the idle deadline.
    /// </summary>
    void Warmup();

    /// <summary>Listening: the listening edge and the live level bars.</summary>
    void ShowRecording();

    /// <summary>Brief warning text while the recording indicator and live level bars remain active.</summary>
    void ShowRecordingWarning(string? reason);

    /// <summary>Processing: three dots, and words that say whether it is transcribing or AI cleanup.</summary>
    void ShowProcessing(bool aiPolishing);

    /// <summary>
    /// A finished dictation's outcome (<see cref="PillOutcome"/>): a check and "Typed" briefly, or a notice with its
    /// reason or next step, held on screen for its hold and then hidden by the overlay itself. Shown with the dictation's
    /// return to idle, in place of the hide, so it rides that change's presentation revision and a late outcome never
    /// covers a newer recording; a new recording replaces it at once. The helper is kept while it is on screen and is
    /// never relaunched for it later (see <see cref="OverlayHelperLifetime"/>).
    /// </summary>
    void ShowOutcome(PillOutcome outcome);

    /// <summary>
    /// The AI cleanup failure flash the outcomes replace, drawn like an error, then auto-hides. It fires before the text is
    /// typed, and the shell also shows it for errors, so it cannot say what reached the target; it stays only until the
    /// shell hands on outcomes instead.
    /// </summary>
    void ShowFailed(string? reason);

    /// <summary>Hides the pill (ignored while an outcome is still holding on screen).</summary>
    void HideOverlay();

    /// <summary>Anchors the pill at the given screen position, now and across overlay relaunches.</summary>
    void SetPosition(OverlayPosition position);

    /// <summary>
    /// Sets the keep-warm period: a helper left idle for this many minutes is ended to reclaim its
    /// memory and relaunches on the next show; zero or less keeps it resident. It mirrors
    /// <see cref="AppSettings.ReleaseModelsAfterIdleMinutes"/>, the speech models' keep-warm setting, and
    /// also applies to an idle period already running. Until the first call the helper is never suspended.
    /// </summary>
    void SetKeepWarm(int minutes);

    /// <summary>
    /// Ends the helper now instead of after the keep-warm period, if nothing needs it (dictation was
    /// paused). A command sent after this call, or a state that keeps the pill on screen, vetoes it, so it
    /// can never end the pill of a recording started later; the next show relaunches the helper.
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
