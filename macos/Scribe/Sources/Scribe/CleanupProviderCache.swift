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
    private let state = OSAllocatedUnfairLock(initialState: CleanupProviderCacheState())

    /// - Parameter checkDeadline: How long Test Connection may take for a provider kind; tests shorten it.
    init(
        store: CleanupSettingsStore,
        environment: [String: String],
        factory: CleanupProviderFactory,
        checkDeadline: @escaping @Sendable (CleanupProviderKind) -> Duration = {
            CleanupProviderCache.checkDeadline(for: $0)
        }
    ) {
        self.store = store
        self.environment = environment
        self.factory = factory
        self.deadlineForCheck = checkDeadline
    }

    /// The provider for the configuration stored now: the cached one when the configuration matches, otherwise a new
    /// one, which replaces it. Throws `CleanupProviderError.notConfigured` for an incomplete configuration and
    /// `.secretUnavailable` when a secret cannot be read. Reads the preferences on every call and the secret store only
    /// when it builds; it never starts a process or sends a request.
    func provider() throws -> any CleanupProvider {
        try entry().provider
    }

    /// Drops the cached provider and credential, and with them any token or secret held in memory, in one step. No
    /// `provider()` that begins after this returns can be handed anything from before it: a build still running
    /// across the call hands its provider to its own caller once and keeps nothing (`CleanupProviderCacheState`).
    func invalidate() {
        state.withLock { $0.invalidate() }
        ScribeLog.debug(.cleanup, "Dropped the cached cleanup provider")
    }

    /// Test Connection: one real cleanup of a one-word transcript, with the default writing style, through the
    /// provider dictation would use. A configuration that cannot clean (a wrong deployment or model, a missing role, a
    /// model that was never pulled) fails here the way a dictation would, rather than passing a token fetch or a model
    /// list.
    ///
    /// The whole check runs against one deadline (`checkDeadline(for:)`): the credential, Foundry Local's endpoint
    /// lookup and the completion together. When it passes, the check's work is cancelled, which stops an `az` or
    /// `foundry` child and the request, and the result says so; cancelling the calling task ends the check the same
    /// way with its own message. Each request also keeps an idle timeout of the same length. The answer is capped as
    /// Windows' readiness probe caps it (`checkOutputCeiling(for:)`).
    func checkConnection() async -> CleanupConnectionCheck {
        let entry: CleanupProviderCacheState.Entry
        do {
            entry = try self.entry()
        } catch {
            return CleanupConnectionCheck(
                reachable: false, message: CleanupFailureText.forSettings(error, providerName: nil))
        }

        let provider = entry.provider
        let kind = entry.connection.kind
        let deadline = deadlineForCheck(kind)
        let request = CleanupRequest(
            transcript: CleanupPrompt.wrapTranscript("ok"),
            writingStylePrompt: CleanupPrompt.systemPrompt(
                writingStyle: CleanupPrompt.defaultWritingStyle, useLocalPrompt: provider.usesLocalCleanupPrompt),
            timeout: ChatCompletionsTransport.seconds(Self.checkDeadline(for: kind)),
            maxOutputTokens: Self.checkOutputCeiling(for: kind))
        let started = ContinuousClock.now
        do {
            _ = try await OperationDeadline.run(within: deadline) {
                try await provider.clean(request)
            }
            let seconds = ChatCompletionsTransport.seconds(started.duration(to: .now))
            ScribeLog.info(.cleanup, "Test Connection succeeded", .name("provider", kind))
            return CleanupConnectionCheck(
                reachable: true,
                message: "\(provider.displayName) cleaned a test phrase in \(String(format: "%.1f", seconds)) s.")
        } catch let error as OperationDeadlineError {
            ScribeLog.info(.cleanup, "Test Connection ran out of time", .name("provider", kind), .failure(error))
            return CleanupConnectionCheck(
                reachable: false, message: Self.deadlineMessage(provider.displayName, kind: kind, deadline: deadline))
        } catch {
            ScribeLog.info(.cleanup, "Test Connection failed", .name("provider", kind), .failure(error))
            return CleanupConnectionCheck(
                reachable: false, message: CleanupFailureText.forSettings(error, providerName: provider.displayName))
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

    private func entry() throws -> CleanupProviderCacheState.Entry {
        let connection = try CleanupProviderResolver.connection(store: store, environment: environment)
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
