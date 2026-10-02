import Foundation

/// The application supplies its send authority. CLI and isolated codec tests need no outbound authority.
/// Begin/end withdraw permission across an async write without keeping a send lock held across an await.
struct LibraryVocabularyPublication: Sendable {
    let revision: @Sendable () -> UInt64
    let publishRead: @Sendable (AiVocabularyScope, UInt64) -> Bool
    let beginChange: @Sendable () -> Void
    let endChange: @Sendable () -> Void

    func changing<Result>(_ change: () throws -> Result) rethrows -> Result {
        beginChange()
        defer { endChange() }
        return try change()
    }

    func changingAsync<Result>(_ change: () async throws -> Result) async rethrows -> Result {
        beginChange()
        defer { endChange() }
        return try await change()
    }
}
