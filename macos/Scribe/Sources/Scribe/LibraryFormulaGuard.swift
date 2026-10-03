import Foundation

enum LibraryFormulaGuard {
    static let version = 1

    static func encode(_ value: String) -> String {
        startsLikeFormula(value, start: 0) ? "'" + value : value
    }

    static func decode(_ value: String) -> String {
        value.count > 1 && value.first == "'" && startsLikeFormula(value, start: 1)
            ? String(value.dropFirst())
            : value
    }

    private static func startsLikeFormula(_ value: String, start: Int) -> Bool {
        let chars = Array(value)
        var index = start
        while index < chars.count && chars[index] == "'" {
            index += 1
        }
        while index < chars.count && chars[index] == " " {
            index += 1
        }
        guard index < chars.count else {
            return false
        }
        return isTrigger(chars[index])
    }

    static func isTrigger(_ char: Character) -> Bool {
        switch char {
        case "=", "+", "-", "@", "＝", "＋", "－", "＠", "\t", "\r", "\n":
            return true
        default:
            return false
        }
    }
}
