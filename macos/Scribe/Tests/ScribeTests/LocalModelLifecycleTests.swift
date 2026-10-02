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
        await resize.value
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
        for _ in 0..<200 where fake.models.isEmpty { await Task.yield() }
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
        await caller.value
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
