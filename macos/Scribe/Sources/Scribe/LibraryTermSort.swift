import Foundation

enum LibraryTermSortOrder: Sendable {
    case savedOrder
    case spokenAscending
    case spokenDescending
    case writtenAscending
    case writtenDescending
}

struct LibraryTermSort: Sendable {
    private let locale: Locale

    init(locale: Locale) {
        self.locale = locale
    }

    static func forCurrentLocale() -> LibraryTermSort {
        LibraryTermSort(locale: .current)
    }

    static func `for`(_ locale: Locale) -> LibraryTermSort {
        LibraryTermSort(locale: locale)
    }

    func sort(_ rows: [DraftTermRow], by order: LibraryTermSortOrder) -> [DraftTermRow] {
        guard order != .savedOrder else {
            return Array(rows)
        }

        let bySpoken = order == .spokenAscending || order == .spokenDescending
        let descending = order == .spokenDescending || order == .writtenDescending

        return rows.enumerated().sorted { lhs, rhs in
            let comparison = compare(
                lhs.element.row.values,
                rhs.element.row.values,
                bySpoken: bySpoken,
                descending: descending)
            return comparison == 0 ? lhs.offset < rhs.offset : comparison < 0
        }.map(\.element)
    }

    private func compare(_ lhs: TermValues, _ rhs: TermValues, bySpoken: Bool, descending: Bool) -> Int {
        let first = bySpoken ? lhs.spoken : lhs.written
        let second = bySpoken ? rhs.spoken : rhs.written
        if first.isEmpty || second.isEmpty {
            if first.isEmpty && second.isEmpty {
                return 0
            }
            return first.isEmpty ? 1 : -1
        }

        let options: String.CompareOptions = [.caseInsensitive, .numeric]
        var result = first.compare(
            second,
            options: options,
            range: nil,
            locale: locale
        ).threeWay
        let otherFirst = bySpoken ? lhs.written : lhs.spoken
        let otherSecond = bySpoken ? rhs.written : rhs.spoken
        if result == 0 {
            result = otherFirst.compare(
                otherSecond,
                options: options,
                range: nil,
                locale: locale
            ).threeWay
        }
        if result == 0 {
            result = first.compare(second, options: [], range: nil, locale: locale).threeWay
        }
        if result == 0 {
            result = otherFirst.compare(
                otherSecond,
                options: [],
                range: nil,
                locale: locale
            ).threeWay
        }
        return descending ? -result : result
    }
}

extension ComparisonResult {
    fileprivate var threeWay: Int {
        switch self {
        case .orderedAscending:
            return -1
        case .orderedDescending:
            return 1
        case .orderedSame:
            return 0
        }
    }
}
