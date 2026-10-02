import Foundation

enum PillOutcomeKind: Equatable, Sendable {
    case typed
    case typedWithoutCleanup
    case nothingTyped
    case partlyTyped
}

struct PillOutcome: Equatable, Sendable {
    static let recoveryStep = "Copy it from the menu bar"
    static let cleanupDidNotRun = "See Settings, AI cleanup"
    static let accessibilityStep = "Allow Accessibility access"
    static let microphoneStep = "Check your microphone"
    static let microphoneAccessStep = "Allow Microphone access"
    static let recognizerStep = "Install Foundry Local"
    static let transcriptionStep = "Try again"

    let kind: PillOutcomeKind
    let detail: String

    var title: String {
        switch kind {
        case .typed:
            return "Typed"
        case .typedWithoutCleanup:
            return "Typed without AI cleanup"
        case .nothingTyped:
            return "Nothing typed"
        case .partlyTyped:
            return "Not all of it was typed"
        }
    }

    var hold: Duration {
        kind == .typed ? PillTiming.typedHold : PillTiming.noticeHold
    }

    var isCaution: Bool {
        kind == .typedWithoutCleanup
    }

    var isFailure: Bool {
        kind == .nothingTyped || kind == .partlyTyped
    }

    static func of(
        _ insertion: InjectionResult,
        cleanupRequested: Bool,
        cleanupOutcome: DictationCleanupOutcome
    ) -> PillOutcome? {
        switch insertion.delivery {
        case .nothingToInsert:
            return nil
        case .accessibility, .pasted, .typed:
            if cleanupRequested, cleanupOutcome == .fellBack {
                return PillOutcome(kind: .typedWithoutCleanup, detail: Self.cleanupDidNotRun)
            }
            return PillOutcome(kind: .typed, detail: "")
        case .typedPartially:
            return PillOutcome(kind: .partlyTyped, detail: Self.recoveryStep)
        case .accessibilityDenied:
            return PillOutcome(kind: .nothingTyped, detail: Self.accessibilityStep)
        case .targetChanged, .targetUnknown, .targetUnresponsive, .noFocusedElement, .cancelled, .failed,
            .accessibilityUnconfirmed:
            return PillOutcome(kind: .nothingTyped, detail: Self.recoveryStep)
        }
    }
}
