import AppKit

/// Where the pill sits within the screen's visible frame. Mirrors Windows'
/// `Scribe.Overlay.OverlayAnchor` (and `Scribe.Core.Models.OverlayPosition`) by name so the same
/// nine-position picker concept applies here, even though macOS drives the pill in-process rather
/// than over an IPC pipe to a second process.
enum OverlayAnchor: String, CaseIterable, Codable {
    case topLeft
    case topCenter
    case topRight
    case middleLeft
    case center
    case middleRight
    case bottomLeft
    case bottomCenter
    case bottomRight

    /// Human-readable label for the position picker menu.
    var displayName: String {
        switch self {
        case .topLeft: return "Top Left"
        case .topCenter: return "Top Center"
        case .topRight: return "Top Right"
        case .middleLeft: return "Middle Left"
        case .center: return "Center"
        case .middleRight: return "Middle Right"
        case .bottomLeft: return "Bottom Left"
        case .bottomCenter: return "Bottom Center"
        case .bottomRight: return "Bottom Right"
        }
    }

    /// Computes the top-left origin for a panel of `size` anchored within `visibleFrame`, with a
    /// fixed margin from the screen edges so the pill never touches the notch/menu bar/dock.
    func origin(for size: NSSize, in visibleFrame: NSRect, margin: CGFloat = 24) -> NSPoint {
        let minX = visibleFrame.minX + margin
        let maxX = visibleFrame.maxX - size.width - margin
        let midX = visibleFrame.midX - size.width / 2
        let minY = visibleFrame.minY + margin
        let maxY = visibleFrame.maxY - size.height - margin
        let midY = visibleFrame.midY - size.height / 2

        switch self {
        case .topLeft: return NSPoint(x: minX, y: maxY)
        case .topCenter: return NSPoint(x: midX, y: maxY)
        case .topRight: return NSPoint(x: maxX, y: maxY)
        case .middleLeft: return NSPoint(x: minX, y: midY)
        case .center: return NSPoint(x: midX, y: midY)
        case .middleRight: return NSPoint(x: maxX, y: midY)
        case .bottomLeft: return NSPoint(x: minX, y: minY)
        case .bottomCenter: return NSPoint(x: midX, y: minY)
        case .bottomRight: return NSPoint(x: maxX, y: minY)
        }
    }
}

/// The visual states the recording pill can display.
enum OverlayState: Equatable, Sendable {
    case hidden
    case listening(level: Double)
    case processing
    case notice(OverlayNotice)
}

/// What a notice on the pill says. Each one belongs to the stage it describes, so a failed insertion never reads as
/// a failed cleanup.
enum OverlayNotice: String, CaseIterable, Equatable, Sendable {
    case typed
    case typedWithoutCleanup
    case cleanupFellBack
    case microphoneUnavailable
    case microphoneAccessNeeded
    case microphoneStoppedEarly
    case recognizerMissing
    case transcriptionFailed
    case textKept
    case partlyInserted
    case mayNotBeInserted
    case accessibilityNeeded
    case durationLimitReached
    case stillProcessing

    var label: String {
        switch self {
        case .typed: return "Typed"
        case .typedWithoutCleanup: return "Typed without AI cleanup"
        case .cleanupFellBack: return "Cleanup failed, raw text used"
        case .microphoneUnavailable: return "Microphone unavailable"
        case .microphoneAccessNeeded: return "Microphone access needed"
        case .microphoneStoppedEarly: return "Microphone stopped early"
        case .recognizerMissing: return "Speech recognizer not found"
        case .transcriptionFailed: return "Transcription failed"
        case .textKept: return "Not inserted, text kept"
        case .partlyInserted: return "Only partly inserted"
        case .mayNotBeInserted: return "May not have been inserted"
        case .accessibilityNeeded: return "Accessibility access needed"
        case .durationLimitReached: return "Stopped at the time limit"
        case .stillProcessing: return "Still processing"
        }
    }

    var isFailure: Bool {
        switch self {
        case .typed, .typedWithoutCleanup, .durationLimitReached, .stillProcessing, .microphoneStoppedEarly:
            return false
        case .cleanupFellBack, .microphoneUnavailable, .microphoneAccessNeeded, .recognizerMissing,
            .transcriptionFailed, .textKept, .partlyInserted, .mayNotBeInserted, .accessibilityNeeded:
            return true
        }
    }

    var pillOutcome: PillOutcome? {
        switch self {
        case .typed:
            return PillOutcome(kind: .typed, detail: "")
        case .typedWithoutCleanup, .cleanupFellBack:
            return PillOutcome(kind: .typedWithoutCleanup, detail: PillOutcome.cleanupDidNotRun)
        case .textKept:
            return PillOutcome(kind: .nothingTyped, detail: PillOutcome.recoveryStep)
        case .partlyInserted:
            return PillOutcome(kind: .partlyTyped, detail: PillOutcome.recoveryStep)
        case .mayNotBeInserted:
            return PillOutcome(kind: .nothingTyped, detail: PillOutcome.recoveryStep)
        case .accessibilityNeeded:
            return PillOutcome(kind: .nothingTyped, detail: PillOutcome.accessibilityStep)
        case .microphoneUnavailable:
            return PillOutcome(kind: .nothingTyped, detail: PillOutcome.microphoneStep)
        case .microphoneAccessNeeded:
            return PillOutcome(kind: .nothingTyped, detail: PillOutcome.microphoneAccessStep)
        case .recognizerMissing:
            return PillOutcome(kind: .nothingTyped, detail: PillOutcome.recognizerStep)
        case .transcriptionFailed:
            return PillOutcome(kind: .nothingTyped, detail: PillOutcome.transcriptionStep)
        case .microphoneStoppedEarly, .durationLimitReached, .stillProcessing:
            return nil
        }
    }
}
