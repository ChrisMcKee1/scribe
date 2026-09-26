namespace Scribe.Core.Overlay;

/// <summary>
/// How long each state of the recording pill stays on screen, and how it fades. The overlay process cannot reference
/// this assembly, so it keeps its own copies of these values, and a test checks them against these; the helper's
/// lifetime (<see cref="OverlayHelperLifetime"/>) keeps the helper for exactly this long after an outcome is shown.
/// </summary>
public static class PillTiming
{
    /// <summary>"Typed" is shown for this long, then the pill hides.</summary>
    public static TimeSpan TypedHold { get; } = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// A notice with a second line ("Typed without AI cleanup", "Nothing typed", "Not all of it was typed") is shown for
    /// this long, then the pill hides: the hold of the failure flash it replaces.
    /// </summary>
    public static TimeSpan NoticeHold { get; } = TimeSpan.FromMilliseconds(1300);

    /// <summary>A warning shown over a live recording (the microphone is muted) gives way to "Listening" after this long.</summary>
    public static TimeSpan RecordingWarningHold { get; } = TimeSpan.FromMilliseconds(1800);

    /// <summary>With Windows animation effects on, the pill fades in over this long when it appears. Off, it just appears.</summary>
    public static TimeSpan FadeIn { get; } = TimeSpan.FromMilliseconds(120);

    /// <summary>With Windows animation effects on, the pill fades out over this long when it hides. Off, it just goes.</summary>
    public static TimeSpan FadeOut { get; } = TimeSpan.FromMilliseconds(150);
}
