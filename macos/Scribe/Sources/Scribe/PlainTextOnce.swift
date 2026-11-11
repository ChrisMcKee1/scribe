import Foundation

/// Where Plain text once stands. Mirrors Windows' `PlainTextOnceStatus`.
enum PlainTextOnceStatus: String, Equatable, Sendable {
    case off
    case armed
    /// The recording just admitted took it.
    case consumed
    /// No recording was admitted within `PlainTextOnce.lifetime`.
    case expired
    case cancelled
    case shutdown
}

/// A change of Plain text once, numbered so a late notice can tell it is no longer the newest.
struct PlainTextOnceState: Equatable, Sendable {
    let status: PlainTextOnceStatus
    let revision: UInt64

    var isArmed: Bool {
        status == .armed
    }
}

/// One transient preference for the next admitted recording: plain text, typed, whatever its app or profile would
/// otherwise get. It is never a cleanup, dictionary or snippet bypass: those still run. Mirrors Windows'
/// `PlainTextOnce`. Armed from the menu bar menu, it lasts `lifetime`, and only an admitted recording consumes it, so a
/// press that was turned away leaves it armed. Nothing is stored: quitting clears it.
@MainActor
final class PlainTextOnce {
    static let lifetime: Duration = .seconds(60)

    /// Every change, on the main actor. A consumption is published only through `notify(_:)`, which the admitting
    /// caller runs on a later turn, because admission can happen inside the event tap's callback.
    var onChange: ((PlainTextOnceState) -> Void)?

    private let now: () -> ContinuousClock.Instant
    private let sleep: @Sendable (ContinuousClock.Instant) async throws -> Void
    private var state = PlainTextOnceState(status: .off, revision: 0)
    private var armedAt: ContinuousClock.Instant?
    private var expiry: Task<Void, Never>?
    private var closed = false

    init(
        now: @escaping () -> ContinuousClock.Instant = { ContinuousClock.now },
        sleep: @escaping @Sendable (ContinuousClock.Instant) async throws -> Void = { deadline in
            try await Task.sleep(until: deadline, clock: .continuous)
        }
    ) {
        self.now = now
        self.sleep = sleep
    }

    /// The state now, expired first if its time has passed.
    var current: PlainTextOnceState {
        if expireIfDue() {
            publish(state)
        }
        return state
    }

    func arm() {
        guard !closed else { return }
        let armed = now()
        armedAt = armed
        set(.armed)
        expiry?.cancel()
        let deadline = armed.advanced(by: Self.lifetime)
        let sleep = self.sleep
        expiry = Task { [weak self] in
            do {
                try await sleep(deadline)
            } catch {
                return
            }
            self?.expireFromTimer()
        }
        publish(state)
    }

    func cancel() {
        guard !closed, !expireIfDue(), state.isArmed else { return }
        set(.cancelled)
        publish(state)
    }

    /// Called only when a recording is admitted, never for a press that was turned away. A recording whose
    /// microphone then fails to open was still admitted, and takes it. Returns whether it was armed, and the change to
    /// publish with `notify(_:)` once admission has returned.
    func consumeForAdmittedRecording() -> (consumed: Bool, change: PlainTextOnceState?) {
        let expired = expireIfDue()
        guard !closed, state.isArmed else {
            return (false, expired ? state : nil)
        }
        set(.consumed)
        return (true, state)
    }

    /// Publishes a change `consumeForAdmittedRecording()` returned, unless a newer one has replaced it since.
    func notify(_ change: PlainTextOnceState?) {
        guard let change, change.revision == state.revision else { return }
        publish(change)
    }

    /// Clears it as Scribe quits.
    func shutDown() {
        guard !closed else { return }
        closed = true
        set(.shutdown)
        publish(state)
    }

    private func expireFromTimer() {
        guard !closed, expireIfDue() else { return }
        publish(state)
    }

    /// Whether this call expired an armed preference whose time had passed.
    private func expireIfDue() -> Bool {
        guard state.isArmed, let armedAt, armedAt.duration(to: now()) >= Self.lifetime else {
            return false
        }
        set(.expired)
        return true
    }

    private func set(_ status: PlainTextOnceStatus) {
        state = PlainTextOnceState(status: status, revision: state.revision + 1)
        if status != .armed {
            expiry?.cancel()
            expiry = nil
            armedAt = nil
        }
    }

    private func publish(_ change: PlainTextOnceState) {
        onChange?(change)
    }
}

/// The words for Plain text once, in the menu bar menu and its notices. Mirrors Windows' tray texts.
enum PlainTextOnceText {
    static let menuTitle = "Use Plain Text Once"
    static let armedMenuTitle = "Use Plain Text Once (Armed)"
    static let armedHelp =
        "The next recording within 60 seconds uses plain text, typed. Choose it again to cancel."
    static let help =
        "Arms the next recording for 60 seconds: plain text, typed, whatever the app or its profile would use. AI "
        + "cleanup, your dictionary and snippets still run."

    /// The notice for a change, or nil for one that needs none.
    static func notice(_ state: PlainTextOnceState) -> (title: String, body: String)? {
        switch state.status {
        case .armed:
            return (
                "Plain text once is armed",
                "The next recording within 60 seconds uses plain text. AI cleanup, your dictionary and snippets "
                    + "still run. Choose Use Plain Text Once again in the menu bar menu to cancel."
            )
        case .consumed:
            return (
                "Plain text once was used",
                "The recording that just started took the plain-text choice. It didn't skip AI cleanup, your "
                    + "dictionary or snippets."
            )
        case .expired:
            return (
                "Plain text once expired",
                "No recording started within 60 seconds. Nothing is armed; choose Use Plain Text Once to try again."
            )
        case .cancelled:
            return (
                "Plain text once cancelled",
                "Nothing is armed. Your next recording uses your saved app preferences."
            )
        case .off, .shutdown:
            return nil
        }
    }
}
