import Foundation
import os

/// A model on this Mac that cleanup uses: the app, its loopback address, the model and the key saved for that server.
/// Two targets are the same model when the app, the server (host and port) and the model name agree; the key is only
/// what opens the server.
struct LocalModelTarget: Sendable, Equatable {
    var endpoint: String
    var model: String
    var app: LocalServerApp
    var apiKey: String?

    static func == (lhs: Self, rhs: Self) -> Bool {
        lhs.app == rhs.app && LocalModelLifecycle.sameServer(lhs.endpoint, rhs.endpoint)
            && LocalServerClient.sameModel(lhs.model, rhs.model)
    }
}

enum LocalModelLifecycleError: Error, Equatable, Sendable {
    /// An unload was still on its way after the bound, so the request is not sent.
    case unloadInProgress
    case drainTimedOut
}

enum LocalModelReleaseReason: Sendable, Equatable {
    /// The idle time passed: only copies Scribe loaded at a chosen size are freed. Every request carried the app's own
    /// keep-alive, so the apps free everything else by themselves.
    case idle
    case pause
    case configurationChanged
    case freeMemory
    case shutdown
}

enum LocalModelReleaseOutcome: Sendable, Equatable {
    case released
    case nothingToRelease
    /// A newer use, or a configuration that uses the model again, made the release unwanted.
    case notWanted
    /// Uses in flight did not finish within the reason's bound. Nothing was unloaded.
    case drainTimedOut
    /// An unload was refused or failed; what could not be freed stays owed.
    case failed
    case cancelled
}

/// The one place that decides when a model on this Mac may be unloaded and which loaded copies are Scribe's.
///
/// This is the macOS side of Windows' release lane and LM Studio ownership in `TextCleanupService`:
/// - every use of a model (a dictation's cleanup, a readiness check, Test connection, a one-off request) holds a
///   `Lease` for its whole length;
/// - a release waits for every lease to end, then decides in one step with the lease count, the use revision and the
///   caller's predicate, and publishes the unload it starts so every use that begins meanwhile waits for it;
/// - a copy LM Studio loaded at a chosen size is recorded once LM Studio names its instance, and is retired by idle,
///   pause, a configuration change, Free memory and shutdown, with the key now saved first and the key it was loaded
///   with second; one that fails to unload stays owed.
/// Nothing here sends a request outside loopback: the unload and load actions are `LocalServerClient`'s, which refuses
/// every other host.
final class LocalModelLifecycle: Sendable {
    struct Bounds: Sendable {
        /// An idle or pause release waits this long for uses in flight. It is deferred, not dropped: the next idle
        /// countdown or pause asks again.
        var automaticDrain = Duration.seconds(300)
        var freeMemoryDrain = Duration.seconds(30)
        /// How long a new use waits for an unload already on its way.
        var unloadWait = Duration.seconds(20)
        var shutdown = Duration.seconds(2)
    }

    struct Actions: Sendable {
        var unloadModel: @Sendable (_ endpoint: String, _ model: String, _ apiKey: String?) async -> Bool
        var unloadInstance: @Sendable (_ endpoint: String, _ instanceID: String, _ apiKey: String?) async -> Bool

        static let live = connected(to: .shared)

        static func connected(to session: URLSession) -> Actions {
            Actions(
                unloadModel: { endpoint, model, apiKey in
                    await LocalServerClient(session: session).unload(endpoint, modelID: model, apiKey: apiKey)
                },
                unloadInstance: { endpoint, instanceID, apiKey in
                    await LocalServerClient(session: session).unloadInstance(
                        endpoint, instanceID: instanceID, apiKey: apiKey)
                })
        }
    }

    /// A copy LM Studio loaded for Scribe at a chosen size.
    struct Copy: Sendable, Equatable {
        var endpoint: String
        var model: String
        var instanceID: String
        var loadedKey: String?
    }

    /// One use of a model. `end()` is idempotent and synchronous, so it is safe in a `defer`.
    final class Lease: Sendable {
        private let lifecycle: LocalModelLifecycle
        private let ended = OSAllocatedUnfairLock(initialState: false)

        fileprivate init(_ lifecycle: LocalModelLifecycle) {
            self.lifecycle = lifecycle
        }

        func end() {
            let first = ended.withLock { done -> Bool in
                defer { done = true }
                return !done
            }
            if first { lifecycle.endUse() }
        }

        /// A second lease on the same model that outlives this one, for a load that is abandoned by its caller.
        fileprivate func extend() -> Lease {
            lifecycle.extendUse()
        }

        /// Whether this lease is the only use of the model: the rule for every change to the copy requests reach.
        var isSoleUse: Bool { lifecycle.useCount == 1 }
    }

    static let shared = LocalModelLifecycle(idle: .seconds(LocalModelDefaults.keepAliveMinutes * 60))

    private struct State {
        var uses = 0
        var revision: UInt64 = 0
        var served: LocalModelTarget?
        var inFlightUnload: LifecycleGate?
        var drainGates: [LifecycleGate] = []
        var copies: [Copy] = []
        var refused: Set<String> = []
        var idleTask: Task<Void, Never>?
        var idleGeneration: UInt64 = 0
        var idle: Duration = .zero
        var isPaused = false
        var idleSince: ContinuousClock.Instant?
    }

    private let state = OSAllocatedUnfairLock(initialState: State())
    private let bounds: Bounds
    private let actions: Actions
    private let sleeper: @Sendable (Duration) async throws -> Void
    private let now: @Sendable () -> ContinuousClock.Instant
    private let releaseLane = AsyncLane()
    private let loadLane = AsyncLane()

    init(
        idle: Duration,
        bounds: Bounds = Bounds(),
        actions: Actions = .live,
        sleeper: @escaping @Sendable (Duration) async throws -> Void = { try await Task.sleep(for: $0) },
        now: @escaping @Sendable () -> ContinuousClock.Instant = { ContinuousClock.now }
    ) {
        state.withLock { $0.idle = idle }
        self.bounds = bounds
        self.actions = actions
        self.sleeper = sleeper
        self.now = now
    }

    var useCount: Int { state.withLock { $0.uses } }
    var ownedCopies: [Copy] { state.withLock { $0.copies } }
    var servedTarget: LocalModelTarget? { state.withLock { $0.served } }

    func notePause(_ paused: Bool) {
        state.withLock {
            guard $0.isPaused != paused else { return }
            $0.isPaused = paused
            $0.revision &+= 1
        }
    }

    // MARK: Uses

    /// Starts a use of `target`. Waits for an unload already on its way, at most `Bounds.unloadWait`, and throws
    /// `unloadInProgress` past that, so a dictation is typed as heard instead of reaching a model being unloaded.
    func beginUse(_ target: LocalModelTarget) async throws -> Lease {
        while true {
            let gate = state.withLock { $0.inFlightUnload }
            guard let gate else { break }
            do {
                try await OperationDeadline.run(within: bounds.unloadWait, sleep: sleeper) {
                    try await gate.wait()
                }
            } catch is OperationDeadlineError {
                ScribeLog.warning(.cleanup, "A local model use gave up waiting for an unload")
                throw LocalModelLifecycleError.unloadInProgress
            }
        }
        let admitted = state.withLock { state -> Bool in
            // An unload published between the check and here sends the caller around again.
            guard state.inFlightUnload == nil else { return false }
            state.uses += 1
            state.revision &+= 1
            state.served = target
            state.idleSince = nil
            state.idleTask?.cancel()
            state.idleTask = nil
            state.idleGeneration &+= 1
            return true
        }
        if admitted { return Lease(self) }
        return try await beginUse(target)
    }

    private func extendUse() -> Lease {
        state.withLock { $0.uses += 1 }
        return Lease(self)
    }

    private func endUse() {
        let gates = state.withLock { state -> [LifecycleGate] in
            state.uses -= 1
            guard state.uses == 0 else { return [] }
            state.idleSince = now()
            let gates = state.drainGates
            state.drainGates = []
            return gates
        }
        for gate in gates { gate.open() }
        scheduleIdleReleaseIfOwed()
    }

    // MARK: Idle

    /// Changes the idle time the next countdown uses; zero never frees on idle. A countdown already running is
    /// recalculated from the end of the last use, so changing the time never postpones a release already owed.
    func setIdle(_ idle: Duration) {
        let changed = state.withLock { state -> Bool in
            guard state.idle != idle else { return false }
            state.idle = idle
            state.idleTask?.cancel()
            state.idleTask = nil
            state.idleGeneration &+= 1
            return true
        }
        if changed { scheduleIdleReleaseIfOwed() }
    }

    private func scheduleIdleReleaseIfOwed() {
        let sleeper = self.sleeper
        state.withLock { state in
            let idle = state.idle
            guard state.uses == 0 else { return }
            let paused = state.isPaused
            guard paused || (!state.copies.isEmpty && idle > .zero) else { return }
            let elapsed = state.idleSince.map { $0.duration(to: now()) } ?? .zero
            let remaining = max(.zero, idle - elapsed)
            state.idleTask?.cancel()
            state.idleGeneration &+= 1
            let generation = state.idleGeneration
            let revision = state.revision
            let target = state.served
            state.idleTask = Task { [weak self] in
                if !paused {
                    do { try await sleeper(remaining) } catch { return }
                }
                guard let self, !Task.isCancelled else { return }
                _ = await self.release(
                    paused ? .pause : .idle, target: paused ? target : nil,
                    decidedAt: revision, generation: generation)
            }
        }
    }

    // MARK: Release

    /// Frees what `reason` frees, once no use is in flight. `wanted` is asked in the committing step, so a
    /// configuration that came back (A, B, A) or a predicate that went false cancels the release.
    func release(
        _ reason: LocalModelReleaseReason,
        target: LocalModelTarget?,
        wanted: @escaping @Sendable () -> Bool = { true }
    ) async -> LocalModelReleaseOutcome {
        let revision = state.withLock { $0.revision }
        return await release(reason, target: target, decidedAt: revision, generation: nil, wanted: wanted)
    }

    private func release(
        _ reason: LocalModelReleaseReason,
        target: LocalModelTarget?,
        decidedAt revision: UInt64,
        generation: UInt64?,
        wanted: @escaping @Sendable () -> Bool = { true }
    ) async -> LocalModelReleaseOutcome {
        let drainBound: Duration
        switch reason {
        case .freeMemory: drainBound = bounds.freeMemoryDrain
        case .shutdown: drainBound = bounds.shutdown
        default: drainBound = bounds.automaticDrain
        }
        let overall: Duration? = reason == .shutdown ? bounds.shutdown : nil

        let work: @Sendable () async throws -> LocalModelReleaseOutcome = {
            try await self.releaseLane.run {
                try await self.runRelease(
                    reason, target: target, revision: revision, generation: generation, drainBound: drainBound,
                    wanted: wanted)
            }
        }
        do {
            let outcome: LocalModelReleaseOutcome
            if let overall {
                outcome = try await OperationDeadline.run(within: overall, sleep: sleeper, work)
            } else {
                outcome = try await work()
            }
            ScribeLog.info(.cleanup, "Local model release", .name("reason", reason), .name("outcome", outcome))
            return outcome
        } catch is OperationDeadlineError {
            ScribeLog.warning(.cleanup, "Local model release ran out of time", .name("reason", reason))
            return .failed
        } catch {
            return .cancelled
        }
    }

    private func runRelease(
        _ reason: LocalModelReleaseReason,
        target: LocalModelTarget?,
        revision: UInt64,
        generation: UInt64?,
        drainBound: Duration,
        wanted: @Sendable () -> Bool
    ) async throws -> LocalModelReleaseOutcome {
        while true {
            let gate = state.withLock { state -> LifecycleGate? in
                guard state.uses > 0 else { return nil }
                let gate = LifecycleGate()
                state.drainGates.append(gate)
                return gate
            }
            if let gate {
                do {
                    try await OperationDeadline.run(within: drainBound, sleep: sleeper) { try await gate.wait() }
                } catch is OperationDeadlineError {
                    return .drainTimedOut
                }
                continue
            }

            enum Decision {
                case retry
                case stop(LocalModelReleaseOutcome)
                case go(explicit: LocalModelTarget?, copies: [Copy], gate: LifecycleGate)
            }
            let decision = state.withLock { state -> Decision in
                guard state.uses == 0 else { return .retry }
                if reason == .pause, !state.isPaused { return .stop(.notWanted) }
                let decidedByRevision = reason == .idle || reason == .pause
                if decidedByRevision, state.revision != revision { return .stop(.notWanted) }
                if let generation, state.idleGeneration != generation { return .stop(.notWanted) }
                guard wanted() else { return .stop(.notWanted) }
                let explicit: LocalModelTarget? = (reason == .idle || reason == .shutdown) ? nil : target
                let copies: [Copy]
                switch reason {
                case .freeMemory, .configurationChanged:
                    copies = state.copies.filter { copy in
                        guard let target else { return false }
                        return Self.sameServer(copy.endpoint, target.endpoint)
                            && LocalServerClient.sameModel(copy.model, target.model)
                    }
                default:
                    copies = state.copies
                }
                if reason == .freeMemory { state.refused.removeAll() }
                guard explicit != nil || !copies.isEmpty else { return .stop(.nothingToRelease) }
                let gate = LifecycleGate()
                state.inFlightUnload = gate
                return .go(explicit: explicit, copies: copies, gate: gate)
            }
            switch decision {
            case .retry: continue
            case .stop(let outcome): return outcome
            case .go(let explicit, let copies, let gate):
                var freed: [Copy] = []
                var modelFreed = false
                if let explicit {
                    modelFreed = await actions.unloadModel(explicit.endpoint, explicit.model, explicit.apiKey)
                }
                var copyFailed = false
                for copy in copies {
                    if modelFreed, let explicit, Self.sameServer(copy.endpoint, explicit.endpoint),
                        LocalServerClient.sameModel(copy.model, explicit.model)
                    {
                        freed.append(copy)
                        continue
                    }
                    if await unload(copy, currentKey: target?.apiKey) {
                        freed.append(copy)
                    } else {
                        copyFailed = true
                    }
                }
                // A model-level unload may be refused for a copy that the instance unload then freed.
                let modelCopyFreed =
                    explicit.map { explicit in
                        freed.contains {
                            Self.sameServer($0.endpoint, explicit.endpoint)
                                && LocalServerClient.sameModel($0.model, explicit.model)
                        }
                    } ?? false
                let failed = copyFailed || (explicit != nil && !modelFreed && !modelCopyFreed)
                let freedNow = freed
                state.withLock { state in
                    state.copies.removeAll { copy in freedNow.contains(copy) }
                    state.inFlightUnload = nil
                }
                gate.open()
                if failed {
                    ScribeLog.warning(
                        .cleanup, "A local model could not be unloaded", .count("owed", copies.count - freed.count))
                    return .failed
                }
                return .released
            }
        }
    }

    /// The key saved now first, then the one the copy was loaded with.
    private func unload(_ copy: Copy, currentKey: String?) async -> Bool {
        if await actions.unloadInstance(copy.endpoint, copy.instanceID, currentKey) { return true }
        if copy.loadedKey != currentKey,
            await actions.unloadInstance(copy.endpoint, copy.instanceID, copy.loadedKey)
        {
            return true
        }
        return false
    }

    /// Forgets what the server no longer lists (it unloaded the copy by itself), for the same server only.
    func forgetUnlisted(endpoint: String, model: String, listed: Set<String>) {
        state.withLock { state in
            state.copies.removeAll { copy in
                Self.sameServer(copy.endpoint, endpoint) && !listed.contains(copy.instanceID)
            }
        }
    }

    // MARK: LM Studio chosen size

    /// Makes the copy of `target.model` that requests reach the size `contextTokens`, only while `lease` is the only
    /// use of the model. A copy at the right size is used as it is, one loaded by hand (no time to live) too, one at
    /// another size is unloaded first, and a size LM Studio refuses is not asked for again. A load another request
    /// abandons keeps a lease of its own until its copy is recorded, so no release frees the model under it.
    func reconcileLMStudio(
        target: LocalModelTarget,
        contextTokens: Int,
        lease: Lease,
        read: @escaping @Sendable (_ endpoint: String, _ apiKey: String?) async -> LocalServerState,
        load: @escaping @Sendable (_ endpoint: String, _ model: String, _ contextTokens: Int) async -> String?
    ) async {
        let refusedKey = "\(target.endpoint)|\(target.model)|\(contextTokens)"
        guard !state.withLock({ $0.refused.contains(refusedKey) }) else { return }
        let observed = await read(target.endpoint, target.apiKey)
        if observed.reach == .reached {
            let listed = Set(observed.loaded.compactMap { $0.instanceID })
            forgetUnlisted(endpoint: target.endpoint, model: target.model, listed: listed)
        }
        let held = observed.reach == .reached ? observed.loaded(for: target.model) : nil
        if let held, held.contextTokens == contextTokens {
            state.withLock { _ = $0.refused.remove(refusedKey) }
            return
        }
        if let held, held.remainingTTLSeconds == nil { return }
        let change = state.withLock { state -> LifecycleGate? in
            guard state.uses == 1, state.inFlightUnload == nil else { return nil }
            let gate = LifecycleGate()
            state.inFlightUnload = gate
            return gate
        }
        guard let change else { return }
        var transferred = false
        defer { if !transferred { finishChange(change) } }
        if let held {
            // Loaded by hand, or by something that is not Scribe: used as it is.
            guard held.remainingTTLSeconds != nil else { return }
            guard lease.isSoleUse else { return }
            if let instance = held.instanceID {
                guard
                    await unload(
                        Copy(
                            endpoint: target.endpoint, model: target.model, instanceID: instance,
                            loadedKey: target.apiKey), currentKey: target.apiKey)
                else { return }
                state.withLock { state in
                    state.copies.removeAll {
                        $0.instanceID == instance && Self.sameServer($0.endpoint, target.endpoint)
                    }
                }
            } else {
                return
            }
        } else if !lease.isSoleUse {
            return
        }
        guard lease.isSoleUse, !Task.isCancelled else { return }

        let settleLease = lease.extend()
        let done = LifecycleGate()
        transferred = true
        let settle = Task { [self] in
            defer {
                finishChange(change)
                settleLease.end()
            }
            do {
                try await loadLane.run {
                    let instance = await load(target.endpoint, target.model, contextTokens)
                    self.recordLoad(target: target, contextTokens: contextTokens, instance: instance)
                }
            } catch {
                ScribeLog.debug(.cleanup, "A local model load was cancelled before it started")
            }
            done.open()
        }
        _ = settle
        // A cancelled caller returns here at once; the load keeps its lease and records its copy when it lands.
        try? await done.wait()
    }

    private func finishChange(_ gate: LifecycleGate) {
        state.withLock {
            if $0.inFlightUnload === gate { $0.inFlightUnload = nil }
        }
        gate.open()
    }

    private func recordLoad(target: LocalModelTarget, contextTokens: Int, instance: String?) {
        state.withLock { state in
            guard let instance else {
                state.refused.insert("\(target.endpoint)|\(target.model)|\(contextTokens)")
                return
            }
            let copy = Copy(
                endpoint: target.endpoint, model: target.model, instanceID: instance, loadedKey: target.apiKey)
            if !state.copies.contains(copy) { state.copies.append(copy) }
        }
    }

    static func sameServer(_ first: String, _ second: String) -> Bool {
        guard let a = URLComponents(string: first), let b = URLComponents(string: second) else {
            return first == second
        }
        func port(_ c: URLComponents) -> Int { c.port ?? (c.scheme == "https" ? 443 : 80) }
        func host(_ c: URLComponents) -> String {
            let h = (c.host ?? "").lowercased()
            return h == "localhost" || h == "::1" ? "127.0.0.1" : h
        }
        return host(a) == host(b) && port(a) == port(b)
    }
}

/// A one-shot, cancellable gate: `wait()` returns once `open()` has been called, or throws `CancellationError` when
/// the waiting task is cancelled first.
final class LifecycleGate: Sendable {
    private struct State {
        var isOpen = false
        var next: UInt64 = 0
        var registering: Set<UInt64> = []
        var waiters: [UInt64: CheckedContinuation<Void, Error>] = [:]
        var cancelledEarly: Set<UInt64> = []
    }

    private let state = OSAllocatedUnfairLock(initialState: State())

    var waiterCountForTests: Int {
        state.withLock { $0.registering.count + $0.waiters.count + $0.cancelledEarly.count }
    }

    func open() {
        let waiters = state.withLock { state -> [CheckedContinuation<Void, Error>] in
            state.isOpen = true
            let all = Array(state.waiters.values)
            state.waiters = [:]
            return all
        }
        for waiter in waiters { waiter.resume() }
    }

    func wait() async throws {
        let id = state.withLock { state -> UInt64 in
            state.next += 1
            state.registering.insert(state.next)
            return state.next
        }
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
                let action = state.withLock { state -> Int in
                    state.registering.remove(id)
                    if state.cancelledEarly.remove(id) != nil { return 1 }
                    if state.isOpen { return 0 }
                    state.waiters[id] = continuation
                    return 2
                }
                switch action {
                case 0: continuation.resume()
                case 1: continuation.resume(throwing: CancellationError())
                default: break
                }
            }
        } onCancel: {
            let waiter = state.withLock { state -> CheckedContinuation<Void, Error>? in
                if let waiter = state.waiters.removeValue(forKey: id) { return waiter }
                guard state.registering.contains(id) else { return nil }
                state.cancelledEarly.insert(id)
                return nil
            }
            waiter?.resume(throwing: CancellationError())
        }
    }
}
