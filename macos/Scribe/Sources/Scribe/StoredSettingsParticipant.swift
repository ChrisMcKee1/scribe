import Foundation

struct StoredSettingCheck: Equatable, Sendable {
    let key: String
    let expected: String?
}

struct StoredSettingWrite: Equatable, Sendable {
    let key: String
    let value: String?
}

enum StoredSettingsCommitError: Error {
    case conflict
    case repeatedKey
}

/// Participants prepare these values without writing. Settings combines every participant before one SQLite commit.
struct StoredSettingsParticipant: Equatable, Sendable {
    let checks: [StoredSettingCheck]
    let writes: [StoredSettingWrite]
}
