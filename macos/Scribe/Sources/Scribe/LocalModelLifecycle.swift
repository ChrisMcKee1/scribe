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
    case closing
}

enum LMStudioContextOutcome: Sendable, Equatable {
    case ready
    case busy
    case loadRefused
    case unavailable
}

enum LocalModelReleaseReason: Sendable, Equatable {
    /// The idle time passed: only copies Scribe loaded at a chosen size are freed. Every request carried the app's own
    /// keep-alive, so the apps free everything else by themselves.
    case idle
    case pause
    case configurationChanged
    case retentionShortened
    case candidateFinished
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
    final class Candidate: Sendable {
        let id = UUID()
        let wanted: @Sendable () -> Bool
        private let ended = OSAllocatedUnfairLock(initialState: false)

        init(wanted: @escaping @Sendable () -> Bool) { self.wanted = wanted }
        var isFinished: Bool { ended.withLock { $0 } }
        func finish(in lifecycle: LocalModelLifecycle) {
            ended.withLock { $0 = true }
            retire(in: lifecycle)
        }
        func retire(in lifecycle: LocalModelLifecycle) {
            lifecycle.scheduleCandidateRetirement(self)
        }
    }

    @TaskLocal static var candidate: Candidate?
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
        var candidateID: UUID? = nil
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

        /// Commits a load's extended use only while this is the sole use and shutdown has not begun.
        fileprivate func extend() -> Lease? {
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
        var releasesClosing = false
        var retirements: [UUID: (token: UUID, task: Task<Void, Never>, again: Bool)] = [:]
        var configurationRetirements: [UUID: LocalModelTarget] = [:]
    }

    private let state = OSAllocatedUnfairLock(initialState: State())
    private let savedKeySource = OSAllocatedUnfairLock<(@Sendable (String) throws -> String?)?>(initialState: nil)
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
    var candidateRetirementCountForTests: Int {
        state.withLock { $0.retirements.count - $0.configurationRetirements.count }
    }
    var configurationRetirementCountForTests: Int { state.withLock { $0.configurationRetirements.count } }

    func useSavedKeys(_ source: @escaping @Sendable (String) throws -> String?) {
        savedKeySource.withLock { $0 = source }
    }

    private func currentKey(at endpoint: String, fallback: String?) -> String? {
        guard let source = savedKeySource.withLock({ $0 }) else { return fallback }
        do {
            return try source(endpoint)
        } catch {
            ScribeLog.warning(.cleanup, "A local retirement could not read the saved key", .failure(error))
            return fallback
        }
    }

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
        try Task.checkCancellation()
        while true {
            guard !state.withLock({ $0.releasesClosing }) else { throw LocalModelLifecycleError.closing }
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
        let admitted = try state.withLock { state -> Bool in
            try Task.checkCancellation()
            guard !state.releasesClosing else { throw LocalModelLifecycleError.closing }
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

    private func extendUse() -> Lease? {
        state.withLock {
            guard !Task.isCancelled, !$0.releasesClosing, $0.uses == 1 else { return nil }
            $0.uses += 1
            return Lease(self)
        }
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

    /// A shorter nonzero time retires the model kept under the old retention. A newer use cancels that retirement
    /// and carries the new retention itself. Other changes recalculate owned-copy countdowns from the last use.
    func setIdle(_ idle: Duration) {
        let changed = state.withLock { state -> Bool in
            guard !state.releasesClosing else { return false }
            guard state.idle != idle else { return false }
            let shorter = idle > .zero && (state.idle == .zero || idle < state.idle)
            state.idle = idle
            state.idleTask?.cancel()
            state.idleTask = nil
            state.idleGeneration &+= 1
            if shorter, let target = state.served {
                let revision = state.revision
                let generation = state.idleGeneration
                state.idleTask = Task { [weak self] in
                    guard let self, !Task.isCancelled else { return }
                    await self.retryAutomaticRelease(
                        .retentionShortened, target: target, revision: revision, generation: generation)
                }
                return false
            }
            return true
        }
        if changed { scheduleIdleReleaseIfOwed() }
    }

    private func scheduleIdleReleaseIfOwed() {
        let sleeper = self.sleeper
        state.withLock { state in
            guard !state.releasesClosing else { return }
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
                await self.retryAutomaticRelease(
                    paused ? .pause : .idle, target: paused ? target : nil,
                    revision: revision, generation: generation)
            }
        }
    }

    private func retryAutomaticRelease(
        _ reason: LocalModelReleaseReason, target: LocalModelTarget?, revision: UInt64, generation: UInt64
    ) async {
        var retryDelay = Duration.seconds(30)
        while !Task.isCancelled {
            let outcome = await release(
                reason, target: target, decidedAt: revision, generation: generation)
            guard outcome == .failed || outcome == .drainTimedOut else { return }
            do { try await sleeper(retryDelay) } catch { return }
            retryDelay = min(.seconds(300), retryDelay * 2)
        }
    }

    // MARK: Release

    fileprivate func scheduleCandidateRetirement(_ candidate: Candidate) {
        state.withLock { state in
            scheduleRetirement(
                state: &state, id: candidate.id, reason: .candidateFinished, target: nil,
                candidateID: candidate.id, wanted: candidate.wanted)
        }
    }

    func scheduleConfigurationRetirement(
        _ target: LocalModelTarget, wanted: @escaping @Sendable () -> Bool
    ) {
        state.withLock { state in
            guard !state.releasesClosing else { return }
            let id = state.configurationRetirements.first(where: { $0.value == target })?.key ?? UUID()
            state.configurationRetirements[id] = target
            scheduleRetirement(
                state: &state, id: id, reason: .configurationChanged, target: target, candidateID: nil, wanted: wanted)
        }
    }

    private func scheduleRetirement(
        state: inout State, id: UUID, reason: LocalModelReleaseReason, target: LocalModelTarget?,
        candidateID: UUID?, wanted: @escaping @Sendable () -> Bool
    ) {
        guard !state.releasesClosing else { return }
        if state.retirements[id] != nil {
            if candidateID != nil { state.retirements[id]?.again = true }
            return
        }
        let token = UUID()
        let task = Task { [weak self] in
            guard let self else { return }
            defer {
                self.state.withLock { state in
                    guard state.retirements[id]?.token == token else { return }
                    let again = state.retirements.removeValue(forKey: id)?.again == true
                    if again {
                        self.scheduleRetirement(
                            state: &state, id: id, reason: reason, target: target,
                            candidateID: candidateID, wanted: wanted)
                    } else {
                        state.configurationRetirements.removeValue(forKey: id)
                    }
                }
            }
            var delay = Duration.seconds(30)
            while !Task.isCancelled {
                let outcome = await self.release(
                    reason, target: target, candidateID: candidateID, wanted: wanted)
                guard outcome == .failed || outcome == .drainTimedOut else { return }
                do { try await self.sleeper(delay) } catch { return }
                delay = min(.seconds(300), delay * 2)
            }
        }
        state.retirements[id] = (token, task, false)
    }

    /// Frees what `reason` frees, once no use is in flight. `wanted` is asked in the committing step, so a
    /// configuration that came back (A, B, A) or a predicate that went false cancels the release.
    func release(
        _ reason: LocalModelReleaseReason,
        target: LocalModelTarget?,
        candidateID: UUID? = nil,
        wanted: @escaping @Sendable () -> Bool = { true }
    ) async -> LocalModelReleaseOutcome {
        if reason == .shutdown {
            state.withLock {
                $0.releasesClosing = true
                $0.idleTask?.cancel()
                $0.idleTask = nil
                $0.idleGeneration &+= 1
                for retirement in $0.retirements.values { retirement.task.cancel() }
                $0.retirements.removeAll()
                $0.configurationRetirements.removeAll()
            }
        }
        let revision = state.withLock { $0.revision }
        return await release(
            reason, target: target, decidedAt: revision, generation: nil, candidateID: candidateID, wanted: wanted)
    }

    private func release(
        _ reason: LocalModelReleaseReason,
        target: LocalModelTarget?,
        decidedAt revision: UInt64,
        generation: UInt64?,
        candidateID: UUID? = nil,
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
                    candidateID: candidateID,
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
        candidateID: UUID?,
        wanted: @Sendable () -> Bool
    ) async throws -> LocalModelReleaseOutcome {
        while true {
            try Task.checkCancellation()
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
                if state.releasesClosing, reason != .shutdown { return .stop(.notWanted) }
                if reason == .pause, !state.isPaused { return .stop(.notWanted) }
                let decidedByRevision = reason == .idle || reason == .pause || reason == .retentionShortened
                if decidedByRevision, state.revision != revision { return .stop(.notWanted) }
                if let generation, state.idleGeneration != generation { return .stop(.notWanted) }
                guard wanted() else { return .stop(.notWanted) }
                let explicit: LocalModelTarget? =
                    (reason == .idle || reason == .shutdown || reason == .candidateFinished) ? nil : target
                let copies: [Copy]
                switch reason {
                case .candidateFinished:
                    copies = state.copies.filter { candidateID != nil && $0.candidateID == candidateID }
                case .freeMemory, .configurationChanged, .retentionShortened:
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
                defer {
                    let freedNow = freed
                    state.withLock { state in
                        state.copies.removeAll { freedNow.contains($0) }
                    }
                    finishChange(gate)
                }
                var modelFreed = false
                if let explicit {
                    try Task.checkCancellation()
                    let key = currentKey(at: explicit.endpoint, fallback: explicit.apiKey)
                    try Task.checkCancellation()
                    modelFreed = await actions.unloadModel(explicit.endpoint, explicit.model, key)
                    if !modelFreed, key != explicit.apiKey {
                        try Task.checkCancellation()
                        modelFreed = await actions.unloadModel(explicit.endpoint, explicit.model, explicit.apiKey)
                    }
                }
                var copyFailed = false
                for copy in copies {
                    if modelFreed, let explicit, Self.sameServer(copy.endpoint, explicit.endpoint),
                        LocalServerClient.sameModel(copy.model, explicit.model)
                    {
                        freed.append(copy)
                        continue
                    }
                    try Task.checkCancellation()
                    let currentKey =
                        target.flatMap { Self.sameServer(copy.endpoint, $0.endpoint) ? $0.apiKey : nil }
                    if await unload(copy, currentKey: currentKey) {
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
                try Task.checkCancellation()
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
        guard !Task.isCancelled else { return false }
        let key = self.currentKey(at: copy.endpoint, fallback: currentKey)
        guard !Task.isCancelled else { return false }
        if await actions.unloadInstance(copy.endpoint, copy.instanceID, key) { return true }
        if !Task.isCancelled, copy.loadedKey != key,
            await actions.unloadInstance(copy.endpoint, copy.instanceID, copy.loadedKey)
        {
            return true
        }
        return false
    }

    /// Forgets what the server no longer lists (it unloaded the copy by itself), for the same server only.
    func forgetUnlisted(endpoint: String, model: String, listed: Set<String>, ifUnchangedSince revision: UInt64? = nil)
    {
        state.withLock { state in
            guard !Task.isCancelled else { return }
            if let revision, state.revision != revision { return }
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
    @discardableResult
    func reconcileLMStudio(
        target: LocalModelTarget,
        contextTokens: Int,
        lease: Lease,
        read: @escaping @Sendable (_ endpoint: String, _ apiKey: String?) async -> LocalServerState,
        load: @escaping @Sendable (_ endpoint: String, _ model: String, _ contextTokens: Int) async -> String?
    ) async -> LMStudioContextOutcome {
        guard !Task.isCancelled else { return .busy }
        let refusedKey = "\(target.endpoint)|\(target.model)|\(contextTokens)"
        guard let revision = state.withLock({ state in state.releasesClosing ? nil : state.revision }) else {
            return .unavailable
        }
        let observed = await read(target.endpoint, target.apiKey)
        guard !Task.isCancelled else { return .busy }
        guard observed.reach == .reached || observed.reach == .notRunning else { return .unavailable }
        if observed.reach == .reached {
            let listed = Set(observed.loaded.compactMap { $0.instanceID })
            forgetUnlisted(endpoint: target.endpoint, model: target.model, listed: listed, ifUnchangedSince: revision)
        }
        let held = observed.reach == .reached ? observed.loaded(for: target.model) : nil
        if observed.reach == .reached {
            let retirement = state.withLock { state -> (LifecycleGate, [Copy])? in
                guard !Task.isCancelled, !state.releasesClosing,
                    state.uses == 1, state.revision == revision, state.inFlightUnload == nil
                else { return nil }
                let copies = state.copies.filter {
                    Self.sameServer($0.endpoint, target.endpoint) && $0.instanceID != held?.instanceID
                }
                guard !copies.isEmpty else { return nil }
                let gate = LifecycleGate()
                state.inFlightUnload = gate
                return (gate, copies)
            }
            if let (gate, copies) = retirement {
                for copy in copies {
                    if await unload(copy, currentKey: target.apiKey) {
                        state.withLock { $0.copies.removeAll { $0 == copy } }
                    } else {
                        ScribeLog.warning(.cleanup, "An unused local model copy could not be unloaded")
                    }
                }
                finishChange(gate)
            }
        }
        guard contextTokens > 0 else { return .ready }
        guard !state.withLock({ $0.refused.contains(refusedKey) }) else { return .loadRefused }
        if let held, held.contextTokens == contextTokens {
            state.withLock { _ = $0.refused.remove(refusedKey) }
            return .ready
        }
        if let held, held.remainingTTLSeconds == nil { return .ready }
        let change = state.withLock { state -> LifecycleGate? in
            guard !Task.isCancelled, !state.releasesClosing,
                state.uses == 1, state.revision == revision, state.inFlightUnload == nil
            else { return nil }
            let gate = LifecycleGate()
            state.inFlightUnload = gate
            return gate
        }
        guard let change else { return .busy }
        var transferred = false
        defer { if !transferred { finishChange(change) } }
        if let held {
            // Loaded by hand, or by something that is not Scribe: used as it is.
            guard held.remainingTTLSeconds != nil else { return .ready }
            guard lease.isSoleUse else { return .busy }
            if let instance = held.instanceID {
                guard
                    await unload(
                        Copy(
                            endpoint: target.endpoint, model: target.model, instanceID: instance,
                            loadedKey: target.apiKey), currentKey: target.apiKey)
                else { return .loadRefused }
                state.withLock { state in
                    state.copies.removeAll {
                        $0.instanceID == instance && Self.sameServer($0.endpoint, target.endpoint)
                    }
                }
            } else {
                return .unavailable
            }
        } else if !lease.isSoleUse {
            return .busy
        }
        guard lease.isSoleUse, !Task.isCancelled else { return .busy }

        guard let settleLease = lease.extend() else { return .unavailable }
        let candidate = Self.candidate
        let done = LifecycleGate()
        let outcome = OSAllocatedUnfairLock(initialState: LMStudioContextOutcome.busy)
        transferred = true
        let settle = Task { [self] in
            do {
                try await loadLane.run {
                    let instance = await load(target.endpoint, target.model, contextTokens)
                    self.recordLoad(
                        target: target, contextTokens: contextTokens, instance: instance, candidateID: candidate?.id)
                    outcome.withLock { $0 = instance == nil ? .loadRefused : .ready }
                }
            } catch {
                ScribeLog.debug(.cleanup, "A local model load was cancelled before it started")
            }
            finishChange(change)
            settleLease.end()
            if state.withLock({ $0.releasesClosing }) {
                _ = await release(.shutdown, target: nil)
            } else if let candidate, candidate.isFinished {
                candidate.retire(in: self)
            }
            done.open()
        }
        _ = settle
        // A cancelled caller returns here at once; the load keeps its lease and records its copy when it lands.
        try? await done.wait()
        return outcome.withLock { $0 }
    }

    private func finishChange(_ gate: LifecycleGate) {
        state.withLock {
            if $0.inFlightUnload === gate { $0.inFlightUnload = nil }
        }
        gate.open()
    }

    private func recordLoad(
        target: LocalModelTarget, contextTokens: Int, instance: String?, candidateID: UUID?
    ) {
        state.withLock { state in
            guard let instance else {
                state.refused.insert("\(target.endpoint)|\(target.model)|\(contextTokens)")
                return
            }
            let copy = Copy(
                endpoint: target.endpoint, model: target.model, instanceID: instance, loadedKey: target.apiKey,
                candidateID: candidateID)
            if !state.copies.contains(where: {
                Self.sameServer($0.endpoint, copy.endpoint) && $0.instanceID == copy.instanceID
            }) {
                state.copies.append(copy)
            }
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
