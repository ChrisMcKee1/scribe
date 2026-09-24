import XCTest

@testable import Scribe

final class TextPostProcessorTests: XCTestCase {
    func testDictionaryWholeWordReplacement() {
        let processor = TextPostProcessor()
        processor.reload(
            dictionaryEntries: [DictionaryEntry(pattern: "github", replacement: "GitHub")],
            snippets: [])

        XCTAssertEqual(processor.process("i love github"), "i love GitHub")
    }

    func testDictionaryWholeWordDoesNotMatchSubstring() {
        let processor = TextPostProcessor()
        processor.reload(
            dictionaryEntries: [DictionaryEntry(pattern: "github", replacement: "GitHub")],
            snippets: [])

        // "githubbing" contains "github" but is not the whole word "github"; must not be replaced.
        XCTAssertEqual(processor.process("githubbing is fun"), "githubbing is fun")
    }

    func testDictionaryCaseInsensitiveMatching() {
        let processor = TextPostProcessor()
        processor.reload(
            dictionaryEntries: [DictionaryEntry(pattern: "sherpa onnx", replacement: "sherpa-onnx")],
            snippets: [])

        XCTAssertEqual(processor.process("we use Sherpa ONNX daily"), "we use sherpa-onnx daily")
    }

    func testSnippetExpansion() {
        let processor = TextPostProcessor()
        processor.reload(
            dictionaryEntries: [],
            snippets: [Snippet(phrase: "sign off block", template: "Best regards,\nScribe Team")])

        XCTAssertEqual(
            processor.process("please add sign off block at the end"),
            "please add Best regards,\nScribe Team at the end")
    }

    func testSnippetExpandsBeforeDictionaryCanonicalizesItsOutput() {
        let processor = TextPostProcessor()
        processor.reload(
            dictionaryEntries: [DictionaryEntry(pattern: "github", replacement: "GitHub")],
            snippets: [Snippet(phrase: "my repo plug", template: "check out my github page")])

        // The snippet's own template text should benefit from dictionary canonicalization in the
        // following phase, matching Windows' documented ordering (snippets first, then dictionary).
        XCTAssertEqual(processor.process("my repo plug"), "check out my GitHub page")
    }

    func testDisabledEntriesAreIgnored() {
        let processor = TextPostProcessor()
        processor.reload(
            dictionaryEntries: [DictionaryEntry(pattern: "github", replacement: "GitHub", enabled: false)],
            snippets: [])

        XCTAssertEqual(processor.process("i love github"), "i love github")
    }

    func testEmptyInputReturnsEmptyString() {
        let processor = TextPostProcessor()
        processor.reload(dictionaryEntries: [], snippets: [])
        XCTAssertEqual(processor.process("   "), "")
    }

    func testNormalizesWhitespaceAndSpaceBeforePunctuation() {
        let processor = TextPostProcessor()
        processor.reload(dictionaryEntries: [], snippets: [])
        XCTAssertEqual(processor.process("hello   world , how are you ?"), "hello world, how are you?")
    }

    // MARK: - processDetailed

    func testProcessDetailedReportsExactChangedDictionarySpans() {
        let processor = TextPostProcessor()
        processor.reload(
            dictionaryEntries: [
                DictionaryEntry(pattern: "a p i m", replacement: "APIM"),
                DictionaryEntry(pattern: "azure", replacement: "Azure"),
            ],
            snippets: [])

        let result = processor.processDetailed("deploy a p i m to azure and API")

        XCTAssertEqual(result.text, "deploy APIM to Azure and API")
        XCTAssertEqual(result.replacements.count, 2)

        XCTAssertEqual(result.replacements[0].pattern, "a p i m")
        XCTAssertEqual(result.replacements[0].kind, .dictionary)
        XCTAssertEqual(
            (result.text as NSString).substring(
                with: NSRange(location: result.replacements[0].start, length: result.replacements[0].length)),
            "APIM")

        XCTAssertEqual(result.replacements[1].pattern, "azure")
        XCTAssertEqual(result.replacements[1].kind, .dictionary)
        XCTAssertEqual(
            (result.text as NSString).substring(
                with: NSRange(location: result.replacements[1].start, length: result.replacements[1].length)),
            "Azure")
    }

    func testProcessDetailedReportsSnippetSpanAfterDictionaryCanonicalization() {
        let processor = TextPostProcessor()
        processor.reload(
            dictionaryEntries: [DictionaryEntry(pattern: "github", replacement: "GitHub")],
            snippets: [Snippet(phrase: "my repo plug", template: "check out my github page")])

        let result = processor.processDetailed("my repo plug")

        XCTAssertEqual(result.text, "check out my GitHub page")
        let snippetReplacement = result.replacements.first { $0.kind == .snippet }
        XCTAssertNotNil(snippetReplacement)
        XCTAssertEqual(snippetReplacement?.pattern, "my repo plug")
        XCTAssertEqual(snippetReplacement?.replacement, "check out my GitHub page")
    }

    func testProcessDetailedReportsNothingWhenTextIsUnchanged() {
        let processor = TextPostProcessor()
        processor.reload(dictionaryEntries: [DictionaryEntry(pattern: "azure", replacement: "azure")], snippets: [])

        let result = processor.processDetailed("we use azure")

        XCTAssertTrue(result.replacements.isEmpty)
    }

    func testProcessDetailedReturnsEmptyResultForBlankInput() {
        let processor = TextPostProcessor()
        processor.reload(dictionaryEntries: [], snippets: [])

        let result = processor.processDetailed("   ")

        XCTAssertEqual(result.text, "")
        XCTAssertTrue(result.replacements.isEmpty)
    }

    // MARK: - Tight punctuation and surrogate pairs

    func testPunctuationRuleAbsorbsTheSpaceBeforeIt() {
        let processor = TextPostProcessor()
        processor.reload(dictionaryEntries: [DictionaryEntry(pattern: "comma", replacement: ",")], snippets: [])

        XCTAssertEqual(processor.process("hello comma world"), "hello, world")
    }

    func testAnEmojiRightBeforeAPunctuationRuleIsKeptAndDoesNotTrap() {
        let processor = TextPostProcessor()
        processor.reload(dictionaryEntries: [DictionaryEntry(pattern: "comma", replacement: ",")], snippets: [])

        XCTAssertEqual(processor.process("\u{1F642}comma"), "\u{1F642},")
    }

    func testASnippetThatEndsInAnEmojiBeforeAPunctuationRuleDoesNotTrap() {
        let processor = TextPostProcessor()
        processor.reload(
            dictionaryEntries: [DictionaryEntry(pattern: "comma", replacement: ",")],
            snippets: [Snippet(phrase: "smile", template: "\u{1F642}comma")])

        let result = processor.processDetailed("smile")

        XCTAssertEqual(result.text, "\u{1F642},")
        XCTAssertEqual(result.replacements.first { $0.kind == .snippet }?.replacement, "\u{1F642},")
    }

    // MARK: - Rules compiled once per reload

    func testRulesLoadedOnceGiveTheSameOutputAsAFreshLoadForEveryDictation() {
        let entries = [
            DictionaryEntry(pattern: "github", replacement: "GitHub"),
            DictionaryEntry(pattern: "york", replacement: "New York"),
            DictionaryEntry(pattern: "comma", replacement: ","),
            DictionaryEntry(pattern: "a p i m", replacement: "APIM"),
        ]
        let snippets = [Snippet(phrase: "sign off", template: "Best regards,\ncheck my github")]
        let corpus = [
            "i love github comma really",
            "we moved to new york from york",
            "deploy a p i m then sign off",
            "nothing to change here",
            "github github comma york",
        ]
        let loadedOnce = TextPostProcessor()
        loadedOnce.reload(dictionaryEntries: entries, snippets: snippets)

        for _ in 0..<3 {
            for text in corpus {
                let fresh = TextPostProcessor()
                fresh.reload(dictionaryEntries: entries, snippets: snippets)
                let expected = fresh.processDetailed(text)
                let actual = loadedOnce.processDetailed(text)
                XCTAssertEqual(actual.text, expected.text, text)
                XCTAssertEqual(actual.replacements, expected.replacements, text)
            }
        }
    }

    func testReloadReplacesTheCompiledRules() {
        let processor = TextPostProcessor()
        processor.reload(dictionaryEntries: [DictionaryEntry(pattern: "github", replacement: "GitHub")], snippets: [])
        XCTAssertEqual(processor.process("github"), "GitHub")

        processor.reload(dictionaryEntries: [DictionaryEntry(pattern: "azure", replacement: "Azure")], snippets: [])

        XCTAssertEqual(processor.process("github and azure"), "github and Azure")
    }

    /// With cleanup off every rule still runs as it always has: snippets first, then the dictionary in one pass over
    /// the result, the templates included, with the tight punctuation and double-expansion guards.
    func testWithCleanupOffTheRulesRunAsTheyAlwaysHave() {
        let processor = TextPostProcessor()
        processor.reload(
            dictionaryEntries: [
                DictionaryEntry(pattern: "york", replacement: "New York"),
                DictionaryEntry(pattern: "comma", replacement: ","),
                DictionaryEntry(pattern: "my sign off", replacement: "Pat Doe\nSupport lead"),
                DictionaryEntry(pattern: "sig", replacement: "signature"),
            ],
            snippets: [Snippet(phrase: "signature", template: "Kind regards from york")])

        XCTAssertEqual(
            processor.process("fly to york comma new york and sig then signature my sign off"),
            "fly to New York, new york and signature then Kind regards from New York Pat Doe\nSupport lead")
    }

    // MARK: - With AI cleanup on

    func testOnlyAOneLineReplacementOfAtMostAHundredCharactersIsVocabulary() {
        let hundred = String(repeating: "a", count: 100)
        XCTAssertTrue(TextPostProcessor.isVocabulary(DictionaryEntry(pattern: "x", replacement: hundred)))
        XCTAssertFalse(TextPostProcessor.isVocabulary(DictionaryEntry(pattern: "x", replacement: hundred + "a")))
        XCTAssertTrue(TextPostProcessor.isVocabulary(DictionaryEntry(pattern: "x", replacement: "")))
        for lineBreak in ["\n", "\r", "\r\n", "\u{85}", "\u{2028}", "\u{2029}"] {
            let entry = DictionaryEntry(pattern: "x", replacement: "a\(lineBreak)b")
            XCTAssertFalse(TextPostProcessor.isVocabulary(entry), "a replacement with a line break is vocabulary")
        }
        // Counted as .NET counts a string: each of these emoji is two UTF-16 units.
        let emoji = String(repeating: "\u{1F642}", count: 51)
        XCTAssertFalse(TextPostProcessor.isVocabulary(DictionaryEntry(pattern: "x", replacement: emoji)))
        // A dash the user wrote runs after cleanup, out of the reply's dash normalization.
        for dash in ["\u{2013}", "\u{2014}"] {
            let entry = DictionaryEntry(pattern: "x", replacement: "Scribe \(dash) Mac")
            XCTAssertFalse(TextPostProcessor.isVocabulary(entry), "a replacement with a dash is vocabulary")
        }
        XCTAssertTrue(TextPostProcessor.isVocabulary(DictionaryEntry(pattern: "x", replacement: "sherpa-onnx")))
    }

    func testBeforeCleanupOnlyTheVocabularyRulesRun() {
        let processor = TextPostProcessor()
        processor.reload(
            dictionaryEntries: [
                DictionaryEntry(pattern: "cube flow", replacement: "Kubeflow"),
                DictionaryEntry(pattern: "my sign off", replacement: "Pat Doe\nSupport lead"),
            ],
            snippets: [Snippet(phrase: "insert my address", template: "12 Harbor Road\nSpringfield")])

        let pass = processor.correctVocabulary("insert my address  and my sign off with cube flow ,")

        XCTAssertEqual(pass.text, "insert my address and my sign off with Kubeflow,")
        XCTAssertEqual(pass.result.replacements.map(\.pattern), ["cube flow"])
    }

    /// The snippets and the template-like rules run on the reply, each once; the templates arrive canonical, as they
    /// would with cleanup off, and no vocabulary rule runs on the reply, so an expansion is never doubled.
    func testAfterCleanupTheSnippetsAndTemplateLikeRulesRunAndNoVocabularyRuleRunsAgain() {
        let processor = TextPostProcessor()
        processor.reload(
            dictionaryEntries: [
                DictionaryEntry(pattern: "york", replacement: "New York"),
                DictionaryEntry(pattern: "my sign off", replacement: "Pat Doe\nSupport lead"),
                DictionaryEntry(pattern: "scribe", replacement: "Scribe"),
            ],
            snippets: [Snippet(phrase: "insert my address", template: "sent from scribe in york")])
        let pass = processor.correctVocabulary("insert my address from york then my sign off")
        XCTAssertEqual(pass.text, "insert my address from New York then my sign off")

        let result = processor.finishAfterCleanup("Insert my address from New York, then my sign off.", after: pass)

        XCTAssertEqual(result.text, "sent from Scribe in New York from New York, then Pat Doe\nSupport lead.")
    }

    /// What a vocabulary rule wrote before cleanup is never the spoken form of a rule after it: "alpha" became "beta"
    /// and "sig" became "signature" on the way to the model, so neither is expanded again on the way back, which is
    /// what cleanup off gives too.
    func testAVocabularyRulesOutputNeverFeedsASecondRuleAfterCleanup() {
        let processor = TextPostProcessor()
        processor.reload(
            dictionaryEntries: [
                DictionaryEntry(pattern: "sig", replacement: "signature"),
                DictionaryEntry(pattern: "signature", replacement: "Pat Doe\nSupport lead"),
                DictionaryEntry(pattern: "alpha", replacement: "beta"),
                DictionaryEntry(pattern: "beta", replacement: "gamma"),
            ],
            snippets: [Snippet(phrase: "signature", template: "Kind regards")])
        let pass = processor.correctVocabulary("alpha then sig")
        XCTAssertEqual(pass.text, "beta then signature")

        let result = processor.finishAfterCleanup("Beta then signature.", after: pass)

        XCTAssertEqual(result.text, "Beta then signature.")
        XCTAssertEqual(processor.process("alpha then sig"), "beta then signature")
    }

    /// A trigger the user did say still expands after cleanup when a vocabulary rule rewrote words inside it: the
    /// model was sent "my K8s notes", and that is the trigger "my k eight s notes" as spoken.
    func testATriggerTheUserSaidStillExpandsWhenAVocabularyRuleRewroteItsWords() {
        let processor = TextPostProcessor()
        processor.reload(
            dictionaryEntries: [
                DictionaryEntry(pattern: "k eight s", replacement: "K8s"),
                DictionaryEntry(pattern: "github", replacement: "GitHub"),
            ],
            snippets: [
                Snippet(phrase: "my k eight s notes", template: "Cluster notes:\n- nodes"),
                Snippet(phrase: "my github link", template: "the link to my profile"),
            ])
        let pass = processor.correctVocabulary("open my k eight s notes and my github link")
        XCTAssertEqual(pass.text, "open my K8s notes and my GitHub link")

        let result = processor.finishAfterCleanup("Open my K8s notes and my GitHub link.", after: pass)

        XCTAssertEqual(result.text, "Open Cluster notes:\n- nodes and the link to my profile.")
    }

    /// A rule edited while the request was out does not change which step it runs in: the step after cleanup uses the
    /// rules the step before it ran with. Here the rule was template-like when the request went out and one line
    /// afterwards; it runs once, after cleanup, rather than in neither step.
    func testTheStepAfterCleanupUsesTheRulesTheStepBeforeItRanWith() {
        let processor = TextPostProcessor()
        processor.reload(
            dictionaryEntries: [DictionaryEntry(pattern: "my address", replacement: "12 Harbor Road\nSpringfield")],
            snippets: [])
        let pass = processor.correctVocabulary("send it to my address")
        XCTAssertEqual(pass.text, "send it to my address")

        processor.reload(
            dictionaryEntries: [DictionaryEntry(pattern: "my address", replacement: "12 Harbor Road")], snippets: [])
        let result = processor.finishAfterCleanup("Send it to my address.", after: pass)

        XCTAssertEqual(result.text, "Send it to 12 Harbor Road\nSpringfield.")
    }

    /// The Playground underlines every replacement where it now sits: what the vocabulary rules wrote, wherever the
    /// reply kept it, the snippet's template and the template-like rule's text.
    func testAfterCleanupEveryReplacementIsReportedWhereItNowSits() {
        let processor = TextPostProcessor()
        processor.reload(
            dictionaryEntries: [
                DictionaryEntry(pattern: "cube flow", replacement: "Kubeflow"),
                DictionaryEntry(pattern: "my sign off", replacement: "Pat Doe\nSupport lead"),
            ],
            snippets: [Snippet(phrase: "insert my address", template: "12 Harbor Road")])
        let pass = processor.correctVocabulary("deploy with cube flow insert my address and my sign off")

        let result = processor.finishAfterCleanup(
            "Deploy with Kubeflow, insert my address and my sign off.", after: pass)

        XCTAssertEqual(result.text, "Deploy with Kubeflow, 12 Harbor Road and Pat Doe\nSupport lead.")
        let text = result.text as NSString
        let underlined = result.replacements.map {
            text.substring(with: NSRange(location: $0.start, length: $0.length))
        }
        XCTAssertEqual(underlined, ["Kubeflow", "12 Harbor Road", "Pat Doe\nSupport lead"])
        XCTAssertEqual(result.replacements.map(\.kind), [.dictionary, .snippet, .dictionary])
    }
}
