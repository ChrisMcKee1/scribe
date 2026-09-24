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

/// The visual states the recording pill can display. Mirrors Windows' `Scribe.Overlay.OverlayState`, with the one
/// failure state split into notices that each name the stage they come from.
enum OverlayState: Equatable, Sendable {
    /// Hidden / parked (no pill visible).
    case hidden
    /// Capturing microphone input: pulsing red dot and live level meter.
    case listening(levelDbfs: Float)
    /// Transcribing or AI-polishing: bouncing dots.
    case processing
    /// A short notice about how a dictation went, shown for a moment and then taken down.
    case notice(OverlayNotice)
}

/// What a notice on the pill says. Each one belongs to the stage it describes, so a failed insertion never reads as
/// a failed cleanup.
enum OverlayNotice: String, CaseIterable, Equatable, Sendable {
    /// AI cleanup did not produce usable text, so the raw transcript went in instead.
    case cleanupFellBack
    /// The microphone could not be opened.
    case microphoneUnavailable
    /// macOS has not given Scribe the microphone.
    case microphoneAccessNeeded
    /// The input device stopped part way; what was heard before it stopped went in.
    case microphoneStoppedEarly
    /// No speech recognizer is installed where Scribe looks.
    case recognizerMissing
    /// The speech recognizer failed.
    case transcriptionFailed
    /// Nothing was inserted, because focus moved or the target could not be confirmed; the transcript is kept.
    case textKept
    /// Typing stopped part way; the transcript is kept.
    case partlyInserted
    /// The target stopped answering during an insertion, so the text may or may not be there.
    case mayNotBeInserted
    /// Scribe is not trusted for Accessibility, so nothing could be inserted.
    case accessibilityNeeded
    /// The recording reached its duration ceiling and was transcribed.
    case durationLimitReached
    /// A new recording was turned away because earlier dictations are still being processed.
    case stillProcessing

    var label: String {
        switch self {
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

    /// Whether the notice reports a failure (red) rather than something the user should know (neutral).
    var isFailure: Bool {
        switch self {
        case .durationLimitReached, .stillProcessing, .microphoneStoppedEarly:
            return false
        case .cleanupFellBack, .microphoneUnavailable, .microphoneAccessNeeded, .recognizerMissing,
            .transcriptionFailed, .textKept, .partlyInserted, .mayNotBeInserted, .accessibilityNeeded:
            return true
        }
    }
}
