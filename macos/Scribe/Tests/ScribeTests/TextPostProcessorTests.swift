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

    /// A deletion, and a replacement whose spacing the normalization of the reply would change, are template-like: the
    /// reply is normalized before the held-back replacements are made, so either would come back other than cleanup
    /// off writes it. Spacing the normalization keeps, such as a no-break space inside the replacement, is vocabulary.
    func testADeletionAndSpacingTheNormalizationWouldChangeAreTemplateLike() {
        XCTAssertFalse(TextPostProcessor.isVocabulary(DictionaryEntry(pattern: "um", replacement: "")))
        let unstable = [
            "a\tb", "a  b", " a", "a ", "\u{00A0}a", "a\u{00A0}", "a ,b", "a .", "\ta", "a  ", "a\u{0B}b",
        ]
        for replacement in unstable {
            let entry = DictionaryEntry(pattern: "x", replacement: replacement)
            XCTAssertFalse(TextPostProcessor.isVocabulary(entry), "\(replacement.debugDescription) is vocabulary")
        }
        for replacement in ["a\u{00A0}b", "a, b", ",", "a.b", "C#"] {
            let entry = DictionaryEntry(pattern: "x", replacement: replacement)
            XCTAssertTrue(TextPostProcessor.isVocabulary(entry), "\(replacement.debugDescription) is template-like")
        }
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

    /// A rule edited while the request was out changes nothing after cleanup: the replacements were decided with the
    /// rules the request went out with, and are made as they were decided. Here the rule was template-like when the
    /// request went out and one line afterwards; it is made once, after cleanup, rather than in neither step.
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

    // MARK: - Nothing matched against the reply

    /// The text a model that changes nothing would give back.
    private func unchangedByCleanup(_ transcript: String, with processor: TextPostProcessor) -> String {
        let pass = processor.correctVocabulary(transcript)
        return processor.finishAfterCleanup(pass.text, after: pass).text
    }

    /// The user said "signature"; no rule wrote it. The snippet "my sig" was not said, so it never expands, whatever
    /// the vocabulary rule "sig" would write and however the reply spells the words.
    func testAWordTheUserSaidIsNeverTakenForATriggerAVocabularyRuleWouldWrite() {
        let processor = TextPostProcessor()
        processor.reload(
            dictionaryEntries: [DictionaryEntry(pattern: "sig", replacement: "signature")],
            snippets: [Snippet(phrase: "my sig", template: "Best,\nPat Doe")])
        let transcript = "I will add my signature tomorrow"

        XCTAssertEqual(processor.process(transcript), transcript)
        XCTAssertEqual(unchangedByCleanup(transcript, with: processor), transcript)
        let pass = processor.correctVocabulary(transcript)
        XCTAssertEqual(
            processor.finishAfterCleanup("I will add my signature tomorrow.", after: pass).text,
            "I will add my signature tomorrow.")
        // Said as its trigger, the snippet does expand.
        XCTAssertEqual(unchangedByCleanup("end with my sig", with: processor), "end with Best,\nPat Doe")
    }

    /// A deletion is made after cleanup, where it deletes and does nothing else: deleting "x" inside "signaturex"
    /// leaves "signature", which was never said and so is never the snippet "signature".
    func testADeletionNeverMakesATrigger() {
        let deletion = DictionaryEntry(pattern: "x", replacement: "", wholeWord: false)
        let processor = TextPostProcessor()
        processor.reload(
            dictionaryEntries: [deletion], snippets: [Snippet(phrase: "signature", template: "Private\nfooter")])

        XCTAssertFalse(TextPostProcessor.isVocabulary(deletion))
        XCTAssertEqual(processor.process("signaturex"), "signature")
        let pass = processor.correctVocabulary("signaturex")
        XCTAssertEqual(pass.text, "signaturex", "the deletion was made in the text sent")
        XCTAssertEqual(processor.finishAfterCleanup(pass.text, after: pass).text, "signature")
        XCTAssertEqual(processor.finishAfterCleanup("Signaturex.", after: pass).text, "Signature.")
    }

    /// A trigger said once expands once, beside the same words said as the rule writes them: "k eight s" is the
    /// snippet's trigger, and "K8s" is what the user said the second time. A reply that drops one of the two leaves
    /// no way to tell which it kept, so it expands neither.
    func testATriggerSaidOnceExpandsOnceBesideItsWrittenForm() {
        let processor = TextPostProcessor()
        processor.reload(
            dictionaryEntries: [DictionaryEntry(pattern: "k eight s", replacement: "K8s")],
            snippets: [Snippet(phrase: "k eight s", template: "Private\nnotes")])
        let transcript = "k eight s then K8s"

        XCTAssertEqual(processor.process(transcript), "Private\nnotes then K8s")
        XCTAssertEqual(unchangedByCleanup(transcript, with: processor), "Private\nnotes then K8s")
        let pass = processor.correctVocabulary(transcript)
        XCTAssertEqual(pass.text, "K8s then K8s")
        XCTAssertEqual(processor.finishAfterCleanup("K8s then K8s.", after: pass).text, "Private\nnotes then K8s.")
        XCTAssertEqual(processor.finishAfterCleanup("Then K8s.", after: pass).text, "Then K8s.")
    }

    /// Held-back words are found only where they stand as words: "IL", which the snippet's trigger "i l" is sent as,
    /// is not also counted inside "email", so a reply that rewrote "email" still gets the snippet.
    func testHeldBackWordsAreFoundOnlyWhereTheyStandAsWords() {
        let processor = TextPostProcessor()
        processor.reload(
            dictionaryEntries: [DictionaryEntry(pattern: "i l", replacement: "IL")],
            snippets: [Snippet(phrase: "i l", template: "Illinois\nUSA")])
        let pass = processor.correctVocabulary("send the email to i l")
        XCTAssertEqual(pass.text, "send the email to IL")

        XCTAssertEqual(
            processor.finishAfterCleanup("Send a message to IL.", after: pass).text, "Send a message to Illinois\nUSA.")
        XCTAssertEqual(
            processor.finishAfterCleanup("Send the email to IL.", after: pass).text, "Send the email to Illinois\nUSA.")
        XCTAssertEqual(processor.process("send the email to i l"), "send the email to Illinois\nUSA")

        // Nor does the written form in the reply make an expansion the user did not say.
        let unsaid = processor.correctVocabulary("send the email")
        let reply = "Send the email to IL."
        XCTAssertEqual(processor.finishAfterCleanup(reply, after: unsaid).text, reply)
    }

    /// A rule that matches inside words is found inside a word again after cleanup, where its match was: the words'
    /// surroundings are recorded as they were in the text sent, not required to be word boundaries.
    func testAMatchInsideAWordIsFoundInsideThatWordAfterCleanup() {
        let processor = TextPostProcessor()
        processor.reload(
            dictionaryEntries: [DictionaryEntry(pattern: "mail", replacement: "Mail\nMerge", wholeWord: false)],
            snippets: [])
        XCTAssertEqual(processor.process("send the email"), "send the eMail\nMerge")

        let pass = processor.correctVocabulary("send the email")

        XCTAssertEqual(pass.text, "send the email")
        XCTAssertEqual(
            processor.finishAfterCleanup("Send the email today.", after: pass).text, "Send the eMail\nMerge today.")
    }

    /// A dictionary rule that reaches across a snippet's edge matches as it does with cleanup off: the snippet
    /// "signoff" writes "hello", and "hello world" then becomes "greeting" across the template's edge. That match is
    /// part of the snippet's held-back replacement, made once, and never made again on the reply.
    func testADictionaryMatchAcrossASnippetsEdgeIsKept() {
        let processor = TextPostProcessor()
        processor.reload(
            dictionaryEntries: [
                DictionaryEntry(pattern: "hello", replacement: "Hello"),
                DictionaryEntry(pattern: "hello world", replacement: "greeting"),
            ],
            snippets: [Snippet(phrase: "signoff", template: "hello")])
        XCTAssertEqual(processor.process("signoff world"), "greeting")

        let pass = processor.correctVocabulary("signoff world")

        XCTAssertEqual(pass.text, "signoff world")
        XCTAssertEqual(processor.finishAfterCleanup(pass.text, after: pass).text, "greeting")
        XCTAssertEqual(processor.finishAfterCleanup("Signoff world.", after: pass).text, "greeting.")
        // The vocabulary rule "hello" is not made again on what the reply says.
        XCTAssertEqual(processor.finishAfterCleanup("Signoff world, hello.", after: pass).text, "greeting, hello.")
    }

    /// The words of a held-back replacement go in the vocabulary rules' spelling only when it begins and ends the way
    /// the words do. Here the rule would end the trigger "gamma" with a period; sent that way, the period a model adds
    /// after the last word would make a second copy of it, and the words could be found in the wrong place. Sent as
    /// spoken, the trigger is found once and expands where it was said.
    func testHeldBackWordsKeepTheirSpokenFormWhenTheSpellingWouldChangeTheirEdges() {
        let processor = TextPostProcessor()
        processor.reload(
            dictionaryEntries: [DictionaryEntry(pattern: "gamma", replacement: "beta.")],
            snippets: [Snippet(phrase: "gamma", template: "S1\nx")])
        let pass = processor.correctVocabulary("gamma then beta")

        XCTAssertEqual(pass.text, "gamma then beta")
        XCTAssertEqual(processor.finishAfterCleanup("Gamma then beta.", after: pass).text, "S1\nx then beta.")
    }

    /// A replacement with a tab or a run of spaces is template-like, since normalizing the reply would change it: it is
    /// made after cleanup, exactly as cleanup off writes it.
    func testSpacingTheNormalizationWouldChangeIsMadeAfterCleanupAsWritten() {
        for replacement in ["a\tb", "a  b", " a", "a ", "a ,b"] {
            let rule = DictionaryEntry(pattern: "zed", replacement: replacement)
            let processor = TextPostProcessor()
            processor.reload(dictionaryEntries: [rule], snippets: [])
            let pass = processor.correctVocabulary("one zed two")

            XCTAssertEqual(pass.text, "one zed two", "\(replacement.debugDescription) was made in the text sent")
            XCTAssertEqual(processor.process("one zed two"), "one \(replacement) two")
            XCTAssertEqual(unchangedByCleanup("one zed two", with: processor), "one \(replacement) two")
        }
    }

    /// A punctuation word after a line break that follows a space: the tight punctuation guard takes the line break,
    /// so cleanup off leaves a space before the comma, which the reply's normalization would take out. The comma is
    /// then made after cleanup instead, and the rest of the dictation's spellings still go in the text sent.
    func testAPunctuationRuleThatWouldLeaveASpaceBeforeItIsMadeAfterCleanup() {
        let processor = TextPostProcessor()
        processor.reload(
            dictionaryEntries: [
                DictionaryEntry(pattern: "comma", replacement: ","),
                DictionaryEntry(pattern: "cube flow", replacement: "Kubeflow"),
            ],
            snippets: [])
        let transcript = "use cube flow \ncomma then comma done"
        XCTAssertEqual(processor.process(transcript), "use Kubeflow , then, done")

        let pass = processor.correctVocabulary(transcript)

        XCTAssertEqual(pass.text, "use Kubeflow \ncomma then, done")
        XCTAssertEqual(processor.finishAfterCleanup(pass.text, after: pass).text, "use Kubeflow , then, done")
    }
}
