import Foundation

enum LibraryMetadataProblem {
    case none
    case doubleQuote
}

enum LibraryMetadata {
    static func commit(_ value: String?) -> String {
        guard let value, !value.isEmpty else {
            return ""
        }

        let cleaned = value.unicodeScalars.map {
            CharacterSet.controlCharacters.contains($0) ? " " : Character($0)
        }
        return String(cleaned).trimmingCharacters(in: .whitespacesAndNewlines)
    }

    static func checkTyped(_ value: String?) -> LibraryMetadataProblem {
        (value ?? "").contains("\"") ? .doubleQuote : .none
    }

    static func readsBackInOlderVersions(_ values: String?...) -> Bool {
        let quoteCount = values.compactMap { $0 }.reduce(0) { partial, value in
            partial + value.filter { $0 == "\"" }.count
        }
        return quoteCount.isMultiple(of: 2)
    }
}
