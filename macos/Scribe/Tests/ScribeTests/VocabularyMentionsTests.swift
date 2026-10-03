import XCTest

@testable import Scribe

final class VocabularyMentionsTests: XCTestCase {
    private static let vocabulary: [DictionaryEntry] = [
        DictionaryEntry(pattern: "o llama", replacement: "Ollama"),
        DictionaryEntry(pattern: "qwen three thirty two b", replacement: "Qwen3-32B"),
        DictionaryEntry(pattern: "phi four mini", replacement: "Phi-4-mini"),
        DictionaryEntry(pattern: "gpt five six terra", replacement: "GPT-5.6-Terra"),
        DictionaryEntry(pattern: "gpt five four mini", replacement: "GPT-5.4-mini"),
        DictionaryEntry(pattern: "text embedding three large", replacement: "text-embedding-3-large"),
        DictionaryEntry(pattern: "cube control", replacement: "kubectl"),
        DictionaryEntry(pattern: "claude opus four point eight", replacement: "Claude Opus 4.8"),
        DictionaryEntry(pattern: "kubernetes", replacement: "Kubernetes"),
        DictionaryEntry(pattern: "large language model", replacement: "Large Language Model"),
    ]

    func testSpeechRecognitionManglesStillCountAsMentioned() {
        let cases: [(String, String)] = [
            ("we tried it locally through o llama", "Ollama"),
            ("we tried quen 332B locally through Alama", "Ollama"),
            ("we tried quen 332B locally through Alama", "Qwen3-32B"),
            ("a LoRa run on Fi4 Mini with 8,000 examples", "Phi-4-mini"),
            ("I'm running a test for GPT 56Tera to see how it does", "GPT-5.6-Terra"),
            ("the embeddings come from text embedding 3 large", "text-embedding-3-large"),
            ("run cube control apply on the cluster", "kubectl"),
            ("comparing Claude Opus 11 against the others", "Claude Opus 4.8"),
            ("a large language mode that runs on the laptop", "Large Language Model"),
        ]

        for (dictation, written) in cases {
            let selected = VocabularyMentions.select(Self.vocabulary, dictation).map(\.replacement)
            XCTAssertTrue(selected.contains(written), "\(dictation) should mention \(written)")
        }
    }

    func testANameCountsOnlyWhenEveryWordOfItDoes() {
        let selected = VocabularyMentions.select(Self.vocabulary, "the gpt five six terra results were fine")
            .map(\.replacement)

        XCTAssertTrue(selected.contains("GPT-5.6-Terra"))
        XCTAssertFalse(selected.contains("GPT-5.4-mini"))
    }

    func testADictationThatNamesNoneCarriesNone() {
        for dictation in [
            "let's get lunch on thursday and talk about the budget",
            "please send the quarterly report to Sarah by Friday",
        ] {
            XCTAssertTrue(VocabularyMentions.select(Self.vocabulary, dictation).isEmpty)
        }
    }

    func testCommonWordsSayNothingOnTheirOwn() {
        let selected = VocabularyMentions.select(Self.vocabulary, "the large model was slow")
        XCTAssertFalse(selected.contains { $0.replacement == "Large Language Model" })
    }

    func testDisabledEntriesAreNeverSelectedAndOrderIsKept() {
        let entries = [
            DictionaryEntry(pattern: "kubernetes", replacement: "Kubernetes"),
            DictionaryEntry(pattern: "o llama", replacement: "Ollama", enabled: false),
            DictionaryEntry(pattern: "cube control", replacement: "kubectl"),
        ]

        let selected = VocabularyMentions.select(entries, "kubernetes and cube control through o llama")
        XCTAssertEqual(selected.map(\.replacement), ["Kubernetes", "kubectl"])
    }

    func testNoWordsMeansNoVocabulary() {
        XCTAssertTrue(VocabularyMentions.select(Self.vocabulary, nil).isEmpty)
        XCTAssertTrue(VocabularyMentions.select(Self.vocabulary, "").isEmpty)
        XCTAssertTrue(VocabularyMentions.select(Self.vocabulary, "   ").isEmpty)
        XCTAssertTrue(VocabularyMentions.select(Self.vocabulary, "... !!!").isEmpty)
    }

    func testSoundKeysMatchWhatSoundsAlike() {
        let cases: [(String, String, Bool)] = [
            ("ollama", "alama", true),
            ("qwen", "quen", true),
            ("terra", "tera", true),
            ("ollama", "llama", false),
            ("kubernetes", "cabernet", false),
        ]

        for (lhs, rhs, same) in cases {
            XCTAssertEqual(VocabularyMentions.soundKey(lhs) == VocabularyMentions.soundKey(rhs), same)
        }
    }

    func testPartsSplitLettersFromDigits() {
        XCTAssertEqual(VocabularyMentions.parts("Qwen3-14B"), ["qwen", "3", "14", "b"])
        XCTAssertEqual(VocabularyMentions.parts("GPT-5.6-Terra"), ["gpt", "5", "6", "terra"])
    }
}
