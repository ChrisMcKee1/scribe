using Scribe.Core.Models;

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

    /// <summary>Listening: pulsing record dot + live input-level meter.</summary>
    void ShowRecording();

    /// <summary>Brief warning text while the recording indicator and live meter remain active.</summary>
    void ShowRecordingWarning(string? reason);

    /// <summary>Processing: bouncing dots while transcribing / AI polishing.</summary>
    void ShowProcessing(bool aiPolishing);

    /// <summary>Brief red "intelligence failed" flash, then auto-hides.</summary>
    void ShowFailed(string? reason);

    /// <summary>Hides the pill (suppressed while a failure flash is still holding).</summary>
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
