import XCTest

@testable import Scribe

final class LocalAppSetupTests: XCTestCase {
    private func model(_ id: String, name: String? = nil) -> LocalServerModel {
        LocalServerModel(id, name ?? id, 1_000_000_000)
    }

    func testTheChosenModelStaysChosenSpelledAsItWasSaved() {
        var choices = LocalAppSetup.modelChoices([model("gemma4:e2b"), model("llama3.2:3b")], "llama3.2:3b")
        XCTAssertEqual(choices.selected, "llama3.2:3b")
        XCTAssertEqual(choices.models.map(\.id), ["gemma4:e2b", "llama3.2:3b"])

        choices = LocalAppSetup.modelChoices([model("llama3.2:latest"), model("gemma4:e2b")], " llama3.2 ")
        XCTAssertEqual(choices.selected, "llama3.2")
        XCTAssertEqual(choices.models.map(\.id), ["llama3.2", "gemma4:e2b"])
        XCTAssertEqual(choices.models[0].displayName, "llama3.2")

        choices = LocalAppSetup.modelChoices([model("google/gemma-4-e2b", name: "Gemma 4 E2B")], "Google/Gemma-4-E2B")
        XCTAssertEqual(choices.selected, "Google/Gemma-4-E2B")
        XCTAssertEqual(choices.models.single?.displayName, "Gemma 4 E2B")
    }

    func testAChosenModelTheAppDoesNotListIsKeptRatherThanSwapped() {
        let choices = LocalAppSetup.modelChoices(
            [model("google/gemma-4-e2b")],
            "my-cleanup-model",
            roomyGraphicsCard: true)
        XCTAssertEqual(choices.selected, "my-cleanup-model")
        XCTAssertEqual(choices.models.map(\.id), ["my-cleanup-model", "google/gemma-4-e2b"])

        let beforeRead = LocalAppSetup.modelChoices([], "gemma4:e2b")
        XCTAssertEqual(beforeRead.selected, "gemma4:e2b")
        XCTAssertEqual(beforeRead.models.count, 1)

        let empty = LocalAppSetup.modelChoices([], nil)
        XCTAssertNil(empty.selected)
        XCTAssertTrue(empty.models.isEmpty)
    }

    func testWithNothingChosenTheBenchmarksOrderPicksInEitherAppsSpelling() {
        XCTAssertEqual(
            LocalAppSetup.pickModel([model("llama3.2:3b"), model("gemma4:e4b"), model("gemma4:e2b")]),
            "gemma4:e2b")
        XCTAssertEqual(LocalAppSetup.pickModel([model("llama3.2:3b"), model("gemma4:e4b")]), "gemma4:e4b")
        XCTAssertEqual(
            LocalAppSetup.pickModel([model("ibm/granite-4-micro"), model("qwen/qwen3-4b-2507")]),
            "qwen/qwen3-4b-2507")
        XCTAssertEqual(
            LocalAppSetup.pickModel([model("ibm/granite-4-micro"), model("google/gemma-4-e2b")]),
            "google/gemma-4-e2b")
        XCTAssertEqual(
            LocalAppSetup.modelChoices([model("llama3.2:3b"), model("gemma4:e2b")], nil).selected,
            "gemma4:e2b")
    }

    func testARoomyGraphicsCardPutsTheLargerGemmaFirst() {
        let models = [model("gemma4:e2b"), model("gemma4:e4b"), model("qwen3:4b-instruct")]
        XCTAssertEqual(LocalAppSetup.pickModel(models, roomyGraphicsCard: true), "gemma4:e4b")
        XCTAssertEqual(LocalAppSetup.pickModel(models, roomyGraphicsCard: false), "gemma4:e2b")
    }

    func testTheStatusLineSaysWhatToDoWhenTheAppIsNotThereOrHasNoModels() {
        let checking = LocalAppSetup.describe(.ollama, nil, "gemma4:e2b", idleMinutes: 10)
        XCTAssertEqual(checking.kind, .busy)
        XCTAssertNil(checking.primary)

        let closed = LocalAppSetup.describe(.lmStudio, .notRunning, "gemma4:e2b", idleMinutes: 10)
        XCTAssertEqual(closed.text, "Scribe can't reach LM Studio. Open LM Studio, then choose Check again.")
        XCTAssertEqual(closed.primary?.id, .checkAgain)

        let failed = LocalAppSetup.describe(.ollama, .failed, "gemma4:e2b", idleMinutes: 10)
        XCTAssertEqual(failed.primary?.id, .checkAgain)

        let empty = LocalAppSetup.describe(
            .ollama,
            LocalServerState(reach: .reached, models: [], loaded: [], failureDetail: nil),
            nil,
            idleMinutes: 10)
        XCTAssertEqual(empty.text, "Ollama has no models yet. Download one in Ollama, then choose Check again.")

        let needsKey = LocalAppSetup.describe(.lmStudio, .needsKey, "google/gemma-4-e2b", idleMinutes: 10)
        XCTAssertEqual(needsKey.kind, .warning)
        XCTAssertEqual(
            needsKey.text,
            "LM Studio asks for an API key. To use one, choose Another AI service and enter the key there.")
    }

    func testALoadedModelShowsItsMemoryAndFreeMemory() {
        let state = LocalServerState(
            reach: .reached,
            models: [model("gemma4:e2b")],
            loaded: [LocalServerLoadedModel("gemma4:e2b", 1_706_000_000)],
            failureDetail: nil)

        let row = LocalAppSetup.describe(.ollama, state, "gemma4:e2b", idleMinutes: 10)
        XCTAssertEqual(row.kind, .success)
        XCTAssertEqual(
            row.text,
            "gemma4:e2b is using 1.6 GB of memory. Scribe asks Ollama to free it after 10 minutes without a dictation. "
                + "Free memory unloads it from Ollama, for other apps too.")
        XCTAssertEqual(row.primary?.id, .unload)
        XCTAssertEqual(row.primary?.text, LocalAppSetup.freeMemoryAction)
    }

    func testAModelNotInMemoryLoadsWhenTheUserDictates() {
        let state = LocalServerState(
            reach: .reached,
            models: [model("gemma4:e2b")],
            loaded: [],
            failureDetail: nil)

        let row = LocalAppSetup.describe(.ollama, state, "gemma4:e2b", idleMinutes: 10)
        XCTAssertEqual(row.kind, .info)
        XCTAssertEqual(row.text, "gemma4:e2b isn't using memory now. It loads when you dictate.")
        XCTAssertNil(row.primary)
    }

    func testFormatSizeReadsAsTaskManagerCountsIt() {
        XCTAssertEqual(LocalAppSetup.formatSize(1_706_000_000), "1.6 GB")
        XCTAssertEqual(LocalAppSetup.formatSize(1_073_741_824), "1.0 GB")
        XCTAssertEqual(LocalAppSetup.formatSize(891_289_600), "850 MB")
        XCTAssertEqual(LocalAppSetup.formatSize(10), "1 MB")
    }
}

extension Collection {
    fileprivate var single: Element? {
        count == 1 ? first : nil
    }
}
