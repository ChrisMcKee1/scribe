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
    /// A short flash that hides itself (the failure notice). Worth showing when the helper can be
    /// reached at once, but it neither keeps the helper resident nor earns a deferred relaunch: by
    /// the time a cooldown ends, the moment it described has passed.
    /// </summary>
    Transient,

    /// <summary>
    /// A state that stays on screen until the engine changes it (recording, processing). The helper
    /// is never idle-suspended while it holds, and a failed launch earns one coalesced retry.
    /// </summary>
    Sustained,
}
