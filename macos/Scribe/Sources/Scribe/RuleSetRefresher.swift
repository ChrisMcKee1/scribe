import Foundation

/// Reads what every dictation applies off the main actor, compiled there too (`DictationRuleSnapshot`), and hands it to
/// `apply` on the main actor. Refreshes can overlap, for example after two Settings edits in a row; only the newest
/// one's rules are applied, whichever read and compile finishes last, and a refresh that fails keeps the rules already
/// in use, so a storage error never leaves dictation running with an empty or older rule set.
@MainActor
final class RuleSetRefresher<Loaded: Sendable> {
    /// What one refresh did.
    enum Outcome: Equatable, Sendable {
        /// Its rules were read and applied.
        case applied
        /// Its read failed; the rules already in use stay.
        case failed
        /// A newer refresh started while it ran, so its result was dropped.
        case superseded
    }

    private let loadRules: @Sendable () async throws -> Loaded
    private let apply: @MainActor (Loaded) -> Void
    private let onFailure: @MainActor (any Error) -> Void
    private var section = SettingsSectionLoad()

    init(
        load: @escaping @Sendable () async throws -> Loaded,
        apply: @escaping @MainActor (Loaded) -> Void,
        onFailure: @escaping @MainActor (any Error) -> Void
    ) {
        loadRules = load
        self.apply = apply
        self.onFailure = onFailure
    }

    @discardableResult
    func refresh() async -> Outcome {
        let ticket = section.begin()
        do {
            let rules = try await loadRules()
            guard section.publish(ticket) else {
                return .superseded
            }
            apply(rules)
            return .applied
        } catch {
            guard section.fail(ticket) else {
                return .superseded
            }
            onFailure(error)
            return .failed
        }
    }

    /// Refreshes until a refresh settles, applied or failed, instead of being overtaken, and reports how. Startup
    /// uses it to learn whether the user's rules are in place: a change made in Settings while the first read runs
    /// starts a newer refresh, and the answer then belongs to that one. Only one caller should loop at a time.
    func refreshUntilSettled() async -> Outcome {
        var outcome = await refresh()
        while outcome == .superseded {
            outcome = await refresh()
        }
        return outcome
    }
}
