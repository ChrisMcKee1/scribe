import Foundation

enum LibraryPrecedence {
    static let builtInOrder = [
        "ai-terminology",
        "ai-model-names",
        "data-and-ai",
        "data-engineering",
        "data-science-machine-learning",
        "github",
        "microsoft-365",
        "microsoft-azure",
        "dotnet-development",
        "modern-developer-stack",
        "software-development",
    ]

    static let retiredBuiltInIDs: [String] = []

    private static let builtInIndex = Dictionary(
        uniqueKeysWithValues: builtInOrder.enumerated().map { ($1.lowercased(), $0) })

    static func compare(_ lhs: DictionaryLibrary, _ rhs: DictionaryLibrary) -> Int {
        compare(
            id: lhs.id,
            builtIn: lhs.builtIn,
            fileName: lhs.fileName,
            otherID: rhs.id,
            otherBuiltIn: rhs.builtIn,
            otherFileName: rhs.fileName)
    }

    static func order<S: Sequence>(_ libraries: S) -> [DictionaryLibrary] where S.Element == DictionaryLibrary {
        libraries.sorted { compare($0, $1) < 0 }
    }

    static func enabled<S: Sequence, T: Sequence>(_ libraries: S, enabledIDs: T) -> [DictionaryLibrary]
    where S.Element == DictionaryLibrary, T.Element == String {
        let enabled = Set(enabledIDs.map { $0.lowercased() })
        return order(libraries.filter { enabled.contains($0.id.lowercased()) })
    }

    static func compare(
        id: String?,
        builtIn: Bool,
        fileName: String?,
        otherID: String?,
        otherBuiltIn: Bool,
        otherFileName: String?
    ) -> Int {
        let lhsRank = rank(of: id, builtIn: builtIn)
        let rhsRank = rank(of: otherID, builtIn: otherBuiltIn)
        if lhsRank != rhsRank {
            return lhsRank.rawValue < rhsRank.rawValue ? -1 : 1
        }

        let byKey: Int
        switch lhsRank {
        case .listed:
            byKey = compareInts(
                builtInIndex[id?.lowercased() ?? ""] ?? .max,
                builtInIndex[otherID?.lowercased() ?? ""] ?? .max)
        case .unlisted:
            byKey = compareCaseInsensitive(id ?? "", otherID ?? "")
        case .custom:
            byKey = compareCaseInsensitive(
                fileName ?? defaultFileName(for: id),
                otherFileName ?? defaultFileName(for: otherID))
        }

        return byKey != 0 ? byKey : compareOrdinal(id ?? "", otherID ?? "")
    }

    private static func defaultFileName(for id: String?) -> String {
        (id ?? "") + ".csv"
    }

    private static func rank(of id: String?, builtIn: Bool) -> Rank {
        guard builtIn else {
            return .custom
        }
        return builtInIndex[id?.lowercased() ?? ""] == nil ? .unlisted : .listed
    }

    private static func compareInts(_ lhs: Int, _ rhs: Int) -> Int {
        lhs == rhs ? 0 : (lhs < rhs ? -1 : 1)
    }

    private static func compareCaseInsensitive(_ lhs: String, _ rhs: String) -> Int {
        let result = lhs.compare(rhs, options: [.caseInsensitive, .literal])
        if result == .orderedAscending { return -1 }
        if result == .orderedDescending { return 1 }
        return 0
    }

    private static func compareOrdinal(_ lhs: String, _ rhs: String) -> Int {
        if lhs == rhs { return 0 }
        return lhs < rhs ? -1 : 1
    }

    private enum Rank: Int {
        case listed
        case unlisted
        case custom
    }
}
