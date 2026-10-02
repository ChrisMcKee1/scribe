import Foundation

enum RecentlyDeletedStore {
    static let retentionDays = 30
    private static let stampLength = 16

    static func parseEntryName(_ name: String) -> (deletedAt: Date, sequence: Int, originalFileName: String)? {
        guard name.count > stampLength + 1 else { return nil }
        let stampEnd = name.index(name.startIndex, offsetBy: stampLength)
        guard let deletedAt = stampFormatter.date(from: String(name[..<stampEnd])) else { return nil }
        var rest = String(name[stampEnd...])
        var sequence = 1
        if rest.first == "-" {
            var digits = ""
            rest.removeFirst()
            while let first = rest.first, first.isNumber {
                digits.append(first)
                rest.removeFirst()
            }
            guard !digits.isEmpty, digits.first != "0", let parsed = Int(digits), parsed >= 2 else {
                return nil
            }
            sequence = parsed
        }
        guard rest.first == "." else { return nil }
        rest.removeFirst()
        guard rest.lowercased().hasSuffix(".csv"), !rest.dropLast(4).isEmpty, !rest.contains("/") else {
            return nil
        }
        return (deletedAt, sequence, rest)
    }

    static func originalID(originalFileName: String) -> String {
        let stem = URL(fileURLWithPath: originalFileName).deletingPathExtension().lastPathComponent
        return BuiltInDictionaryLibraries.all.contains { $0.id.caseInsensitiveCompare(stem) == .orderedSame }
            ? "custom-\(stem)"
            : stem
    }

    static func nextEntryName(originalFileName: String, stamp: Date, taken: Set<String>) -> String {
        let prefix = stampFormatter.string(from: stamp)
        var candidate = "\(prefix).\(originalFileName)"
        var sequence = 2
        while taken.contains(candidate) {
            candidate = "\(prefix)-\(sequence).\(originalFileName)"
            sequence += 1
        }
        return candidate
    }

    static func list(deletedDirectory: URL, metadata: [String: RecentlyDeletedMetadata]) -> [RecentlyDeletedLibrary] {
        let fileManager = FileManager.default
        let urls = (try? fileManager.contentsOfDirectory(at: deletedDirectory, includingPropertiesForKeys: nil)) ?? []
        var entries: [(RecentlyDeletedLibrary, Int)] = []
        for url in urls where !url.hasDirectoryPath {
            let entryName = url.lastPathComponent
            let parsed = parseEntryName(entryName)
            let fallbackID =
                parsed.map { originalID(originalFileName: $0.originalFileName) }
                ?? url.deletingPathExtension().lastPathComponent
            guard let data = try? Data(contentsOf: url) else {
                entries.append(
                    (
                        RecentlyDeletedLibrary(
                            entryName: entryName,
                            originalID: metadata[entryName]?.originalID ?? fallbackID,
                            name: metadata[entryName]?.name ?? BuiltInDictionaryLibraries.humanize(fallbackID),
                            termCount: metadata[entryName]?.termCount ?? 0,
                            deletedAt: parsed?.deletedAt ?? .distantPast,
                            state: parsed == nil ? .unreadable : .awaitingRelease,
                            contentHash: metadata[entryName]?.contentHash.map(LibraryContentHash.init(value:))),
                        parsed?.sequence ?? 1
                    ))
                continue
            }
            let document = DictionaryLibraryCsv.parseManaged(data)
            let state: LibraryFileState =
                parsed == nil ? .unreadable : (document.errors.isEmpty ? .available : .partlyReadable)
            let hash = LibraryContentHash(data: data)
            entries.append(
                (
                    RecentlyDeletedLibrary(
                        entryName: entryName,
                        originalID: metadata[entryName]?.originalID ?? fallbackID,
                        name: metadata[entryName]?.name ?? document.name
                            ?? BuiltInDictionaryLibraries.humanize(fallbackID),
                        termCount: metadata[entryName]?.termCount ?? document.terms.count,
                        deletedAt: parsed?.deletedAt ?? metadata[entryName]?.deletedAt ?? .distantPast,
                        state: state,
                        contentHash: hash),
                    parsed?.sequence ?? 1
                ))
        }
        return entries.sorted {
            if $0.0.deletedAt != $1.0.deletedAt {
                return $0.0.deletedAt > $1.0.deletedAt
            }
            if $0.1 != $1.1 {
                return $0.1 > $1.1
            }
            return $0.0.entryName < $1.0.entryName
        }.map(\.0)
    }

    static func expiredEntryNames(_ entries: [RecentlyDeletedLibrary], now: Date = Date()) -> [String] {
        entries.compactMap { entry in
            guard parseEntryName(entry.entryName) != nil,
                entry.deletedAt <= now,
                Calendar(identifier: .gregorian).dateComponents([.day], from: entry.deletedAt, to: now).day ?? 0
                    >= retentionDays
            else { return nil }
            return entry.entryName
        }
    }

    static var stampFormatter: DateFormatter {
        let formatter = DateFormatter()
        formatter.calendar = Calendar(identifier: .gregorian)
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = TimeZone(secondsFromGMT: 0)
        formatter.dateFormat = "yyyyMMdd'T'HHmmss'Z'"
        return formatter
    }
}

struct RecentlyDeletedMetadata: Codable, Equatable, Sendable {
    let entryName: String
    let originalID: String
    let name: String
    let termCount: Int
    let deletedAt: Date
    let contentHash: String?
}
