import Foundation

/// The identity of a word pack term: the spoken form trimmed, with inner white space kept, compared the way
/// Windows' `StringComparison.OrdinalIgnoreCase` compares it. This keeps 0.4.3's composition behavior, including
/// the older rows whose inner spacing never matched dictated text and therefore must not suppress a row that does.
struct LibraryTermKey: Equatable, Hashable, Sendable, CustomStringConvertible {
    private static let asciiSpace = UnicodeScalar(0x20)!
    private static let preservedOrdinalFoldScalars: Set<UInt32> = [
        0x00DF,  // ß
        0x0131,  // ı
        0x017F,  // ſ
        0x1E9E,  // ẞ
        0x212A,  // K
        0x212B,  // Å
    ]

    let value: String

    static let empty = LibraryTermKey(value: "")

    var isEmpty: Bool { value.isEmpty }

    var description: String { "LibraryTermKey(\(value.count) characters)" }

    static func from(_ spoken: String?) -> LibraryTermKey {
        LibraryTermKey(value: trimWhitespace(spoken ?? ""))
    }

    static func normalize(_ spoken: String?) -> String {
        guard let spoken, !spoken.isEmpty else {
            return ""
        }

        if isInCommitForm(spoken) {
            return spoken
        }

        var scalars = String.UnicodeScalarView()
        var pendingSpace = false
        for scalar in spoken.unicodeScalars {
            if scalar.properties.isWhitespace {
                pendingSpace = !scalars.isEmpty
                continue
            }

            if pendingSpace {
                scalars.append(asciiSpace)
                pendingSpace = false
            }

            scalars.append(scalar)
        }

        return String(scalars)
    }

    static func isInCommitForm(_ spoken: String?) -> Bool {
        guard let spoken, !spoken.isEmpty else {
            return true
        }

        let scalars = Array(spoken.unicodeScalars)
        guard let first = scalars.first, let last = scalars.last else {
            return true
        }
        guard !first.properties.isWhitespace, !last.properties.isWhitespace else {
            return false
        }

        for index in 1..<scalars.count {
            let scalar = scalars[index]
            if scalar.properties.isWhitespace && (scalar != asciiSpace || scalars[index - 1] == asciiSpace) {
                return false
            }
        }

        return true
    }

    static func areSame(_ lhs: String?, _ rhs: String?) -> Bool {
        from(lhs) == from(rhs)
    }

    func hash(into hasher: inout Hasher) {
        hasher.combine(value.utf16.count)
        for scalar in value.unicodeScalars {
            hasher.combine(Self.ordinalIgnoreCaseScalarKey(scalar))
        }
    }

    static func == (lhs: LibraryTermKey, rhs: LibraryTermKey) -> Bool {
        guard lhs.value.utf16.count == rhs.value.utf16.count else {
            return false
        }

        var left = lhs.value.unicodeScalars.makeIterator()
        var right = rhs.value.unicodeScalars.makeIterator()
        while true {
            let leftScalar = left.next()
            let rightScalar = right.next()
            switch (leftScalar, rightScalar) {
            case (nil, nil):
                return true
            case let (.some(l), .some(r)):
                if ordinalIgnoreCaseScalarKey(l) != ordinalIgnoreCaseScalarKey(r) {
                    return false
                }
            default:
                return false
            }
        }
    }

    private static func trimWhitespace(_ text: String) -> String {
        let scalars = Array(text.unicodeScalars)
        var start = scalars.startIndex
        var end = scalars.endIndex

        while start < end, scalars[start].properties.isWhitespace {
            start += 1
        }

        while start < end, scalars[end - 1].properties.isWhitespace {
            end -= 1
        }

        return String(String.UnicodeScalarView(scalars[start..<end]))
    }

    private static func ordinalIgnoreCaseScalarKey(_ scalar: UnicodeScalar) -> String {
        if preservedOrdinalFoldScalars.contains(scalar.value) {
            return String(scalar)
        }

        let upper = String(scalar).uppercased()
        return upper.unicodeScalars.count == 1 ? upper : String(scalar)
    }
}
