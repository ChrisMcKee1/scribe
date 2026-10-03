import Foundation
import XCTest

@testable import Scribe

final class FoundryLocalResidencyTests: XCTestCase {
    func testCachedRealFoundryModelCanStartAndCompleteWithoutADownload() async throws {
        guard ProcessInfo.processInfo.environment["SCRIBE_REAL_FOUNDRY_COLD"] == "1" else {
            throw XCTSkip("Opt in with qwen2.5-0.5b already cached in Foundry Local.")
        }
        let source = FoundryLocalResidencySource.live()
        let before = try await source.isLoaded("qwen2.5-0.5b")
        let provider = FoundryLocalCleanupProvider(modelAlias: "qwen2.5-0.5b")
        let prepared = try await provider.prepareLocalModel(isCurrent: { true }, onStarting: {})
        XCTAssertEqual(prepared, before ? .resident : .started)
        let held = try await source.isLoaded("qwen2.5-0.5b")
        XCTAssertTrue(held)
        let answer = try await provider.clean(
            CleanupRequest(
                transcript: "Please send the notes tomorrow.",
                writingStylePrompt: "Return only the sentence with punctuation corrected.", maxOutputTokens: 64))
        XCTAssertFalse(answer.cleanedText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
    }

    func testOnlyAMatchingCachedChatVariantCanBeLoaded() throws {
        let valid = #"{"model":{"alias":"qwen","id":"qwen-gpu:4","type":"Chat","cached":true}}"#
        XCTAssertEqual(try FoundryLocalResidencySource.cachedVariant(Data(valid.utf8), model: "qwen"), "qwen-gpu:4")
        XCTAssertEqual(
            try FoundryLocalResidencySource.cachedVariant(Data(valid.utf8), model: "qwen-gpu:4"), "qwen-gpu:4")
        for json in [
            #"{"model":{"alias":"other","id":"qwen-gpu:4","type":"Chat","cached":true}}"#,
            #"{"model":{"alias":"qwen","id":"qwen-gpu:4","type":"Speech","cached":true}}"#,
            #"{"model":{"alias":"qwen","id":"qwen-gpu:4","type":"Chat","cached":false}}"#,
            #"{"model":{"alias":"qwen","id":"qwen-gpu:4","type":"Chat"}}"#,
            #"{"model":{"alias":"qwen","id":"--help","type":"Chat","cached":true}}"#,
            #"{"model":{"alias":"qwen","type":"Chat","cached":true}}"#,
        ] {
            XCTAssertThrowsError(try FoundryLocalResidencySource.cachedVariant(Data(json.utf8), model: "qwen"))
        }
    }

    func testLoadedListRequiresTheRightChatModelAndMalformedListsFail() throws {
        let list = #"{"models":[{"alias":"qwen","id":"qwen-gpu:4","type":"Chat"}]}"#
        XCTAssertTrue(try FoundryLocalResidencySource.containsLoadedModel(Data(list.utf8), model: "qwen"))
        XCTAssertTrue(try FoundryLocalResidencySource.containsLoadedModel(Data(list.utf8), model: "qwen-gpu:4"))
        XCTAssertFalse(try FoundryLocalResidencySource.containsLoadedModel(Data(list.utf8), model: "another"))
        XCTAssertFalse(try FoundryLocalResidencySource.containsLoadedModel(Data(#"{"models":[]}"#.utf8), model: "qwen"))
        XCTAssertThrowsError(try FoundryLocalResidencySource.containsLoadedModel(Data("{}".utf8), model: "qwen"))
    }

    func testLiveLoadUsesTheExactCachedVariantAndNeverADownloadCommand() async throws {
        let directory = try makeTemporaryDirectory(label: "foundry-residency")
        let arguments = directory.appendingPathComponent("arguments")
        let script = try makeScript(
            named: "foundry",
            body: """
                printf '%s\\n' "$@" >> '\(arguments.path(percentEncoded: false))'
                if [ "$2" = "info" ]; then
                  echo '{"model":{"alias":"qwen","id":"qwen-gpu:4","type":"Chat","cached":true}}'
                elif [ "$2" = "load" ]; then
                  echo '{"success":true}'
                elif [ "$2" = "list" ]; then
                  echo '{"models":[{"alias":"qwen","id":"qwen-gpu:4","type":"Chat"}]}'
                else
                  exit 1
                fi
                """)
        let source = FoundryLocalResidencySource.live(environment: ["SCRIBE_FOUNDRY_CLI": script.path])
        try await source.loadCached("qwen")
        let loaded = try await source.isLoaded("qwen")
        XCTAssertTrue(loaded)
        XCTAssertEqual(
            try String(contentsOf: arguments, encoding: .utf8).split(separator: "\n").map(String.init),
            [
                "model", "info", "qwen", "-o", "json", "model", "load", "qwen-gpu:4", "-o", "json",
                "model", "list", "--loaded", "-o", "json",
            ])
    }

    func testAnUncachedModelNeverReachesLoad() async throws {
        let directory = try makeTemporaryDirectory(label: "foundry-uncached")
        let arguments = directory.appendingPathComponent("arguments")
        let script = try makeScript(
            named: "foundry",
            body: """
                printf '%s\\n' "$@" >> '\(arguments.path(percentEncoded: false))'
                echo '{"model":{"alias":"qwen","id":"qwen-gpu:4","type":"Chat","cached":false}}'
                """)
        let source = FoundryLocalResidencySource.live(environment: ["SCRIBE_FOUNDRY_CLI": script.path])
        do {
            try await source.loadCached("qwen")
            XCTFail("An absent variant must not be loaded or downloaded")
        } catch {
            XCTAssertTrue(error is LocalModelReadinessError)
        }
        XCTAssertEqual(
            try String(contentsOf: arguments, encoding: .utf8).split(separator: "\n").map(String.init),
            ["model", "info", "qwen", "-o", "json"])
    }

    func testReadinessStartsOnceThenUsesTheResidentModel() async throws {
        let loaded = StubSwitch()
        let starting = StubSwitch()
        let provider = provider(
            residency: .init(isLoaded: { _ in loaded.isOn }, loadCached: { _ in loaded.turnOn() }))
        let first = try await provider.prepareLocalModel(isCurrent: { true }, onStarting: { starting.turnOn() })
        XCTAssertEqual(first, .started)
        XCTAssertTrue(starting.isOn)
        let second = try await provider.prepareLocalModel(isCurrent: { true }, onStarting: { XCTFail("Already held") })
        XCTAssertEqual(second, .resident)
    }

    func testAChangedConfigurationNeverLoadsAndAnUnconfirmedLoadNeverSendsText() async throws {
        let loading = StubSwitch()
        let log = RequestLog()
        let provider = provider(
            residency: .init(isLoaded: { _ in false }, loadCached: { _ in loading.turnOn() }), log: log)
        let result = try await provider.prepareLocalModel(isCurrent: { false }, onStarting: { XCTFail("Changed") })
        XCTAssertEqual(result, .configurationChanged)
        XCTAssertFalse(loading.isOn)
        do {
            _ = try await provider.clean(CleanupRequest(transcript: "never sent", maxOutputTokens: 64))
            XCTFail("Load must be confirmed")
        } catch {
            XCTAssertTrue(error is LocalModelReadinessError)
        }
        XCTAssertTrue(loading.isOn)
        XCTAssertTrue(log.all.isEmpty)
    }

    func testDirectCompletionAlsoLoadsAndConfirmsBeforeSending() async throws {
        let loaded = StubSwitch()
        let log = RequestLog()
        let provider = provider(
            residency: .init(isLoaded: { _ in loaded.isOn }, loadCached: { _ in loaded.turnOn() }), log: log)
        _ = try await provider.clean(
            CleanupRequest(transcript: "hello", writingStylePrompt: "Fix spelling.", maxOutputTokens: 64))
        XCTAssertTrue(loaded.isOn)
        XCTAssertEqual(log.all.count, 1)
    }

    func testCacheReadinessRejectsChangedBackSettingsBeforeStartingTheModel() async throws {
        let store = makeCleanupStore().store
        store.isEnabled = true
        let loaded = StubSwitch()
        var factory = CleanupProviderFactory.testing(
            session: makeStubSession { request in
                StubReply.completion(request, "never")
            })
        factory.foundryLocalResidency = .init(
            isLoaded: { _ in
                store.isEnabled = false
                store.isEnabled = true
                return false
            },
            loadCached: { _ in loaded.turnOn() })
        let cache = CleanupProviderCache(store: store, environment: [:], factory: factory)
        let result = await cache.prepareLocalModel(isCurrent: { true }, onStarting: { XCTFail("Stale readiness") })
        XCTAssertFalse(result.permitsCleanup)
        XCTAssertFalse(loaded.isOn)
    }

    func testCancelledReadinessLoadsNothing() async throws {
        let loaded = StubSwitch()
        let provider = provider(
            residency: .init(
                isLoaded: { _ in throw CancellationError() }, loadCached: { _ in loaded.turnOn() }))
        do {
            _ = try await provider.prepareLocalModel(isCurrent: { true }, onStarting: { XCTFail("Cancelled") })
            XCTFail("Cancellation must propagate")
        } catch {
            XCTAssertTrue(error is CancellationError)
        }
        XCTAssertFalse(loaded.isOn)
    }

    private func provider(residency: FoundryLocalResidencySource, log: RequestLog = RequestLog())
        -> FoundryLocalCleanupProvider
    {
        FoundryLocalCleanupProvider(
            modelAlias: "qwen", status: .init(lookup: { URL(string: "http://localhost:5273")! }),
            context: .init(lookup: { _ in 4096 }), residency: residency, modelLane: AsyncLane(),
            session: makeStubSession { request in
                log.record(request)
                return StubReply.completion(request, "Cleaned.")
            })
    }
}
