using Scribe.Core.Settings;

namespace Scribe.Core.Diagnostics;

public enum DiagnosticsSpeedReadState
{
    Reading,
    Empty,
    Failure,
}

public sealed record DiagnosticsSpeedStepText(
    string Hint,
    string EmptyLine,
    bool HasData,
    string Average,
    string Fastest,
    string Slowest);

public sealed record DiagnosticsSpeedTextView(
    string Description,
    bool ShowRetry,
    bool ShowTable,
    bool ShowDetails,
    string TypicalSpeechRecognition,
    string TypicalCleanup,
    string TypicalCombined,
    string SlowSpeechRecognition,
    string SlowCleanup,
    string SlowCombined,
    string SampleCountLine,
    bool ShowCapLine,
    string CapLine,
    string BestLine,
    DiagnosticsSpeedStepText SpeechRecognition,
    DiagnosticsSpeedStepText Cleanup,
    DiagnosticsSpeedStepText Combined);

public static class DiagnosticsSpeedText
{
    public const string ReadingDescription = "Reading your statistics...";
    public const string EmptyDescription = "No dictations in the last 7 days.";
    public const string FailureDescription = "Couldn't read the statistics.";
    public const string TryAgain = "Try again";
    public const string NoneYet = "None yet";
    public const string NoDictationsForModel = "No dictations with this speech model in the last 7 days.";
    public const string CapLine = "Only your latest 1,000 dictations are counted.";
    public const string SpeechRecognitionHint = "Speech recognition time only. AI cleanup isn't counted here.";
    public const string CleanupHint = "AI cleanup time after speech recognition finishes.";
    public const string CombinedHint = "Recognition plus AI cleanup.";
    public const string NoSpeechRecognitionRuns = "No speech recognition runs in this period yet.";
    public const string NoCleanupRuns = "No AI cleanup runs in this period yet.";
    public const string NoCombinedRuns = "No cleanup-enabled runs in this period yet.";

    public static DiagnosticsSpeedTextView ForState(DiagnosticsSpeedReadState state) => state switch
    {
        DiagnosticsSpeedReadState.Reading => EmptyView(ReadingDescription, showRetry: false),
        DiagnosticsSpeedReadState.Failure => EmptyView(FailureDescription, showRetry: true),
        _ => EmptyView(EmptyDescription, showRetry: false),
    };

    public static DiagnosticsSpeedTextView ForStats(DictationStats.Snapshot stats)
    {
        ArgumentNullException.ThrowIfNull(stats);
        if (stats.Count == 0)
        {
            // The details holding the cap line are hidden here, so the description carries it: a run of this model may
            // sit beyond the read limit.
            var empty = DescriptionFor(stats) + " " + NoDictationsForModel;
            return EmptyView(stats.ReachedReadLimit ? empty + " " + CapLine : empty, showRetry: false);
        }

        var speech = Step(
            SpeechRecognitionHint,
            NoSpeechRecognitionRuns,
            stats.SpeechRecognitionMs);
        var cleanup = Step(
            CleanupHint,
            NoCleanupRuns,
            stats.CleanupMs);
        var combined = Step(
            CombinedHint,
            NoCombinedRuns,
            stats.CombinedMs);

        return new DiagnosticsSpeedTextView(
            Description: DescriptionFor(stats),
            ShowRetry: false,
            ShowTable: true,
            ShowDetails: true,
            TypicalSpeechRecognition: stats.SpeechRecognitionMs is { } speechMs ? FormatDuration(speechMs.P50) : NoneYet,
            TypicalCleanup: stats.CleanupMs is { } cleanupMs ? FormatDuration(cleanupMs.P50) : NoneYet,
            TypicalCombined: stats.CombinedMs is { } combinedMs ? FormatDuration(combinedMs.P50) : NoneYet,
            SlowSpeechRecognition: stats.SpeechRecognitionMs is { } slowSpeech ? FormatDuration(slowSpeech.P95) : NoneYet,
            SlowCleanup: stats.CleanupMs is { } slowCleanup ? FormatDuration(slowCleanup.P95) : NoneYet,
            SlowCombined: stats.CombinedMs is { } slowCombined ? FormatDuration(slowCombined.P95) : NoneYet,
            SampleCountLine: stats.Count == 1
                ? "From 1 dictation in the last 7 days."
                : $"From {stats.Count:N0} dictations in the last 7 days.",
            ShowCapLine: stats.ReachedReadLimit,
            CapLine: CapLine,
            BestLine: stats.FastestRtf > 0
                ? $"Best: {TryDictationTiming.SpeedLabel(stats.FastestRtf)}."
                : string.Empty,
            SpeechRecognition: speech,
            Cleanup: cleanup,
            Combined: combined);
    }

    private static DiagnosticsSpeedTextView EmptyView(string description, bool showRetry) => new(
        Description: description,
        ShowRetry: showRetry,
        ShowTable: false,
        ShowDetails: false,
        TypicalSpeechRecognition: NoneYet,
        TypicalCleanup: NoneYet,
        TypicalCombined: NoneYet,
        SlowSpeechRecognition: NoneYet,
        SlowCleanup: NoneYet,
        SlowCombined: NoneYet,
        SampleCountLine: string.Empty,
        ShowCapLine: false,
        CapLine: CapLine,
        BestLine: string.Empty,
        SpeechRecognition: EmptyStep(SpeechRecognitionHint, NoSpeechRecognitionRuns),
        Cleanup: EmptyStep(CleanupHint, NoCleanupRuns),
        Combined: EmptyStep(CombinedHint, NoCombinedRuns));

    private static DiagnosticsSpeedStepText Step(
        string hint,
        string emptyLine,
        DictationStats.MetricSummary? metrics) =>
        metrics is null
            ? EmptyStep(hint, emptyLine)
            : new(
                Hint: hint,
                EmptyLine: emptyLine,
                HasData: true,
                Average: FormatDuration(metrics.Average),
                Fastest: FormatDuration(metrics.Min),
                Slowest: FormatDuration(metrics.Max));

    private static DiagnosticsSpeedStepText EmptyStep(string hint, string emptyLine) => new(
        Hint: hint,
        EmptyLine: emptyLine,
        HasData: false,
        Average: string.Empty,
        Fastest: string.Empty,
        Slowest: string.Empty);

    private static string DescriptionFor(DictationStats.Snapshot stats)
    {
        var modelName = ModelNameWithoutTrailingParenthetical(stats.CurrentModelName);
        var description = $"Speech model: {modelName}.";
        return stats.HasEarlierModelDictations
            ? description + " Earlier dictations with another speech model aren't counted."
            : description;
    }

    private static string ModelNameWithoutTrailingParenthetical(string name)
    {
        var trimmed = name.Trim();
        if (!trimmed.EndsWith(")", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var start = trimmed.LastIndexOf(" (", StringComparison.Ordinal);
        return start > 0 ? trimmed[..start] : trimmed;
    }

    private static string FormatDuration(double ms) =>
        ms < 1000 ? $"{ms:0} ms" : $"{ms / 1000.0:0.0} s";
}
