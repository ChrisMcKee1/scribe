import Foundation
import os

/// Keeps the cleanup provider for the configuration stored now, and hands the same instance to every dictation, Test
/// Connection and the usage summary until that configuration changes.
///
/// A provider built per dictation threw away everything worth keeping: the Azure token, so `az` ran or Entra was asked
/// on every dictation; Foundry Local's endpoint, so `foundry status` ran every time; and the Keychain read of the API
/// key or client secret. Now each call reads the preferences, works out the `CleanupConnection` and reuses the provider
/// when it matches. The connection covers the provider kind, endpoint, model or deployment, auth mode, tenant, client
/// id and the secret store's revision, never a secret, and the prompt is per request and not part of it. Microsoft
/// Foundry credentials are kept per identity in `AzureCredentialCache`, so a new deployment keeps the old token.
///
/// The lifecycle owner holds one cache for the app's lifetime (`shared`) and calls `invalidate()` when cleanup is
/// switched off, which drops tokens from memory, or when it learns the identity changed outside Scribe (`az login` as
/// someone else). A change of settings needs no call: the next `provider()` sees a different connection and builds a
/// new provider.
final class CleanupProviderCache: Sendable {
    /// The app's cache, over the live settings store, the process environment and the live factory. No test uses it.
    static let shared = CleanupProviderCache(
        store: .live, environment: ProcessInfo.processInfo.environment, factory: .live)

    private struct Entry: Sendable {
        let connection: CleanupConnection
        let provider: any CleanupProvider
    }

    private struct State: Sendable {
        var entry: Entry?
        var generation: UInt64 = 0
    }

    let store: CleanupSettingsStore
    private let environment: [String: String]
    private let factory: CleanupProviderFactory
    private let credentials = AzureCredentialCache()
    private let state = OSAllocatedUnfairLock(initialState: State())

    init(store: CleanupSettingsStore, environment: [String: String], factory: CleanupProviderFactory) {
        self.store = store
        self.environment = environment
        self.factory = factory
    }

    /// The provider for the configuration stored now: the cached one when the configuration matches, otherwise a new
    /// one, which replaces it. Throws `CleanupProviderError.notConfigured` for an incomplete configuration and
    /// `.secretUnavailable` when a secret cannot be read. Reads the preferences on every call and the secret store only
    /// when it builds; it never starts a process or sends a request.
    func provider() throws -> any CleanupProvider {
        try entry().provider
    }

    /// Drops the cached provider and credentials, and with them any token held in memory. The next `provider()`
    /// builds from scratch.
    func invalidate() {
        state.withLock { current in
            current.entry = nil
            current.generation &+= 1
        }
        credentials.invalidate()
        ScribeLog.debug(.cleanup, "Dropped the cached cleanup provider")
    }

    /// Test Connection: one real cleanup of a one-word transcript, with the default writing style, through the
    /// provider dictation would use. A configuration that cannot clean (a wrong deployment or model, a missing role, a
    /// model that was never pulled) fails here the way a dictation would, rather than passing a token fetch or a model
    /// list.
    ///
    /// The request carries no token limit. Azure refuses one below 16, and a reasoning deployment spends hidden tokens
    /// before its first visible one, so a tight limit would fail models that clean perfectly well; the one-word
    /// transcript keeps the answer to a word or two anyway.
    func checkConnection() async -> CleanupConnectionCheck {
        let entry: Entry
        do {
            entry = try self.entry()
        } catch {
            return CleanupConnectionCheck(
                reachable: false, message: CleanupFailureText.forSettings(error, providerName: nil))
        }

        let provider = entry.provider
        let request = CleanupRequest(
            transcript: CleanupPrompt.wrapTranscript("ok"),
            writingStylePrompt: CleanupPrompt.systemPrompt(
                writingStyle: CleanupPrompt.defaultWritingStyle, useLocalPrompt: provider.usesLocalCleanupPrompt),
            timeout: Self.checkTimeout(for: entry.connection.kind))
        let started = ContinuousClock.now
        do {
            _ = try await provider.clean(request)
            let seconds = ChatCompletionsTransport.seconds(started.duration(to: .now))
            ScribeLog.info(.cleanup, "Test Connection succeeded", .name("provider", entry.connection.kind))
            return CleanupConnectionCheck(
                reachable: true,
                message: "\(provider.displayName) cleaned a test phrase in \(String(format: "%.1f", seconds)) s.")
        } catch {
            ScribeLog.info(
                .cleanup, "Test Connection failed", .name("provider", entry.connection.kind), .failure(error))
            return CleanupConnectionCheck(
                reachable: false, message: CleanupFailureText.forSettings(error, providerName: provider.displayName))
        }
    }

    /// How long Test Connection waits. A first request to an on-device model can wait for the model to load, which
    /// takes minutes for a large model; a cloud deployment answers within seconds unless it is cold or thinking hard.
    /// Windows' readiness probe allows the same 180 and 90 seconds.
    static func checkTimeout(for kind: CleanupProviderKind) -> TimeInterval {
        switch kind {
        case .foundryLocal, .ollama:
            return 180
        case .openAICompatible, .microsoftFoundry:
            return 90
        }
    }

    private func entry() throws -> Entry {
        let connection = try CleanupProviderResolver.connection(store: store, environment: environment)
        let (cached, generation) = state.withLock { current -> (Entry?, UInt64) in
            (current.entry?.connection == connection ? current.entry : nil, current.generation)
        }
        if let cached {
            return cached
        }

        let provider = try CleanupProviderResolver.makeProvider(
            for: connection, store: store, environment: environment, credentials: credentials, factory: factory)
        let built = Entry(connection: connection, provider: provider)
        let kept = state.withLock { current -> Entry in
            if let existing = current.entry, existing.connection == connection {
                return existing
            }
            // An invalidation arrived while this one was being built: hand it out this once, but do not keep a
            // provider that may hold a credential from before it.
            guard current.generation == generation else {
                return built
            }
            current.entry = built
            return built
        }
        ScribeLog.debug(.cleanup, "Built a cleanup provider", .name("provider", connection.kind))
        return kept
    }
}
