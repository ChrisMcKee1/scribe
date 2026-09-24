import Foundation

/// Which stage of the dictation pipeline failed, if any. Mirrors the stage names on Windows'
/// `DictationPipelineReport` (see src/Scribe.App/Dictation/DictationController.cs), minus the voice activity stage,
/// which macOS does not run as a step of its own.
enum PipelineFailureStage: String {
    case capture
    case decode
    case cleanup
    case postProcessing
    case injection
}

/// A snapshot of one dictation run through the full pipeline: what was captured, how long each stage took, and what
/// the text looked like at each step, in the pipeline's order (raw recognition, AI cleanup, snippets and dictionary,
/// line breaks for the target). Reported to the Playground settings tab so testers can see raw recognition,
/// replacement highlights, and per-step timings without digging through the log.
///
/// The macOS analog of Windows' `DictationPipelineReport`, filled in by `DictationController` as the dictation moves
/// through its stages and published once it ends. It holds text, so it stays in memory for the Playground and never
/// reaches a log.
struct PipelineReport {
    let dictationID: UInt64
    let capturedAt: Date
    let trigger: DictationTrigger
    let stopReason: DictationStopReason
    let captureDuration: TimeInterval
    var decodeDuration: TimeInterval?
    /// How long the cleanup request took, when one was sent.
    var cleanupDuration: TimeInterval?
    var postProcessingDuration: TimeInterval?
    var injectionDuration: TimeInterval?
    var realTimeFactor: Double?

    var rawText: String?
    var cleanupOutcome = DictationCleanupOutcome.off
    /// The model's accepted reply, before snippets and the dictionary.
    var cleanedText: String?
    var postProcessing: TextPostProcessingResult?
    var finalText: String?
    var injectionResult: InjectionResult?

    var failureStage: PipelineFailureStage?
    var failureReason: String?

    init(
        dictationID: UInt64,
        capturedAt: Date,
        trigger: DictationTrigger,
        stopReason: DictationStopReason,
        captureDuration: TimeInterval
    ) {
        self.dictationID = dictationID
        self.capturedAt = capturedAt
        self.trigger = trigger
        self.stopReason = stopReason
        self.captureDuration = captureDuration
    }

    var totalDuration: TimeInterval {
        captureDuration + (decodeDuration ?? 0) + (cleanupDuration ?? 0) + (postProcessingDuration ?? 0)
            + (injectionDuration ?? 0)
    }

    /// Whether AI cleanup ran and its reply was used (false when it was off or fell back to the raw transcript).
    var cleanupApplied: Bool {
        cleanupOutcome == .cleaned || cleanupOutcome == .unchanged
    }
}

/// Publishes the most recent `PipelineReport` for the Playground settings tab to observe. A dedicated, minimal
/// `ObservableObject`, since the report has to reach a Settings window that may already be open.
@MainActor
final class PipelineReportStore: ObservableObject {
    @Published var latest: PipelineReport?

    func publish(_ report: PipelineReport) {
        latest = report
    }
}
