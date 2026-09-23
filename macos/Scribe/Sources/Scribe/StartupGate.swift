import Foundation

/// Whether dictation may use the rules the user stored yet. The app starts with an empty rule set and loads the real
/// one asynchronously at launch, so without this a dictation that finished before that load would skip the user's
/// dictionary rules, snippets and app profiles, and nothing would say so.
///
/// Startup opens the gate exactly once (`open(afterMigrating:then:loadingRules:)`): after the database migration
/// queued at launch has run and the first attempt to load the rules has finished, whether or not either succeeded.
/// Dictation's post-processing waits for it (`whenOpen(_:)`), and so does Quick Add, which shows and changes rules.
/// Other storage calls need no gate of their own: the migration is the first operation on the storage queue
/// (`PersistenceStore.beginPreparing()`), and the queue runs operations in the order they arrive, so every later
/// read and write meets the migrated schema, or the migration's failure.
///
/// If the migration or the first rule read fails, the gate still opens, as `.withoutStoredRules`. Dictation keeps
/// working with no dictionary rules, snippets or app profiles rather than waiting forever, and the app says so once,
/// with a log line and a notification. A later refresh that succeeds, after a change in Settings or Quick Add,
/// applies the rules as usual.
///
/// Main-actor state without locks. The app owns it today; the dictation lifecycle owner (stream mf) is expected to
/// take it over, with `whenOpen(_:)` as the one step its pipeline needs.
@MainActor
final class StartupGate {
    enum State: Equatable, Sendable {
        /// The database is migrated and the user's rules are in place.
        case ready
        /// The migration or the first rule read failed; dictation runs with no stored rules.
        case withoutStoredRules
    }

    private(set) var state: State?
    private var waiters: [CheckedContinuation<State, Never>] = []

    init() {}

    var isOpen: Bool {
        state != nil
    }

    /// How many callers are waiting for the gate to open.
    var waitingCount: Int {
        waiters.count
    }

    /// Opens the gate. The first call decides the state; later calls change nothing.
    func open(_ state: State) {
        guard self.state == nil else {
            return
        }
        self.state = state
        let waiting = waiters
        waiters.removeAll()
        for waiter in waiting {
            waiter.resume(returning: state)
        }
    }

    /// Returns the state at once if the gate is open, otherwise when it opens.
    func wait() async -> State {
        if let state {
            return state
        }
        return await withCheckedContinuation { (continuation: CheckedContinuation<State, Never>) in
            waiters.append(continuation)
        }
    }

    /// Dictation's post-processing step: runs `process` once the gate is open, so a transcript that finished before
    /// startup loaded the user's rules waits for them instead of meeting the empty rule set.
    func whenOpen<Value>(_ process: () -> Value) async -> Value {
        _ = await wait()
        return process()
    }

    /// Startup's storage steps, in order, then opens the gate and returns how it opened. `migrate` waits for the
    /// migration queued at launch, `started` runs once it has succeeded (maintenance starts there), and `loadRules`
    /// makes the first attempt to load the rules and reports whether they were applied. A failure at either step
    /// opens the gate `.withoutStoredRules`.
    @discardableResult
    func open(
        afterMigrating migrate: @MainActor () async throws -> Void,
        then started: @MainActor () -> Void,
        loadingRules loadRules: @MainActor () async -> Bool
    ) async -> State {
        let outcome: State
        do {
            try await migrate()
            started()
            outcome = await loadRules() ? .ready : .withoutStoredRules
        } catch {
            outcome = .withoutStoredRules
        }
        open(outcome)
        return state ?? outcome
    }
}
