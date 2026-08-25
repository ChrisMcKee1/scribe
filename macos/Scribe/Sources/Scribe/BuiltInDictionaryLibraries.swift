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
        guard let directory = librariesDirectory() else {
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

    /// Checked in an order that keeps `Bundle.module` off the hot path for a packaged, signed
    /// app. `Bundle.module`'s SwiftPM-generated accessor is hardcoded to look for
    /// `ScribeMac_Scribe.bundle` at `Bundle.main.bundleURL` (the .app's own root for a packaged
    /// app) and calls `fatalError` if it isn't there; a loose "*.bundle" folder at the app root is
    /// also exactly what makes codesign refuse to seal the bundle ("unsealed contents present in
    /// the bundle root"). So `build-app.sh` instead copies just the CSVs into the standard,
    /// fully-signable `Contents/Resources/Libraries`, which this checks first. `Bundle.module` is
    /// only reached as a dev-only fallback (`swift build`/`swift run` outside a packaged .app),
    /// where its `.build` directory fallback path resolves correctly.
    private static func librariesDirectory() -> URL? {
        let packaged = Bundle.main.bundleURL.appendingPathComponent("Contents/Resources/Libraries")
        var isDirectory: ObjCBool = false
        if FileManager.default.fileExists(atPath: packaged.path, isDirectory: &isDirectory), isDirectory.boolValue {
            return packaged
        }
        return Bundle.module.url(forResource: "Libraries", withExtension: nil)
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
