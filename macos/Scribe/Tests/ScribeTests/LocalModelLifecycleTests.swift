import Foundation
import XCTest

@testable import Scribe

/// A timer the test fires by hand, so no test sleeps and every deadline passes exactly where the test says.
private final class ManualClock: @unchecked Sendable {
    private let lock = NSLock()
    private var gates: [LifecycleGate] = []
    private var requested: [Duration] = []
    private var instant = ContinuousClock.now

    var sleeper: @Sendable (Duration) async throws -> Void {
        { [self] duration in
            let gate = LifecycleGate()
            lock.withLock {
                gates.append(gate)
                requested.append(duration)
            }
            try await gate.wait()
        }
    }

    var pending: Int { lock.withLock { gates.count } }
    var durations: [Duration] { lock.withLock { requested } }
    var now: @Sendable () -> ContinuousClock.Instant { { self.lock.withLock { self.instant } } }

    func advance(_ duration: Duration) {
        lock.withLock { instant = instant.advanced(by: duration) }
    }

    func fire() {
        let all = lock.withLock { () -> [LifecycleGate] in
            defer { gates = [] }
            return gates
        }
        for gate in all { gate.open() }
    }

    func waitForSleepers(_ count: Int) async {
        for _ in 0..<2000 where pending < count { await Task.yield() }
    }
}

private final class FakeUnloads: @unchecked Sendable {
    private let lock = NSRecursiveLock()
    private var storedModels: [String] = []
    private var storedInstances: [(id: String, key: String?)] = []
    private var storedModelResult = true
    private var storedRequiredKey: String?
    private var storedInstanceResult = true
    private var storedBarrier: LifecycleGate?
    let modelStarted = LifecycleGate()
    let instanceStarted = LifecycleGate()

    var models: [String] { lock.withLock { storedModels } }
    var instances: [(id: String, key: String?)] { lock.withLock { storedInstances } }
    var modelResult: Bool {
        get { lock.withLock { storedModelResult } }
        set { lock.withLock { storedModelResult = newValue } }
    }
    /// Instances whose unload succeeds only with this key.
    var requiredKey: String? {
        get { lock.withLock { storedRequiredKey } }
        set { lock.withLock { storedRequiredKey = newValue } }
    }
    var instanceResult: Bool {
        get { lock.withLock { storedInstanceResult } }
        set { lock.withLock { storedInstanceResult = newValue } }
    }
    var barrier: LifecycleGate? {
        get { lock.withLock { storedBarrier } }
        set { lock.withLock { storedBarrier = newValue } }
    }

    var actions: LocalModelLifecycle.Actions {
        LocalModelLifecycle.Actions(
            unloadModel: { [self] _, model, _ in
                lock.withLock { storedModels.append(model) }
                modelStarted.open()
                if (try? await barrier?.wait()) == nil, barrier != nil { return false }
                return lock.withLock { modelResult }
            },
            unloadInstance: { [self] _, id, key in
                lock.withLock { storedInstances.append((id, key)) }
                instanceStarted.open()
                if (try? await barrier?.wait()) == nil, barrier != nil { return false }
                return lock.withLock { requiredKey.map { $0 == key } ?? instanceResult }
            })
    }

    func set(_ change: (FakeUnloads) -> Void) { lock.withLock { change(self) } }
}

private func lmTarget(_ model: String = "m", key: String? = nil) -> LocalModelTarget {
    LocalModelTarget(endpoint: "http://127.0.0.1:1234/v1", model: model, app: .lmStudio, apiKey: key)
}

final class LocalModelLifecycleTests: XCTestCase {
    func testCandidateRetryBackoffIsBoundedAndReadsRotatedServerKey() async throws {
        let fake = FakeUnloads()
        let clock = ManualClock()
        let lifecycle = make(fake, clock: clock, idle: .zero)
        let candidate = LocalModelLifecycle.Candidate { true }
        try await LocalModelLifecycle.$candidate.withValue(candidate) { try await owned(lifecycle, "copy") }
        fake.instanceResult = false
        candidate.finish(in: lifecycle)
        for delay in [30, 60, 120, 240, 300, 300] {
            await clock.waitForSleepers(1)
            XCTAssertEqual(clock.pending, 1)
            XCTAssertEqual(clock.durations.last, .seconds(delay))
            clock.fire()
        }
        await clock.waitForSleepers(1)
        XCTAssertEqual(clock.pending, 1)
        lifecycle.useSavedKeys { _ in "rotated-test-key" }
        fake.requiredKey = "rotated-test-key"
        clock.fire()
        let completed = await finishes(within: 30) {
            while lifecycle.candidateRetirementCountForTests != 0 { await Task.yield() }
        }
        XCTAssertTrue(completed)
        XCTAssertTrue(lifecycle.ownedCopies.isEmpty)
        XCTAssertEqual(fake.instances.last?.key, "rotated-test-key")
        XCTAssertTrue(fake.models.isEmpty)
        _ = await lifecycle.release(.shutdown, target: nil)
    }

    func testRefusedCandidateRetirementRetriesOnlyItsInstanceWithNeverIdle() async throws {
        let fake = FakeUnloads()
        let clock = ManualClock()
        let lifecycle = make(fake, clock: clock, idle: .zero)
        try await owned(lifecycle, "served", model: "saved")
        let candidate = LocalModelLifecycle.Candidate { true }
        try await LocalModelLifecycle.$candidate.withValue(candidate) {
            try await owned(lifecycle, "candidate", model: "unsaved")
        }
        fake.instanceResult = false
        candidate.finish(in: lifecycle)
        await clock.waitForSleepers(1)
        XCTAssertEqual(clock.pending, 1)
        XCTAssertEqual(clock.durations, [.seconds(30)])
        XCTAssertEqual(fake.instances.map(\.id), ["candidate"])
        candidate.retire(in: lifecycle)
        fake.instanceResult = true
        clock.fire()
        let completed = await finishes(within: 30) {
            while lifecycle.candidateRetirementCountForTests != 0 { await Task.yield() }
        }
        XCTAssertTrue(completed)
        XCTAssertEqual(fake.instances.map(\.id), ["candidate", "candidate"])
        XCTAssertEqual(lifecycle.ownedCopies.map(\.instanceID), ["served"])
        XCTAssertTrue(fake.models.isEmpty)
        _ = await lifecycle.release(.shutdown, target: nil)
    }

    func testCandidateRetryRechecksSavedChoiceAndStopsAtShutdown() async throws {
        for shutdown in [false, true] {
            let fake = FakeUnloads()
            let clock = ManualClock()
            let lifecycle = make(fake, clock: clock, idle: .zero)
            let wanted = LockedValue<Bool>()
            wanted.set(true)
            let candidate = LocalModelLifecycle.Candidate { wanted.value == true }
            try await LocalModelLifecycle.$candidate.withValue(candidate) { try await owned(lifecycle, "copy") }
            fake.instanceResult = false
            candidate.finish(in: lifecycle)
            await clock.waitForSleepers(1)
            XCTAssertEqual(clock.pending, 1)
            if shutdown {
                _ = await lifecycle.release(.shutdown, target: nil)
            } else {
                wanted.set(false)
            }
            let attempts = fake.instances.count
            clock.fire()
            candidate.retire(in: lifecycle)
            let completed = await finishes(within: 30) {
                while lifecycle.candidateRetirementCountForTests != 0 { await Task.yield() }
            }
            XCTAssertTrue(completed)
            XCTAssertEqual(fake.instances.count, attempts)
            XCTAssertEqual(lifecycle.ownedCopies.map(\.instanceID), ["copy"])
            XCTAssertTrue(fake.models.isEmpty)
            _ = await lifecycle.release(.shutdown, target: nil)
        }
    }

    func testACancelledReconciliationReadsAndChangesNothing() async throws {
        let lifecycle = make(idle: .zero)
        let lease = try await lifecycle.beginUse(lmTarget())
        let work = Task {
            _ = withUnsafeCurrentTask { $0?.cancel() }
            return await lifecycle.reconcileLMStudio(
                target: lmTarget(), contextTokens: 8192, lease: lease,
                read: { _, _ in
                    XCTFail("Cancelled reconciliation cannot read the app")
                    return .notRunning.reached
                },
                load: { _, _, _ in
                    XCTFail("Cancelled reconciliation cannot load")
                    return "never"
                })
        }
        let result = await work.value
        lease.end()
        XCTAssertEqual(result, .busy)
        XCTAssertTrue(lifecycle.ownedCopies.isEmpty)
        XCTAssertEqual(lifecycle.useCount, 0)
    }

    func testACancelledResidencyReadCannotForgetTrackedOwnership() async throws {
        let fake = FakeUnloads()
        let lifecycle = make(fake, idle: .zero)
        try await owned(lifecycle, "kept")
        let began = LifecycleGate()
        let resume = LifecycleGate()
        let lease = try await lifecycle.beginUse(lmTarget())
        let work = Task {
            await lifecycle.reconcileLMStudio(
                target: lmTarget(), contextTokens: 8192, lease: lease,
                read: { _, _ in
                    began.open()
                    try? await resume.wait()
                    return LocalServerState(reach: .reached, models: [], loaded: [], failureDetail: nil)
                },
                load: { _, _, _ in
                    XCTFail("A cancelled read cannot start a resize")
                    return "never"
                })
        }
        try await began.wait()
        work.cancel()
        let result = await work.value
        resume.open()
        lease.end()
        XCTAssertEqual(result, .busy)
        XCTAssertEqual(lifecycle.ownedCopies.map(\.instanceID), ["kept"])
        XCTAssertTrue(fake.instances.isEmpty)
        let released = await lifecycle.release(.shutdown, target: nil)
        XCTAssertEqual(released, .released)
        XCTAssertEqual(fake.instances.map(\.id), ["kept"])
    }

    func testShutdownCancellationKeepsOnlyTheCopiesNotConfirmedFreed() async throws {
        let clock = ManualClock()
        let secondStarted = LifecycleGate()
        let blocked = LifecycleGate()
        let lifecycle = LocalModelLifecycle(
            idle: .zero,
            actions: .init(
                unloadModel: { _, _, _ in
                    XCTFail("Shutdown cannot unload an ordinary model")
                    return false
                },
                unloadInstance: { _, id, _ in
                    if id == "freed" { return true }
                    secondStarted.open()
                    try? await blocked.wait()
                    return false
                }),
            sleeper: clock.sleeper, now: clock.now)
        try await owned(lifecycle, "freed")
        try await owned(lifecycle, "owed")
        let shutdown = Task { await lifecycle.release(.shutdown, target: nil) }
        try await secondStarted.wait()
        await clock.waitForSleepers(1)
        XCTAssertGreaterThanOrEqual(clock.pending, 1)
        clock.fire()
        let result = await shutdown.value
        XCTAssertEqual(result, .failed)
        XCTAssertEqual(lifecycle.ownedCopies.map(\.instanceID), ["owed"])
        XCTAssertEqual(lifecycle.useCount, 0)
    }

    func testShutdownDeadlineStopsKeyFallbackAndRemainingCopiesAndClearsTheBarrier() async throws {
        let fake = FakeUnloads()
        let clock = ManualClock()
        let lifecycle = make(fake, clock: clock, idle: .zero)
        try await owned(lifecycle, "first", key: "original")
        try await owned(lifecycle, "second")
        fake.requiredKey = "original"
        fake.barrier = LifecycleGate()
        let shutdown = Task { await lifecycle.release(.shutdown, target: nil) }
        try await fake.instanceStarted.wait()
        await clock.waitForSleepers(1)
        XCTAssertGreaterThanOrEqual(clock.pending, 1)
        clock.fire()
        let result = await shutdown.value
        XCTAssertEqual(result, .failed)
        XCTAssertEqual(fake.instances.map(\.id), ["first"])
        XCTAssertEqual(Set(lifecycle.ownedCopies.map(\.instanceID)), ["first", "second"])
        fake.barrier = nil
        fake.requiredKey = nil
        let retry = await lifecycle.release(.shutdown, target: nil)
        XCTAssertEqual(retry, .released, "The cancelled unload must release its barrier and lane")
        XCTAssertTrue(lifecycle.ownedCopies.isEmpty)
        XCTAssertTrue(fake.models.isEmpty)
    }

    func testACancelledUseDoesNotWithdrawTheOwedIdleRetirement() async throws {
        let fake = FakeUnloads()
        let clock = ManualClock()
        let lifecycle = make(fake, clock: clock)
        try await owned(lifecycle, "idle-copy")
        await clock.waitForSleepers(1)
        XCTAssertGreaterThanOrEqual(clock.pending, 1)
        let cancelled = Task {
            _ = withUnsafeCurrentTask { $0?.cancel() }
            do {
                let lease = try await lifecycle.beginUse(lmTarget("other"))
                lease.end()
                XCTFail("A cancelled request cannot take a local-model lease")
            } catch {
                XCTAssertTrue(error is CancellationError)
            }
        }
        await cancelled.value
        XCTAssertEqual(lifecycle.useCount, 0)
        clock.fire()
        try await fake.instanceStarted.wait()
        _ = await lifecycle.release(.shutdown, target: nil)
        XCTAssertEqual(fake.instances.map(\.id), ["idle-copy"])
        XCTAssertTrue(lifecycle.ownedCopies.isEmpty)
        XCTAssertTrue(fake.models.isEmpty)
    }

    func testShutdownDuringResizeUnloadPreventsTheReplacementLoad() async throws {
        let fake = FakeUnloads()
        let clock = ManualClock()
        let lifecycle = make(fake, clock: clock, idle: .zero)
        let finishUnload = LifecycleGate()
        fake.barrier = finishUnload
        let loaded = StubSwitch()
        let lease = try await lifecycle.beginUse(lmTarget())
        let listing = LocalServerState(
            reach: .reached, models: [],
            loaded: [
                LocalServerLoadedModel(
                    "m", 1, contextTokens: 4096, instanceID: "wrong-size", remainingTTLSeconds: 5)
            ], failureDetail: nil)
        let resize = Task {
            await lifecycle.reconcileLMStudio(
                target: lmTarget(), contextTokens: 8192, lease: lease,
                read: { _, _ in listing },
                load: { _, _, _ in
                    loaded.turnOn()
                    return "replacement"
                })
        }
        try await fake.instanceStarted.wait()
        let shutdown = Task { await lifecycle.release(.shutdown, target: nil) }
        await clock.waitForSleepers(1)
        XCTAssertGreaterThanOrEqual(clock.pending, 1)
        finishUnload.open()
        let result = await resize.value
        lease.end()
        _ = await shutdown.value
        XCTAssertEqual(result, .unavailable)
        XCTAssertFalse(loaded.isOn)
        XCTAssertEqual(fake.instances.map(\.id), ["wrong-size"])
        XCTAssertTrue(fake.models.isEmpty)
        XCTAssertEqual(lifecycle.useCount, 0)
        XCTAssertTrue(lifecycle.ownedCopies.isEmpty)
    }

    func testTheLateSettlementRetirementHasItsOwnShutdownBoundAndKeepsRefusedOwnership() async throws {
        let fake = FakeUnloads()
        let clock = ManualClock()
        let lifecycle = make(fake, clock: clock, idle: .zero)
        let began = LifecycleGate()
        let finishLoad = LifecycleGate()
        let blockedUnload = LifecycleGate()
        fake.barrier = blockedUnload
        let lease = try await lifecycle.beginUse(lmTarget())
        let caller = Task {
            await lifecycle.reconcileLMStudio(
                target: lmTarget(), contextTokens: 8192, lease: lease,
                read: { _, _ in .notRunning.reached },
                load: { _, _, _ in
                    began.open()
                    try? await finishLoad.wait()
                    return "still-owed"
                })
        }
        try await began.wait()
        lease.end()
        let shutdown = Task { await lifecycle.release(.shutdown, target: nil) }
        await clock.waitForSleepers(1)
        XCTAssertGreaterThanOrEqual(clock.pending, 1)
        clock.fire()
        _ = await shutdown.value
        finishLoad.open()
        try await fake.instanceStarted.wait()
        await clock.waitForSleepers(1)
        XCTAssertGreaterThanOrEqual(clock.pending, 1)
        clock.fire()
        _ = await caller.value
        XCTAssertEqual(fake.instances.map(\.id), ["still-owed"])
        XCTAssertEqual(lifecycle.ownedCopies.map(\.instanceID), ["still-owed"])
        XCTAssertEqual(lifecycle.useCount, 0)
        XCTAssertTrue(fake.models.isEmpty)
        clock.fire()
        for _ in 0..<200 { await Task.yield() }
        XCTAssertEqual(fake.instances.count, 1, "Shutdown settlement does not rearm automatic retries")
    }

    func testALoadSettlingAfterTheShutdownBoundRetiresItsRecordedInstance() async throws {
        let fake = FakeUnloads()
        fake.requiredKey = "original"
        let clock = ManualClock()
        let lifecycle = make(fake, clock: clock, idle: .zero)
        let began = LifecycleGate()
        let finishLoad = LifecycleGate()
        let lease = try await lifecycle.beginUse(lmTarget(key: "original"))
        let caller = Task {
            await lifecycle.reconcileLMStudio(
                target: lmTarget(key: "original"), contextTokens: 8192, lease: lease,
                read: { _, _ in .notRunning.reached },
                load: { _, _, _ in
                    began.open()
                    try? await finishLoad.wait()
                    return "late-shutdown-copy"
                })
        }
        try await began.wait()
        caller.cancel()
        _ = await caller.value
        lease.end()
        XCTAssertEqual(lifecycle.useCount, 1)
        let shutdown = Task { await lifecycle.release(.shutdown, target: nil) }
        await clock.waitForSleepers(1)
        XCTAssertGreaterThanOrEqual(clock.pending, 1)
        clock.fire()
        let first = await shutdown.value
        XCTAssertEqual(first, .failed)
        XCTAssertTrue(fake.instances.isEmpty)
        finishLoad.open()
        try await fake.instanceStarted.wait()
        _ = await lifecycle.release(.shutdown, target: nil)
        XCTAssertEqual(fake.instances.map(\.id), ["late-shutdown-copy", "late-shutdown-copy"])
        XCTAssertNil(fake.instances.first?.key)
        XCTAssertEqual(fake.instances.last?.key, "original")
        XCTAssertTrue(fake.models.isEmpty)
        XCTAssertTrue(lifecycle.ownedCopies.isEmpty)
        XCTAssertEqual(lifecycle.useCount, 0)
        do {
            _ = try await lifecycle.beginUse(lmTarget())
            XCTFail("Settling a late load must not reopen admission")
        } catch {
            XCTAssertEqual(error as? LocalModelLifecycleError, .closing)
        }
    }

    func testAUseWaitingBehindTheFinalUnloadIsRefusedWhenTheUnloadEnds() async throws {
        let fake = FakeUnloads()
        let barrier = LifecycleGate()
        fake.barrier = barrier
        let clock = ManualClock()
        let lifecycle = make(fake, clock: clock, idle: .zero)
        try await owned(lifecycle, "i1")
        let release = Task { await lifecycle.release(.freeMemory, target: lmTarget()) }
        try await fake.modelStarted.wait()
        let use = Task { try await lifecycle.beginUse(lmTarget()) }
        await clock.waitForSleepers(1)
        let shutdown = Task { await lifecycle.release(.shutdown, target: nil) }
        await clock.waitForSleepers(2)
        barrier.open()
        _ = await release.value
        _ = await shutdown.value
        if case .failure(let error) = await use.result {
            XCTAssertEqual(error as? LocalModelLifecycleError, .closing)
        } else {
            XCTFail("A waiting use cannot enter after shutdown")
        }
        XCTAssertEqual(lifecycle.useCount, 0)
    }

    func testReconciliationReadAcrossShutdownCannotUnloadOrLoadACopy() async throws {
        let fake = FakeUnloads()
        let clock = ManualClock()
        let lifecycle = make(fake, clock: clock, idle: .zero)
        let served = target()
        let lease = try await lifecycle.beginUse(served)
        let readStarted = LifecycleGate()
        let resumeRead = LifecycleGate()
        let loads = StubSwitch()
        let reconcile = Task {
            await lifecycle.reconcileLMStudio(
                target: served, contextTokens: 8192, lease: lease,
                read: { _, _ in
                    readStarted.open()
                    try? await resumeRead.wait()
                    return LocalServerState(reach: .reached, models: [], loaded: [], failureDetail: nil)
                },
                load: { _, _, _ in
                    loads.turnOn()
                    return "late"
                })
        }
        try await readStarted.wait()
        let shutdown = Task { await lifecycle.release(.shutdown, target: nil) }
        await clock.waitForSleepers(2)
        resumeRead.open()
        let reconciled = await reconcile.value
        XCTAssertEqual(reconciled, .busy)
        lease.end()
        _ = await shutdown.value
        XCTAssertFalse(loads.isOn)
        XCTAssertTrue(fake.models.isEmpty)
        XCTAssertTrue(fake.instances.isEmpty)
        XCTAssertTrue(lifecycle.ownedCopies.isEmpty)
    }

    func testCacheShutdownClosesAnUnusedLifecycleBeforeItsFirstUse() async throws {
        let store = makeCleanupStore().store
        let factory = CleanupProviderFactory.testing(
            session: makeStubSession { request in
                XCTFail("A closed lifecycle cannot reach transport")
                return StubReply.completion(request, "never")
            })
        let cache = CleanupProviderCache(store: store, environment: [:], factory: factory)
        store.isEnabled = true
        store.providerKind = .openAICompatible
        store.openAIBaseURL = "http://localhost:11434/v1"
        store.openAIModel = "m"
        let provider = try cache.admittedProvider()
        let outcome = await cache.releaseLocalModel(.shutdown)
        XCTAssertEqual(outcome, .nothingToRelease)
        do {
            _ = try await factory.localModelLifecycle.beginUse(target())
            XCTFail("Empty shutdown still closes admission")
        } catch {
            XCTAssertEqual(error as? LocalModelLifecycleError, .closing)
        }
        do {
            _ = try await provider.clean(
                CleanupRequest(transcript: "never", writingStylePrompt: "Fix spelling.", maxOutputTokens: 64))
            XCTFail("A provider cannot send after empty shutdown")
        } catch {
            XCTAssertTrue(error is CancellationError)
        }
    }

    func testLateExplicitAndCandidateRetirementsCannotCommitAfterShutdown() async throws {
        let fake = FakeUnloads()
        fake.instanceResult = false
        let lifecycle = make(fake, idle: .zero)
        let candidate = LocalModelLifecycle.Candidate { true }
        try await LocalModelLifecycle.$candidate.withValue(candidate) {
            try await owned(lifecycle, "candidate")
        }
        let final = await lifecycle.release(.shutdown, target: nil)
        XCTAssertEqual(final, .failed)
        let attempts = fake.instances.count
        fake.instanceResult = true
        for reason in [
            LocalModelReleaseReason.configurationChanged, .candidateFinished, .freeMemory, .pause,
            .retentionShortened, .idle,
        ] {
            let outcome = await lifecycle.release(reason, target: target(), candidateID: candidate.id)
            XCTAssertEqual(outcome, .notWanted)
        }
        XCTAssertEqual(fake.instances.count, attempts)
        XCTAssertTrue(fake.models.isEmpty)
        XCTAssertEqual(lifecycle.ownedCopies.map(\.instanceID), ["candidate"])
    }

    func testAReleaseWaitingForAUseCannotCommitIfShutdownWinsBeforeTheUseEnds() async throws {
        let fake = FakeUnloads()
        let clock = ManualClock()
        let lifecycle = make(fake, clock: clock, idle: .zero)
        let served = target()
        let lease = try await lifecycle.beginUse(served)
        let release = Task { await lifecycle.release(.configurationChanged, target: served) }
        await clock.waitForSleepers(1)
        XCTAssertEqual(clock.pending, 1)
        let shutdown = Task { await lifecycle.release(.shutdown, target: nil) }
        await clock.waitForSleepers(2)
        XCTAssertEqual(clock.pending, 2)
        lease.end()
        let old = await release.value
        let final = await shutdown.value
        XCTAssertEqual(old, .notWanted)
        XCTAssertEqual(final, .nothingToRelease)
        XCTAssertTrue(fake.models.isEmpty)
        XCTAssertTrue(fake.instances.isEmpty)
    }

    func testShutdownWithdrawsAutomaticRetryAndRefusesNewUses() async throws {
        for paused in [false, true] {
            let fake = FakeUnloads()
            fake.modelResult = false
            fake.instanceResult = false
            let clock = ManualClock()
            let lifecycle = make(fake, clock: clock)
            try await owned(lifecycle, "i1")
            if paused {
                lifecycle.notePause(true)
                let use = try await lifecycle.beginUse(target())
                use.end()
            } else {
                await clock.waitForSleepers(1)
                clock.fire()
            }
            await clock.waitForSleepers(1)
            XCTAssertEqual(clock.durations.last, .seconds(30))
            let shutdown = await lifecycle.release(.shutdown, target: nil)
            XCTAssertEqual(shutdown, .failed)
            let models = fake.models.count
            let instances = fake.instances.count
            fake.modelResult = true
            fake.instanceResult = true
            do {
                _ = try await lifecycle.beginUse(target())
                XCTFail("Shutdown must close local use admission")
            } catch {
                XCTAssertEqual(error as? LocalModelLifecycleError, .closing)
            }
            lifecycle.setIdle(.seconds(1))
            clock.fire()
            for _ in 0..<500 { await Task.yield() }
            XCTAssertEqual(fake.models.count, models)
            XCTAssertEqual(fake.instances.count, instances)
            XCTAssertEqual(lifecycle.ownedCopies.map(\.instanceID), ["i1"])
        }
    }

    func testRetirementTriesTheRotatedSavedKeyBeforeTheOriginalLoadedKey() async throws {
        let fake = FakeUnloads()
        fake.requiredKey = "rotated"
        let lifecycle = make(fake, idle: .zero)
        try await owned(lifecycle, "copy", key: "original")
        lifecycle.useSavedKeys { endpoint in
            XCTAssertTrue(LocalModelLifecycle.sameServer(endpoint, "http://localhost:1234/v1"))
            return "rotated"
        }
        let outcome = await lifecycle.release(.shutdown, target: nil)
        XCTAssertEqual(outcome, .released)
        XCTAssertEqual(fake.instances.count, 1)
        XCTAssertEqual(fake.instances.first?.key, "rotated")
    }

    func testAnUnreadableCurrentKeyUsesOnlyTheOriginalCopyKeyAndLogsByShape() async throws {
        let fake = FakeUnloads()
        fake.requiredKey = "original"
        let lifecycle = make(fake, idle: .zero)
        try await owned(lifecycle, "copy", key: "original")
        lifecycle.useSavedKeys { _ in throw CleanupSendHandoff.Refusal.settingsChanged }
        let outcome = await lifecycle.release(.shutdown, target: nil)
        XCTAssertEqual(outcome, .released)
        XCTAssertEqual(fake.instances.map(\.key), [nil, "original"])
    }

    func testFinishedCandidateRetiresOnlyItsOwnCopies() async throws {
        let fake = FakeUnloads()
        let lifecycle = make(fake, idle: .zero)
        try await owned(lifecycle, "served", model: "saved")
        let owner = LocalModelLifecycle.Candidate { true }
        try await LocalModelLifecycle.$candidate.withValue(owner) {
            try await owned(lifecycle, "candidate", model: "unsaved")
        }
        owner.finish(in: lifecycle)
        try await fake.instanceStarted.wait()
        _ = await lifecycle.release(.candidateFinished, target: nil, candidateID: owner.id)
        XCTAssertEqual(fake.instances.map(\.id), ["candidate"])
        XCTAssertEqual(lifecycle.ownedCopies.map(\.instanceID), ["served"])
        XCTAssertTrue(fake.models.isEmpty)
    }

    func testCandidateThatBecameSavedIsNotRetired() async throws {
        let fake = FakeUnloads()
        let lifecycle = make(fake, idle: .zero)
        let owner = LocalModelLifecycle.Candidate { false }
        try await LocalModelLifecycle.$candidate.withValue(owner) { try await owned(lifecycle, "saved") }
        let outcome = await lifecycle.release(
            .candidateFinished, target: nil, candidateID: owner.id, wanted: owner.wanted)
        XCTAssertEqual(outcome, .notWanted)
        XCTAssertEqual(lifecycle.ownedCopies.map(\.instanceID), ["saved"])
        XCTAssertTrue(fake.instances.isEmpty)
    }

    func testCancelledCandidateRetiresACopyThatLandsAfterTheCheckFinished() async throws {
        let fake = FakeUnloads()
        let lifecycle = make(fake, idle: .zero)
        let owner = LocalModelLifecycle.Candidate { true }
        let started = LifecycleGate()
        let land = LifecycleGate()
        let lease = try await lifecycle.beginUse(target())
        let check = Task {
            await LocalModelLifecycle.$candidate.withValue(owner) {
                await lifecycle.reconcileLMStudio(
                    target: lmTarget(), contextTokens: 8192, lease: lease,
                    read: { _, _ in LocalServerState(reach: .reached, models: [], loaded: []) },
                    load: { _, _, _ in
                        started.open()
                        try? await land.wait()
                        return "late-candidate"
                    })
            }
        }
        try await started.wait()
        check.cancel()
        _ = await check.value
        lease.end()
        owner.finish(in: lifecycle)
        XCTAssertTrue(fake.instances.isEmpty)
        land.open()
        try await fake.instanceStarted.wait()
        _ = await lifecycle.release(.candidateFinished, target: nil, candidateID: owner.id)
        XCTAssertTrue(lifecycle.ownedCopies.isEmpty)
        XCTAssertEqual(fake.instances.map(\.id), ["late-candidate"])
        XCTAssertTrue(fake.models.isEmpty)
    }

    func testReconciliationRetiresOtherOwnedCopiesAndKeepsTheCopyRequestsReach() async throws {
        let fake = FakeUnloads()
        let lifecycle = make(fake, idle: .zero)
        try await owned(lifecycle, "old", model: "previous")
        try await owned(lifecycle, "current")
        let held = LocalServerState(
            reach: .reached, models: [],
            loaded: [
                LocalServerLoadedModel("previous", 1, contextTokens: 8192, instanceID: "old", remainingTTLSeconds: 10),
                LocalServerLoadedModel("m", 1, contextTokens: 8192, instanceID: "current", remainingTTLSeconds: 10),
            ])
        let lease = try await lifecycle.beginUse(target())
        await lifecycle.reconcileLMStudio(
            target: target(), contextTokens: 0, lease: lease, read: { _, _ in held }, load: { _, _, _ in nil })
        lease.end()
        XCTAssertEqual(fake.instances.map(\.id), ["old"])
        XCTAssertEqual(lifecycle.ownedCopies.map(\.instanceID), ["current"])
    }

    func testOtherCopyRetirementWaitsUntilTheReconcilingRequestIsTheOnlyUse() async throws {
        let fake = FakeUnloads()
        let lifecycle = make(fake, idle: .zero)
        try await owned(lifecycle, "old", model: "previous")
        let held = LocalServerState(
            reach: .reached, models: [],
            loaded: [
                LocalServerLoadedModel("previous", 1, contextTokens: 8192, instanceID: "old", remainingTTLSeconds: 10)
            ])
        let lease = try await lifecycle.beginUse(target())
        let other = try await lifecycle.beginUse(target("previous"))
        await lifecycle.reconcileLMStudio(
            target: target(), contextTokens: 0, lease: lease, read: { _, _ in held }, load: { _, _, _ in nil })
        XCTAssertTrue(fake.instances.isEmpty)
        other.end()
        await lifecycle.reconcileLMStudio(
            target: target(), contextTokens: 0, lease: lease, read: { _, _ in held }, load: { _, _, _ in nil })
        lease.end()
        XCTAssertEqual(fake.instances.map(\.id), ["old"])
    }

    func testAReadOvertakenByAnotherUseDoesNotAuthorizeRetirement() async throws {
        let fake = FakeUnloads()
        let lifecycle = make(fake, idle: .zero)
        try await owned(lifecycle, "old", model: "previous")
        let held = LocalServerState(
            reach: .reached, models: [],
            loaded: [
                LocalServerLoadedModel("previous", 1, contextTokens: 8192, instanceID: "old", remainingTTLSeconds: 10)
            ])
        let lease = try await lifecycle.beginUse(target())
        await lifecycle.reconcileLMStudio(
            target: target(), contextTokens: 0, lease: lease,
            read: { _, _ in
                let intervening = try? await lifecycle.beginUse(lmTarget("previous"))
                intervening?.end()
                return held
            }, load: { _, _, _ in nil })
        lease.end()
        XCTAssertTrue(fake.instances.isEmpty)
        XCTAssertEqual(lifecycle.ownedCopies.map(\.instanceID), ["old"])
    }

    func testARefusedUnusedCopyRetirementStaysOwed() async throws {
        let fake = FakeUnloads()
        fake.instanceResult = false
        let lifecycle = make(fake, idle: .zero)
        try await owned(lifecycle, "old", model: "previous")
        let held = LocalServerState(
            reach: .reached, models: [],
            loaded: [
                LocalServerLoadedModel("previous", 1, contextTokens: 8192, instanceID: "old", remainingTTLSeconds: 10)
            ])
        let lease = try await lifecycle.beginUse(target())
        await lifecycle.reconcileLMStudio(
            target: target(), contextTokens: 0, lease: lease, read: { _, _ in held }, load: { _, _, _ in nil })
        XCTAssertEqual(lifecycle.ownedCopies.map(\.instanceID), ["old"])
        fake.instanceResult = true
        await lifecycle.reconcileLMStudio(
            target: target(), contextTokens: 0, lease: lease, read: { _, _ in held }, load: { _, _, _ in nil })
        lease.end()
        XCTAssertTrue(lifecycle.ownedCopies.isEmpty)
        XCTAssertEqual(fake.instances.map(\.id), ["old", "old"])
    }

    func testAnOpenedGateKeepsNoBookkeepingAfterCancellation() async {
        let gate = LifecycleGate()
        gate.open()
        for _ in 0..<100 {
            let waiter = Task { try await gate.wait() }
            waiter.cancel()
            _ = try? await waiter.value
            XCTAssertEqual(gate.waiterCountForTests, 0)
        }
    }

    func testACancelledClosedGateWaitLeavesNoBookkeeping() async {
        let gate = LifecycleGate()
        let waiter = Task { try await gate.wait() }
        waiter.cancel()
        do {
            try await waiter.value
            XCTFail("A cancelled gate wait must throw")
        } catch is CancellationError {
        } catch {
            XCTFail("Unexpected error: \(error)")
        }
        XCTAssertEqual(gate.waiterCountForTests, 0)
    }

    func testResumeWithdrawsAPauseWaitingForAnActiveUse() async throws {
        let fake = FakeUnloads()
        let clock = ManualClock()
        let lifecycle = make(fake, clock: clock, idle: .zero)
        let lease = try await lifecycle.beginUse(target())
        lifecycle.notePause(true)
        let release = Task { await lifecycle.release(.pause, target: lmTarget()) }
        await clock.waitForSleepers(1)
        XCTAssertEqual(clock.pending, 1)
        lifecycle.notePause(false)
        lease.end()
        let outcome = await release.value
        XCTAssertEqual(outcome, .notWanted)
        XCTAssertTrue(fake.models.isEmpty)
    }

    func testAPauseTaskStartingAfterResumeUnloadsNothing() async {
        let fake = FakeUnloads()
        let lifecycle = make(fake, idle: .zero)
        lifecycle.notePause(true)
        lifecycle.notePause(false)
        let outcome = await lifecycle.release(.pause, target: lmTarget())
        XCTAssertEqual(outcome, .notWanted)
        XCTAssertTrue(fake.models.isEmpty)
    }

    func testANewUseWaitsForAnAbandonedResizeToSettle() async throws {
        let clock = ManualClock()
        let lifecycle = make(clock: clock, idle: .zero)
        let first = try await lifecycle.beginUse(target())
        let started = LifecycleGate()
        let finish = LifecycleGate()
        let model = target()
        let resize = Task {
            await lifecycle.reconcileLMStudio(
                target: model, contextTokens: 8192, lease: first,
                read: { _, _ in .notRunning.reached },
                load: { _, _, _ in
                    started.open()
                    try? await finish.wait()
                    return "resized"
                })
        }
        try await started.wait()
        let next = Task { try await lifecycle.beginUse(model) }
        await clock.waitForSleepers(1)
        XCTAssertEqual(clock.pending, 1)
        XCTAssertEqual(lifecycle.useCount, 2)
        resize.cancel()
        _ = await resize.value
        first.end()
        XCTAssertEqual(lifecycle.useCount, 1, "the resize owns its barrier after the caller leaves")
        finish.open()
        let second = try await next.value
        XCTAssertEqual(lifecycle.ownedCopies.map(\.instanceID), ["resized"])
        second.end()
        XCTAssertEqual(lifecycle.useCount, 0)
    }

    func testTheUseEndingWhilePausedPaysThePauseRelease() async throws {
        let fake = FakeUnloads()
        let finish = LifecycleGate()
        fake.set { $0.barrier = finish }
        let lifecycle = make(fake, idle: .zero)
        lifecycle.notePause(true)
        let lease = try await lifecycle.beginUse(target())
        lease.end()
        try await fake.modelStarted.wait()
        XCTAssertEqual(fake.models, ["m"])
        lifecycle.notePause(false)
        finish.open()
        let next = try await lifecycle.beginUse(target())
        next.end()
    }

    func testLengtheningOwnedCopyIdleUsesTheOriginalEndOfUse() async throws {
        let clock = ManualClock()
        let lifecycle = make(clock: clock)
        try await owned(lifecycle, "copy")
        await clock.waitForSleepers(1)
        XCTAssertEqual(clock.durations.last, .seconds(600))
        clock.advance(.seconds(400))
        lifecycle.setIdle(.seconds(900))
        await clock.waitForSleepers(2)
        XCTAssertEqual(clock.durations.last, .seconds(500))
        clock.fire()
        _ = await lifecycle.release(.freeMemory, target: target())
    }

    func testShorterRetentionFreesAnOrdinaryModelAfterItsUseEnds() async throws {
        let fake = FakeUnloads()
        let clock = ManualClock()
        let lifecycle = make(fake, clock: clock)
        let lease = try await lifecycle.beginUse(target())
        lifecycle.setIdle(.seconds(60))
        await clock.waitForSleepers(1)
        XCTAssertEqual(clock.pending, 1)
        XCTAssertTrue(fake.models.isEmpty)
        lease.end()
        try await fake.modelStarted.wait()
        XCTAssertEqual(fake.models, ["m"])
        XCTAssertTrue(lifecycle.ownedCopies.isEmpty)
    }

    func testRefusedShorterRetentionRetriesWithTheSameBoundedBackoff() async throws {
        let fake = FakeUnloads()
        fake.modelResult = false
        let clock = ManualClock()
        let lifecycle = make(fake, clock: clock)
        let lease = try await lifecycle.beginUse(target())
        lease.end()
        lifecycle.setIdle(.seconds(60))
        await clock.waitForSleepers(1)
        XCTAssertEqual(fake.models, ["m"])
        XCTAssertEqual(clock.durations.last, .seconds(30))
        for expected in [60, 120, 240, 300, 300] {
            clock.fire()
            await clock.waitForSleepers(1)
            XCTAssertEqual(clock.durations.last, .seconds(expected))
        }
        let attempts = fake.models.count
        fake.modelResult = true
        clock.fire()
        for _ in 0..<2000 where fake.models.count == attempts { await Task.yield() }
        // Joining the release lane waits for the retry's commit without starting an ordinary-model release.
        _ = await lifecycle.release(.shutdown, target: nil)
        XCTAssertEqual(fake.models.count, attempts + 1)
        XCTAssertTrue(lifecycle.ownedCopies.isEmpty)
        lifecycle.setIdle(.zero)
    }

    func testNewUseOrChangedRetentionWithdrawsRefusedShorterRetentionRetry() async throws {
        for change in ["use", "never", "longer"] {
            let fake = FakeUnloads()
            fake.modelResult = false
            let clock = ManualClock()
            let lifecycle = make(fake, clock: clock)
            let first = try await lifecycle.beginUse(target())
            first.end()
            lifecycle.setIdle(.seconds(60))
            await clock.waitForSleepers(1)
            XCTAssertEqual(clock.durations.last, .seconds(30))
            XCTAssertEqual(fake.models.count, 1)
            var newer: LocalModelLifecycle.Lease?
            if change == "use" { newer = try await lifecycle.beginUse(target()) }
            if change == "never" { lifecycle.setIdle(.zero) }
            if change == "longer" { lifecycle.setIdle(.seconds(120)) }
            fake.modelResult = true
            clock.fire()
            newer?.end()
            _ = await lifecycle.release(.shutdown, target: nil)
            XCTAssertEqual(fake.models.count, 1, change)
            lifecycle.setIdle(.zero)
        }
    }

    func testANewerUseWithdrawsShorterRetentionRetirement() async throws {
        let fake = FakeUnloads()
        let clock = ManualClock()
        let lifecycle = make(fake, clock: clock)
        let first = try await lifecycle.beginUse(target())
        lifecycle.setIdle(.seconds(60))
        await clock.waitForSleepers(1)
        XCTAssertEqual(clock.pending, 1)
        let newer = try await lifecycle.beginUse(target())
        first.end()
        newer.end()
        // Entering the same lane joins the cancelled retirement before observing its effects.
        let outcome = await lifecycle.release(.shutdown, target: nil)
        XCTAssertEqual(outcome, .nothingToRelease)
        XCTAssertTrue(fake.models.isEmpty)
    }

    func testTurningOnRetentionFreesAnOrdinaryModelKeptWithoutALimit() async throws {
        let fake = FakeUnloads()
        let lifecycle = make(fake, idle: .zero)
        let lease = try await lifecycle.beginUse(target())
        lease.end()
        lifecycle.setIdle(.seconds(60))
        try await fake.modelStarted.wait()
        XCTAssertEqual(fake.models, ["m"])
    }

    func testNeverWithdrawsAPendingShorterRetentionRetirement() async throws {
        let fake = FakeUnloads()
        let clock = ManualClock()
        let lifecycle = make(fake, clock: clock)
        let lease = try await lifecycle.beginUse(target())
        lifecycle.setIdle(.seconds(60))
        await clock.waitForSleepers(1)
        XCTAssertEqual(clock.pending, 1)
        lifecycle.setIdle(.zero)
        lease.end()
        _ = await lifecycle.release(.shutdown, target: nil)
        XCTAssertTrue(fake.models.isEmpty)
    }

    func testLengtheningOrdinaryModelRetentionDoesNotUnloadIt() async throws {
        let fake = FakeUnloads()
        let lifecycle = make(fake)
        let lease = try await lifecycle.beginUse(target())
        lease.end()
        lifecycle.setIdle(.seconds(900))
        _ = await lifecycle.release(.shutdown, target: nil)
        XCTAssertTrue(fake.models.isEmpty)
    }

    func testRetiringCopiesNeverSendsAnotherServersKey() async throws {
        let fake = FakeUnloads()
        let lifecycle = make(fake, idle: .zero)
        try await owned(lifecycle, "first", key: "first-server-key")
        let other = LocalModelTarget(
            endpoint: "http://127.0.0.1:1235/v1", model: "m", app: .lmStudio, apiKey: "second-server-key")
        let lease = try await lifecycle.beginUse(other)
        await lifecycle.reconcileLMStudio(
            target: other, contextTokens: 8192, lease: lease,
            read: { _, _ in LocalServerState(reach: .reached, models: [], loaded: []) },
            load: { _, _, _ in "second" })
        lease.end()
        XCTAssertEqual(lifecycle.ownedCopies.count, 2)
        let outcome = await lifecycle.release(.shutdown, target: other)
        XCTAssertEqual(outcome, .released)
        XCTAssertEqual(fake.instances.map(\.id), ["first", "second"])
        XCTAssertNil(fake.instances[0].key)
        XCTAssertEqual(fake.instances[1].key, "second-server-key")
    }

    private func target(_ model: String = "m", key: String? = nil) -> LocalModelTarget {
        lmTarget(model, key: key)
    }

    private func make(
        _ fake: FakeUnloads = FakeUnloads(), clock: ManualClock = ManualClock(), idle: Duration = .seconds(600)
    ) -> LocalModelLifecycle {
        LocalModelLifecycle(idle: idle, actions: fake.actions, sleeper: clock.sleeper, now: clock.now)
    }

    private func owned(
        _ lifecycle: LocalModelLifecycle, _ instance: String, model: String = "m", key: String? = nil
    ) async throws {
        let destination = target(model, key: key)
        let lease = try await lifecycle.beginUse(destination)
        await lifecycle.reconcileLMStudio(
            target: destination, contextTokens: 8192, lease: lease,
            read: { _, _ in .notRunning.reached },
            load: { _, _, _ in instance })
        lease.end()
    }

    func testAReleaseWaitsForAUseInFlightAndThenFreesTheModel() async throws {
        let fake = FakeUnloads()
        let lifecycle = make(fake)
        let lease = try await lifecycle.beginUse(target())
        let release = Task { await lifecycle.release(.freeMemory, target: lmTarget()) }
        for _ in 0..<200 { await Task.yield() }
        XCTAssertTrue(fake.models.isEmpty, "nothing is unloaded under a use")
        lease.end()
        let outcome = await release.value
        XCTAssertEqual(outcome, .released)
        XCTAssertEqual(fake.models, ["m"])
    }

    func testLeaseEndIsIdempotent() async throws {
        let lifecycle = make()
        let lease = try await lifecycle.beginUse(target())
        lease.end()
        lease.end()
        XCTAssertEqual(lifecycle.useCount, 0)
    }

    func testFreeMemoryReportsDrainTimedOutAfterItsBoundAndUnloadsNothing() async throws {
        let fake = FakeUnloads()
        let clock = ManualClock()
        let lifecycle = make(fake, clock: clock)
        let lease = try await lifecycle.beginUse(target())
        let release = Task { await lifecycle.release(.freeMemory, target: lmTarget()) }
        await clock.waitForSleepers(1)
        clock.fire()
        let outcome = await release.value
        XCTAssertEqual(outcome, .drainTimedOut)
        XCTAssertTrue(fake.models.isEmpty)
        lease.end()
    }

    func testAUseThatBeginsAfterAnIdleReleaseWasDecidedCancelsIt() async throws {
        let fake = FakeUnloads()
        let lifecycle = make(fake)
        let first = try await lifecycle.beginUse(target())
        let release = Task { await lifecycle.release(.pause, target: lmTarget()) }
        for _ in 0..<100 { await Task.yield() }
        first.end()
        // The release is already past its wait only if it saw no newer use; start one before it commits.
        let second = try await lifecycle.beginUse(target())
        second.end()
        let outcome = await release.value
        XCTAssertTrue([.released, .notWanted].contains(outcome))
        if outcome == .notWanted { XCTAssertTrue(fake.models.isEmpty) }
    }

    func testAPauseDecidedBeforeANewUseIsNotWanted() async throws {
        let fake = FakeUnloads()
        let lifecycle = make(fake)
        let lease = try await lifecycle.beginUse(target())
        let release = Task { await lifecycle.release(.pause, target: lmTarget()) }
        for _ in 0..<100 { await Task.yield() }
        lease.end()
        let again = try await lifecycle.beginUse(target())
        again.end()
        let outcome = await release.value
        XCTAssertTrue([.released, .notWanted].contains(outcome))
    }

    func testAConfigurationThatCameBackIsNotReleased() async throws {
        let fake = FakeUnloads()
        let lifecycle = make(fake)
        let outcome = await lifecycle.release(.configurationChanged, target: target(), wanted: { false })
        XCTAssertEqual(outcome, .notWanted)
        XCTAssertTrue(fake.models.isEmpty)
    }

    func testAConfigurationChangeUnloadsTheModelItNoLongerUses() async throws {
        let fake = FakeUnloads()
        let lifecycle = make(fake)
        let outcome = await lifecycle.release(.configurationChanged, target: target(), wanted: { true })
        XCTAssertEqual(outcome, .released)
        XCTAssertEqual(fake.models, ["m"])
    }

    func testANewUseWaitsForAnUnloadOnItsWayAndThenProceeds() async throws {
        let fake = FakeUnloads()
        let barrier = LifecycleGate()
        fake.set { $0.barrier = barrier }
        let lifecycle = make(fake)
        let release = Task { await lifecycle.release(.freeMemory, target: lmTarget()) }
        try await fake.modelStarted.wait()
        let began = LockedFlag()
        let use = Task {
            let lease = try await lifecycle.beginUse(lmTarget())
            began.set()
            lease.end()
        }
        for _ in 0..<200 { await Task.yield() }
        XCTAssertFalse(began.value, "a use never reaches a model that is being unloaded")
        barrier.open()
        _ = await release.value
        try await use.value
        XCTAssertTrue(began.value)
    }

    func testAUseGivesUpAfterTheUnloadWaitBound() async throws {
        let fake = FakeUnloads()
        let barrier = LifecycleGate()
        fake.set { $0.barrier = barrier }
        let clock = ManualClock()
        let lifecycle = make(fake, clock: clock)
        let release = Task { await lifecycle.release(.freeMemory, target: lmTarget()) }
        for _ in 0..<200 where fake.models.isEmpty { await Task.yield() }
        let use = Task { try await lifecycle.beginUse(lmTarget()) }
        await clock.waitForSleepers(1)
        clock.fire()
        do {
            _ = try await use.value
            XCTFail("expected a timeout")
        } catch let error as LocalModelLifecycleError {
            XCTAssertEqual(error, .unloadInProgress)
        }
        barrier.open()
        _ = await release.value
    }

    func testAFailedUnloadStaysOwedAndIsRetried() async throws {
        let fake = FakeUnloads()
        let lifecycle = make(fake)
        try await owned(lifecycle, "i1")
        XCTAssertEqual(lifecycle.ownedCopies.map(\.instanceID), ["i1"])
        fake.set {
            $0.instanceResult = false
            $0.modelResult = false
        }
        let first = await lifecycle.release(.freeMemory, target: target())
        XCTAssertEqual(first, .failed)
        XCTAssertEqual(lifecycle.ownedCopies.count, 1, "a copy that could not be freed stays owed")
        fake.set {
            $0.instanceResult = true
            $0.modelResult = true
        }
        let second = await lifecycle.release(.freeMemory, target: target())
        XCTAssertEqual(second, .released)
        XCTAssertTrue(lifecycle.ownedCopies.isEmpty)
    }

    func testAnUnloadTriesTheKeySavedNowThenTheKeyTheCopyWasLoadedWith() async throws {
        let fake = FakeUnloads()
        fake.set {
            $0.requiredKey = "old"
            $0.modelResult = false
        }
        let lifecycle = make(fake)
        try await owned(lifecycle, "i1", key: "old")
        let outcome = await lifecycle.release(.freeMemory, target: target(key: "new"))
        XCTAssertEqual(outcome, .released)
        XCTAssertEqual(fake.instances.map(\.key), ["new", "old"])
    }

    func testACopyOnAnotherServerIsLeftAloneByFreeMemory() async throws {
        let fake = FakeUnloads()
        let lifecycle = make(fake)
        try await owned(lifecycle, "i1")
        let other = LocalModelTarget(
            endpoint: "http://127.0.0.1:11434/v1", model: "m", app: .ollama, apiKey: nil)
        _ = await lifecycle.release(.freeMemory, target: other)
        XCTAssertTrue(fake.instances.isEmpty)
        XCTAssertEqual(lifecycle.ownedCopies.count, 1)
    }

    func testTheIdleReleaseFreesOnlyScribeLoadedCopiesAndAsksTheAppNothing() async throws {
        let fake = FakeUnloads()
        let clock = ManualClock()
        let lifecycle = make(fake, clock: clock)
        try await owned(lifecycle, "i1")
        await clock.waitForSleepers(1)
        clock.fire()
        for _ in 0..<500 where lifecycle.ownedCopies.count > 0 { await Task.yield() }
        XCTAssertTrue(lifecycle.ownedCopies.isEmpty)
        XCTAssertTrue(fake.models.isEmpty, "idle sends no explicit model unload")
        XCTAssertEqual(fake.instances.map(\.id), ["i1"])
    }

    func testARefusedIdleCopyRetriesWithBoundedBackoffAndUsesTheNewSavedKey() async throws {
        let fake = FakeUnloads()
        fake.requiredKey = "rotated"
        let clock = ManualClock()
        let lifecycle = make(fake, clock: clock)
        try await owned(lifecycle, "i1")
        await clock.waitForSleepers(1)
        clock.fire()
        await clock.waitForSleepers(1)
        XCTAssertEqual(lifecycle.ownedCopies.count, 1)
        XCTAssertEqual(clock.durations.last, .seconds(30))
        for expected in [60, 120, 240, 300, 300] {
            clock.fire()
            await clock.waitForSleepers(1)
            XCTAssertEqual(clock.durations.last, .seconds(expected))
            XCTAssertEqual(lifecycle.ownedCopies.count, 1)
        }
        lifecycle.useSavedKeys { _ in "rotated" }
        clock.fire()
        for _ in 0..<2000 where !lifecycle.ownedCopies.isEmpty { await Task.yield() }
        XCTAssertTrue(lifecycle.ownedCopies.isEmpty)
        XCTAssertEqual(fake.instances.last?.key, "rotated")
        XCTAssertTrue(fake.models.isEmpty, "idle retries only tracked instance ids")
        lifecycle.setIdle(.zero)
    }

    func testANewUseOrNeverSettingWithdrawsARefusedIdleRetry() async throws {
        for startsUse in [false, true] {
            let fake = FakeUnloads()
            fake.instanceResult = false
            let clock = ManualClock()
            let lifecycle = make(fake, clock: clock)
            try await owned(lifecycle, "i1")
            await clock.waitForSleepers(1)
            clock.fire()
            await clock.waitForSleepers(1)
            XCTAssertEqual(clock.durations.last, .seconds(30))
            let attempts = fake.instances.count
            let lease = startsUse ? try await lifecycle.beginUse(target()) : nil
            if !startsUse { lifecycle.setIdle(.zero) }
            fake.instanceResult = true
            clock.fire()
            for _ in 0..<200 { await Task.yield() }
            XCTAssertEqual(fake.instances.count, attempts)
            XCTAssertEqual(lifecycle.ownedCopies.count, 1)
            lifecycle.setIdle(.zero)
            lease?.end()
            _ = await lifecycle.release(.shutdown, target: nil)
        }
    }

    func testResumingWithdrawsARefusedPauseRetry() async throws {
        let fake = FakeUnloads()
        fake.modelResult = false
        let clock = ManualClock()
        let lifecycle = make(fake, clock: clock, idle: .zero)
        let lease = try await lifecycle.beginUse(target())
        lifecycle.notePause(true)
        lease.end()
        await clock.waitForSleepers(1)
        XCTAssertEqual(clock.durations.last, .seconds(30))
        XCTAssertEqual(fake.models.count, 1)
        lifecycle.notePause(false)
        fake.modelResult = true
        clock.fire()
        for _ in 0..<200 { await Task.yield() }
        XCTAssertEqual(fake.models.count, 1)
        XCTAssertTrue(lifecycle.ownedCopies.isEmpty)
    }

    func testAChangedIdleTimeRestartsTheCountdownAndZeroNeverFrees() async throws {
        let fake = FakeUnloads()
        let clock = ManualClock()
        let lifecycle = make(fake, clock: clock)
        try await owned(lifecycle, "i1")
        await clock.waitForSleepers(1)
        lifecycle.setIdle(.zero)
        clock.fire()
        for _ in 0..<50 { await Task.yield() }
        XCTAssertEqual(lifecycle.ownedCopies.count, 1, "zero is never")
        lifecycle.setIdle(.seconds(60))
        try await fake.modelStarted.wait()
        _ = await lifecycle.release(.shutdown, target: nil)
        XCTAssertTrue(lifecycle.ownedCopies.isEmpty)
    }

    func testIdleMinutesDefaultToTenAndAreStoredNeverNegative() {
        let fixture = makeCleanupStore()
        XCTAssertEqual(fixture.store.localModelIdleMinutes, 10)
        fixture.store.localModelIdleMinutes = 0
        XCTAssertEqual(fixture.store.localModelIdleMinutes, 0)
        fixture.store.localModelIdleMinutes = -4
        XCTAssertEqual(fixture.store.localModelIdleMinutes, 0)
        fixture.store.localModelIdleMinutes = 25
        XCTAssertEqual(fixture.store.localModelIdleMinutes, 25)
    }

    func testANewUseCancelsTheIdleCountdown() async throws {
        let fake = FakeUnloads()
        let clock = ManualClock()
        let lifecycle = make(fake, clock: clock)
        try await owned(lifecycle, "i1")
        await clock.waitForSleepers(1)
        let lease = try await lifecycle.beginUse(target())
        clock.fire()
        for _ in 0..<200 { await Task.yield() }
        XCTAssertTrue(fake.instances.isEmpty, "a model in use is never freed by the idle countdown")
        lease.end()
    }

    func testAnAbandonedLoadStillRecordsItsCopyAndKeepsItsLease() async throws {
        let fake = FakeUnloads()
        let lifecycle = make(fake)
        let barrier = LifecycleGate()
        let lease = try await lifecycle.beginUse(target())
        let caller = Task {
            await lifecycle.reconcileLMStudio(
                target: lmTarget(), contextTokens: 8192, lease: lease,
                read: { _, _ in .notRunning.reached },
                load: { _, _, _ in
                    try? await barrier.wait()
                    return "late"
                })
        }
        for _ in 0..<200 { await Task.yield() }
        caller.cancel()
        _ = await caller.value
        lease.end()
        XCTAssertEqual(lifecycle.useCount, 1, "the abandoned load still counts as a use")
        let release = Task { await lifecycle.release(.freeMemory, target: lmTarget()) }
        for _ in 0..<200 { await Task.yield() }
        XCTAssertTrue(fake.instances.isEmpty, "no release frees the model under the load")
        barrier.open()
        let outcome = await release.value
        XCTAssertEqual(outcome, .released)
        XCTAssertEqual(fake.models, ["m"], "the copy the abandoned load made is freed once it lands")
        XCTAssertTrue(lifecycle.ownedCopies.isEmpty)
    }

    func testARefusedSizeIsNotAskedForAgainUntilFreeMemory() async throws {
        let lifecycle = make()
        let loads = Counter()
        for _ in 0..<2 {
            let lease = try await lifecycle.beginUse(target())
            await lifecycle.reconcileLMStudio(
                target: target(), contextTokens: 8192, lease: lease,
                read: { _, _ in .notRunning.reached },
                load: { _, _, _ in
                    loads.bump()
                    return nil
                })
            lease.end()
        }
        XCTAssertEqual(loads.value, 1)
        _ = await lifecycle.release(.freeMemory, target: target())
        let lease = try await lifecycle.beginUse(target())
        await lifecycle.reconcileLMStudio(
            target: target(), contextTokens: 8192, lease: lease,
            read: { _, _ in .notRunning.reached },
            load: { _, _, _ in
                loads.bump()
                return nil
            })
        lease.end()
        XCTAssertEqual(loads.value, 2)
    }

    func testACopyAtAnotherSizeIsReplacedOnlyWhileTheLeaseIsTheOnlyUse() async throws {
        let fake = FakeUnloads()
        let lifecycle = make(fake)
        let held = LocalServerState(
            reach: .reached, models: [],
            loaded: [LocalServerLoadedModel("m", 1, contextTokens: 4096, instanceID: "old", remainingTTLSeconds: 100)],
            failureDetail: nil)
        let loads = Counter()
        let first = try await lifecycle.beginUse(target())
        let second = try await lifecycle.beginUse(target())
        await lifecycle.reconcileLMStudio(
            target: target(), contextTokens: 8192, lease: first, read: { _, _ in held },
            load: { _, _, _ in
                loads.bump()
                return "new"
            })
        XCTAssertEqual(loads.value, 0, "another use is in flight, so the copy is used as it is")
        XCTAssertTrue(fake.instances.isEmpty)
        second.end()
        await lifecycle.reconcileLMStudio(
            target: target(), contextTokens: 8192, lease: first, read: { _, _ in held },
            load: { _, _, _ in
                loads.bump()
                return "new"
            })
        XCTAssertEqual(loads.value, 1)
        XCTAssertEqual(fake.instances.map(\.id), ["old"])
        first.end()
    }

    func testACopyLoadedByHandIsUsedAsItIs() async throws {
        let fake = FakeUnloads()
        let lifecycle = make(fake)
        let held = LocalServerState(
            reach: .reached, models: [],
            loaded: [LocalServerLoadedModel("m", 1, contextTokens: 4096, instanceID: "hand", remainingTTLSeconds: nil)],
            failureDetail: nil)
        let loads = Counter()
        let lease = try await lifecycle.beginUse(target())
        await lifecycle.reconcileLMStudio(
            target: target(), contextTokens: 8192, lease: lease, read: { _, _ in held },
            load: { _, _, _ in
                loads.bump()
                return "x"
            })
        lease.end()
        XCTAssertEqual(loads.value, 0)
        XCTAssertTrue(fake.instances.isEmpty)
    }

    func testACopyTheServerNoLongerListsIsForgotten() async throws {
        let lifecycle = make()
        try await owned(lifecycle, "gone")
        let lease = try await lifecycle.beginUse(target())
        let listing = LocalServerState(
            reach: .reached, models: [],
            loaded: [LocalServerLoadedModel("m", 1, contextTokens: 8192, instanceID: "other", remainingTTLSeconds: 5)],
            failureDetail: nil)
        await lifecycle.reconcileLMStudio(
            target: target(), contextTokens: 8192, lease: lease, read: { _, _ in listing },
            load: { _, _, _ in nil })
        lease.end()
        XCTAssertTrue(lifecycle.ownedCopies.isEmpty)
    }

    func testShutdownReleaseIsBoundedAndCancelledWorkReportsFailed() async throws {
        let fake = FakeUnloads()
        let barrier = LifecycleGate()
        let clock = ManualClock()
        let lifecycle = make(fake, clock: clock)
        try await owned(lifecycle, "i1")
        fake.set { $0.barrier = barrier }
        let release = Task { await lifecycle.release(.shutdown, target: nil) }
        for _ in 0..<200 where fake.instances.isEmpty { await Task.yield() }
        await clock.waitForSleepers(1)
        clock.fire()
        let outcome = await release.value
        XCTAssertEqual(outcome, .failed)
        XCTAssertEqual(lifecycle.ownedCopies.count, 1, "an interrupted unload stays owed")
    }

    func testShutdownFreesEveryOwnedCopyWithoutAnExplicitModelUnload() async throws {
        let fake = FakeUnloads()
        let lifecycle = make(fake)
        try await owned(lifecycle, "i1")
        try await owned(lifecycle, "i2")
        let outcome = await lifecycle.release(.shutdown, target: nil)
        XCTAssertEqual(outcome, .released)
        XCTAssertEqual(Set(fake.instances.map(\.id)), ["i1", "i2"])
        XCTAssertTrue(fake.models.isEmpty)
    }

    func testNothingToReleaseIsReportedAsSuch() async {
        let outcome = await make().release(.idle, target: nil)
        XCTAssertEqual(outcome, .nothingToRelease)
    }

    func testTargetsCompareByServerAndModelAndIgnoreTheKey() {
        let a = LocalModelTarget(endpoint: "http://localhost:1234/v1", model: "m", app: .lmStudio, apiKey: "a")
        let b = LocalModelTarget(endpoint: "http://127.0.0.1:1234/v1", model: "m", app: .lmStudio, apiKey: "b")
        let c = LocalModelTarget(endpoint: "http://127.0.0.1:1234/v1", model: "other", app: .lmStudio, apiKey: "a")
        XCTAssertEqual(a, b)
        XCTAssertNotEqual(a, c)
    }
}

private final class LockedFlag: @unchecked Sendable {
    private let lock = NSLock()
    private var flag = false
    func set() { lock.withLock { flag = true } }
    var value: Bool { lock.withLock { flag } }
}

private final class Counter: @unchecked Sendable {
    private let lock = NSLock()
    private var count = 0
    func bump() { lock.withLock { count += 1 } }
    var value: Int { lock.withLock { count } }
}

extension LocalServerState {
    fileprivate var reached: LocalServerState { self }
}
