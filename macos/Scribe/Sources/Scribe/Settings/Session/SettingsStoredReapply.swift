import Foundation

/// Explicit commands may reapply storage, never the window's editing document, including after a failed Save.
@MainActor
struct SettingsStoredReapply {
    let load: @MainActor () async throws -> SettingsDocument
    let publish: @MainActor (SettingsDocument) async -> SettingsApplicationOutcome

    func run() async -> SettingsApplicationOutcome {
        do {
            let stored = try await load()
            return await publish(stored)
        } catch {
            return .notApplied
        }
    }
}
