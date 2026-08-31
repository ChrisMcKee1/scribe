import XCTest
@testable import Scribe

/// Direct port of Windows' `LastTranscriptStoreTests`, covering the ring buffer semantics, the
/// content-keyed `update`, `seed`, and `formatPreview` truncation rules.
final class LastTranscriptStoreTests: XCTestCase {
    func testSetLastWriteWins() {
        let store = LastTranscriptStore()
        store.set("first")
        store.set("Second line\r\nwith spacing.  ")

        XCTAssertEqual(store.get(), "Second line\r\nwith spacing.  ")
    }

    func testEmptyUpdatesDoNotEraseRecoverableText() {
        let store = LastTranscriptStore()
        store.set("recover me")
        store.set("  ")

        XCTAssertEqual(store.get(), "recover me")
    }

    func testRecentReturnsMostRecentFirst() {
        let store = LastTranscriptStore()
        store.set("first")
        store.set("second")
        store.set("third")

        XCTAssertEqual(store.recent(), ["third", "second", "first"])
    }

    func testRecentEvictsOldestBeyondCapacity() {
        let store = LastTranscriptStore()
        for i in 1...(LastTranscriptStore.capacity + 2) {
            store.set("dictation \(i)")
        }

        let recent = store.recent()
        XCTAssertEqual(recent.count, LastTranscriptStore.capacity)
        XCTAssertEqual(recent.first, "dictation 7")
        XCTAssertEqual(recent.last, "dictation 3")
    }

    func testConsecutiveDuplicateTextOccupiesASingleSlot() {
        let store = LastTranscriptStore()
        store.set("repeat me")
        store.set("repeat me")

        XCTAssertEqual(store.recent(), ["repeat me"])
    }

    func testNonadjacentDuplicateTextIsKeptAsADistinctEntry() {
        let store = LastTranscriptStore()
        store.set("alpha")
        store.set("beta")
        store.set("alpha")

        XCTAssertEqual(store.recent(), ["alpha", "beta", "alpha"])
    }

    func testRecentSnapshotIsUnaffectedByLaterWrites() {
        let store = LastTranscriptStore()
        store.set("original")

        let snapshot = store.recent()
        store.set("newer")

        XCTAssertEqual(snapshot, ["original"])
        XCTAssertEqual(store.recent(), ["newer", "original"])
    }

    func testRecentIsEmptyBeforeAnyDictation() {
        XCTAssertTrue(LastTranscriptStore().recent().isEmpty)
    }

    func testFormatPreviewReturnsShortTextUnchanged() {
        XCTAssertEqual(LastTranscriptStore.formatPreview("Hello there."), "Hello there.")
    }

    func testFormatPreviewKeepsTextExactlyAtTheCap() {
        let exact = String(repeating: "a", count: LastTranscriptStore.previewLength)
        XCTAssertEqual(LastTranscriptStore.formatPreview(exact), exact)
    }

    func testFormatPreviewTruncatesOverCapTextWithAnEllipsisInsideTheBudget() {
        let over = String(repeating: "a", count: LastTranscriptStore.previewLength + 1)
        let preview = LastTranscriptStore.formatPreview(over)

        XCTAssertEqual(preview.count, LastTranscriptStore.previewLength)
        XCTAssertTrue(preview.hasSuffix("…"))
        XCTAssertEqual(String(preview.dropLast()), String(repeating: "a", count: LastTranscriptStore.previewLength - 1))
    }

    func testFormatPreviewCollapsesMultilineAndRepeatedWhitespace() {
        XCTAssertEqual(
            LastTranscriptStore.formatPreview("  First line\r\n\r\n\tsecond   line. "),
            "First line second line.")
    }

    func testFormatPreviewRendersEmptyForNilOrWhitespace() {
        XCTAssertEqual(LastTranscriptStore.formatPreview(nil), "")
        XCTAssertEqual(LastTranscriptStore.formatPreview("  \r\n "), "")
    }

    // A correction saved from a fix-up UI rewrites the transcript it came from, so that "copy last
    // dictation" hands back the fixed wording. The ring must stay the same size doing it.

    func testUpdateRewritesInPlaceWithoutReorderingOrGrowingTheRing() {
        let store = LastTranscriptStore()
        store.set("one")
        store.set("two")
        store.set("three")

        XCTAssertTrue(store.update(original: "two", updated: "TWO"))
        XCTAssertEqual(store.recent(), ["three", "TWO", "one"])
    }

    func testUpdateOfTheNewestEntryIsReflectedByGet() {
        let store = LastTranscriptStore()
        store.set("teh quick fox")

        XCTAssertTrue(store.update(original: "teh quick fox", updated: "the quick fox"))
        XCTAssertEqual(store.get(), "the quick fox")
        XCTAssertEqual(store.recent().count, 1)
    }

    func testUpdateIgnoresATranscriptThatHasAlreadyBeenEvicted() {
        let store = LastTranscriptStore()
        for i in 0..<(LastTranscriptStore.capacity + 1) {
            store.set("entry \(i)")
        }

        XCTAssertFalse(store.update(original: "entry 0", updated: "corrected"))
        XCTAssertFalse(store.recent().contains("corrected"))
        XCTAssertEqual(store.recent().count, LastTranscriptStore.capacity)
    }

    func testUpdateFollowsTheTranscriptAfterANewDictationShiftsTheRing() {
        let store = LastTranscriptStore()
        store.set("target text")
        store.set("arrived while the popup was open")

        XCTAssertTrue(store.update(original: "target text", updated: "corrected text"))
        XCTAssertEqual(store.recent(), ["arrived while the popup was open", "corrected text"])
    }

    func testUpdateIsANoOpWhenNothingActuallyChanges() {
        let store = LastTranscriptStore()
        store.set("same")

        XCTAssertFalse(store.update(original: "same", updated: "same"))
        XCTAssertFalse(store.update(original: nil, updated: "x"))
        XCTAssertFalse(store.update(original: "same", updated: nil))
        XCTAssertFalse(store.update(original: "same", updated: "   "))
        XCTAssertEqual(store.recent(), ["same"])
    }

    func testUpdateKeepsTheRingIntactWhenTheCorrectionDuplicatesAnotherEntry() {
        let store = LastTranscriptStore()
        store.set("say hello")
        store.set("say helo")

        XCTAssertTrue(store.update(original: "say helo", updated: "say hello"))
        XCTAssertEqual(store.recent(), ["say hello", "say hello"])
    }

    func testUpdateRewritesEverySlotHoldingThatExactTranscript() {
        let store = LastTranscriptStore()
        store.set("okay thanks")
        store.set("something else")
        store.set("okay thanks")

        XCTAssertTrue(store.update(original: "okay thanks", updated: "OK, thanks"))
        XCTAssertEqual(store.recent(), ["OK, thanks", "something else", "OK, thanks"])
    }

    func testSeedFillsAnEmptyRingSoHistoryBackedTranscriptsCanBeRepaired() {
        let store = LastTranscriptStore()
        store.seed(["newest", "older"])

        XCTAssertEqual(store.recent(), ["newest", "older"])
        XCTAssertTrue(store.update(original: "newest", updated: "newest, corrected"))
        XCTAssertEqual(store.get(), "newest, corrected")
    }

    func testSeedNeverDisplacesLiveDictationsOrOverflowsTheRing() {
        let store = LastTranscriptStore()
        store.set("live")
        store.seed(["from history"])

        XCTAssertEqual(store.recent(), ["live"])

        let empty = LastTranscriptStore()
        empty.seed((0..<(LastTranscriptStore.capacity + 3)).map { "h\($0)" })

        XCTAssertEqual(empty.recent().count, LastTranscriptStore.capacity)
    }

    func testUpdateMatchesCaseSensitivelySoACasingFixIsNotMistakenForANoOp() {
        let store = LastTranscriptStore()
        store.set("aspire is great")

        XCTAssertFalse(store.update(original: "Aspire is great", updated: "Aspire is great!"))
        XCTAssertTrue(store.update(original: "aspire is great", updated: "Aspire is great"))
        XCTAssertEqual(store.get(), "Aspire is great")
    }
}
