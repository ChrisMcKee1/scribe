import Foundation

/// Rewrites em dashes and en dashes out of AI cleanup output. A port of Windows'
/// `Scribe.Core.Cleanup.DashNormalizer`.
///
/// The writing style tells the model not to use them, but instructions are advisory and every model tested ignores
/// them some of the time, so this is the deterministic backstop that makes the house style hold. It runs only on the
/// model's answer, after `CleanupResponseGuard` has accepted it: never on raw speech (the recognizer does not emit
/// these characters) and never on dictionary replacements or snippet templates, which are the user's own text and
/// may legitimately contain a dash.
///
/// It reads Unicode scalars, as the Windows version reads UTF-16 code units, so a dash followed by a combining mark
/// is still a dash it replaces.
enum DashNormalizer {
    private static let emDash: Unicode.Scalar = "\u{2014}"
    private static let enDash: Unicode.Scalar = "\u{2013}"
    private static let space: Unicode.Scalar = " "
    private static let tab: Unicode.Scalar = "\t"
    private static let lineFeed: Unicode.Scalar = "\n"
    private static let carriageReturn: Unicode.Scalar = "\r"
    private static let sentencePunctuation: Set<Unicode.Scalar> = [",", ".", ";", ":", "!", "?"]
    // A comma before one of these would read as a dangling clause ("(aside, )"), so the dash is dropped instead.
    // Line breaks count: the break already does the separating.
    private static let clauseClosing: Set<Unicode.Scalar> = [")", "]", "}", "\"", "'", "\n", "\r"]

    /// Whether `value` holds an em dash or an en dash.
    static func containsDash(_ value: String) -> Bool {
        value.unicodeScalars.contains { isDash($0) }
    }

    /// `value` with each em or en dash replaced by the punctuation a careful writer would have used: a comma between
    /// clauses, "to" between numbers, and nothing at the start of a line or before a closing bracket. Text without a
    /// dash comes back unchanged.
    static func normalize(_ value: String) -> String {
        guard containsDash(value) else { return value }

        let source = Array(value.unicodeScalars)
        var output: [Unicode.Scalar] = []
        output.reserveCapacity(source.count + 8)

        var index = 0
        while index < source.count {
            let scalar = source[index]
            guard isDash(scalar) else {
                output.append(scalar)
                index += 1
                continue
            }

            // A run of dashes (a double dash typed as two em dashes) is one decision.
            while index + 1 < source.count, isDash(source[index + 1]) {
                index += 1
            }

            let beforeIndex = lastNonSpaceIndex(in: output)
            let before = beforeIndex.map { output[$0] }
            let afterIndex = nextNonSpaceIndex(in: source, from: index + 1)
            let after = afterIndex.map { source[$0] }

            // A dash between digits is a range: "pages 3 to 7", "about 1 to 2 GB".
            if let before, let after, let afterIndex, isDigit(before), isDigit(after) {
                trimTrailingSpaces(&output)
                output.append(contentsOf: " to ".unicodeScalars)
                index = afterIndex
                continue
            }

            // A dash that opens a line is a bullet or a dialogue dash: dropped rather than turned into a comma.
            if before == nil || before == lineFeed || before == carriageReturn {
                index = indexAfterSpaces(in: source, following: index)
                continue
            }

            // Already punctuated on the left ("wait, - I mean"): the dash adds nothing.
            if let before, sentencePunctuation.contains(before) {
                trimTrailingSpaces(&output)
                output.append(space)
                index = indexAfterSpaces(in: source, following: index)
                continue
            }

            if let after, !clauseClosing.contains(after) {
                trimTrailingSpaces(&output)
                output.append(contentsOf: ", ".unicodeScalars)
                index = indexAfterSpaces(in: source, following: index)
            } else {
                // Nothing follows, or what follows closes the clause: the dash was trailing, so it goes without
                // leaving a comma before ")" or a line break.
                trimTrailingSpaces(&output)
                index += 1
            }
        }

        var result = ""
        result.unicodeScalars.append(contentsOf: output)
        return result
    }

    private static func isDash(_ scalar: Unicode.Scalar) -> Bool {
        scalar == emDash || scalar == enDash
    }

    private static func isDigit(_ scalar: Unicode.Scalar) -> Bool {
        scalar.properties.generalCategory == .decimalNumber
    }

    private static func isHorizontalSpace(_ scalar: Unicode.Scalar) -> Bool {
        scalar == space || scalar == tab
    }

    /// The index after the dash at `index` and the spaces and tabs that follow it. A line break is structure the
    /// model chose, so it stays.
    private static func indexAfterSpaces(in source: [Unicode.Scalar], following index: Int) -> Int {
        var next = index + 1
        while next < source.count, isHorizontalSpace(source[next]) {
            next += 1
        }
        return next
    }

    private static func trimTrailingSpaces(_ output: inout [Unicode.Scalar]) {
        while let last = output.last, isHorizontalSpace(last) {
            output.removeLast()
        }
    }

    private static func lastNonSpaceIndex(in output: [Unicode.Scalar]) -> Int? {
        output.lastIndex { !isHorizontalSpace($0) }
    }

    private static func nextNonSpaceIndex(in source: [Unicode.Scalar], from start: Int) -> Int? {
        guard start < source.count else { return nil }
        return (start..<source.count).first { !isHorizontalSpace(source[$0]) }
    }
}
