import Foundation

struct TermValues: Codable, Equatable, Sendable {
    var spoken: String
    var written: String
    var wholeWord: Bool
    var enabled: Bool

    init(_ spoken: String, _ written: String, _ wholeWord: Bool = true, _ enabled: Bool = true) {
        self.spoken = spoken
        self.written = written
        self.wholeWord = wholeWord
        self.enabled = enabled
    }

    init(entry: DictionaryEntry) {
        spoken = entry.pattern
        written = entry.replacement
        wholeWord = entry.wholeWord
        enabled = entry.enabled
    }

    var dictionaryEntry: DictionaryEntry {
        DictionaryEntry(pattern: spoken, replacement: written, wholeWord: wholeWord, enabled: enabled)
    }
}

struct LibraryCsvContent: Equatable, Sendable {
    let name: String
    let category: String
    let description: String?
    let basedOn: String?
    let rows: [TermValues]
}

struct LibraryCsvDocument: Equatable, Sendable {
    let name: String?
    let category: String?
    let description: String?
    let basedOn: String?
    let terms: [TermValues]
    let errors: [LibraryCsvRowError]
    let encoding: LibraryTextEncoding
    let formulaGuardVersion: Int?
    let issues: LibraryCsvIssues
}

struct LibraryCsvRowError: Equatable, Sendable {
    let line: Int
    let kind: LibraryCsvRowErrorKind
    let field: String?

    var legacyMessage: String {
        switch kind {
        case .missingFields:
            return "Line \(line): expected at least a pattern and a replacement."
        case .emptySpoken:
            return "Line \(line): the pattern (spoken form) is empty."
        case .invalidWholeWord:
            return "Line \(line): whole_word should be true or false, not \"\(field ?? "")\"."
        case .invalidEnabled:
            return "Line \(line): enabled should be true or false, not \"\(field ?? "")\"."
        case .unclosedQuote:
            return "Line \(line): quoted field is missing its closing quote."
        case .fieldTooLong:
            return "Line \(line): a field is longer than \(LibraryLimits.maxFieldLength) characters."
        }
    }
}

enum LibraryCsvRowErrorKind: String, Codable, Sendable {
    case missingFields = "MissingFields"
    case emptySpoken = "EmptySpoken"
    case invalidWholeWord = "InvalidWholeWord"
    case invalidEnabled = "InvalidEnabled"
    case unclosedQuote = "UnclosedQuote"
    case fieldTooLong = "FieldTooLong"
}

struct LibraryCsvIssues: OptionSet, Equatable, Sendable {
    let rawValue: Int

    static let headerPaddingRemoved = LibraryCsvIssues(rawValue: 1 << 0)
    static let rowLimitExceeded = LibraryCsvIssues(rawValue: 1 << 1)
    static let sizeLimitExceeded = LibraryCsvIssues(rawValue: 1 << 2)

    static let none: LibraryCsvIssues = []

    var names: [String] {
        var result: [String] = []
        if contains(.headerPaddingRemoved) { result.append("HeaderPaddingRemoved") }
        if contains(.rowLimitExceeded) { result.append("RowLimitExceeded") }
        if contains(.sizeLimitExceeded) { result.append("SizeLimitExceeded") }
        return result
    }
}

struct LibraryTextEncoding: Equatable, Sendable {
    let codePage: Int
    let byteOrderMark: Bool
    let ansiFallback: Bool
    let invalidBytesReplaced: Bool
}
