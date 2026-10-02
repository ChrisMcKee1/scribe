import Foundation

struct LibrarySearchSource: Sendable {
    let libraryID: String
    let rows: [DraftTermRow]
}

struct LibrarySearchMatches: Sendable, Equatable {
    let libraryID: String
    let rowIDs: [Int64]

    var count: Int { rowIDs.count }
}

struct LibrarySearchResult: Sendable, Equatable {
    let query: String
    let libraries: [LibrarySearchMatches]

    var isActive: Bool { !query.isEmpty }
    var totalMatches: Int { libraries.reduce(0) { $0 + $1.count } }

    func count(in libraryID: String) -> Int {
        libraries.first { $0.libraryID.caseInsensitiveCompare(libraryID) == .orderedSame }?.count ?? 0
    }

    func matches(in libraryID: String) -> [Int64] {
        libraries.first { $0.libraryID.caseInsensitiveCompare(libraryID) == .orderedSame }?.rowIDs ?? []
    }

    func foundElsewhere(selectedLibraryID: String) -> [LibrarySearchMatches] {
        libraries.filter { $0.count > 0 && $0.libraryID.caseInsensitiveCompare(selectedLibraryID) != .orderedSame }
    }
}

struct LibrarySearch: Sendable {
    private let locale: Locale

    init(locale: Locale) {
        self.locale = locale
    }

    static func forCurrentLocale() -> LibrarySearch {
        LibrarySearch(locale: .current)
    }

    static func `for`(_ locale: Locale) -> LibrarySearch {
        LibrarySearch(locale: locale)
    }

    func normalize(_ query: String?) -> String {
        let trimmed = (query ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else {
            return ""
        }
        let emptyLike = trimmed.compare(
            "",
            options: [.caseInsensitive, .diacriticInsensitive],
            range: nil,
            locale: locale) == .orderedSame
        return emptyLike ? "" : trimmed
    }

    func matches(_ values: TermValues, query: String?) -> Bool {
        let normalized = normalize(query)
        guard !normalized.isEmpty else {
            return false
        }
        let options: String.CompareOptions = [.caseInsensitive, .diacriticInsensitive]
        let spokenMatches = values.spoken.range(of: normalized, options: options, range: nil, locale: locale) != nil
        let writtenMatches = values.written.range(of: normalized, options: options, range: nil, locale: locale) != nil
        return spokenMatches || writtenMatches
    }

    func search(_ workspace: LibraryWorkspace, query: String?) -> LibrarySearchResult {
        search(
            workspace.draft.libraries
                .filter { !$0.pendingDelete }
                .map { LibrarySearchSource(libraryID: $0.id, rows: $0.rows) },
            query: query)
    }

    func search(_ libraries: [LibrarySearchSource], query: String?) -> LibrarySearchResult {
        let normalized = normalize(query)
        var results: [LibrarySearchMatches] = []
        for library in libraries {
                let rowIDs: [Int64]
            if normalized.isEmpty {
                rowIDs = []
            } else {
                rowIDs = library.rows.compactMap { row in
                    matches(row.row.values, query: normalized) ? row.rowID : nil
                }
            }
            results.append(LibrarySearchMatches(libraryID: library.libraryID, rowIDs: rowIDs))
        }
        return LibrarySearchResult(query: normalized, libraries: results)
    }
}
