import XCTest

@testable import Scribe

final class CustomAPIStyleTests: XCTestCase {
    func testAnAddressNamesTheAPIItsPathEndsIn() {
        XCTAssertEqual(
            CustomServiceAddress.namedStyle("https://ai.example.invalid/v1/chat/completions"),
            .chatCompletions)
        XCTAssertEqual(CustomServiceAddress.namedStyle("https://ai.example.invalid/v1/responses"), .responses)
        XCTAssertNil(CustomServiceAddress.namedStyle("https://ai.example.invalid/v1"))
    }

    func testRequestsAreBuiltOnTheAddressWithoutTheAPIsPath() {
        XCTAssertEqual(
            CustomServiceAddress.baseURL(URL(string: "https://openrouter.ai/api/v1/chat/completions")!).absoluteString,
            "https://openrouter.ai/api/v1")
        XCTAssertEqual(
            CustomServiceAddress.baseURL(URL(string: "https://ai.example.invalid/v1/responses?api-version=1")!)
                .absoluteString,
            "https://ai.example.invalid/v1?api-version=1")
    }

    func testOnlyTheOlderCompletionsPathIsTheOlderAPI() {
        XCTAssertTrue(CustomServiceAddress.namesOldCompletions("https://ai.example.invalid/v1/completions"))
        XCTAssertFalse(CustomServiceAddress.namesOldCompletions("https://ai.example.invalid/v1/chat/completions"))
        XCTAssertFalse(CustomServiceAddress.namesOldCompletions("https://ai.example.invalid/v1"))
    }

    func testTheAddressDecidesFirstThenTheChoice() {
        XCTAssertEqual(
            CustomServiceAddress.effective("https://ai.example.invalid/v1", chosen: .responses),
            .responses)
        XCTAssertEqual(
            CustomServiceAddress.effective("https://ai.example.invalid/v1/responses", chosen: .chatCompletions),
            .responses)
        XCTAssertEqual(
            CustomServiceAddress.effective(LocalAiServer.lmStudioAddress, chosen: .responses),
            .chatCompletions)
    }

    func testSettingsExplainWhyTheAPIIsWhatItIs() {
        XCTAssertTrue(CustomAPIStyleText.canChoose("https://ai.example.invalid/v1"))
        XCTAssertFalse(CustomAPIStyleText.canChoose(LocalAiServer.lmStudioAddress))
        XCTAssertEqual(CustomAPIStyleText.name(of: .responses), "Responses")
        XCTAssertTrue(CustomAPIStyleText.hint("https://ai.example.invalid/v1/responses").contains("/responses"))
        XCTAssertTrue(CustomAPIStyleText.hint(LocalAiServer.ollamaAddress).contains("Ollama"))
    }
}
