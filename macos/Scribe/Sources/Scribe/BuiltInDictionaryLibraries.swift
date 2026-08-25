import Foundation

/// The dictionary libraries that ship inside the app, loaded once from CSV files copied into the
/// app bundle as SwiftPM resources under `Resources/Libraries` (see `Package.swift`). Each file's
/// comment header supplies its display name, category, and description; the stable library id is
/// the file name without its extension (e.g. `microsoft-azure`), which is what the enabled-set in
/// `DictionaryLibrarySettingsStore` references. Direct port of Windows'
/// `Scribe.Core.PostProcessing.BuiltInDictionaryLibraries`, sharing the same CSV files.
enum BuiltInDictionaryLibraries {
    private static let cached: [DictionaryLibrary] = load()

    /// All built-in libraries, ordered by category then name.
    static var all: [DictionaryLibrary] { cached }

    private static func load() -> [DictionaryLibrary] {
        guard let directory = Bundle.module.url(forResource: "Libraries", withExtension: nil) else {
            return []
        }

        guard
            let fileURLs = try? FileManager.default.contentsOfDirectory(
                at: directory, includingPropertiesForKeys: nil)
        else {
            return []
        }

        var libraries: [DictionaryLibrary] = []
        for fileURL in fileURLs where fileURL.pathExtension.lowercased() == "csv" {
            let id = fileURL.deletingPathExtension().lastPathComponent
            guard !id.isEmpty, let text = try? String(contentsOf: fileURL, encoding: .utf8) else {
                continue
            }

            let file = DictionaryLibraryCsv.parse(text)
            guard !file.entries.isEmpty else { continue }

            libraries.append(DictionaryLibrary(
                id: id,
                name: file.name ?? humanize(id),
                category: file.category ?? "General",
                description: file.description,
                builtIn: true,
                entries: file.entries))
        }

        return libraries.sorted {
            $0.category.localizedCaseInsensitiveCompare($1.category) == .orderedSame
                ? $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending
                : $0.category.localizedCaseInsensitiveCompare($1.category) == .orderedAscending
        }
    }

    /// "microsoft-azure" -> "Microsoft Azure": a readable fallback when a file omits its name
    /// header, and reused by the custom-library loader for imported files without a name.
    static func humanize(_ id: String) -> String {
        let words = id.split(whereSeparator: { $0 == "-" || $0 == "_" || $0 == " " })
            .map { word -> String in
                guard let first = word.first else { return String(word) }
                return String(first).uppercased() + word.dropFirst()
            }
        let joined = words.joined(separator: " ")
        return joined.isEmpty ? id : joined
    }
}
