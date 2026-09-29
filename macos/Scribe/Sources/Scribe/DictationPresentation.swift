import Foundation

/// One change to what the tray and the recording pill show, numbered by the lifecycle that made it.
///
/// Every change the dictation lifecycle makes takes the next revision, and a renderer applies a change only when its
/// revision is higher than the last one it applied (`PresentationRevisionGate`), so a late, older change can never
/// undo a newer one. The macOS side of Windows' `DictationPresentation` and `PresentationRelay`.
struct DictationPresentation: Equatable, Sendable {
    /// Positive, and higher for every later change.
    let revision: UInt64
    let overlay: OverlayState
    /// A recording is admitted, opening or live: the tray's test dictation item offers Stop.
    let isRecording: Bool
    let isPaused: Bool
}

/// Keeps a renderer on the newest change: `admit` accepts a revision only when it is higher than every one accepted
/// before.
struct PresentationRevisionGate: Sendable {
    private(set) var lastAdmitted: UInt64 = 0

    mutating func admit(_ revision: UInt64) -> Bool {
        guard revision > lastAdmitted else { return false }
        lastAdmitted = revision
        return true
    }
}
