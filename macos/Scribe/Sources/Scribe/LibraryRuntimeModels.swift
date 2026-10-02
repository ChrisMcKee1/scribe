import CryptoKit
import Foundation

enum LibraryFileState: String, Codable, Sendable {
    case available
    case unreadable
    case partlyReadable
    case newer
    case awaitingRelease
}

enum LocalStateHealth: String, Codable, Sendable {
    case absent
    case ok
    case unreadable
    case newer
}

enum LibraryOrigin: String, Codable, Sendable {
    case existing
    case created
    case imported
    case duplicated
    case restored
    case retiredBuiltIn
    case changedOutside
    case discovered
}

enum RuleTier: String, Codable, Sendable {
    case authored
    case shipped
    case legacy
}

enum BuiltInTermIntent: String, Codable, Sendable {
    case edited
    case added
    case pinned
    case off
}

struct LibraryContentHash: Codable, Equatable, Hashable, Sendable {
    let value: String

    init(value: String) {
        self.value = value
    }

    init(data: Data) {
        let digest = SHA256.hash(data: data)
        value = digest.map { String(format: "%02x", $0) }.joined()
    }
}

struct LegacyMarker: Codable, Equatable, Hashable, Sendable {
    let libraryId: String
    let key: String

    var termKey: LibraryTermKey {
        LibraryTermKey.from(key)
    }
}

struct BuiltInTermEdit: Codable, Equatable, Sendable {
    let key: String
    let intent: BuiltInTermIntent
    let base: TermValues?
    let value: TermValues?
    let acknowledged: TermValues?

    var termKey: LibraryTermKey {
        LibraryTermKey.from(key)
    }
}

struct BuiltInLibraryEdits: Codable, Equatable, Sendable {
    let version: Int
    let library: String
    let terms: [BuiltInTermEdit]

    static let currentVersion = 1
}

struct BuiltInEditsReadResult: Equatable, Sendable {
    let state: LibraryFileState
    let edits: BuiltInLibraryEdits?
    let version: Int?
}

struct LibraryLocalState: Codable, Equatable, Sendable {
    var generation: Int64
    var enabledIds: [String]
    var legacyEnabledIds: [String]
    var aiPermissions: [String: Bool]
    var acceptedContent: [String: String]
    var legacyMarkers: [LegacyMarker]
    var aiUpgradeNotice: [String]
    var health: LocalStateHealth
    var aiPermissionsLost: Bool

    static let absent = LibraryLocalState(
        generation: 0,
        enabledIds: [],
        legacyEnabledIds: [],
        aiPermissions: [:],
        acceptedContent: [:],
        legacyMarkers: [],
        aiUpgradeNotice: [],
        health: .absent,
        aiPermissionsLost: false)

    var enabledIdSet: Set<String> {
        Set(enabledIds.map { $0.lowercased() })
    }

    var legacyEnabledIdSet: Set<String> {
        Set(legacyEnabledIds.map { $0.lowercased() })
    }

    var acceptedHashes: [String: LibraryContentHash] {
        Dictionary(
            uniqueKeysWithValues: acceptedContent.map {
                ($0.key.lowercased(), LibraryContentHash(value: $0.value))
            }
        )
    }

    mutating func normalize() {
        enabledIds = Self.normalizedIDs(enabledIds)
        legacyEnabledIds = Self.normalizedIDs(legacyEnabledIds)
        aiUpgradeNotice = Self.normalizedIDs(aiUpgradeNotice)
        aiPermissions = Dictionary(uniqueKeysWithValues: aiPermissions.map { ($0.key.lowercased(), $0.value) })
        acceptedContent = Dictionary(uniqueKeysWithValues: acceptedContent.map { ($0.key.lowercased(), $0.value) })
        var seen = Set<String>()
        legacyMarkers = legacyMarkers.filter {
            let libraryId = $0.libraryId.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
            let key = LibraryTermKey.from($0.key).value.lowercased()
            guard !libraryId.isEmpty, !key.isEmpty else {
                return false
            }
            let composite = libraryId + "|" + key
            return seen.insert(composite).inserted
        }
    }

    mutating func setAcceptedContent(_ hash: LibraryContentHash?, for id: String) {
        let key = id.lowercased()
        if let hash {
            acceptedContent[key] = hash.value
        } else {
            acceptedContent.removeValue(forKey: key)
        }
    }

    mutating func setEnabled(_ enabled: Bool, for id: String) {
        var ids = enabledIdSet
        if enabled {
            ids.insert(id.lowercased())
        } else {
            ids.remove(id.lowercased())
        }
        enabledIds = Self.normalizedIDs(Array(ids))
    }

    mutating func setAIPermission(_ permitted: Bool, for id: String) {
        aiPermissions[id.lowercased()] = permitted
    }

    mutating func removeState(for id: String) {
        let key = id.lowercased()
        aiPermissions.removeValue(forKey: key)
        acceptedContent.removeValue(forKey: key)
        enabledIds.removeAll { $0.caseInsensitiveCompare(id) == .orderedSame }
        legacyEnabledIds.removeAll { $0.caseInsensitiveCompare(id) == .orderedSame }
        aiUpgradeNotice.removeAll { $0.caseInsensitiveCompare(id) == .orderedSame }
        legacyMarkers.removeAll { $0.libraryId.caseInsensitiveCompare(id) == .orderedSame }
    }

    private static func normalizedIDs(_ ids: [String]) -> [String] {
        var seen = Set<String>()
        var result: [String] = []
        for raw in ids {
            let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !trimmed.isEmpty else { continue }
            let key = trimmed.lowercased()
            if seen.insert(key).inserted {
                result.append(trimmed)
            }
        }
        return result.sorted { $0.localizedCaseInsensitiveCompare($1) == .orderedAscending }
    }
}

struct CatalogLibrary: Equatable, Sendable {
    let library: DictionaryLibrary
    let state: LibraryFileState
    let contentHash: LibraryContentHash?
    let origin: LibraryOrigin
    let edits: BuiltInLibraryEdits?
    let previousEditsAvailable: Bool
    let readErrorCount: Int

    var id: String { library.id }
    var builtIn: Bool { library.builtIn }
    var fileName: String? { library.fileName }
}

struct LibraryCatalog: Equatable, Sendable {
    let generation: Int64
    let libraries: [CatalogLibrary]
    let localState: LibraryLocalState

    func find(id: String) -> CatalogLibrary? {
        libraries.first { $0.id.caseInsensitiveCompare(id) == .orderedSame }
    }
}

struct AiVocabularyScope: Equatable, Sendable {
    let generation: Int64
    let permittedContent: [String: String?]

    static let none = AiVocabularyScope(generation: 0, permittedContent: [:])

    var permittedLibraryIds: Set<String> {
        Set(permittedContent.keys.map { $0.lowercased() })
    }

    func covers(_ admitted: AiVocabularyScope) -> Bool {
        for (id, content) in admitted.permittedContent {
            guard permittedContent[id.lowercased()] == content else {
                return false
            }
        }
        return true
    }
}

struct LibraryVocabulary: Equatable, Sendable {
    let generation: Int64
    let entries: [DictionaryEntry]
    let aiEntries: [DictionaryEntry]
    let aiScope: AiVocabularyScope

    static let empty = LibraryVocabulary(generation: 0, entries: [], aiEntries: [], aiScope: .none)
}

struct ComposedLibraryRule: Equatable, Sendable {
    let entry: DictionaryEntry
    let libraryId: String
    let key: LibraryTermKey
    let tier: RuleTier
}

struct LibraryComposition: Equatable, Sendable {
    let rules: [ComposedLibraryRule]
    let entries: [DictionaryEntry]
    let aiEntries: [DictionaryEntry]
    let aiExcludedLibraryIds: Set<String>
    let enabledLibraries: [DictionaryLibrary]
}
