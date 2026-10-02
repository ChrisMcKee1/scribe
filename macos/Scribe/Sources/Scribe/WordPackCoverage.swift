import Foundation

enum WordPackCoverageKind: Sendable {
    case redundant
    case override
}

struct WordPackCoverage: Sendable {
    let entry: DictionaryEntry
    let written: String
    let sourceName: String
    let kind: WordPackCoverageKind

    static func analyze(
        dictionary: [DictionaryEntry], composition: LibraryComposition
    ) -> [WordPackCoverage] {
        dictionary.filter(\.enabled).compactMap { entry in
            let key = LibraryTermKey.from(entry.pattern)
            guard let rule = composition.rules.first(where: { $0.key == key }),
                let source = composition.enabledLibraries.first(where: { $0.id == rule.libraryId })
            else { return nil }
            return WordPackCoverage(
                entry: entry, written: rule.entry.replacement, sourceName: source.name,
                kind: entry.replacement == rule.entry.replacement ? .redundant : .override)
        }
    }
}
