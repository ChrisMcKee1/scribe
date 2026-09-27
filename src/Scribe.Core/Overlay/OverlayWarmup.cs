namespace Scribe.Core.Overlay;

/// <summary>
/// When the shell warms the overlay helper besides the startup warmup, so the first pill after a pause or a settings change
/// shows at once instead of waiting for a launch (0.5 s on an idle machine, 2 to 11 s while the speech models reload). A
/// warmup on a running helper is only the no-op WARMUP write, so the rule is about not warming a helper nobody wants. The
/// shell reads the paused state from the dictation state it last rendered, on its UI thread.
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
}