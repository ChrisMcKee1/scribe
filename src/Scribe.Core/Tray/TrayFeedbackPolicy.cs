namespace Scribe.Core.Tray;

public enum TrayFeedbackChannel
{
    None,
    Tooltip,
    Pill,
    Notice,
}

public enum TrayFeedbackEvent
{
    DictationProblem,
    TypingFailure,
    AiCleanupFailure,
    AiCleanupSuccess,
    AiCleanupSetupChanged,
    LastingCondition,
}

public sealed record TrayFeedbackDecision(TrayFeedbackChannel Channel, TrayCondition Condition = TrayCondition.None);

public sealed class TrayFeedbackPolicy
{
    private bool _aiCleanupEpisodeNotified;

    public TrayFeedbackDecision Decide(TrayFeedbackEvent feedbackEvent, bool recordingIndicatorOn = false, TrayCondition condition = TrayCondition.None)
    {
        switch (feedbackEvent)
        {
            case TrayFeedbackEvent.DictationProblem:
                return new TrayFeedbackDecision(recordingIndicatorOn ? TrayFeedbackChannel.Pill : TrayFeedbackChannel.Notice);
            case TrayFeedbackEvent.TypingFailure:
                return new TrayFeedbackDecision(TrayFeedbackChannel.Notice);
            case TrayFeedbackEvent.AiCleanupFailure:
                if (_aiCleanupEpisodeNotified) return new TrayFeedbackDecision(TrayFeedbackChannel.None);
                _aiCleanupEpisodeNotified = true;
                return new TrayFeedbackDecision(TrayFeedbackChannel.Notice);
            case TrayFeedbackEvent.AiCleanupSuccess:
            case TrayFeedbackEvent.AiCleanupSetupChanged:
                _aiCleanupEpisodeNotified = false;
                return new TrayFeedbackDecision(TrayFeedbackChannel.None);
            case TrayFeedbackEvent.LastingCondition:
                return new TrayFeedbackDecision(TrayFeedbackChannel.Tooltip, condition);
            default:
                return new TrayFeedbackDecision(TrayFeedbackChannel.None);
        }
    }
}
