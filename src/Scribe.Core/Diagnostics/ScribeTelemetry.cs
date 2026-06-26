using System.Diagnostics;
using System.Reflection;

namespace Scribe.Core.Diagnostics;

/// <summary>
/// Central <see cref="ActivitySource"/> for Scribe's dictation pipeline. Tracing is opt-in: spans are
/// only created when a listener (the OpenTelemetry SDK in the app host) subscribes to
/// <see cref="SourceName"/>, so when telemetry is off these calls are nearly free. The spans make the
/// end-to-end loop — capture, VAD, transcription, AI cleanup, post-processing and text injection —
/// inspectable, which is how an intermittent "the text didn't appear" can be traced to the exact
/// stage that dropped it (a VAD discard, an empty decode, or a partial <c>SendInput</c>).
/// </summary>
public static class ScribeTelemetry
{
    /// <summary>The ActivitySource / OpenTelemetry source name to register a listener for.</summary>
    public const string SourceName = "Scribe.Dictation";

    /// <summary>Root span covering one hotkey-release: stop → decode → (clean) → inject.</summary>
    public const string DictationActivity = "dictation.process";

    /// <summary>Child span covering the text-injection keystroke/paste delivery.</summary>
    public const string InjectActivity = "text.inject";

    // Tag names (snake_case under a "scribe." namespace) — stable keys the log bridge and any OTLP
    // backend surface for filtering and dashboards.
    public const string TagOutcome = "scribe.outcome";
    public const string TagCaptureSeconds = "scribe.capture_seconds";
    public const string TagVadEnabled = "scribe.vad_enabled";
    public const string TagVadKept = "scribe.vad_kept";
    public const string TagRecognizerReady = "scribe.recognizer_ready";
    public const string TagDecodeChars = "scribe.decode_chars";
    public const string TagRealTimeFactor = "scribe.rtf";
    public const string TagAiCleanup = "scribe.ai_cleanup";
    public const string TagAiChanged = "scribe.ai_changed";
    public const string TagAiOutcome = "scribe.ai_outcome";
    public const string TagFinalChars = "scribe.final_chars";
    public const string TagTargetApp = "scribe.target_app";
    public const string TagInjectMethod = "scribe.inject.method";
    public const string TagInjectChars = "scribe.inject.chars";
    public const string TagInjectSent = "scribe.inject.sent";
    public const string TagInjectTotal = "scribe.inject.total";
    public const string TagInjectComplete = "scribe.inject.complete";
    public const string TagInjectFallback = "scribe.inject.fallback";

    /// <summary>The shared source. Dispose is not needed for a process-lifetime static.</summary>
    public static readonly ActivitySource Source = new(
        SourceName,
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0");
}

/// <summary>Stable values for the <see cref="ScribeTelemetry.TagOutcome"/> tag.</summary>
public static class DictationOutcome
{
    public const string Injected = "injected";
    public const string EmptyCapture = "empty-capture";
    public const string VadNoSpeech = "vad-no-speech";
    public const string NoSpeech = "no-speech";
    public const string EmptyAfterPostProcess = "empty-after-postprocess";
    public const string Error = "error";
}
