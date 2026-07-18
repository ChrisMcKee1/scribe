using Scribe.Core.Models;

namespace Scribe.App.Overlay;

/// <summary>
/// Abstraction over the recording pill so the WPF host can drive it without caring whether it is the
/// in-process WPF window or the out-of-process WinUI 3 overlay. The WinUI overlay
/// (<see cref="OverlayProcessClient"/>) is the default: it renders the pill in a separate kept-warm
/// process via DWM composition, sidestepping the WPF <c>AllowsTransparency</c>/layered-window path
/// that produced the recurring "black box".
/// </summary>
public interface IOverlayController
{
    /// <summary>Pre-warms the overlay (launches the helper process / presents the surface) before first use.</summary>
    void Warmup();

    /// <summary>Listening: pulsing record dot + live input-level meter.</summary>
    void ShowRecording();

    /// <summary>Brief warning text while the recording indicator and live meter remain active.</summary>
    void ShowRecordingWarning(string? reason);

    /// <summary>Processing: bouncing dots while transcribing / AI polishing.</summary>
    void ShowProcessing(bool polishing);

    /// <summary>Brief red "intelligence failed" flash, then auto-hides.</summary>
    void ShowFailed(string? reason);

    /// <summary>Hides the pill (suppressed while a failure flash is still holding).</summary>
    void HideOverlay();

    /// <summary>Anchors the pill at the given screen position, now and across overlay relaunches.</summary>
    void SetPosition(OverlayPosition position);

    /// <summary>
    /// Briefly shows the pill at a candidate position — with a synthetic level-meter sweep so it
    /// looks alive — then hides it and restores the applied position. Lets the settings window
    /// demonstrate a position before the user saves it.
    /// </summary>
    void Preview(OverlayPosition position);

    /// <summary>Permanently tears the overlay down during application shutdown.</summary>
    void CloseOverlay();
}
