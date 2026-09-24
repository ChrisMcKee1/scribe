import Foundation

/// How a pill notice relates to the recordings and dictations around it.
enum OverlayNoticeRole: Equatable, Sendable {
    /// Feedback on the press just made (`stillProcessing`). Shown only when nothing else holds the pill, and never
    /// kept for later: it means nothing once the moment has passed.
    case feedback
    /// An outcome worth knowing that asks nothing of the user.
    case informational
    /// An outcome that needs the user: a permission, a missing recognizer, or text kept for recovery.
    case actionable
}

extension OverlayNotice {
    var role: OverlayNoticeRole {
        switch self {
        case .stillProcessing:
            return .feedback
        case .cleanupFellBack, .transcriptionFailed, .microphoneStoppedEarly, .durationLimitReached:
            return .informational
        case .microphoneAccessNeeded, .microphoneUnavailable, .recognizerMissing, .textKept, .partlyInserted,
            .mayNotBeInserted, .accessibilityNeeded:
            return .actionable
        }
    }

    /// The informational outcomes that must not go unsaid: cleanup could not be used for a dictation, or its speech could
    /// not be recognized. When the pill cannot show one at once, a notification says it instead of the pill, rather than
    /// the pill saying it later as well. Such a notification is posted before the dictation's delivery, so it says
    /// nothing about whether the text went in.
    var notifiesWhenThePillIsBusy: Bool {
        self == .cleanupFellBack || self == .transcriptionFailed
    }
}

/// The stage of a dictation an outcome reports on.
enum DictationNoticeStage: String, Equatable, Sendable {
    case admission
    case capture
    case recognition
    case cleanup
    case delivery
}

/// One outcome for the pill: a unique id, what it says, the recording or dictation it is about and its stage, and for
/// an outcome about text kept for recovery, the recovery generation the text was kept in.
struct DictationOutcomeNotice: Equatable, Sendable {
    let id: UInt64
    let kind: OverlayNotice
    /// Nil for feedback on a press that started nothing.
    let source: RecordingID?
    let stage: DictationNoticeStage
    /// `LastTranscriptStore.generation` when the outcome's text was kept for recovery. Clear history starts a new
    /// generation, which makes the outcome obsolete: the text it offers is gone.
    let recoveryGeneration: UInt64?
}

/// Which outcome the pill shows, and in what order the rest follow. Pure state, so every rule is tested on its own;
/// `DictationController` applies what it decides.
///
/// One notice is on the pill at a time, for its whole time, and every outcome that cannot be shown yet waits, in the
/// order it arrived. A recording owns the pill from its admission, so an outcome that arrives meanwhile waits for the
/// recording to end: a newer recording does not make an older failure obsolete. An outcome never replaces a notice
/// already on the pill (only feedback gives way), so an older dictation's late outcome cannot take down a newer
/// actionable failure. A cleanup fallback or a failed recognition that cannot be shown at once is posted as a
/// notification instead. Duplicates, outcomes after shutdown began and outcomes about text a Clear has removed are
/// rejected.
struct DictationNoticeSchedule: Equatable, Sendable {
    /// A notice on the pill.
    struct Shown: Equatable, Sendable {
        let notice: DictationOutcomeNotice
        /// The presentation revision that put it there. Its timed end has to carry the same token.
        let token: UInt64
    }

    /// What to do with an outcome that just arrived.
    enum Arrival: Equatable, Sendable {
        /// Put it on the pill now.
        case show
        /// It waits for the pill.
        case waiting
        /// The pill cannot show it now; post it as a notification instead.
        case notify
        case rejected(Rejection)
    }

    enum Rejection: String, Equatable, Sendable {
        /// Shutdown has begun.
        case closing
        /// The same outcome of the same recording is already on the pill or waiting.
        case duplicate
        /// Feedback while the pill is taken.
        case pillBusy
        /// Its text was cleared from recovery since.
        case cleared
    }

    private(set) var shown: Shown?
    private(set) var waiting: [DictationOutcomeNotice] = []

    /// Decides what happens to `notice`. `pillIsOwned` while a recording exists; `recoveryGeneration` is the store's
    /// generation now.
    mutating func arrive(
        _ notice: DictationOutcomeNotice, pillIsOwned: Bool, isClosing: Bool, recoveryGeneration: UInt64
    ) -> Arrival {
        if isClosing {
            return .rejected(.closing)
        }
        if let generation = notice.recoveryGeneration, generation != recoveryGeneration {
            return .rejected(.cleared)
        }
        if isDuplicate(notice) {
            return .rejected(.duplicate)
        }
        let pillIsFree = !pillIsOwned && waiting.isEmpty && (shown == nil || shown?.notice.kind.role == .feedback)
        switch notice.kind.role {
        case .feedback:
            return pillIsFree ? .show : .rejected(.pillBusy)
        case .informational, .actionable:
            if pillIsFree {
                return .show
            }
            if notice.kind.notifiesWhenThePillIsBusy {
                return .notify
            }
            waiting.append(notice)
            return .waiting
        }
    }

    /// `notice` went on the pill under `token`.
    mutating func didShow(_ notice: DictationOutcomeNotice, token: UInt64) {
        shown = Shown(notice: notice, token: token)
    }

    /// The timed end of the notice shown under `token`. True when that notice was still on the pill, which it leaves;
    /// false for a stale end, which changes nothing.
    mutating func expire(token: UInt64) -> Bool {
        guard let shown, shown.token == token else { return false }
        self.shown = nil
        return true
    }

    /// A recording was admitted and takes the pill: the notice on it has been seen, and goes.
    mutating func yieldToRecording() {
        shown = nil
    }

    /// The outcome to show next, taken from the front of the queue, when the pill is free and one is waiting.
    mutating func takeNext(pillIsOwned: Bool) -> DictationOutcomeNotice? {
        guard !pillIsOwned, shown == nil, !waiting.isEmpty else { return nil }
        return waiting.removeFirst()
    }

    /// Clear history started generation `current`: an outcome about text kept before it is obsolete. True when the
    /// notice on the pill was one, which leaves the pill.
    mutating func recoveryCleared(current: UInt64) -> Bool {
        waiting.removeAll { notice in
            notice.recoveryGeneration.map { $0 != current } ?? false
        }
        guard let shown, let generation = shown.notice.recoveryGeneration, generation != current else { return false }
        self.shown = nil
        return true
    }

    /// Shutdown: nothing more is shown.
    mutating func close() {
        shown = nil
        waiting = []
    }

    private func isDuplicate(_ notice: DictationOutcomeNotice) -> Bool {
        let known = waiting + (shown.map { [$0.notice] } ?? [])
        return known.contains { other in
            other.id == notice.id || (notice.source != nil && other.source == notice.source && other.kind == notice.kind)
        }
    }
}
