namespace Scribe.Core.Overlay;

/// <summary>
/// When the shell warms the overlay helper besides the startup warmup, so the first pill after a pause or a settings change
/// shows at once instead of waiting for a launch (0.5 s on an idle machine, 2 to 11 s while the speech models reload), and
/// when it keeps the helper resident. A warmup on a running helper is only the no-op WARMUP write, so the rule is about not
/// warming a helper nobody wants. The shell reads the paused state from the dictation state it last rendered, on its UI
/// thread.
/// </summary>
public static class OverlayWarmup
{
    /// <summary>
    /// After a dictation state was rendered: true when the state rendered before it was paused, this one is not (dictation
    /// resumed), and the pill is turned on. The pause released the helper, and the first dictation after the resume would
    /// otherwise find it gone.
    /// </summary>
    public static bool AfterRender(bool wasPaused, bool isPaused, bool pillOn) => wasPaused && !isPaused && pillOn;

    /// <summary>
    /// After settings were applied: true when the pill is turned on and dictation is not paused, so turning the pill on does
    /// not leave the next dictation to launch the helper. While paused the helper stays released, as the pause asked; the
    /// resume warms it.
    /// </summary>
    public static bool AfterSettingsApplied(bool pillOn, bool paused) => pillOn && !paused;

    /// <summary>
    /// Whether the helper is kept resident, so the idle deadline trims it instead of ending it
    /// (<see cref="OverlayHelperLifetime.SetKeepResident"/>): only while the pill is turned on and dictation is not paused.
    /// While paused the idle deadline ends it, as it did before the pill kept it, whatever brought it back: a release that a
    /// stamped command vetoed (a setting saved while the pause's outcome showed) or a position preview. Kept resident while
    /// paused, such a helper stayed until the resume, since nothing but the idle deadline ends it then. The shell pushes it
    /// with the keep-warm period: at startup, where dictation is never paused, with every state change, from the state it
    /// renders, so a pause pushes false before its release, and after settings are applied.
    /// </summary>
    public static bool KeepResident(bool pillOn, bool paused) => pillOn && !paused;
}