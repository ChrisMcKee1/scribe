using Scribe.Core.Tray;

namespace Scribe.Core.Overlay;

/// <summary>Routing and episode feedback, independent of the speech-model tray condition.</summary>
public sealed class OverlayFeedback
{
    private long _noticedEpisode;

    public static bool CanShow(bool indicatorOn, OverlayAvailability? availability) =>
        indicatorOn && availability?.IsAvailable == true;

    public static bool IsCurrent(long revision, long lastRenderedRevision) =>
        revision > 0 && revision == lastRenderedRevision;

    public static DictationProblemSurface DecideProblem(
        DictationProblem problem, bool indicatorOn, OverlayAvailability? availability) =>
        DictationProblemRouting.Decide(problem, CanShow(indicatorOn, availability));

    public static DictationProblemSurface DecideRecordingWarning(
        DictationProblem problem, bool indicatorOn, OverlayAvailability? availability,
        long recordingRevision, long lastRenderedRevision) =>
        DictationProblemRouting.DecideRecordingWarning(
            problem, CanShow(indicatorOn, availability), recordingRevision, lastRenderedRevision);

    public bool TryTakeFailureNotice(
        OverlayAvailability availability, bool indicatorOn, OverlayDemand demand,
        long revision, long lastRenderedRevision)
    {
        if (!indicatorOn || demand != OverlayDemand.Sustained || availability.IsAvailable || !availability.IsFaulted ||
            !IsCurrent(revision, lastRenderedRevision) || availability.FailureEpisode <= _noticedEpisode)
        {
            return false;
        }

        _noticedEpisode = availability.FailureEpisode;
        return true;
    }

    public static TrayNotice FailureNotice() => new(
        "Recording indicator unavailable",
        "Scribe is still running, but its recording indicator couldn't start or stopped. Watch the tray icon for recording and processing. Scribe tries again when the indicator is needed. If it keeps happening, save diagnostics in Settings, Diagnostics.",
        TrayNoticeKind.Warning,
        TrayNoticeAction.OpenSettingsDiagnostics);
}

/// <summary>One outcome's delivery fallback, kept on the presentation thread and bound to its revision.</summary>
public sealed class OverlayOutcomeFeedback(long revision, PillOutcome outcome, bool problemAlreadyNoticed)
{
    private bool _noticed = problemAlreadyNoticed;

    public void NoteProblemNoticed() => _noticed = true;

    public TrayNotice? TakeNotice(bool delivered, long lastRenderedRevision)
    {
        if (delivered || _noticed || revision <= 0 || revision != lastRenderedRevision || outcome.Kind == PillOutcomeKind.Typed)
        {
            return null;
        }

        _noticed = true;
        var title = outcome.Kind switch
        {
            PillOutcomeKind.TypedWithoutCleanup => "Typed without AI cleanup",
            PillOutcomeKind.PartlyTyped => "Not all of it was typed",
            _ => "Nothing typed",
        };
        var recovery = outcome.Detail == PillOutcome.RecoveryStep;
        var body = recovery
            ? "Right-click the Scribe icon and choose Copy last dictation, then paste it."
            : outcome.Kind == PillOutcomeKind.TypedWithoutCleanup
                ? "Your dictation was typed without AI cleanup. See Settings, AI cleanup."
                : outcome.Detail + ". If it keeps happening, save diagnostics in Settings, Diagnostics.";
        return new TrayNotice(title, body,
            outcome.Kind == PillOutcomeKind.TypedWithoutCleanup ? TrayNoticeKind.Warning : TrayNoticeKind.Error,
            recovery ? TrayNoticeAction.CopyLastDictation :
            outcome.Kind == PillOutcomeKind.TypedWithoutCleanup ? TrayNoticeAction.OpenSettingsAiCleanup :
            TrayNoticeAction.OpenSettingsDiagnostics);
    }
}
