import Foundation

enum DictionaryOverlapKind: Sendable {
    case redundant
    case override
}

struct DictionaryOverlap: Equatable, Sendable {
    let kind: DictionaryOverlapKind
    let pattern: String
    let replacement: String
    let wordPackReplacement: String
    let wordPackId: String

    var isRedundant: Bool { kind == .redundant }
}

struct LibraryCoverage: Equatable, Sendable {
    let entry: DictionaryEntry
    let libraryId: String
    let libraryName: String
    let builtIn: Bool
    let fileName: String?
}

struct DictionaryOverlapReport: Equatable, Sendable {
    let overlaps: [DictionaryOverlap]

    var redundant: [DictionaryOverlap] { overlaps.filter(\.isRedundant) }
    var overrides: [DictionaryOverlap] { overlaps.filter { !$0.isRedundant } }
    var redundantCount: Int { redundant.count }
    var overrideCount: Int { overrides.count }
    var hasAny: Bool { !overlaps.isEmpty }
}

enum DictionaryLibraryOverlapAnalyzer {
    static func analyze(
        personal: [DictionaryEntry]?,
        libraryEntries: [DictionaryEntry]?,
        libraryIdsByPattern: [String: String]? = nil
    ) -> DictionaryOverlapReport {
        guard let personal, let libraryEntries else {
            return DictionaryOverlapReport(overlaps: [])
        }

        var libraryByPattern: [String: DictionaryEntry] = [:]
        for entry in libraryEntries where entry.enabled {
            let pattern = entry.pattern.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !pattern.isEmpty else { continue }
            if libraryByPattern[pattern.lowercased()] == nil {
                libraryByPattern[pattern.lowercased()] = entry
            }
        }

        var overlaps: [DictionaryOverlap] = []
        for entry in personal where entry.enabled {
            let pattern = entry.pattern.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !pattern.isEmpty, let covering = libraryByPattern[pattern.lowercased()] else { continue }
            let replacement = entry.replacement.trimmingCharacters(in: .whitespacesAndNewlines)
            let libraryReplacement = covering.replacement.trimmingCharacters(in: .whitespacesAndNewlines)
            let kind: DictionaryOverlapKind =
                replacement == libraryReplacement && entry.wholeWord == covering.wholeWord ? .redundant : .override
            overlaps.append(
                DictionaryOverlap(
                    kind: kind,
                    pattern: pattern,
                    replacement: replacement,
                    wordPackReplacement: libraryReplacement,
                    wordPackId: libraryIdsByPattern?[pattern] ?? libraryIdsByPattern?[pattern.lowercased()] ?? ""))
        }
        return DictionaryOverlapReport(overlaps: overlaps)
    }

    static func coverage(
        libraries: [DictionaryLibrary]?,
        enabledIds: some Sequence<String>
    ) -> [String: LibraryCoverage] {
        guard let libraries else { return [:] }
        var result: [String: LibraryCoverage] = [:]
        for library in LibraryPrecedence.enabled(libraries, enabledIDs: Set(enabledIds)) {
            for entry in library.entries where entry.enabled {
                let pattern = entry.pattern.trimmingCharacters(in: .whitespacesAndNewlines)
                guard !pattern.isEmpty else { continue }
                let key = pattern.lowercased()
                if result[key] == nil {
                    result[key] = LibraryCoverage(
                        entry: entry,
                        libraryId: library.id,
                        libraryName: library.name,
                        builtIn: library.builtIn,
                        fileName: library.builtIn ? nil : library.fileName ?? "\(library.id).csv")
                }
            }
        }
        return result
    }

    static func analyzeEnabledLibraries(
        personal: [DictionaryEntry]?,
        libraries: [DictionaryLibrary]?,
        enabledIds: some Sequence<String>
    ) -> DictionaryOverlapReport {
        guard let libraries else {
            return DictionaryOverlapReport(overlaps: [])
        }
        let enabled = LibraryPrecedence.enabled(libraries, enabledIDs: Set(enabledIds))
        let entries = enabled.flatMap(\.entries)
        var names: [String: String] = [:]
        for library in enabled {
            for entry in library.entries where entry.enabled {
                let pattern = entry.pattern.trimmingCharacters(in: .whitespacesAndNewlines)
                guard !pattern.isEmpty else { continue }
                names[pattern] = names[pattern] ?? library.name
                names[pattern.lowercased()] = names[pattern.lowercased()] ?? library.name
            }
        }
        return analyze(personal: personal, libraryEntries: entries, libraryIdsByPattern: names)
    }

    static func removeRedundant(
        personal: [DictionaryEntry],
        report: DictionaryOverlapReport
    ) -> [DictionaryEntry] {
        guard report.redundantCount > 0 else { return personal }
        let redundant = Set(report.redundant.map { "\($0.pattern.lowercased())\u{0}\($0.replacement)" })
        return personal.filter { entry in
            let key = "\(entry.pattern.trimmingCharacters(in: .whitespacesAndNewlines).lowercased())\u{0}\(entry.replacement.trimmingCharacters(in: .whitespacesAndNewlines))"
            return !redundant.contains(key)
        }
    }
}

struct LibraryUsage: Equatable, Sendable {
    let id: String
    let name: String
    let copyTerms: [DictionaryEntry]
    let unusedCount: Int
    let builtIn: Bool
    let fileName: String?

    init(
        id: String,
        name: String,
        copyTerms: [DictionaryEntry],
        unusedCount: Int,
        builtIn: Bool,
        fileName: String? = nil
    ) {
        self.id = id
        self.name = name
        self.copyTerms = copyTerms
        self.unusedCount = unusedCount
        self.builtIn = builtIn
        self.fileName = fileName
    }
}

enum SpokenFormFold {
    static func fold(_ value: String) -> String {
        value
            .folding(options: [.caseInsensitive, .diacriticInsensitive], locale: .init(identifier: "en_US_POSIX"))
            .replacingOccurrences(of: #"\s+"#, with: " ", options: .regularExpression)
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }
}

enum LibrarySwitchOffCopy {
    struct Row: Equatable, Sendable {
        let pattern: String?
        let replacement: String?
        let wholeWord: Bool
        let enabled: Bool
    }

    struct LibraryRow: Equatable, Sendable {
        let id: String
        let builtIn: Bool
        let enabled: Bool
    }

    struct KeptOnLibrary: Equatable, Sendable {
        let id: String
        let builtIn: Bool
        let name: String
        let overlappingTerms: Int
    }

    struct Result: Equatable, Sendable {
        let copies: [DictionaryEntry]
        let collided: Int
        let keptOn: [KeptOnLibrary]

        func keepsOn(_ id: String, builtIn: Bool) -> Bool {
            keptOn.contains { $0.builtIn == builtIn && $0.id.caseInsensitiveCompare(id) == .orderedSame }
        }
    }

    static func plan(
        rows: [Row],
        libraries: [DictionaryLibrary],
        libraryRows: [LibraryRow],
        switchingOff: [LibraryUsage],
        verdicts: [LibraryUsage]? = nil
    ) -> Result {
        let enabledIds = Set(libraryRows.filter(\.enabled).map(\.id))
        let offIds = Set(switchingOff.map { $0.id.lowercased() })
        let stayingIds = enabledIds.filter { !offIds.contains($0.lowercased()) }
        let enabled = LibraryPrecedence.enabled(libraries, enabledIDs: enabledIds)
        let staying = LibraryPrecedence.enabled(libraries, enabledIDs: Set(stayingIds))
        let going = enabled.filter { offIds.contains($0.id.lowercased()) }
        return decide(rows: rows, staying: staying, goingOff: going, requested: switchingOff)
    }

    static func plan(rows: [Row], composition: LibraryComposition, switchingOff: [LibraryUsage]) -> Result {
        let offIds = Set(switchingOff.map { $0.id.lowercased() })
        let staying = composition.enabledLibraries.filter { !offIds.contains($0.id.lowercased()) }
        let going = composition.enabledLibraries.filter { offIds.contains($0.id.lowercased()) }
        return decide(rows: rows, staying: staying, goingOff: going, requested: switchingOff)
    }

    static func describeKeptOn(_ keptOn: [KeptOnLibrary]) -> String {
        guard !keptOn.isEmpty else { return "" }
        let names = keptOn.map { "\"\($0.name)\"" }
        let list = names.count == 1 ? names[0] : names.dropLast().joined(separator: ", ") + " and " + names.last!
        if names.count == 1 {
            return "Kept on: \(list). Some of its terms overlap other terms dictation applies, so switching it off could change what dictation writes."
        }
        return "Kept on: \(list). Some of their terms overlap other terms dictation applies, so switching them off could change what dictation writes."
    }

    private static func decide(
        rows: [Row],
        staying: [DictionaryLibrary],
        goingOff: [DictionaryLibrary],
        requested: [LibraryUsage]
    ) -> Result {
        let dictionary = stored(rows)
        let dictionaryPatterns = Set(dictionary.map { $0.pattern.lowercased() })
        let stayingForms = Set(staying.flatMap(\.enabledEntries).map { SpokenFormFold.fold($0.pattern) })

        var copies: [DictionaryEntry] = []
        var collided = 0
        var kept: [KeptOnLibrary] = []

        for library in goingOff {
            let usage = requested.first { $0.id.caseInsensitiveCompare(library.id) == .orderedSame }
            let terms = usage?.copyTerms ?? library.enabledEntries
            let libraryForms = library.enabledEntries.map { SpokenFormFold.fold($0.pattern) }
            let overlaps = libraryForms.filter { form in
                stayingForms.contains { other in formsMeet(form, other, runningInto: true) }
            }
            if !overlaps.isEmpty {
                kept.append(
                    KeptOnLibrary(
                        id: library.id,
                        builtIn: library.builtIn,
                        name: library.name,
                        overlappingTerms: overlaps.count))
                continue
            }
            for term in terms where term.enabled {
                let pattern = term.pattern.trimmingCharacters(in: .whitespacesAndNewlines)
                guard !pattern.isEmpty else { continue }
                if dictionaryPatterns.contains(pattern.lowercased()) {
                    collided += 1
                } else {
                    copies.append(
                        DictionaryEntry(
                            pattern: pattern,
                            replacement: term.replacement.trimmingCharacters(in: .whitespacesAndNewlines),
                            wholeWord: term.wholeWord,
                            enabled: true))
                }
            }
        }

        return Result(copies: copies, collided: collided, keptOn: kept)
    }

    private static func stored(_ rows: [Row]) -> [DictionaryEntry] {
        rows.compactMap { row in
            let pattern = (row.pattern ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
            guard !pattern.isEmpty else { return nil }
            return DictionaryEntry(
                pattern: pattern,
                replacement: (row.replacement ?? "").trimmingCharacters(in: .whitespacesAndNewlines),
                wholeWord: row.wholeWord,
                enabled: row.enabled)
        }
    }

    private static func formsMeet(_ lhs: String, _ rhs: String, runningInto: Bool) -> Bool {
        if lhs.contains(rhs) || rhs.contains(lhs) {
            return true
        }
        guard runningInto else { return false }
        let left = Array(lhs)
        let right = Array(rhs)
        guard left.count > 1, right.count > 1 else { return false }
        for shared in 1..<min(left.count, right.count) {
            if Array(left.suffix(shared)) == Array(right.prefix(shared)) {
                return true
            }
            if Array(right.suffix(shared)) == Array(left.prefix(shared)) {
                return true
            }
        }
        return false
    }
}
