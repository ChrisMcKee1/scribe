import Foundation

/// Which stage of the dictation pipeline failed, if any. Mirrors the stage names on Windows'
/// `DictationPipelineReport` (see src/Scribe.App/Dictation/DictationController.cs), minus stages
/// that don't exist as discrete steps on macOS yet (there is no live cleanup or true VAD decode
/// step wired into the pipeline; see PORTING-PLAN.md).
enum PipelineFailureStage: String {
    case capture
    case decode
    case postProcessing
    case injection
}

/// A snapshot of one dictation run through the full pipeline: what was captured, how long each
/// stage took, and what the text looked like at each step. Reported to the Playground settings
/// tab so testers can see raw recognition, replacement highlights, and per-step timings without
/// digging through the log file.
///
/// This is a macOS-scoped analog of Windows' `DictationPipelineReport`. Two Windows stages are
/// intentionally not represented: "AI cleanup" (macOS has no live AI cleanup wired into the
/// interactive pipeline yet; only reachable via the `--cleanup-text` CLI verb) and a true "VAD
/// decode" duration (macOS uses an energy-threshold auto-stop detector, not a trained Silero VAD
/// model, so there is no discrete VAD inference step to time the way Windows has).
struct PipelineReport {
    let capturedAt: Date
    let source: CaptureStopSource
    let captureDuration: TimeInterval
    let decodeDuration: TimeInterval?
    let postProcessingDuration: TimeInterval?
    let injectionDuration: TimeInterval?
    var totalDuration: TimeInterval {
        captureDuration + (decodeDuration ?? 0) + (postProcessingDuration ?? 0) + (injectionDuration ?? 0)
    }
    let realTimeFactor: Double?

    let rawText: String?
    let postProcessing: TextPostProcessingResult?
    let finalText: String?
    let injectionResult: InjectionResult?

    let failureStage: PipelineFailureStage?
    let failureReason: String?

    static func failure(
        capturedAt: Date,
        source: CaptureStopSource,
        captureDuration: TimeInterval,
        stage: PipelineFailureStage,
        reason: String,
        rawText: String? = nil
    ) -> PipelineReport {
        PipelineReport(
            capturedAt: capturedAt,
            source: source,
            captureDuration: captureDuration,
            decodeDuration: nil,
            postProcessingDuration: nil,
            injectionDuration: nil,
            realTimeFactor: nil,
            rawText: rawText,
            postProcessing: nil,
            finalText: nil,
            injectionResult: nil,
            failureStage: stage,
            failureReason: reason)
    }
}

/// Publishes the most recent `PipelineReport` for the Playground settings tab to observe. A
/// dedicated, minimal `ObservableObject` since `SettingsView` has no other pub/sub mechanism from
/// `AppDelegate` beyond the `onProfilesOrRulesChanged` closure, and this needs to update live
/// while the Settings window may already be open.
final class PipelineReportStore: ObservableObject {
    @Published var latest: PipelineReport?

    func publish(_ report: PipelineReport) {
        latest = report
    }
}
