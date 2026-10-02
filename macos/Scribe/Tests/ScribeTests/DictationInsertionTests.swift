import XCTest

@testable import Scribe

@MainActor
final class DictationInsertionTests: XCTestCase {
    func testTextToTypeAddsOneSpaceWhenTheTextDoesNotAlreadyEndInWhiteSpace() {
        XCTAssertEqual(DictationInsertion.textToType("Hello world.", addSpaceAfterDictation: true), "Hello world. ")
        XCTAssertEqual(DictationInsertion.textToType("a", addSpaceAfterDictation: true), "a ")
    }

    func testTextToTypeLeavesAnExistingTrailingWhiteSpaceAlone() {
        for text in ["ends in a space ", "ends in a tab\t", "ends in a line break\n"] {
            XCTAssertEqual(DictationInsertion.textToType(text, addSpaceAfterDictation: true), text)
        }
    }

    func testTextToTypeLeavesEmptyTextAndTheOffSettingAlone() {
        XCTAssertEqual(DictationInsertion.textToType("", addSpaceAfterDictation: true), "")
        XCTAssertEqual(DictationInsertion.textToType("Hello.", addSpaceAfterDictation: false), "Hello.")
    }

    func testInsertKeepsTheRecordedTextAndHandsOnlyTheTypedTextToTheTarget() async {
        let recovery = LastTranscriptStore()
        var typed: [String] = []

        let result = await DictationInsertion.insert(
            "Hello world.",
            addSpaceAfterDictation: true,
            recovery: recovery
        ) { text in
            typed.append(text)
            return InjectionResult(delivery: .typed)
        }

        XCTAssertEqual(result.recorded, "Hello world.")
        XCTAssertEqual(result.typed, "Hello world. ")
        XCTAssertTrue(result.spaceAdded)
        XCTAssertEqual(typed, ["Hello world. "])
        XCTAssertEqual(result.recoveryGeneration, recovery.generation)
        XCTAssertEqual(recovery.recent(), ["Hello world."])
    }

    func testInsertKeepsTheRecordedTextEvenWhenCancellationStopsDelivery() async {
        let recovery = LastTranscriptStore()
        let task = Task { @MainActor in
            await DictationInsertion.insert(
                "Cancelled text",
                addSpaceAfterDictation: true,
                recovery: recovery
            ) { _ in
                XCTFail("delivery should not have run")
                return InjectionResult(delivery: .typed)
            }
        }

        task.cancel()
        let result = await task.value

        XCTAssertEqual(result.injection.delivery, .cancelled)
        XCTAssertEqual(result.recorded, "Cancelled text")
        XCTAssertEqual(result.typed, "Cancelled text ")
        XCTAssertEqual(result.recoveryGeneration, recovery.generation)
        XCTAssertEqual(recovery.recent(), ["Cancelled text"])
    }
}
