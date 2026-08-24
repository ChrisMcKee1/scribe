import XCTest
@testable import Scribe

/// Guards the boundary that keeps auto-learning honest: a generated pattern must be something the
/// recognizer was actually observed to produce, and must never be able to rewrite ordinary prose.
final class DictionaryTermVariantsTests: XCTestCase {
    func testAcronymsAreSpelledOut() {
        let cases: [(String, String)] = [
            ("CSU", "c s u"),
            ("ATU", "a t u"),
            ("MCAP", "m c a p"),
            (".NET", "n e t"),
        ]
        for (term, expected) in cases {
            XCTAssertTrue(DictionaryTermVariants.variants(for: term).contains(expected), term)
        }
    }

    func testTwoLetterAcronymsAreSkipped() {
        // Their spelled form is two single letters, which collides with "a", "I" and friends.
        for term in ["AI", "IQ", "PR"] {
            XCTAssertTrue(DictionaryTermVariants.variants(for: term).isEmpty, term)
        }
    }

    func testCompoundsAreSplit() {
        let cases: [(String, String)] = [
            ("WebIQ", "web iq"),
            ("GitHub", "git hub"),
            ("JavaScript", "java script"),
            ("DeepSeek", "deep seek"),
        ]
        for (term, expected) in cases {
            XCTAssertTrue(DictionaryTermVariants.variants(for: term).contains(expected), term)
        }
    }

    func testCompoundsMadeOfEverydayWordsAreRejected() {
        // "and then" is prose, not a rendering fix; "the issue linked in the PR" must survive untouched.
        for term in ["AndThen", "TheOther", "IfNot", "LinkedIn"] {
            XCTAssertTrue(DictionaryTermVariants.variants(for: term).isEmpty, term)
        }
    }

    func testAGeneratedPatternNeverEqualsItsReplacement() {
        let terms = ["CSU", "WebIQ", "GitHub", "MCAP", "JavaScript", "ReBAC"]

        for term in terms {
            XCTAssertFalse(
                DictionaryTermVariants.variants(for: term).contains {
                    $0.caseInsensitiveCompare(term) == .orderedSame
                },
                term)
        }
    }

    func testAGeneratedPatternDiffersFromTheTermOnlyInCaseAndSpacing() {
        let terms = ["CSU", "ATU", "WebIQ", "GitHub", "JavaScript", "MCAP"]

        for term in terms {
            let squashedTerm = squash(term)
            for pattern in DictionaryTermVariants.variants(for: term) {
                // This is what makes a generated rule safe: it can only ever restore rendering,
                // never substitute a different word.
                XCTAssertEqual(squash(pattern), squashedTerm, term)
            }
        }
    }

    func testOrdinaryTokensYieldNothing() {
        for term in ["hello", "Hello", "42", "", "   "] {
            XCTAssertTrue(DictionaryTermVariants.variants(for: term).isEmpty, "'\(term)'")
        }
    }

    private func squash(_ value: String) -> String {
        String(value.filter { $0.isLetter || $0.isNumber }).lowercased()
    }
}
