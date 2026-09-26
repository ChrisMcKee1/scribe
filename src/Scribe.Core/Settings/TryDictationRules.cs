using Scribe.Core.Cleanup;
using Scribe.Core.Diagnostics;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;

namespace Scribe.Core.Settings;

public static class TryDictationSample
{
    public const string Default = "Not sure what to say? Try: \"This is a test of Scribe on my PC.\"";

    public static IReadOnlyList<string> For(IEnumerable<DictionaryEntry> dictionaryEntries)
    {
        ArgumentNullException.ThrowIfNull(dictionaryEntries);
        var samples = new List<string> { Default };
        var spoken = dictionaryEntries
            .Where(entry => entry.Enabled)
            .Select(entry => entry.Pattern.Trim())
            .FirstOrDefault(pattern => pattern.Length > 0);
        if (spoken is not null)
        {
            samples.Add($"Or try: \"Please book a meeting about {spoken}.\"");
        }

        return samples;
    }
}

public static class InjectionMethodLabel
{
    public static string Describe(InjectionMethod method, bool addedSpace) =>
        WithSpace(method switch
        {
            InjectionMethod.UnicodeType => "Typed as keystrokes",
            InjectionMethod.ClipboardPaste => "Pasted",
            _ => "Inserted directly",
        }, addedSpace);

    public static string Describe(string? methodCode, bool addedSpace)
    {
        var label = (methodCode ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "unicode" or "unicode-type" or "type" or "keystrokes" => "Typed as keystrokes",
            "clipboard" or "clipboard-paste" or "paste" => "Pasted",
            "win32-edit" or "direct" => "Inserted directly",
            _ => "Inserted directly",
        };
        return WithSpace(label, addedSpace);
    }

    private static string WithSpace(string label, bool addedSpace) =>
        addedSpace ? $"{label}, then a space" : label;
}

public static class TryDictationTiming
{
    public static TimeSpan ProcessingDuration(
        TimeSpan trimmingSilence,
        TimeSpan speechRecognition,
        TimeSpan aiCleanup,
        TimeSpan dictionaryAndSnippets,
        TimeSpan typing) =>
        trimmingSilence + speechRecognition + aiCleanup + dictionaryAndSnippets + typing;

    public static string SpeedLabel(double realTimeFactor)
    {
        if (realTimeFactor <= 0 || double.IsNaN(realTimeFactor) || double.IsInfinity(realTimeFactor))
        {
            return string.Empty;
        }

        var faster = Math.Max(1, (int)Math.Round(1 / realTimeFactor, MidpointRounding.AwayFromZero));
        return $"{faster:N0} times faster than real time";
    }
}

public static class TryDictationRestartNotice
{
    public static bool Needed(
        string? committedModelId,
        int committedDecodeThreads,
        string? runningModelId,
        int runningDecodeThreads) =>
        !string.Equals(committedModelId, runningModelId, StringComparison.Ordinal) ||
        committedDecodeThreads != runningDecodeThreads;
}

public static class TryDictationChangeList
{
    public const string DictionaryOrWordPackSource = "your dictionary or a word pack";
    public const string AiCleanupChanged = "AI cleanup rewrote the text.";

    public static IReadOnlyList<string> Describe(
        IEnumerable<TextReplacement> replacements,
        bool aiCleanupChanged)
    {
        ArgumentNullException.ThrowIfNull(replacements);
        var lines = replacements.Select(Describe).ToList();
        if (aiCleanupChanged)
        {
            lines.Add(AiCleanupChanged);
        }

        return lines;
    }

    public static string Describe(TextReplacement replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        var source = replacement.Kind == TextReplacementKind.Snippet
            ? "snippet"
            : DictionaryOrWordPackSource;
        return $"\"{replacement.Pattern}\" became \"{replacement.Replacement}\" ({source})";
    }
}

public enum TryDictationSummaryAction
{
    None,
    OpenSoundSettings,
    OpenAiCleanup,
}

public sealed record TryDictationResultView(
    TryDictationSummaryInput Summary,
    bool ShowHeard,
    bool ShowTyped,
    bool ShowChanges,
    bool ShowTimingDetails,
    TryDictationSummaryAction Action)
{
    public static TryDictationResultView For(TryDictationResultViewInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (TryDictationReportClassifier.IsNoSpeech(input.FailureStage, input.FailureReason))
        {
            return new(new(false, NoSpeech: true), false, false, false, true, TryDictationSummaryAction.None);
        }

        if (TryDictationReportClassifier.IsMicrophoneProblem(input.FailureStage))
        {
            return new(new(false, MicrophoneProblem: true), false, false, false, true, TryDictationSummaryAction.OpenSoundSettings);
        }

        if (TryDictationReportClassifier.StageFrom(input.FailureStage) is { } stopped)
        {
            return new(new(false, StoppedAt: stopped, Reason: input.FailureReason), !string.IsNullOrWhiteSpace(input.RawText), false, false, true, TryDictationSummaryAction.None);
        }

        if (input.InjectionSucceeded != true)
        {
            return new(new(false, StoppedAt: FailureStage.TextInsertion), !string.IsNullOrWhiteSpace(input.RawText), false, false, true, TryDictationSummaryAction.None);
        }

        if (input.CleanupFailed)
        {
            return new(new(false, CleanupFailed: true, Reason: input.CleanupReason), true, true, true, true, TryDictationSummaryAction.OpenAiCleanup);
        }

        if (input.CleanupNotReady)
        {
            return new(new(false, CleanupNotReady: true, Reason: input.CleanupReason), true, true, true, true, TryDictationSummaryAction.OpenAiCleanup);
        }

        return new(
            new(true, input.ProcessingSeconds, input.AiCleanupEnabled, input.CleanupPhrase),
            true,
            true,
            true,
            true,
            TryDictationSummaryAction.None);
    }
}

public sealed record TryDictationResultViewInput(
    string? FailureStage,
    string? FailureReason,
    string? RawText,
    bool? InjectionSucceeded,
    double ProcessingSeconds,
    bool AiCleanupEnabled,
    string? CleanupPhrase,
    bool CleanupFailed = false,
    bool CleanupNotReady = false,
    string? CleanupReason = null);

public static class TryDictationReportClassifier
{
    public const string StageAudioCapture = "Audio capture";
    public const string StageVoiceActivityDetection = "Voice activity detection";
    public const string StageSpeechRecognition = "Speech recognition";
    public const string StageAiCleanup = "AI cleanup";
    public const string StageDictionaryAndSnippets = "Dictionary and snippets";
    public const string StageTextInsertion = "Text insertion";
    public const string AudioCaptureFailed = "Recording did not finish.";
    public const string NoSpeechDetected = "No speech was detected.";
    public const string NoSpeechRecognized = "No speech was recognized.";
    public const string SilenceTrimmingFailed = "Silence trimming failed.";
    public const string SpeechRecognitionFailed = "Speech recognition failed.";
    public const string SilentCapture = "Only silence was recorded.";
    public const string AiCleanupFailed = "AI cleanup did not finish.";
    public const string DictionaryAndSnippetsFailed = "Dictionary and snippets did not finish.";
    public const string TextInsertionFailed = "Typing did not finish.";

    public static string FailureReasonForStage(string? stage) => stage switch
    {
        StageAudioCapture => AudioCaptureFailed,
        StageVoiceActivityDetection => SilenceTrimmingFailed,
        StageSpeechRecognition => SpeechRecognitionFailed,
        StageAiCleanup => AiCleanupFailed,
        StageDictionaryAndSnippets => DictionaryAndSnippetsFailed,
        StageTextInsertion => TextInsertionFailed,
        _ => "Dictation did not finish.",
    };

    public static FailureStage? StageFrom(string? stage) => stage switch
    {
        StageAudioCapture => FailureStage.AudioCapture,
        StageVoiceActivityDetection => FailureStage.VoiceActivityDetection,
        StageSpeechRecognition => FailureStage.SpeechRecognition,
        StageAiCleanup => FailureStage.AiCleanup,
        StageDictionaryAndSnippets => FailureStage.DictionaryAndSnippets,
        StageTextInsertion => FailureStage.TextInsertion,
        _ => null,
    };

    public static bool IsNoSpeech(string? stage, string? reason) =>
        (string.Equals(stage, StageVoiceActivityDetection, StringComparison.Ordinal) && string.Equals(reason, NoSpeechDetected, StringComparison.Ordinal)) ||
        (string.Equals(stage, StageSpeechRecognition, StringComparison.Ordinal) && string.Equals(reason, NoSpeechRecognized, StringComparison.Ordinal)) ||
        (string.Equals(stage, StageVoiceActivityDetection, StringComparison.Ordinal) && string.Equals(reason, SilentCapture, StringComparison.Ordinal)) ||
        (string.Equals(stage, StageSpeechRecognition, StringComparison.Ordinal) && string.Equals(reason, SilentCapture, StringComparison.Ordinal));

    public static bool IsMicrophoneProblem(string? stage) =>
        string.Equals(stage, StageAudioCapture, StringComparison.Ordinal);
}

public static class TryDictationTimingDetail
{
    public const string SilenceTrimmingUnavailable = "Off, silence trimming isn't available";

    public static string Cleanup(CleanupResult? cleanup, bool enabled)
    {
        if (!enabled)
        {
            return "Off in settings";
        }

        return cleanup?.Outcome switch
        {
            CleanupOutcome.Cleaned => "Cleaned",
            CleanupOutcome.Unchanged => "Ran, no changes needed",
            CleanupOutcome.Failed => "Didn't finish",
            CleanupOutcome.Skipped when cleanup.SkippedUnexpectedly => "Not ready",
            CleanupOutcome.Skipped => "Skipped",
            _ => "Not reached",
        };
    }

    public static string ChangeCount(int count) => count switch
    {
        0 => "No changes",
        1 => "1 change",
        _ => $"{count:N0} changes",
    };
}

public static class TryDictationCleanupPhrase
{
    public static string For(CleanupOptions options) => CleanupActivationMessage.TryDictationPhrase(options);
}
