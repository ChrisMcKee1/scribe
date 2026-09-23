import Foundation
import os

/// Keeps the cleanup provider for the configuration stored now, and hands the same instance to every dictation, Test
/// Connection and the usage summary until that configuration changes.
///
/// A provider built per dictation threw away everything worth keeping: the Azure token, so `az` ran or Entra was asked
/// on every dictation; Foundry Local's endpoint, so `foundry status` ran every time; and the Keychain read of the API
/// key or client secret. Now each call reads the preferences, works out the `CleanupConnection` and reuses the provider
/// when it matches. The connection covers the provider kind, endpoint, model or deployment, auth mode, tenant, client
/// id and the secret store's revision, never a secret, and the prompt is per request and not part of it. The Microsoft
/// Foundry credential is kept per identity beside the provider, so a new deployment keeps the old token.
///
/// The lifecycle owner holds one cache for the app's lifetime (`shared`) and calls `invalidate()` when cleanup is
/// switched off, which drops tokens and secrets from memory, or when it learns the identity changed outside Scribe
/// (`az login` as someone else). A change of settings needs no call, since the next `provider()` sees a different
/// connection and builds a new provider, but one is harmless and drops the old credential at once.
final class CleanupProviderCache: Sendable {
    /// The app's cache, over the live settings store, the process environment and the live factory. No test uses it.
    static let shared = CleanupProviderCache(
        store: .live, environment: ProcessInfo.processInfo.environment, factory: .live)

    let store: CleanupSettingsStore
    private let environment: [String: String]
    private let factory: CleanupProviderFactory
    private let deadlineForCheck: @Sendable (CleanupProviderKind) -> Duration
    private let checkTimer: @Sendable (Duration) async throws -> Void
    private let state = OSAllocatedUnfairLock(initialState: CleanupProviderCacheState())

    /// - Parameters:
    ///   - checkDeadline: How long Test Connection may take for a provider kind; tests shorten it.
    ///   - checkTimer: Waits out that deadline, `Task.sleep` by default; a test passes a timer it fires by hand.
    init(
        store: CleanupSettingsStore,
        environment: [String: String],
        factory: CleanupProviderFactory,
        checkDeadline: @escaping @Sendable (CleanupProviderKind) -> Duration = {
            CleanupProviderCache.checkDeadline(for: $0)
        },
        checkTimer: @escaping @Sendable (Duration) async throws -> Void = { try await Task.sleep(for: $0) }
    ) {
        self.store = store
        self.environment = environment
        self.factory = factory
        self.deadlineForCheck = checkDeadline
        self.checkTimer = checkTimer
    }

    /// The provider for the configuration stored now: the cached one when the configuration matches, otherwise a new
    /// one, which replaces it. Throws `CleanupProviderError.notConfigured` for an incomplete configuration and
    /// `.secretUnavailable` when a secret cannot be read. Reads the preferences on every call and the secret store only
    /// when it builds; it never starts a process or sends a request.
    func provider() throws -> any CleanupProvider {
        let connection = try CleanupProviderResolver.connection(store: store, environment: environment)
        return try entry(for: connection).provider
    }

    /// Drops the cached provider and credential, and with them any token or secret held in memory, in one step. No
    /// `provider()` that begins after this returns can be handed anything from before it: a build still running
    /// across the call hands its provider to its own caller once and keeps nothing (`CleanupProviderCacheState`).
    func invalidate() {
        state.withLock { $0.invalidate() }
        ScribeLog.debug(.cleanup, "Dropped the cached cleanup provider")
    }

    /// Test Connection: a real cleanup request for a one-word transcript, with the default writing style, through the
    /// provider dictation would use, and at most one more without the output ceiling (`probe`). A configuration that
    /// cannot serve a completion (a wrong deployment or model, a missing role, a model that was never pulled) fails
    /// here the way a dictation would, rather than passing a token fetch or a model list. As Windows' readiness probe
    /// does, it asks whether the model answers, not what it answered, and it asks for text: it passes where dictation
    /// works and fails where dictation would (`probe`).
    ///
    /// The whole check, from the button to the result, runs against one deadline (`checkDeadline(for:)`): building
    /// the provider with its Keychain read, the credential, Foundry Local's endpoint lookup and the completion. Only
    /// reading the preferences comes first, since the deadline depends on the provider kind, and that reads no
    /// Keychain item and starts nothing. When the deadline passes, the check's work is cancelled, which stops an `az`
    /// or `foundry` child and the request, and the result says so; cancelling the calling task ends the check the
    /// same way with its own message. Cancellation is cooperative (`OperationDeadline`): the check returns once
    /// Scribe's own work has stopped, so a Keychain read already waiting on a prompt is let finish, and then nothing
    /// more is sent. Each request also keeps an idle timeout of the same length.
    func checkConnection() async -> CleanupConnectionCheck {
        let connection: CleanupConnection
        do {
            connection = try CleanupProviderResolver.connection(store: store, environment: environment)
        } catch {
            return CleanupConnectionCheck(
                reachable: false, message: CleanupFailureText.forSettings(error, providerName: nil))
        }

        let kind = connection.kind
        let deadline = deadlineForCheck(kind)
        do {
            return try await OperationDeadline.run(within: deadline, sleep: checkTimer) {
                try await self.check(connection)
            }
        } catch let error as OperationDeadlineError {
            ScribeLog.info(.cleanup, "Test Connection ran out of time", .name("provider", kind), .failure(error))
            return CleanupConnectionCheck(
                reachable: false, message: Self.deadlineMessage(kind.providerName, kind: kind, deadline: deadline))
        } catch {
            ScribeLog.info(.cleanup, "Test Connection stopped", .name("provider", kind), .failure(error))
            return CleanupConnectionCheck(
                reachable: false, message: CleanupFailureText.forSettings(error, providerName: kind.providerName))
        }
    }

    /// How the model answered Test Connection.
    enum ProbeAnswer: Sendable, Equatable {
        /// With text, under the probe's output ceiling.
        case text
        /// It stopped at the ceiling with nothing visible, and then answered with text when asked without one.
        case textAfterOutputLimit
        /// The endpoint refused the ceiling field, and the model answered with text when asked without one.
        case textWithoutOutputLimit
    }

    /// What one probe request came back with, short of a failure.
    private enum AttemptResult: Sendable, Equatable {
        case text
        /// `length` with nothing visible, under the request's own output ceiling.
        case stoppedAtCeiling
    }

    /// Test Connection's requests, as Windows' readiness probe makes them (`ProbeAgentAsync` awaits the answer and
    /// discards it): the model served a request with text, or it did not. At most two attempts, both inside the check's
    /// one deadline, and neither the older `max_tokens` field nor both fields together are ever sent. (As for a
    /// dictation, Foundry Local's provider sends an attempt once more when its remembered endpoint refused the
    /// connection, to the endpoint `foundry status` reports now; the refused connection delivered nothing.)
    ///
    /// The first request carries the probe's output ceiling (`checkOutputCeiling(for:)`), sent as
    /// `max_completion_tokens`. Two answers to it are not yet verdicts, so one request without any ceiling follows:
    ///
    /// - A `length` stop with nothing visible. A reasoning model that spent the whole ceiling thinking does that, but
    ///   so does anything that stops every request (a deployment cap, a proxy, a model that never writes text), which
    ///   no dictation would get past. The request without a ceiling tells them apart: the check passes only if it
    ///   comes back with text, and a `length` stop there too is a failure.
    /// - HTTP 400 or 422, from a server that refuses the field outright (vLLM 0.6.0 declares only `max_tokens` and
    ///   forbids anything else). The request without a ceiling is the retry, and its answer is the verdict: text
    ///   passes, and anything else, a `length` stop included, fails without a third request.
    ///
    /// Every request first checks for cancellation, so a check stopped between two requests sends no second one.
    static func probe(_ provider: any CleanupProvider, kind: CleanupProviderKind) async throws -> ProbeAnswer {
        let capped = CleanupRequest(
            transcript: CleanupPrompt.wrapTranscript("ok"),
            writingStylePrompt: CleanupPrompt.systemPrompt(
                writingStyle: CleanupPrompt.defaultWritingStyle, useLocalPrompt: provider.usesLocalCleanupPrompt),
            timeout: ChatCompletionsTransport.seconds(checkDeadline(for: kind)),
            maxOutputTokens: checkOutputCeiling(for: kind))
        let uncapped = capped.withoutOutputLimit()

        let first: AttemptResult
        do {
            first = try await attempt(provider, capped)
        } catch let error as CleanupProviderError {
            guard case .rejected(let status, _, _) = error, status == 400 || status == 422 else {
                throw error
            }
            ScribeLog.info(
                .cleanup, "Test Connection retrying without an output limit", .name("provider", kind), .failure(error))
            return try await lastAttempt(provider, uncapped, answer: .textWithoutOutputLimit)
        }

        switch first {
        case .text:
            return .text
        case .stoppedAtCeiling:
            ScribeLog.info(.cleanup, "Test Connection confirming without an output limit", .name("provider", kind))
            return try await lastAttempt(provider, uncapped, answer: .textAfterOutputLimit)
        }
    }

    /// One probe request. A `length` stop with nothing visible is `stoppedAtCeiling` only when this request set the
    /// ceiling; without one, it stays the failure it is for a dictation.
    private static func attempt(
        _ provider: any CleanupProvider, _ request: CleanupRequest
    ) async throws -> AttemptResult {
        try Task.checkCancellation()
        do {
            _ = try await provider.clean(request)
            return .text
        } catch let error as CleanupProviderError {
            guard request.maxOutputTokens != nil, error == .invalidResponse(.outputLimitReachedBeforeText) else {
                throw error
            }
            return .stoppedAtCeiling
        }
    }

    /// The second and last request, without a ceiling: text is `answer`, and anything else is a failure.
    private static func lastAttempt(
        _ provider: any CleanupProvider, _ request: CleanupRequest, answer: ProbeAnswer
    ) async throws -> ProbeAnswer {
        switch try await attempt(provider, request) {
        case .text:
            return answer
        case .stoppedAtCeiling:
            throw CleanupProviderError.invalidResponse(.outputLimitReachedBeforeText)
        }
    }

    /// Everything the deadline covers: building the provider, its Keychain read included, and the probe. Returns the
    /// result, failures included, and throws only a cancellation, so the deadline or the caller says what it meant.
    private func check(_ connection: CleanupConnection) async throws -> CleanupConnectionCheck {
        let kind = connection.kind
        // A check cancelled before it began (Cancel pressed at once, Settings closed) builds nothing and reads no
        // Keychain item: the task group still starts its child when the group is already cancelled.
        try Task.checkCancellation()
        let provider: any CleanupProvider
        do {
            provider = try entry(for: connection).provider
        } catch {
            return CleanupConnectionCheck(
                reachable: false, message: CleanupFailureText.forSettings(error, providerName: nil))
        }
        // A build that outlasted the deadline, a Keychain read waiting on a prompt, sends nothing once it returns.
        try Task.checkCancellation()

        let started = ContinuousClock.now
        do {
            let answer = try await Self.probe(provider, kind: kind)
            let seconds = ChatCompletionsTransport.seconds(started.duration(to: .now))
            ScribeLog.info(.cleanup, "Test Connection succeeded", .name("provider", kind), .name("answer", answer))
            return CleanupConnectionCheck(
                reachable: true, message: Self.connectedMessage(provider.displayName, answer: answer, seconds: seconds))
        } catch is CancellationError {
            throw CancellationError()
        } catch {
            ScribeLog.info(.cleanup, "Test Connection failed", .name("provider", kind), .failure(error))
            return CleanupConnectionCheck(
                reachable: false, message: CleanupFailureText.forSettings(error, providerName: provider.displayName))
        }
    }

    private static func connectedMessage(_ providerName: String, answer: ProbeAnswer, seconds: TimeInterval) -> String {
        let elapsed = String(format: "%.1f", seconds)
        let connected = "\(providerName) is connected: the model answered the test in \(elapsed) s."
        switch answer {
        case .text:
            return connected
        case .textAfterOutputLimit:
            return connected
                + " Its first answer stopped at the test's small output allowance before any text, so Scribe asked once"
                + " more without one, and it answered."
        case .textWithoutOutputLimit:
            return connected
                + " The endpoint refused the test's output allowance, so Scribe asked once more without one."
        }
    }

    /// How long Test Connection may take from start to finish. A first request to an on-device model can wait for the
    /// model to load, which takes minutes for a large model; a cloud deployment answers within seconds unless it is
    /// cold or thinking hard. Windows' readiness probe allows the same 180 and 90 seconds, as one `CancelAfter` over
    /// the whole probe.
    static func checkDeadline(for kind: CleanupProviderKind) -> Duration {
        switch kind {
        case .foundryLocal, .ollama:
            return .seconds(180)
        case .openAICompatible, .microsoftFoundry:
            return .seconds(90)
        }
    }

    /// The most output Test Connection asks for, Windows' readiness probe ceilings (`InitProbeMaxOutputTokens` and
    /// `CloudInitProbeMaxOutputTokens`): 4096 for Microsoft Foundry, whose reasoning deployments spend hidden tokens
    /// before the first visible one (a short input does not bound them; Windows measured about 530 for a one-sentence
    /// edit), and 16 for the rest, where the endpoint is usually a small local model with a short context that can
    /// refuse a request reserving thousands.
    static func checkOutputCeiling(for kind: CleanupProviderKind) -> Int {
        switch kind {
        case .microsoftFoundry:
            return 4096
        case .foundryLocal, .ollama, .openAICompatible:
            return 16
        }
    }

    private static func deadlineMessage(
        _ providerName: String, kind: CleanupProviderKind, deadline: Duration
    ) -> String {
        let seconds = Int(deadline.components.seconds)
        let limit = seconds == 1 ? "1 second" : seconds > 1 ? "\(seconds) seconds" : "the time allowed"
        let stopped = "\(providerName) did not finish the test within \(limit), so Scribe stopped it."
        switch kind {
        case .foundryLocal, .ollama:
            return stopped + " A large model can take minutes to load the first time; try again once it has loaded."
        case .openAICompatible, .microsoftFoundry:
            return stopped + " Check the network and the endpoint, then try again."
        }
    }

    private func entry(for connection: CleanupConnection) throws -> CleanupProviderCacheState.Entry {
        let snapshot = state.withLock { $0.snapshot(for: connection) }
        if let cached = snapshot.entry {
            return cached
        }

        var made: CleanupProviderCacheState.HeldCredential?
        let provider = try CleanupProviderResolver.makeProvider(
            for: connection, store: store, environment: environment, factory: factory
        ) { identity, make in
            if let held = snapshot.credential, held.identity == identity {
                return held.credential
            }
            let credential = try make()
            made = CleanupProviderCacheState.HeldCredential(identity: identity, credential: credential)
            return credential
        }
        let built = CleanupProviderCacheState.Entry(connection: connection, provider: provider)
        let madeCredential = made
        let kept = state.withLock { $0.publish(built, credential: madeCredential, since: snapshot) }
        ScribeLog.debug(.cleanup, "Built a cleanup provider", .name("provider", connection.kind))
        return kept
    }
}

/// What `CleanupProviderCache` holds, and the rule that keeps an invalidation whole.
///
/// There are two tiers, the provider for one connection and the Microsoft Foundry credential for one identity, and one
/// epoch for both. `invalidate()` clears both tiers and advances the epoch in one step. A build starts from a
/// `snapshot` (the entry for its connection, the credential held and the epoch, read together) and ends with
/// `publish`, which keeps its provider, and the credential it made, only when the epoch is still the snapshot's. A
/// build that began before an invalidation therefore keeps neither the credential it found nor one it made while the
/// invalidation ran, and no later build can find them. A value, so each interleaving can be tested step by step; the
/// cache applies every step under its lock.
struct CleanupProviderCacheState: Sendable {
    struct Entry: Sendable {
        let connection: CleanupConnection
        let provider: any CleanupProvider
    }

    struct HeldCredential: Sendable {
        let identity: AzureIdentity
        let credential: any AzureCredentialProvider
    }

    /// Where a build starts: the entry held for its connection, the credential held, and the epoch they belong to.
    struct Snapshot: Sendable {
        let entry: Entry?
        let credential: HeldCredential?
        let epoch: UInt64
    }

    private(set) var entry: Entry?
    private(set) var credential: HeldCredential?
    /// Advanced by every `invalidate()`.
    private(set) var epoch: UInt64 = 0

    init() {}

    func snapshot(for connection: CleanupConnection) -> Snapshot {
        Snapshot(entry: entry?.connection == connection ? entry : nil, credential: credential, epoch: epoch)
    }

    /// What the build's caller gets. The build's provider and the credential it made are kept only when no
    /// invalidation came after `snapshot`; otherwise nothing is kept and the provider is its caller's alone. An entry
    /// another build of the same connection kept first wins, so concurrent builds share one provider.
    mutating func publish(_ built: Entry, credential made: HeldCredential?, since snapshot: Snapshot) -> Entry {
        guard epoch == snapshot.epoch else {
            return built
        }
        if let entry, entry.connection == built.connection {
            return entry
        }
        entry = built
        if let made {
            credential = made
        }
        return built
    }

    mutating func invalidate() {
        entry = nil
        credential = nil
        epoch &+= 1
    }
}
