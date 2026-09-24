import XCTest

@testable import Scribe

/// The rules with AI cleanup on against cleanup off, on thousands of small random rule sets and dictations, with the
/// rules frozen: a model that returns what it was sent must get exactly the cleanup-off text from the rules, one that
/// only capitalizes the first letter, adds a final period or puts a prefix before everything must get the cleanup-off
/// text with that change, and no snippet template or template-like replacement may reach the text sent. The changed
/// replies keep an answer that only held for an unchanged one from passing. The response guard, which rewrites dashes
/// and strips wrappers from a real reply, is not in this test; `DictationPipelineTests` covers it. Deterministic: one
/// seed and a generator of its own, so every run meets the same cases.
final class TextPostProcessorPropertyTests: XCTestCase {
    private static let cases = 4_000

    func testCleanupOnGivesTheCleanupOffTextToAModelThatChangesNothing() {
        var generator = RuleSetGenerator(seed: 20_260_924)
        var failures: [String] = []
        for index in 0..<Self.cases {
            let rules = generator.ruleSet()
            let transcript = generator.dictation()
            let processor = TextPostProcessor()
            processor.reload(dictionaryEntries: rules.entries, snippets: rules.snippets)
            let off = processor.process(transcript)
            let pass = processor.correctVocabulary(transcript)
            let sent = pass.text
            let firstCapital = sent.prefix(1).uppercased() + sent.dropFirst()
            let unchanged = processor.finishAfterCleanup(sent, after: pass).text
            let capitalized = processor.finishAfterCleanup(firstCapital, after: pass).text
            let closed = processor.finishAfterCleanup(sent + ".", after: pass).text
            // Every held-back replacement moves five places along in this reply, so none can be made by position.
            let prefixed = processor.finishAfterCleanup("Note:" + sent, after: pass).text

            var broken: [String] = []
            if unchanged != off {
                broken.append("unchanged reply gave \(unchanged.debugDescription)")
            }
            if capitalized.lowercased() != off.lowercased() {
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
    }
}

/// Small random rule sets and dictations over a four-word vocabulary, so rules overlap, nest and feed each other
/// often: the reviewer's fuzz, with deletions, rules inside words, punctuation words, replacements the whitespace
/// normalization would change, casing fixes, expansions that hold their own pattern, and templates of every shape,
/// one-line ones made of the same words among them, so a dictionary rule often matches across a template's edge (60
/// of the 4,000 cases here). Every snippet template and template-like replacement it makes that could be told apart
/// holds a "#", which nothing else it makes does, so the text sent must never hold one.
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

    /// A spelling, a casing fix, an expansion, punctuation, a deletion, or one that is template-like only by its
    /// spacing.
    private mutating func oneLineReplacement(for pattern: String) -> String {
        switch below(14) {
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
        default: return pick(Self.words) + pick(Self.words)
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
