import Foundation

/// Persisted user-facing settings for dictionary libraries: which library ids are switched on.
/// UserDefaults-backed, the same stopgap pattern used by `CleanupSettingsStore` and the overlay
/// anchor, pending a general structured settings store on macOS.
enum DictionaryLibrarySettingsStore {
    private static let enabledIdsKey = "ScribeEnabledDictionaryLibraryIds"

    /// The ids of every library the user has switched on. Order is not meaningful; membership is.
    static var enabledLibraryIds: Set<String> {
        get { Set(UserDefaults.standard.stringArray(forKey: enabledIdsKey) ?? []) }
        set { UserDefaults.standard.set(Array(newValue), forKey: enabledIdsKey) }
    }

    static func setEnabled(_ enabled: Bool, id: String) {
        var ids = enabledLibraryIds
        if enabled {
            ids.insert(id)
        } else {
            ids.remove(id)
        }
        enabledLibraryIds = ids
    }
}

enum DictionaryLibraryServiceError: Error, LocalizedError {
    case invalidCsv(String)
    case noUsableEntries
    case builtInCannotBeRemoved
    case invalidLibraryId

    var errorDescription: String? {
        switch self {
        case .invalidCsv(let detail):
            return "That library contains invalid CSV rows:\n\(detail)"
        case .noUsableEntries:
            return "That file has no usable dictionary rows. Each row needs at least a spoken form and a replacement."
        case .builtInCannotBeRemoved:
            return "Built-in libraries can't be removed. Turn it off instead."
        case .invalidLibraryId:
            return "That library id is not valid."
        }
    }
}

/// Manages the dictionary libraries available to the app: the built-in set that ships embedded
/// plus any custom libraries the user has imported (stored as CSV files under `librariesDirectory`,
/// `~/Library/Application Support/Scribe/Libraries` by default). Also composes the entries of the
/// currently enabled libraries, which `TextPostProcessor` layers on top of the base dictionary.
/// Direct port of Windows' `Scribe.Core.PostProcessing.DictionaryLibraryService`.
final class DictionaryLibraryService {
    let librariesDirectory: URL
    private let fileManager: FileManager

    init(fileManager: FileManager = .default, librariesDirectory overrideDirectory: URL? = nil) {
        self.fileManager = fileManager
        if let overrideDirectory {
            self.librariesDirectory = overrideDirectory
        } else {
            let applicationSupportURL = fileManager.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            self.librariesDirectory = applicationSupportURL
                .appendingPathComponent("Scribe", isDirectory: true)
                .appendingPathComponent("Libraries", isDirectory: true)
        }
    }

    /// All libraries, built-in first then custom. Malformed custom files are skipped.
    func libraries() -> [DictionaryLibrary] {
        BuiltInDictionaryLibraries.all + loadCustom()
    }

    /// The de-duplicated entries of every library the user has switched on (per
    /// `DictionaryLibrarySettingsStore`), for layering on top of the base dictionary. Empty when
    /// nothing is enabled.
    func enabledLibraryEntries() -> [DictionaryEntry] {
        let enabledIds = DictionaryLibrarySettingsStore.enabledLibraryIds
        guard !enabledIds.isEmpty else { return [] }

        let matching = libraries().filter { enabledIds.contains($0.id) }
        return DictionaryLibraryComposer.composeLibraries(matching)
    }

    /// Imports a library from CSV text, writing it into the libraries folder as a new custom
    /// library and returning it. The display name comes from the file's `name` header, else
    /// `suggestedName` (typically the file name). Throws if the CSV has no usable entries.
    @discardableResult
    func `import`(csv: String, suggestedName: String?) throws -> DictionaryLibrary {
        let file = DictionaryLibraryCsv.parse(csv)
        if !file.errors.isEmpty {
            throw DictionaryLibraryServiceError.invalidCsv(file.errors.prefix(5).joined(separator: "\n"))
        }
        guard !file.entries.isEmpty else {
            throw DictionaryLibraryServiceError.noUsableEntries
        }

        let trimmedSuggestion = suggestedName?.trimmingCharacters(in: .whitespacesAndNewlines)
        let name = file.name ?? (trimmedSuggestion?.isEmpty == false ? trimmedSuggestion : nil) ?? "Imported library"
        let category = file.category ?? "Custom"

        try fileManager.createDirectory(at: librariesDirectory, withIntermediateDirectories: true)
        let id = uniqueId(baseSlug: slugify(name))
        let library = DictionaryLibrary(
            id: id, name: name, category: category, description: file.description, builtIn: false, entries: file.entries)

        // Re-export through the library writer so the stored file is normalized and always
        // carries a header, regardless of what the source file looked like.
        let text = DictionaryLibraryCsv.export(library)
        try text.write(
            to: librariesDirectory.appendingPathComponent("\(id).csv"), atomically: true, encoding: .utf8)
        return library
    }

    /// Removes a custom library by id (deletes its file and its enabled state). Built-in libraries
    /// cannot be removed; turn them off in Settings instead, and attempting to throws.
    func remove(id: String) throws {
        guard !id.isEmpty else { return }

        if BuiltInDictionaryLibraries.all.contains(where: { $0.id.caseInsensitiveCompare(id) == .orderedSame }) {
            throw DictionaryLibraryServiceError.builtInCannotBeRemoved
        }

        let fileName = "\(id).csv"
        guard fileName.rangeOfCharacter(from: .init(charactersIn: "/\\:")) == nil else {
            throw DictionaryLibraryServiceError.invalidLibraryId
        }

        let path = librariesDirectory.appendingPathComponent(fileName)
        if fileManager.fileExists(atPath: path.path) {
            try fileManager.removeItem(at: path)
        }
        DictionaryLibrarySettingsStore.setEnabled(false, id: id)
    }

    private func loadCustom() -> [DictionaryLibrary] {
        guard
            let fileURLs = try? fileManager.contentsOfDirectory(at: librariesDirectory, includingPropertiesForKeys: nil)
        else {
            return []
        }

        var libraries: [DictionaryLibrary] = []
        for fileURL in fileURLs.sorted(by: { $0.lastPathComponent.localizedCaseInsensitiveCompare($1.lastPathComponent) == .orderedAscending })
        where fileURL.pathExtension.lowercased() == "csv" {
            let id = fileURL.deletingPathExtension().lastPathComponent
            guard !id.isEmpty, let text = try? String(contentsOf: fileURL, encoding: .utf8) else { continue }

            let file = DictionaryLibraryCsv.parse(text)
            guard !file.entries.isEmpty else { continue }

            libraries.append(DictionaryLibrary(
                id: id,
                name: file.name ?? BuiltInDictionaryLibraries.humanize(id),
                category: file.category ?? "Custom",
                description: file.description,
                builtIn: false,
                entries: file.entries))
        }
        return libraries
    }

    // Ensures the new custom library's id collides with neither a built-in id nor an existing file.
    private func uniqueId(baseSlug: String) -> String {
        let builtinIds = Set(BuiltInDictionaryLibraries.all.map { $0.id.lowercased() })

        var candidate = baseSlug
        var n = 2
        while builtinIds.contains(candidate.lowercased())
            || fileManager.fileExists(atPath: librariesDirectory.appendingPathComponent("\(candidate).csv").path) {
            candidate = "\(baseSlug)-\(n)"
            n += 1
        }
        return candidate
    }

    // Lowercase, alphanumerics kept, every other run collapsed to a single hyphen; a safe file
    // name and stable id derived from the library's display name.
    private func slugify(_ value: String) -> String {
        var result = ""
        var pendingDash = false
        for scalar in value.trimmingCharacters(in: .whitespacesAndNewlines).lowercased().unicodeScalars {
            if CharacterSet.alphanumerics.contains(scalar) {
                if pendingDash, !result.isEmpty {
                    result.append("-")
                }
                result.unicodeScalars.append(scalar)
                pendingDash = false
            } else {
                pendingDash = true
            }
        }
        return result.isEmpty ? "library" : result
    }
}
