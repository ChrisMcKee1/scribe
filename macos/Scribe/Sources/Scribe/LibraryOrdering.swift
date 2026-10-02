import Foundation

struct LibraryOrdering: Sendable {
    func compare(_ lhs: DictionaryLibrary, _ rhs: DictionaryLibrary) -> Int {
        let byName = lhs.name.localizedStandardCompare(rhs.name)
        if byName == .orderedAscending { return -1 }
        if byName == .orderedDescending { return 1 }

        let byOrdinalName = lhs.name.compare(rhs.name, options: [.literal])
        if byOrdinalName == .orderedAscending { return -1 }
        if byOrdinalName == .orderedDescending { return 1 }

        let byOrdinalID = lhs.id.compare(rhs.id, options: [.literal])
        if byOrdinalID == .orderedAscending { return -1 }
        if byOrdinalID == .orderedDescending { return 1 }

        return 0
    }

    func sort<S: Sequence>(_ libraries: S) -> [DictionaryLibrary] where S.Element == DictionaryLibrary {
        libraries.sorted { compare($0, $1) < 0 }
    }
}
