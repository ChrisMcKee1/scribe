import Foundation

enum TokenEstimate {
    static let proseCharsPerToken = 3.6
    static let vocabularyCharsPerToken = 2.6
    static let shortTextAllowance = 12

    static func prose(_ text: String) -> Int {
        estimate(text, charsPerToken: proseCharsPerToken)
    }

    static func transcript(_ text: String) -> Int {
        estimate(text, charsPerToken: proseCharsPerToken) + shortTextAllowance
    }

    static func vocabulary(_ text: String) -> Int {
        estimate(text, charsPerToken: vocabularyCharsPerToken)
    }

    private static func estimate(_ text: String, charsPerToken: Double) -> Int {
        var ascii = 0
        var other = 0
        for scalar in text.unicodeScalars {
            if scalar.value < 0x80 {
                ascii += 1
            } else {
                other += 1
            }
        }
        return Int(ceil(Double(ascii) / charsPerToken)) + other
    }
}
