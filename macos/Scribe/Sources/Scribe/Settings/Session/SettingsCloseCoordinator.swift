import Foundation

/// The lifecycle's destructive shutdown starts only after the settings guard permits it.
@MainActor
final class SettingsCloseCoordinator {
    private let session: SettingsSession
    private let prompt: @MainActor (SettingsCloseDecision) async -> SettingsCloseChoice
    private let proceed: @MainActor (SettingsCloseTrigger) -> Void
    private var deciding = false

    init(
        session: SettingsSession,
        prompt: @escaping @MainActor (SettingsCloseDecision) async -> SettingsCloseChoice,
        proceed: @escaping @MainActor (SettingsCloseTrigger) -> Void
    ) {
        self.session = session
        self.prompt = prompt
        self.proceed = proceed
    }

    @discardableResult
    func request(_ trigger: SettingsCloseTrigger) async -> Bool {
        guard !deciding else { return false }
        deciding = true
        defer { deciding = false }
        let decision = session.closeDecision(trigger)
        if decision.ask {
            let choice = await prompt(decision)
            guard await session.resolveClose(choice) else { return false }
        }
        proceed(trigger)
        return true
    }
}
