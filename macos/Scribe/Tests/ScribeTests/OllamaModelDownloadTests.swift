import Foundation
import XCTest
import os

@testable import Scribe

/// Windows' `OllamaModelDownloadTests`, case for case where the platforms agree.
final class OllamaModelDownloadTests: XCTestCase {
    func testTheCatalogIsOllamasSearchPage() {
        XCTAssertEqual(OllamaModelDownload.catalogURL, URL(string: "https://ollama.com/search"))
    }

    func testValidModelNamesAreTrimmedNotRewritten() {
        let cases = [
            (" gemma4:12b ", "gemma4:12b"),
            ("gemma4", "gemma4"),
            ("namespace/model-name:Q4_K_M", "namespace/model-name:Q4_K_M"),
            ("deepseek-r1", "deepseek-r1"),
            ("VicRodger27/Writex", "VicRodger27/Writex"),
            ("VicRodger27/Writex:4b", "VicRodger27/Writex:4b"),
            (" ollama run VicRodger27/Writex:4b ", "VicRodger27/Writex:4b"),
            ("ollama pull deepseek-r1", "deepseek-r1"),
            ("OLLAMA RUN deepseek-r1:8b", "deepseek-r1:8b"),
            ("\tgemma4\n", "gemma4"),
        ]
        for (input, expected) in cases {
            let request = OllamaModelDownload.validate(input)
            XCTAssertEqual(request.model, expected, input)
            XCTAssertNil(request.error, input)
        }
    }

    func testInvalidNamesHaveAnExplicitReason() {
        let cases: [String?] = [
            nil, " ", "gemma4 12b", "https://ollama.com/library/gemma4", "../gemma4", "host:11434/model",
            "gemma4:12b\nanother", "gemma4:cloud", "gemma4:31b-CLOUD", "ollama run gemma4:cloud",
            "ollama pull gemma4:31b-cloud", "ollama run", "ollama run VicRodger27/Writex:4b --verbose",
            "ollama run VicRodger27/Writex:4b; shutdown", "ollama run deepseek-r1\nollama run other-model",
            "a/b/c", "gemma4:12b:q4", "gemma4:", ":12b", "/gemma4", "publisher/", "gémma4", "gemma4\u{FF1A}12b",
            "-gemma4", "gemma4:.q4", "ollamarun gemma4",
        ]
        for input in cases {
            let request = OllamaModelDownload.validate(input)
            XCTAssertNil(request.model, input ?? "nil")
            XCTAssertFalse(
                (request.error ?? "").trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, input ?? "nil")
        }
    }

    func testACloudModelIsRefusedWithItsOwnReason() {
        XCTAssertEqual(
            OllamaModelDownload.validate("gemma4:31b-cloud").error,
            "Choose a model that runs on this Mac. Ollama cloud models aren't supported here.")
        XCTAssertNotNil(OllamaModelDownload.validate("cloud").model, "Only a tag that ends in cloud is a cloud model")
        XCTAssertNotNil(OllamaModelDownload.validate("cloud/gemma4").model)
    }

    func testAModelNameIsBounded() {
        let long = String(repeating: "a", count: 201)
        let limit = String(repeating: "a", count: 200)
        XCTAssertNil(OllamaModelDownload.validate(long).model)
        XCTAssertNotNil(OllamaModelDownload.validate(limit).model)
        XCTAssertNil(OllamaModelDownload.validate("ollama run " + long).model)
        XCTAssertNotNil(OllamaModelDownload.validate("ollama run " + limit).model)
    }

    func testProgressDoesNotCallAFileWithNoBytesDownloadedOneMegabyte() {
        let message = OllamaModelDownload.describe(
            OllamaDownloadProgress(stage: .downloading, completedBytes: 0, totalBytes: 1024 * 1024 * 1024, percent: 0))
        XCTAssertTrue(message.contains("0 MB"), message)
        XCTAssertTrue(message.contains("1.0 GB"), message)
        XCTAssertTrue(message.contains("(0%)"), message)
        XCTAssertEqual(
            OllamaModelDownload.describe(
                OllamaDownloadProgress(stage: .downloading, completedBytes: 512, totalBytes: 1024, percent: 50)),
            "Downloading a model file: 1 MB of 1 MB (50%).")
        XCTAssertEqual(
            OllamaModelDownload.describe(OllamaDownloadProgress(stage: .downloading)),
            "Ollama is preparing the download...",
            "A file without a size reads as preparing")
        XCTAssertEqual(OllamaModelDownload.describe(OllamaDownloadProgress(stage: .completed)), "Download complete.")
    }

    func testErrorNoticesNeverQuoteTheServerOrTheError() {
        struct PrivateFailure: LocalizedError {
            var errorDescription: String? { "private host or response" }
        }
        let failures: [any Error] = [
            PrivateFailure(),
            URLError(.cannotConnectToHost, userInfo: [NSLocalizedDescriptionKey: "private host"]),
            NSError(domain: "private", code: 1, userInfo: [NSLocalizedDescriptionKey: "private downloaded data"]),
            CancellationError(),
        ]
        let notices =
            failures.map(OllamaModelDownload.failure)
            + [
                OllamaDownloadError.invalidName, .notOllama, .alreadyDownloading, .unreachable, .timedOut,
                .httpStatus(404), .httpStatus(401), .httpStatus(500), .serverError(missingManifest: true),
                .serverError(missingManifest: false), .unreadableAnswer, .notConfirmed, .interrupted,
            ].map(OllamaModelDownload.failure)
        for notice in notices {
            XCTAssertFalse(notice.contains("private"), notice)
            XCTAssertFalse(notice.isEmpty)
        }
        XCTAssertEqual(
            OllamaModelDownload.failure(PrivateFailure()),
            "Ollama couldn't finish the download. Check that it's open and you have enough disk space, then try again.",
            "Only a classified failure gets a specific notice")
    }

    func testACompletionWithoutAReachedModelListExplainsTheRefreshFailure() {
        for reading in [LocalServerState.notRunning, .failed, .needsKey, nil] {
            XCTAssertEqual(
                OllamaModelDownload.completionProblem(reading, model: "gemma4"),
                "Downloaded, but Scribe couldn't refresh Ollama's model list. Choose Check again.")
        }
    }

    func testACompletionChecksTheReadingAndTheExistingModelNameEquivalence() {
        let reading = LocalServerState(
            reach: .reached, models: [LocalServerModel("gemma4:latest", "Gemma", 1)], loaded: [], failureDetail: nil)

        XCTAssertNil(OllamaModelDownload.completionProblem(reading, model: "gemma4"))
        XCTAssertNil(OllamaModelDownload.completionProblem(reading, model: "GEMMA4:latest"))
        XCTAssertEqual(
            OllamaModelDownload.completionProblem(reading, model: "other-model"),
            "Downloaded, but Ollama doesn't list it as a model for AI cleanup. Choose a text model from the catalog.")
    }

    func testTheQuestionNamesTheModelAndSaysWhatIsSaved() {
        XCTAssertEqual(
            OllamaModelDownload.switchQuestion("gemma4:12b"),
            "Use gemma4:12b for AI cleanup? This saves the model choice immediately. Other unsaved changes stay "
                + "unsaved.")
    }
}

@MainActor
final class OllamaModelDownloadModelTests: XCTestCase {
    private let listing = LocalServerState(
        reach: .reached,
        models: [LocalServerModel("gemma4:12b", "gemma4:12b", 1), LocalServerModel("qwen2.5:3b", "qwen2.5:3b", 1)],
        loaded: [],
        failureDetail: nil)

    private func makeModel(_ fake: FakeDownload, center: NotificationCenter = NotificationCenter())
        -> OllamaModelDownloadModel
    {
        OllamaModelDownloadModel(download: fake.download, center: center)
    }

    private func refresh(_ reading: LocalServerState?, count: OSAllocatedUnfairLockBox<Int>? = nil)
        -> OllamaModelDownloadModel.Refresh
    {
        { _ in
            count?.mutate { $0 += 1 }
            return reading
        }
    }

    /// Waits, at most five seconds, until the model's download has ended or is waiting for its question.
    private func settle(_ model: OllamaModelDownloadModel) async {
        var waited = 0
        while model.isDownloading, waited < 5000 {
            waited += 1
            try? await Task.sleep(nanoseconds: 1_000_000)
        }
    }

    func testAFinishedDownloadRefreshesTheListAndAsksBeforeChoosingTheModel() async {
        let fake = FakeDownload(steps: [
            OllamaDownloadProgress(stage: .preparing),
            OllamaDownloadProgress(stage: .downloading, completedBytes: 40, totalBytes: 100, percent: 40),
            OllamaDownloadProgress(stage: .completed),
        ])
        let model = makeModel(fake)
        let refreshes = OSAllocatedUnfairLockBox(0)
        var applied: [String] = []
        model.modelName = " ollama run gemma4:12b "

        model.start(endpoint: LocalAiServer.ollamaAddress, refresh: refresh(listing, count: refreshes))
        XCTAssertTrue(model.isDownloading)
        XCTAssertEqual(model.message, "Ollama is preparing the download...")
        await settle(model)

        XCTAssertEqual(fake.asked, ["gemma4:12b at http://localhost:11434/v1"])
        XCTAssertEqual(refreshes.value, 1)
        XCTAssertFalse(model.isDownloading)
        XCTAssertNil(model.percent)
        XCTAssertEqual(model.pendingChoice, "gemma4:12b")
        XCTAssertEqual(model.message, "Download complete.")
        XCTAssertTrue(applied.isEmpty, "Nothing is chosen before the question is answered")

        model.confirm { applied.append($0) }

        XCTAssertEqual(applied, ["gemma4:12b"])
        XCTAssertNil(model.pendingChoice)
        XCTAssertEqual(
            model.message,
            "Model choice saved. Check the AI cleanup status above to see when it's ready. Other unsaved changes stay "
                + "unsaved.")
    }

    func testKeepCurrentModelChoosesNothing() async {
        let model = makeModel(FakeDownload())
        var applied: [String] = []
        model.modelName = "gemma4:12b"

        model.start(endpoint: LocalAiServer.ollamaAddress, refresh: refresh(listing))
        await settle(model)
        model.decline()
        model.confirm { applied.append($0) }

        XCTAssertTrue(applied.isEmpty)
        XCTAssertNil(model.pendingChoice)
        XCTAssertEqual(model.message, "Downloaded. Your model choice hasn't changed.")
    }

    func testADownloadOllamaDoesNotListIsNotOffered() async {
        let model = makeModel(FakeDownload())
        model.modelName = "nomic-embed-text"

        model.start(endpoint: LocalAiServer.ollamaAddress, refresh: refresh(listing))
        await settle(model)

        XCTAssertNil(model.pendingChoice)
        XCTAssertEqual(
            model.message,
            "Downloaded, but Ollama doesn't list it as a model for AI cleanup. Choose a text model from the catalog.")
    }

    func testARefreshThatFailsIsNotOffered() async {
        let model = makeModel(FakeDownload())
        model.modelName = "gemma4:12b"

        model.start(endpoint: LocalAiServer.ollamaAddress, refresh: refresh(.failed))
        await settle(model)

        XCTAssertNil(model.pendingChoice)
        XCTAssertEqual(
            model.message, "Downloaded, but Scribe couldn't refresh Ollama's model list. Choose Check again.")
    }

    func testANameTheFormRefusesStartsNothing() {
        let fake = FakeDownload()
        let model = makeModel(fake)
        model.modelName = "https://ollama.com/library/gemma4"

        model.start(endpoint: LocalAiServer.ollamaAddress, refresh: refresh(listing))

        XCTAssertFalse(model.isDownloading)
        XCTAssertTrue(fake.asked.isEmpty)
        XCTAssertEqual(
            model.message,
            "Enter a model name or its ollama run command, without a web address, extra options or instructions.")
    }

    func testAFailedDownloadSaysWhyAndOffersNothing() async {
        let model = makeModel(FakeDownload(ending: .failure(.serverError(missingManifest: true))))
        model.modelName = "VicRodger27/Writex"

        model.start(endpoint: LocalAiServer.ollamaAddress, refresh: refresh(listing))
        await settle(model)

        XCTAssertFalse(model.isDownloading)
        XCTAssertNil(model.pendingChoice)
        XCTAssertEqual(model.message, OllamaModelDownload.missingModel)
    }

    func testCancelStopsTheDownloadAndSaysTheDownloadedPartsAreKept() async {
        let gate = SettingsTestGate()
        let fake = FakeDownload(steps: [OllamaDownloadProgress(stage: .preparing)], ending: .held(gate))
        let model = makeModel(fake)
        model.modelName = "gemma4:12b"

        model.start(endpoint: LocalAiServer.ollamaAddress, refresh: refresh(listing))
        await gate.waitForArrival()
        model.cancel()
        await gate.open()
        await settle(model)

        XCTAssertFalse(model.isDownloading)
        XCTAssertNil(model.pendingChoice)
        XCTAssertEqual(
            model.message,
            "Download canceled. Ollama keeps the downloaded parts so you can resume by choosing Download again.")
    }

    func testOneDownloadAtATime() async {
        let gate = SettingsTestGate()
        let fake = FakeDownload(ending: .held(gate))
        let model = makeModel(fake)
        model.modelName = "gemma4:12b"

        model.start(endpoint: LocalAiServer.ollamaAddress, refresh: refresh(listing))
        await gate.waitForArrival()
        model.modelName = "qwen2.5:3b"
        model.start(endpoint: LocalAiServer.ollamaAddress, refresh: refresh(listing))

        XCTAssertEqual(fake.asked, ["gemma4:12b at http://localhost:11434/v1"])
        model.cancel()
        await gate.open()
        await settle(model)
    }

    func testStoppingWhenTheFormGoesAwayIgnoresLateProgressAndAsksNothing() async {
        let gate = SettingsTestGate()
        let late = OllamaDownloadProgress(stage: .downloading, completedBytes: 90, totalBytes: 100, percent: 90)
        let fake = FakeDownload(ending: .held(gate), afterGate: [late, OllamaDownloadProgress(stage: .completed)])
        let model = makeModel(fake)
        var applied: [String] = []
        model.modelName = "gemma4:12b"

        model.start(endpoint: LocalAiServer.ollamaAddress, refresh: refresh(listing))
        await gate.waitForArrival()
        model.stop()
        XCTAssertFalse(model.isDownloading)
        await gate.open()
        await settle(model)
        try? await Task.sleep(nanoseconds: 20_000_000)

        XCTAssertNil(model.percent)
        XCTAssertNil(model.pendingChoice)
        XCTAssertNotEqual(model.message, OllamaModelDownload.describe(late))
        XCTAssertEqual(
            model.message,
            "Download canceled. Ollama keeps the downloaded parts so you can resume by choosing Download again.")
        model.confirm { applied.append($0) }
        XCTAssertTrue(applied.isEmpty)
    }

    func testClosingSettingsStopsTheDownloadAndDropsAWaitingQuestion() async {
        let center = NotificationCenter()
        let model = makeModel(FakeDownload(), center: center)
        model.modelName = "gemma4:12b"
        model.start(endpoint: LocalAiServer.ollamaAddress, refresh: refresh(listing))
        await settle(model)
        XCTAssertEqual(model.pendingChoice, "gemma4:12b")

        center.post(name: SettingsWindowController.willCloseNotification, object: nil)

        XCTAssertNil(model.pendingChoice)
        XCTAssertEqual(model.message, "Downloaded. Your model choice hasn't changed.")
    }

    func testUseModelStoresOnlyTheOllamaChoiceAndKeepsAnotherServiceRemembered() async {
        let backing = CleanupSettingsBackingFake()
        backing.stored.isEnabled = true
        backing.stored.providerKind = .openAICompatible
        backing.stored.selectedLocalApp = .ollama
        backing.stored.openAIBaseURL = "http://127.0.0.1:11434/v1/"
        backing.stored.openAIModel = "qwen2.5:3b"
        backing.stored.ollamaModel = "qwen2.5:3b"
        backing.stored.ollamaContextTokens = 32768
        backing.stored.otherServiceBaseURL = "https://example.invalid/v1"
        backing.stored.otherServiceModel = "remembered-model"
        backing.stored.otherServiceApiStyle = .responses
        backing.stored.writingStyle = "saved writing style"
        let before = backing.stored
        let settings = CleanupSettingsModel(access: backing.access, drafts: backing.drafts, center: backing.center)
        backing.drafts.openAIApiKey = "unsaved draft key"
        let model = makeModel(FakeDownload())
        model.modelName = "gemma4:12b"

        model.start(endpoint: settings.localAppEndpoint(for: .ollama), refresh: refresh(listing))
        await settle(model)
        XCTAssertEqual(backing.stored, before, "Downloading chooses nothing")
        model.confirm { settings.setLocalModel($0, for: .ollama) }

        var expected = before
        expected.ollamaModel = "gemma4:12b"
        expected.openAIModel = "gemma4:12b"
        XCTAssertEqual(backing.stored, expected)
        XCTAssertEqual(backing.stored.openAIBaseURL, "http://127.0.0.1:11434/v1/", "The saved address stays as saved")
        XCTAssertEqual(backing.drafts.openAIApiKey, "unsaved draft key", "Other unsaved changes stay unsaved")
        XCTAssertEqual(settings.localModel(for: .ollama), "gemma4:12b")
        XCTAssertEqual(settings.localAppChoice, .ollama)
    }
}

/// A download that records what it was asked for and plays `steps`, then finishes or fails as told.
private final class FakeDownload: Sendable {
    enum Ending: Sendable {
        case success
        case failure(OllamaDownloadError)
        /// Waits on the gate, plays `afterGate`, then ends as a real download whose task was cancelled ends.
        case held(SettingsTestGate)
    }

    private let requests = OSAllocatedUnfairLockBox<[String]>([])
    let steps: [OllamaDownloadProgress]
    let afterGate: [OllamaDownloadProgress]
    let ending: Ending

    init(
        steps: [OllamaDownloadProgress] = [], ending: Ending = .success, afterGate: [OllamaDownloadProgress] = []
    ) {
        self.steps = steps
        self.ending = ending
        self.afterGate = afterGate
    }

    var asked: [String] { requests.value }

    var download: OllamaModelDownloadModel.Download {
        { model, endpoint, progress in
            self.requests.mutate { $0.append("\(model) at \(endpoint)") }
            for step in self.steps {
                await progress(step)
            }
            switch self.ending {
            case .success:
                return
            case .failure(let error):
                throw error
            case .held(let gate):
                await gate.pass()
                for step in self.afterGate {
                    await progress(step)
                }
                try Task.checkCancellation()
            }
        }
    }
}

/// A lock-protected value for the closures these tests hand the model, which may run on any thread.
final class OSAllocatedUnfairLockBox<Value: Sendable>: Sendable {
    private let lock: OSAllocatedUnfairLock<Value>

    init(_ value: Value) {
        lock = OSAllocatedUnfairLock(initialState: value)
    }

    var value: Value {
        lock.withLock { $0 }
    }

    func mutate(_ change: @Sendable (inout Value) -> Void) {
        lock.withLock { change(&$0) }
    }
}
