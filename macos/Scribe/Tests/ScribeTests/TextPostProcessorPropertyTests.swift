import XCTest

@testable import Scribe

/// The rules with AI cleanup on against cleanup off, on thousands of small random rule sets and dictations, with the
/// rules frozen. `testCleanupOnGivesTheCleanupOffTextToAModelThatChangesNothing`: a model that returns what it was sent
/// must get exactly the cleanup-off text from the rules, compared as strings with nothing normalized on either side;
/// one that only capitalizes the first letter, adds a final period or puts a prefix before everything must get the
/// cleanup-off text with exactly that change; and no snippet template or template-like replacement may reach the text
/// sent. The changed replies keep an answer that only held for an unchanged one from passing.
/// `testEveryReplacementMadeAfterCleanupAnswersToItsOwnWordsInTheReply` holds for any reply at all: every replacement
/// made after cleanup answers to one held-back replacement, at the occurrence of its words that stands where its own
/// stood in the text sent, found by an account of the rule written apart from `TextPostProcessor`; none is made twice;
/// and none is made when the reply holds its words a different number of times than the text sent. The response guard,
/// which rewrites dashes and strips wrappers from a real reply, is not in these tests; `DictationPipelineTests` covers
/// it. Deterministic: fixed seeds and a generator of its own, so every run meets the same cases.
final class TextPostProcessorPropertyTests: XCTestCase {
    private static let cases = 4_000
    private static let repliesPerCase = 3

    func testCleanupOnGivesTheCleanupOffTextToAModelThatChangesNothing() {
        var generator = RuleSetGenerator(seed: 20_260_924)
        var failures: [String] = []
        var spacesKeptBeforePunctuation = 0
        for index in 0..<Self.cases {
            let rules = generator.ruleSet()
            let transcript = generator.dictation()
            let processor = TextPostProcessor()
            processor.reload(dictionaryEntries: rules.entries, snippets: rules.snippets)
            let off = processor.process(transcript)
            let pass = processor.correctVocabulary(transcript)
            let sent = pass.text
            let unchanged = processor.finishAfterCleanup(sent, after: pass).text
            let capitalized = processor.finishAfterCleanup(Self.capitalizingFirst(sent), after: pass).text
            let closed = processor.finishAfterCleanup(sent + ".", after: pass).text
            // Every held-back replacement moves five places along in this reply, so none can be made by position.
            let prefixed = processor.finishAfterCleanup("Note:" + sent, after: pass).text
            // A held-back replacement at the very start takes the capitalized words' place with what cleanup off
            // writes; anywhere else the reply's own first letter stays capitalized.
            let heldBackFirst = pass.heldBack.contains { $0.location == 0 }
            let capitalizedOff = heldBackFirst ? off : Self.capitalizingFirst(off)
            let whole = NSRange(location: 0, length: sent.utf16.count)
            if Self.spaceBeforeAWordLedByPunctuation.firstMatch(in: sent, range: whole) != nil {
                spacesKeptBeforePunctuation += 1
            }

            var broken: [String] = []
            if unchanged != off {
                broken.append("unchanged reply gave \(unchanged.debugDescription)")
            }
            if capitalized != capitalizedOff {
                broken.append("capitalized reply gave \(capitalized.debugDescription)")
            }
            if closed != off + "." {
                broken.append("reply with a period gave \(closed.debugDescription)")
            }
            if prefixed != "Note:" + off {
                broken.append("prefixed reply gave \(prefixed.debugDescription)")
            }
            if sent.contains("#") {
                broken.append("a template or template-like replacement was sent")
            }
            if !broken.isEmpty, failures.count < 5 {
                let described = "case \(index): \(rules.entries) \(rules.snippets) \(transcript.debugDescription)"
                let texts = "sent \(sent.debugDescription), cleanup off \(off.debugDescription)"
                failures.append(([described, texts] + broken).joined(separator: "; "))
            }
        }
        XCTAssertEqual(failures, [])
        // The text sent does reach the spacing the reply's normalization keeps and the transcript's would not: a space
        // before a punctuation mark that begins a word, as in "use .NET" (122 of the 4,000 cases).
        XCTAssertGreaterThan(spacesKeptBeforePunctuation, 50)
    }

    func testEveryReplacementMadeAfterCleanupAnswersToItsOwnWordsInTheReply() {
        var generator = RuleSetGenerator(seed: 20_260_925)
        var failures: [String] = []
        var made = 0
        var refusedAsAmbiguous = 0
        for index in 0..<Self.cases {
            let rules = generator.ruleSet()
            let transcript = generator.dictation()
            let processor = TextPostProcessor()
            processor.reload(dictionaryEntries: rules.entries, snippets: rules.snippets)
            let pass = processor.correctVocabulary(transcript)
            let sent = pass.text
            for _ in 0..<Self.repliesPerCase {
                let reply = generator.reply(to: sent)
                let restoration = TextPostProcessor.restore(reply, after: pass)
                var broken: [String] = []

                var previous: CleanupRestoration.Made?
                for replacement in restoration.made {
                    if let previous,
                        replacement.heldBack <= previous.heldBack
                            || replacement.range.location < previous.range.location + previous.range.length
                    {
                        broken.append("replacements out of order or made twice")
                    }
                    previous = replacement
                }
                for (position, edit) in pass.heldBack.enumerated() {
                    let before = Self.isWordCharacter(before: edit.location, in: sent)
                    let after = Self.isWordCharacter(at: edit.location + edit.words.utf16.count, in: sent)
                    let inSent = Self.occurrences(of: edit.words, in: sent, before: before, after: after)
                    let inReply = Self.occurrences(of: edit.words, in: restoration.reply, before: before, after: after)
                    if let replacement = restoration.made.first(where: { $0.heldBack == position }) {
                        made += 1
                        let answers =
                            inSent.count == inReply.count
                            && inSent.firstIndex(of: edit.location).map { inReply[$0] } == replacement.range.location
                            && replacement.range.length == edit.words.utf16.count
                        if !answers {
                            broken.append("\(edit.words.debugDescription) was made where its own words did not stand")
                        }
                    } else if inSent.count != inReply.count {
                        refusedAsAmbiguous += 1
                    }
                }
                let marks = restoration.made.reduce(Self.marks(in: restoration.reply)) { total, replacement in
                    total + Self.marks(in: pass.heldBack[replacement.heldBack].output)
                }
                if Self.marks(in: restoration.result.text) != marks {
                    broken.append("a template appeared that no held-back replacement made")
                }
                let rebuilt = Self.rebuild(restoration, heldBack: pass.heldBack)
                if restoration.result.text != rebuilt {
                    broken.append("the result is not the reply with the replacements made: \(rebuilt.debugDescription)")
                }
                if !broken.isEmpty, failures.count < 5 {
                    let described = "case \(index): \(rules.entries) \(rules.snippets) \(transcript.debugDescription)"
                    let texts = "sent \(sent.debugDescription), reply \(reply.debugDescription)"
                    failures.append(([described, texts] + broken).joined(separator: "; "))
                }
            }
        }
        XCTAssertEqual(failures, [])
        // Both ways are exercised: replacements made, and replacements refused because the reply holds their words a
        // different number of times than the text sent.
        XCTAssertGreaterThan(made, 1_000)
        XCTAssertGreaterThan(refusedAsAmbiguous, 500)
    }

    private static func capitalizingFirst(_ text: String) -> String {
        text.prefix(1).uppercased() + text.dropFirst()
    }

    private static let spaceBeforeAWordLedByPunctuation = try! NSRegularExpression(
        pattern: "[ \\t][,.!?;:][\\p{L}\\p{N}]")

    /// The template marks in `text`: every generated template and template-like replacement holds one.
    private static func marks(in text: String) -> Int {
        text.reduce(0) { $0 + ($1 == "#" ? 1 : 0) }
    }

    /// Where `words` occurs in `text`, ignoring case, with a word character before and after it exactly as `before`
    /// and `after` say: the rule `finishAfterCleanup` follows, written as lookarounds rather than taken from it.
    private static func occurrences(of words: String, in text: String, before: Bool, after: Bool) -> [Int] {
        let lead = before ? "(?<=\\w)" : "(?<!\\w)"
        let trail = after ? "(?=\\w)" : "(?!\\w)"
        let pattern = lead + "(?=(" + NSRegularExpression.escapedPattern(for: words) + ")" + trail + ")"
        guard !words.isEmpty, let regex = try? NSRegularExpression(pattern: pattern, options: [.caseInsensitive]) else {
            return []
        }
        let whole = NSRange(location: 0, length: text.utf16.count)
        return regex.matches(in: text, range: whole).map { $0.range(at: 1).location }
    }

    private static let wordCharacter = try! NSRegularExpression(pattern: "^\\w$")

    private static func isWordCharacter(before offset: Int, in text: String) -> Bool {
        offset > 0 && isWordCharacter(at: offset - 1, in: text)
    }

    /// The generator writes only characters of one UTF-16 unit, so a unit is a character here.
    private static func isWordCharacter(at offset: Int, in text: String) -> Bool {
        let units = text as NSString
        guard offset >= 0, offset < units.length else {
            return false
        }
        let character = units.substring(with: NSRange(location: offset, length: 1))
        return wordCharacter.firstMatch(in: character, range: NSRange(location: 0, length: 1)) != nil
    }

    /// The normalized reply with each made replacement in place of the words it answered to.
    private static func rebuild(
        _ restoration: CleanupRestoration, heldBack: [TextPostProcessor.HeldBackEdit]
    ) -> String {
        let reply = restoration.reply as NSString
        var text = ""
        var position = 0
        for replacement in restoration.made {
            text += reply.substring(with: NSRange(location: position, length: replacement.range.location - position))
            text += heldBack[replacement.heldBack].output
            position = replacement.range.location + replacement.range.length
        }
        return text + reply.substring(from: position)
    }
}

/// Small random rule sets and dictations over a four-word vocabulary, so rules overlap, nest and feed each other
/// often: the reviewer's fuzz, with deletions, rules inside words, punctuation words, rules that write a lone space or
/// tab, spellings that begin with a punctuation mark (".NET") or hold one after a space, replacements the whitespace
/// normalization would change, casing fixes, expansions that hold their own pattern, and templates of every shape,
/// one-line ones made of the same words among them, so a dictionary rule often matches across a template's edge, and a
/// snippet and a longer or later rule often want the same words. Every snippet template and template-like replacement
/// it makes that could be told apart holds a "#", which nothing else it makes does, so the text sent must never hold
/// one. `reply(to:)` changes a text sent the way a model might.
private struct RuleSetGenerator {
    private static let words = ["alpha", "beta", "gamma", "delta"]
    /// What goes between two dictated words, and how often, weighted toward one space.
    private static let weightedSeparators: [(String, Int)] = [
        (" ", 40), ("  ", 3), ("\t", 2), (", ", 3), (" , ", 2), ("\n", 2), (" \n", 2), ("\u{00A0}", 2),
        (" \u{00A0}", 2), ("", 3), (".", 2),
    ]
    private static let separators = weightedSeparators.flatMap { Array(repeating: $0.0, count: $0.1) }

    private var state: UInt64

    init(seed: UInt64) {
        state = seed
    }

    mutating func ruleSet() -> (entries: [DictionaryEntry], snippets: [Snippet]) {
        var entries: [DictionaryEntry] = []
        var seen: Set<String> = []
        for _ in 0..<(1 + below(5)) {
            let pattern = chance(10) ? pick(["x", "a", "ta", "comma"]) : phrase()
            let wholeWord = chance(80)
            let replacement = chance(60) ? oneLineReplacement(for: pattern) : templateLikeReplacement()
            if seen.insert(pattern.lowercased()).inserted {
                entries.append(DictionaryEntry(pattern: pattern, replacement: replacement, wholeWord: wholeWord))
            }
        }
        var snippets: [Snippet] = []
        for _ in 0..<below(4) {
            let trigger = phrase()
            snippets.append(Snippet(phrase: trigger, template: template()))
        }
        return (entries, snippets)
    }

    mutating func dictation() -> String {
        var text = word()
        for _ in 0..<below(6) {
            let separator = pick(Self.separators)
            text += separator + word()
        }
        if chance(10) {
            text = " " + text
        }
        if chance(20) {
            text += pick([".", " .", "?", " "])
        }
        return text
    }

    /// A spelling, a casing fix, an expansion, punctuation, a deletion, a lone space or tab, a spelling led by a
    /// punctuation mark, or one that is template-like only by its spacing.
    private mutating func oneLineReplacement(for pattern: String) -> String {
        switch below(16) {
        case 0: return pick(Self.words)
        case 1: return pick(Self.words).capitalized
        case 2: return phrase()
        case 3: return "Z" + digit()
        case 4: return pick(Self.words) + " " + pick(["x", "y"])
        case 5: return pattern.prefix(1).uppercased() + pattern.dropFirst()
        case 6: return pick(Self.words) + " " + pattern
        case 7: return pick([",", ".", "?", ";"])
        case 8: return pick([".com", ",x", "!yes"])
        case 9: return pick(["a\tb", "a  b", " a", "a ", "\u{00A0}a", "a\u{00A0}b", "a ,b"])
        case 10: return ""
        case 11: return pattern + " " + pick(Self.words)
        case 12: return pick(["alpha,", "beta.", "Gamma!"])
        case 13: return pick(Self.words) + pick(Self.words)
        case 14: return pick([" ", "\t", "  "])
        default: return pick([".NET", ".NET Core", "a .NET", ", Inc", ". x", ".5"])
        }
    }

    private mutating func templateLikeReplacement() -> String {
        switch below(4) {
        case 0: return pick(["T", "Line"]) + digit() + "#\n" + pick(Self.words + ["end"])
        case 1: return String(repeating: "L", count: 100) + "#"
        case 2: return pick(Self.words) + " \u{2013}# " + pick(Self.words)
        default: return pick(Self.words) + "#\n" + pick(Self.words)
        }
    }

    private mutating func template() -> String {
        switch below(6) {
        case 0: return "S" + digit() + "#\n" + pick(Self.words + ["x"])
        case 1: return pick(Self.words) + " " + pick(Self.words)
        case 2: return pick([".", ",", "!"])
        case 3: return pick(Self.words) + "# "
        case 4: return "S" + digit() + "# " + pick(Self.words)
        default: return pick(Self.words).uppercased() + "#"
        }
    }

    /// A reply a model might give: `sent` with one to three changes to its words (one dropped, repeated, moved or
    /// added, one written in capitals or given a punctuation mark, or two run together).
    mutating func reply(to sent: String) -> String {
        var words = sent.components(separatedBy: " ")
        for _ in 0..<(1 + below(3)) {
            guard !words.isEmpty else {
                break
            }
            let index = below(words.count)
            switch below(7) {
            case 0:
                if words.count > 1 {
                    words.remove(at: index)
                }
            case 1:
                words.insert(words[index], at: index)
            case 2:
                if words.count > 1 {
                    words.swapAt(index, (index + 1) % words.count)
                }
            case 3:
                words.insert(pick(Self.words + ["sig", "x"]), at: index)
            case 4:
                words[index] = words[index].uppercased()
            case 5:
                words[index] += pick([".", ",", "!"])
            default:
                if index + 1 < words.count {
                    words[index] += words.remove(at: index + 1)
                }
            }
        }
        return words.joined(separator: " ")
    }

    private mutating func word() -> String {
        chance(15) ? pick(["comma", "x", "sig", "um", "K8s", "Alpha"]) : pick(Self.words)
    }

    private mutating func phrase() -> String {
        let first = word()
        return chance(50) ? first : first + " " + word()
    }

    private mutating func digit() -> String {
        String(1 + below(9))
    }

    private mutating func pick(_ choices: [String]) -> String {
        choices[below(choices.count)]
    }

    private mutating func chance(_ percent: Int) -> Bool {
        below(100) < percent
    }

    /// A number from 0 up to `bound`, excluded.
    private mutating func below(_ bound: Int) -> Int {
        Int(next() % UInt64(bound))
    }

    /// SplitMix64.
    private mutating func next() -> UInt64 {
        state &+= 0x9E37_79B9_7F4A_7C15
        var mixed = state
        mixed = (mixed ^ (mixed >> 30)) &* 0xBF58_476D_1CE4_E5B9
        mixed = (mixed ^ (mixed >> 27)) &* 0x94D0_49BB_1331_11EB
        return mixed ^ (mixed >> 31)
    }
}
