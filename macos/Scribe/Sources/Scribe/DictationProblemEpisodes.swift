import Foundation

struct DictationNoticeConfiguration: Equatable, Sendable {
    var microphoneUID: String?
    var shortcut: HotkeyBinding?
}

/// Notification episodes are independent of the per-dictation pill and recovery actions. A success in one stage
/// does not clear another stage's fault, and a late result cannot change a newer configuration's episode.
struct DictationProblemEpisodes: Sendable {
    enum Problem: String, CaseIterable, Sendable {
        case microphoneUnavailable
        case microphoneAccess
        case fallbackMicrophone
        case tooQuick
        case noAudio
        case onlySilence
        case noWords
        case recognizerMissing
        case transcriptionFailed
        case microphoneDisconnected
    }

    struct Ticket: Sendable, Equatable {
        let recording: UInt64
        let generation: UInt64
    }

    private struct Episode: Sendable {
        var latest: UInt64 = 0
        var announced = false
    }

    private var generation: UInt64 = 0
    private var episodes: [Problem: Episode] = [:]

    func begin(_ recording: RecordingID) -> Ticket {
        Ticket(recording: recording.rawValue, generation: generation)
    }

    mutating func configurationChanged() {
        generation &+= 1
        episodes = [:]
    }

    mutating func failed(_ problem: Problem, under ticket: Ticket) -> Bool {
        guard ticket.generation == generation else { return false }
        var episode = episodes[problem] ?? Episode()
        guard ticket.recording >= episode.latest else { return false }
        episode.latest = ticket.recording
        let announce = !episode.announced
        episode.announced = true
        episodes[problem] = episode
        return announce
    }

    mutating func recovered(_ problems: [Problem], under ticket: Ticket) {
        guard ticket.generation == generation else { return }
        for problem in problems {
            var episode = episodes[problem] ?? Episode()
            guard ticket.recording >= episode.latest else { continue }
            episode.latest = ticket.recording
            episode.announced = false
            episodes[problem] = episode
        }
    }

    mutating func selectionOpened(
        _ selection: MicrophoneSelectionOutcome, under ticket: Ticket,
        matchesCommittedSelection: Bool, retained: Bool
    ) -> Bool {
        let failed = selection.result != .selected
        guard ticket.generation == generation, matchesCommittedSelection else { return failed && retained }
        if !failed {
            recovered([.fallbackMicrophone], under: ticket)
            return false
        }
        return retained && self.failed(.fallbackMicrophone, under: ticket)
    }
}

struct MicrophoneSelectionOutcome: Sendable, Equatable {
    enum Result: Sendable, Equatable {
        case selected
        case systemDefault
        case unconfirmed
    }

    let requestedUID: String
    let result: Result
}

struct TrayActionNoticeEpisode: Sendable {
    private var announced = false

    mutating func failed() -> Bool {
        defer { announced = true }
        return !announced
    }

    mutating func recovered() {
        announced = false
    }
}

/// Classification uses the raw capture peak as on Windows, not the resampled audio or a recognition guess.
enum DictationCaptureProblem {
    static func hasOnlySilence(_ signal: CaptureSignalReport?) -> Bool {
        guard let signal else { return false }
        return signal.peak < CaptureSignalReport.nearSilenceThreshold
    }

    static func empty(held: Duration, reason: DictationStopReason) -> OverlayNotice {
        if reason == .deviceFault { return .microphoneStoppedEarly }
        return held < .seconds(1) ? .tooQuick : .noAudio
    }
}
