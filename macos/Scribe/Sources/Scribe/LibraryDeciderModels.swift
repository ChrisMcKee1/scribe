import Foundation

struct DraftTermRow: Equatable, Sendable {
    let rowID: Int64
    let row: LibraryRow
    let removalIntent: Bool
    let legacyEmpty: Bool
}

enum TermOrigin: Sendable {
    case custom
    case shipped
    case edited
    case pinned
    case off
    case added
    case noLongerShipped
}

struct LibraryRow: Equatable, Sendable {
    let key: LibraryTermKey
    let values: TermValues
    let origin: TermOrigin
    let shipped: TermValues?

    init(key: LibraryTermKey? = nil, values: TermValues, origin: TermOrigin, shipped: TermValues? = nil) {
        self.key = key ?? LibraryTermKey.from(values.spoken)
        self.values = values
        self.origin = origin
        self.shipped = shipped
    }

    static func custom(_ values: TermValues) -> LibraryRow {
        LibraryRow(values: values, origin: .custom)
    }
}

struct DraftLibrary: Equatable, Sendable {
    let id: String
    let name: String
    let category: String
    let description: String?
    let builtIn: Bool
    let rows: [DraftTermRow]
    let basedOn: String?
    let pendingDelete: Bool

    init(
        id: String,
        name: String,
        category: String,
        description: String?,
        builtIn: Bool,
        rows: [DraftTermRow],
        basedOn: String? = nil,
        pendingDelete: Bool = false
    ) {
        self.id = id
        self.name = name
        self.category = category
        self.description = description
        self.builtIn = builtIn
        self.rows = rows
        self.basedOn = basedOn
        self.pendingDelete = pendingDelete
    }
}

struct LibraryDraft: Equatable, Sendable {
    let revision: Int64
    let libraries: [DraftLibrary]

    func find(_ id: String?) -> DraftLibrary? {
        guard let id else {
            return nil
        }
        return libraries.first { $0.id.caseInsensitiveCompare(id) == .orderedSame }
    }
}

struct LibraryWorkspace: Equatable, Sendable {
    let draft: LibraryDraft

    init(revision: Int64 = 1, libraries: [DraftLibrary]) {
        draft = LibraryDraft(revision: revision, libraries: libraries)
    }

    func rowsOf(_ libraryID: String) -> [DraftTermRow] {
        draft.find(libraryID)?.rows ?? []
    }

    static func rowIDIn(_ draft: LibraryDraft, _ libraryID: String, _ index: Int) -> Int64? {
        guard let library = draft.find(libraryID), library.rows.indices.contains(index) else {
            return nil
        }
        return library.rows[index].rowID
    }
}

enum TermField: Sendable {
    case none
    case spoken
    case written
}

struct TermCommands: OptionSet, Sendable {
    let rawValue: Int

    static let turnOff = TermCommands(rawValue: 1 << 0)
    static let turnOn = TermCommands(rawValue: 1 << 1)
    static let delete = TermCommands(rawValue: 1 << 2)
    static let restoreBuiltIn = TermCommands(rawValue: 1 << 3)
    static let showOtherSources = TermCommands(rawValue: 1 << 4)
    static let copy = TermCommands(rawValue: 1 << 5)
    static let copyToDictionary = TermCommands(rawValue: 1 << 6)
}

enum LibraryMetadataField: Sendable {
    case none
    case name
    case category
    case description
    case basedOn
}

enum LibraryValidationKind: Sendable {
    case writtenWithoutSpoken
    case duplicateSpoken
    case emptyWrittenWithoutIntent
    case fieldTooLong
    case tooManyTerms
    case emptyName
    case duplicateName
    case metadataDoubleQuote
    case metadataUnreadableInOlder
    case contentNotSaveable
    case malformedText
}

struct LibraryValidationIssue: Sendable {
    let libraryID: String
    let rowID: Int64?
    let kind: LibraryValidationKind
    let field: TermField
    let metadata: LibraryMetadataField
    let otherRowID: Int64?

    init(
        libraryID: String,
        rowID: Int64?,
        kind: LibraryValidationKind,
        field: TermField = .none,
        metadata: LibraryMetadataField = .none,
        otherRowID: Int64? = nil
    ) {
        self.libraryID = libraryID
        self.rowID = rowID
        self.kind = kind
        self.field = field
        self.metadata = metadata
        self.otherRowID = otherRowID
    }
}
