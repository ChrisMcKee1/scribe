import Foundation

enum WordPackError: Error, Equatable {
    case invalidEdits
    case readOnly
    case unavailable
    case staleDraft
    case staleCatalog
    case recoveryPending
    case unsafePath
    case outsideChange
    case invalidPreparation
}

struct LibraryEditResult: Sendable {
    let applied: Bool
    let issue: LibraryValidationIssue?
}

struct LibraryCaptureResult: Sendable {
    let changeSet: LibraryChangeSet?
    let issues: [LibraryValidationIssue]
}

/// Immutable Save input. Capturing it never writes files or changes the committed vocabulary.
struct LibraryChangeSet: Equatable, Sendable {
    let draftRevision: Int64
    let expectedGeneration: Int64
    let libraries: [DraftLibrary]
    let localState: LibraryLocalState
    let recentlyDeleted: [RecentlyDeletedWordPack]
    let purgeIDs: Set<UUID>
    let restoreIDs: Set<UUID>

    var isEmpty: Bool { libraries.isEmpty && purgeIDs.isEmpty && restoreIDs.isEmpty }
}

/// Deleted content is retained for 30 days from commit, never from the draft's Delete click.
struct RecentlyDeletedWordPack: Codable, Equatable, Sendable, Identifiable {
    let id: UUID
    let libraryID: String
    let name: String
    let category: String
    let description: String?
    let basedOn: String?
    let values: [TermValues]
    let deletedAt: Date

    static let retentionDays = 30

    func expired(at now: Date) -> Bool {
        now.timeIntervalSince(deletedAt) >= Double(Self.retentionDays * 24 * 60 * 60)
    }
}

struct WordPackWorkspaceState: Equatable, Sendable {
    var libraries: [DraftLibrary]
    var local: LibraryLocalState
    var deleted: [RecentlyDeletedWordPack]
    var purgeIDs: Set<UUID> = []
    var restoreIDs: Set<UUID> = []
}

struct WordPackUndoEntry: Equatable, Sendable {
    let label: String
    let before: WordPackWorkspaceState
    let after: WordPackWorkspaceState
}
