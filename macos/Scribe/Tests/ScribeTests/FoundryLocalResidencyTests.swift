import Foundation
import XCTest

@testable import Scribe

final class FoundryLocalResidencyTests: XCTestCase {
    func testRealResidentFoundryReadinessChecksCapacityWithoutLoadOrText() async throws {
        guard ProcessInfo.processInfo.environment["SCRIBE_REAL_FOUNDRY_CLEANUP"] == "1" else {
            throw XCTSkip("Opt in with qwen2.5-1.5b already resident in Foundry Local.")
        }
        let residency = FoundryLocalResidencySource.live()
        guard try await residency.isLoaded("qwen2.5-1.5b") else {
            throw XCTSkip("This read-only check requires an already resident model.")
        }
        let provider = FoundryLocalCleanupProvider(
            modelAlias: "qwen2.5-1.5b",
            residency: .init(
                isLoaded: residency.isLoaded,
                loadCached: { _ in
                    XCTFail("The read-only integration check cannot load a model")
                    throw LocalModelReadinessError.unavailable
                }),
            session: makeStubSession { request in
                XCTFail("Resident readiness sends no text")
                return StubReply.completion(request, "never")
            })
        let result = try await provider.prepareLocalModel(
            isCurrent: { true }, onStarting: { XCTFail("Resident readiness cannot announce loading") })
        XCTAssertEqual(result, .resident)
    }

    func testRecordingReadinessRefusesUnknownOrInsufficientContextBeforeResidencyOrLoading() async throws {
        for capacity in [0, 256] {
            let provider = FoundryLocalCleanupProvider(
                modelAlias: "qwen",
                context: .init(lookup: { _ in capacity }),
                residency: .init(
                    isLoaded: { _ in
                        XCTFail("Refused readiness cannot inspect residency")
                        return false
                    },
                    loadCached: { _ in XCTFail("Refused readiness cannot load") }),
                modelLane: AsyncLane(),
                session: makeStubSession { request in
                    XCTFail("Refused readiness cannot send")
                    return StubReply.completion(request, "never")
                })
            do {
                _ = try await provider.prepareLocalModel(
                    isCurrent: { true }, onStarting: { XCTFail("Refused readiness cannot announce loading") })
                XCTFail("Readiness must refuse an unusable context")
            } catch {
                XCTAssertEqual(
                    error as? CleanupProviderError,
                    capacity == 0 ? .localContextUnknown : .localRequestTooLarge)
            }
        }
    }

    func testReadinessDeadlineCancelsAnInFlightLoadAndTheNextRequestCanUseTheLane() async throws {
        let store = makeCleanupStore().store
        store.isEnabled = true
        let load = HeldWork()
        let resident = StubSwitch()
        let log = RequestLog()
        var factory = CleanupProviderFactory.testing(
            session: makeStubSession { request in
                log.record(request)
                return StubReply.completion(request, "Cleaned.")
            },
            foundryStatus: .init(lookup: { URL(string: "http://localhost:5273")! }))
        factory.foundryLocalResidency = .init(
            isLoaded: { _ in resident.isOn },
            loadCached: { _ in try await load.hold() })
        let cache = CleanupProviderCache(
            store: store, environment: [:], factory: factory,
            readinessTimer: { _ in
                await load.waitUntilStarted()
                throw OperationDeadlineError.exceeded(seconds: 30)
            })
        let result = await cache.prepareLocalModel(isCurrent: { true }, onStarting: {})
        XCTAssertEqual(result, .timedOut)
        XCTAssertTrue(load.sawCancellation)
        XCTAssertTrue(log.all.isEmpty)
        resident.turnOn()
        let provider = try cache.admittedProvider()
        _ = try await provider.clean(
            CleanupRequest(transcript: "hello", writingStylePrompt: "Fix spelling.", maxOutputTokens: 64))
        XCTAssertEqual(log.all.count, 1)
    }

    func testCancelledCompletionWaitingBehindALoadNeverInspectsOrSends() async throws {
        let lane = AsyncLane()
        let hold = HeldWork()
        let holder = Task { try await lane.run { try await hold.hold() } }
        await hold.waitUntilStarted()
        let inspected = StubSwitch()
        let log = RequestLog()
        let provider = FoundryLocalCleanupProvider(
            modelAlias: "qwen", status: .init(lookup: { URL(string: "http://localhost:5273")! }),
            context: .init(lookup: { _ in 4096 }),
            residency: .init(
                isLoaded: { _ in
                    inspected.turnOn()
                    return true
                },
                loadCached: { _ in XCTFail("Queued cancellation must not load") }),
            modelLane: lane,
            session: makeStubSession { request in
                log.record(request)
                return StubReply.completion(request, "never")
            })
        let waiting = Task {
            try await provider.clean(
                CleanupRequest(transcript: "hello", writingStylePrompt: "Fix spelling.", maxOutputTokens: 64))
        }
        await lane.waitUntilWaiting(atLeast: 1)
        waiting.cancel()
        if case .failure(let error) = await waiting.result {
            XCTAssertTrue(error is CancellationError)
        } else {
            XCTFail("Queued cancellation must fail")
        }
        XCTAssertEqual(lane.waitingCount, 0)
        XCTAssertFalse(inspected.isOn)
        XCTAssertTrue(log.all.isEmpty)
        holder.cancel()
        _ = await holder.result
        _ = try await provider.clean(
            CleanupRequest(transcript: "hello", writingStylePrompt: "Fix spelling.", maxOutputTokens: 64))
        XCTAssertTrue(inspected.isOn)
        XCTAssertEqual(log.all.count, 1)
    }

    func testLoadReplyRequiresExplicitBooleanSuccess() throws {
        XCTAssertNoThrow(try FoundryLocalResidencySource.confirmLoadReply(Data(#"{"success":true}"#.utf8)))
        for reply in ["{}", #"{"success":false}"#, #"{"success":"true"}"#, "not json"] {
            XCTAssertThrowsError(try FoundryLocalResidencySource.confirmLoadReply(Data(reply.utf8)))
        }
    }

    func testUnknownOrInsufficientCapacityNeverLoadsTheColdModel() async throws {
        for capacity in [0, 256] {
            let loaded = StubSwitch()
            let inspected = StubSwitch()
            let log = RequestLog()
            let provider = FoundryLocalCleanupProvider(
                modelAlias: "qwen", status: .init(lookup: { URL(string: "http://localhost:5273")! }),
                context: .init(lookup: { _ in capacity }),
                residency: .init(
                    isLoaded: { _ in
                        inspected.turnOn()
                        return false
                    },
                    loadCached: { _ in loaded.turnOn() }),
                modelLane: AsyncLane(),
                session: makeStubSession { request in
                    log.record(request)
                    return StubReply.completion(request, "never")
                })
            let request = CleanupRequest(
                transcript: String(repeating: "words ", count: 100), writingStylePrompt: "Fix spelling.",
                maxOutputTokens: 64)
            let failure = try await cleanupFailure(of: provider, request)
            XCTAssertEqual(failure, capacity == 0 ? .localContextUnknown : .localRequestTooLarge)
            XCTAssertFalse(inspected.isOn)
            XCTAssertFalse(loaded.isOn)
            XCTAssertTrue(log.all.isEmpty)
        }
    }

    func testPostLoadCapacityChangeRefusesTheTextDespiteAPassingPreflight() async throws {
        let loaded = StubSwitch()
        let log = RequestLog()
        let provider = FoundryLocalCleanupProvider(
            modelAlias: "qwen", status: .init(lookup: { URL(string: "http://localhost:5273")! }),
            context: .init(lookup: { _ in loaded.isOn ? 256 : 4096 }),
            residency: .init(isLoaded: { _ in loaded.isOn }, loadCached: { _ in loaded.turnOn() }),
            modelLane: AsyncLane(),
            session: makeStubSession { request in
                log.record(request)
                return StubReply.completion(request, "never")
            })
        let request = CleanupRequest(
            transcript: String(repeating: "words ", count: 100), writingStylePrompt: "Fix spelling.",
            maxOutputTokens: 64)
        let failure = try await cleanupFailure(of: provider, request)
        XCTAssertEqual(failure, .localRequestTooLarge)
        XCTAssertTrue(loaded.isOn)
        XCTAssertTrue(log.all.isEmpty)
    }

    func testChangedBackSettingsDuringLocalResidencyReadWithdrawReadiness() async throws {
        let store = makeCleanupStore().store
        store.isEnabled = true
        store.providerKind = .openAICompatible
        store.openAIBaseURL = "http://localhost:1234/v1"
        store.openAIModel = "qwen"
        store.selectedLocalApp = .lmStudio
        let starting = StubSwitch()
        let session = makeStubSession { request in
            XCTFail("Stale readiness cannot send")
            return StubReply.completion(request, "never")
        }
        let factory = CleanupProviderFactory.testing(
            session: session,
            readLocalServer: { _, _ in
                store.isEnabled = false
                store.isEnabled = true
                return LocalServerState(reach: .reached, models: [], loaded: [], failureDetail: nil)
            })
        let cache = CleanupProviderCache(store: store, environment: [:], factory: factory)
        let result = await cache.prepareLocalModel(isCurrent: { true }, onStarting: { starting.turnOn() })
        XCTAssertEqual(result, .configurationChanged)
        XCTAssertFalse(starting.isOn)
    }

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
