namespace Scribe.Core.Overlay;

/// <summary>
/// What the overlay's latest requested state asks of the helper process. The engine derives it from
/// the state it last asked the pill to show; <see cref="OverlayHelperLifetime"/> uses it to decide
/// whether the helper may be suspended and whether a failed launch is worth retrying.
/// </summary>
public enum OverlayDemand
{
    /// <summary>Nothing on screen. The helper may be suspended once idle and is never relaunched for this state.</summary>
    None,

    /// <summary>
    /// A pill that hides itself after a hold: a dictation's outcome ("Typed", "Typed without AI cleanup",
    /// "Nothing typed", "Not all of it was typed"), and the failure flash the outcomes replace. Worth showing
    /// when the helper can be reached at once, so its own command may launch it, but it is never replayed, never
    /// brings a lost helper back and never earns a deferred relaunch: by then the moment it described has
    /// passed. While it is on screen the helper is not ended: an idle suspend waits for it, and so does a pause
    /// release, both counted from when its write returned (see <see cref="OverlayHelperLifetime.OnShown"/>).
    /// </summary>
    Transient,

    /// <summary>
    /// A state that stays on screen until the engine changes it (recording, processing). The helper
    /// is never idle-suspended while it holds, and a failed launch earns one coalesced retry.
    /// </summary>
    Sustained,
}
