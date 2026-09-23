import Foundation

/// Reads what every dictation applies (`PersistenceRuleSet`) off the main actor and hands it to `apply` on the main
/// actor. Refreshes can overlap, for example after two Settings edits in a row; only the newest one's rules are
/// applied, whichever read finishes last, and a refresh that fails keeps the rules already in use, so a storage error
/// never leaves dictation running with an empty or older rule set.
@MainActor
final class RuleSetRefresher {
    private let loadRules: @Sendable () async throws -> PersistenceRuleSet
    private let apply: @MainActor (PersistenceRuleSet) -> Void
    private let onFailure: @MainActor (any Error) -> Void
    private var section = SettingsSectionLoad()

    init(
        load: @escaping @Sendable () async throws -> PersistenceRuleSet,
        apply: @escaping @MainActor (PersistenceRuleSet) -> Void,
        onFailure: @escaping @MainActor (any Error) -> Void
    ) {
        loadRules = load
        self.apply = apply
        self.onFailure = onFailure
    }

    func refresh() async {
        let ticket = section.begin()
        do {
            let rules = try await loadRules()
            guard section.publish(ticket) else {
                return
            }
            apply(rules)
        } catch {
            guard section.fail(ticket) else {
                return
            }
            onFailure(error)
        }
    }
}
