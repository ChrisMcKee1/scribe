import Foundation

enum TermReviewChoice: Sendable {
    case keepMine
    case useUpdated
}

struct TermReview: Equatable, Sendable {
    let yours: TermValues
    let updatedBuiltIn: TermValues
    let differing: TermFields
}

struct TermFields: OptionSet, Equatable, Sendable {
    let rawValue: Int

    static let spoken = TermFields(rawValue: 1 << 0)
    static let written = TermFields(rawValue: 1 << 1)
    static let wholeWord = TermFields(rawValue: 1 << 2)
    static let enabled = TermFields(rawValue: 1 << 3)

    static let all: [TermFields] = [.spoken, .written, .wholeWord, .enabled]

    static func differences(_ left: TermValues, _ right: TermValues) -> TermFields {
        var result: TermFields = []
        for field in all where !same(field, left, right) {
            result.insert(field)
        }
        return result
    }

    static func same(_ field: TermFields, _ left: TermValues, _ right: TermValues) -> Bool {
        switch field {
        case .spoken: return left.spoken == right.spoken
        case .written: return left.written == right.written
        case .wholeWord: return left.wholeWord == right.wholeWord
        case .enabled: return left.enabled == right.enabled
        default: return false
        }
    }

    func taking(_ changed: TermValues, otherwise kept: TermValues) -> TermValues {
        TermValues(
            contains(.spoken) ? changed.spoken : kept.spoken,
            contains(.written) ? changed.written : kept.written,
            contains(.wholeWord) ? changed.wholeWord : kept.wholeWord,
            contains(.enabled) ? changed.enabled : kept.enabled)
    }
}
