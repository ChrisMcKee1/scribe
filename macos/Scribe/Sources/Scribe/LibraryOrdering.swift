import Foundation

struct LibraryOrdering: Sendable {
    func compare(_ lhs: DictionaryLibrary, _ rhs: DictionaryLibrary) -> Int {
        let byName = lhs.name.localizedStandardCompare(rhs.name)
        if byName == .orderedAscending { return -1 }
        if byName == .orderedDescending { return 1 }

        if lhs.name != rhs.name {
            return lhs.name < rhs.name ? -1 : 1
        }

        if lhs.id == rhs.id {
            return 0
        }

        return lhs.id < rhs.id ? -1 : 1
    }

    func sort<S: Sequence>(_ libraries: S) -> [DictionaryLibrary] where S.Element == DictionaryLibrary {
        libraries.sorted { compare($0, $1) < 0 }
    }
}
