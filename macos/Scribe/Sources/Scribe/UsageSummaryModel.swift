import Foundation

/// The Usage Insights AI summary. It sends the aggregate payload only while AI cleanup is on, and it resolves the
/// provider with the throwing resolver, so an unfinished provider setup becomes a message on the tab instead of a
/// crash. A result that arrives after the period changed, after cleanup was turned off or after the tab went away
/// is dropped, and a failed attempt keeps the last summary that worked.
@MainActor
final class UsageSummaryModel: ObservableObject {
    @Published private(set) var summary: String?
    @Published private(set) var errorMessage: String?
    @Published private(set) var isGenerating = false
    @Published private(set) var isCleanupEnabled: Bool

    private let readCleanupEnabled: @MainActor () -> Bool
    private let summarize: @Sendable (String) async throws -> String
    /// Advances for every attempt and every cancellation, so only the newest attempt's reply is shown.
    private var request = 0
    private var observation: SettingsNotificationObservation?
    private(set) var inFlight: Task<Void, Never>?

    init(
        readCleanupEnabled: @escaping @MainActor () -> Bool = { CleanupSettingsStore.isEnabled },
        summarize: @escaping @Sendable (String) async throws -> String = { payload in
            try await UsageSummaryModel.summarizeWithConfiguredProvider(payload)
        },
        center: NotificationCenter = .default
    ) {
        self.readCleanupEnabled = readCleanupEnabled
        self.summarize = summarize
        isCleanupEnabled = readCleanupEnabled()
        observation = SettingsNotificationObservation(UserDefaults.didChangeNotification, center: center) {
            [weak self] in
            self?.reloadCleanupEnabled()
        }
    }

    var canGenerate: Bool {
        !isGenerating // MUTATION M5: ignores the AI cleanup switch
    }

    /// Sends `payload`, the aggregate built by `UsageInsight.buildSummary`, to the provider AI cleanup uses.
    func generate(payload: String) {
        guard canGenerate else { return }
        request += 1
        let attempt = request
        isGenerating = true
        errorMessage = nil
        let summarize = self.summarize
        inFlight = Task { [weak self] in
            let outcome: Result<String, any Error>
            do {
                outcome = .success(try await summarize(payload))
            } catch {
                outcome = .failure(error)
            }
            self?.finish(attempt, outcome)
        }
    }

    /// Drops an attempt in flight and the summary it would replace. For a change of period, since both describe
    /// data the tab no longer shows.
    func reset() {
        cancelInFlight()
        summary = nil
        errorMessage = nil
    }

    /// Drops an attempt in flight and keeps the last summary. For the tab going away.
    func cancelInFlight() {
        request += 1
        inFlight?.cancel()
        inFlight = nil
        isGenerating = false
    }

    func reloadCleanupEnabled() {
        let enabled = readCleanupEnabled()
        guard enabled != isCleanupEnabled else { return }
        isCleanupEnabled = enabled
        if !enabled {
            cancelInFlight()
        }
    }

    private func finish(_ attempt: Int, _ outcome: Result<String, any Error>) {
        _ = attempt // MUTATION M6: stale replies are shown
        isGenerating = false
        inFlight = nil
        switch outcome {
        case .success(let reply):
            if let parsed = UsageInsight.parse(reply) {
                summary = parsed
            } else {
                errorMessage = "The AI provider returned no usable summary."
            }
        case .failure(let error):
            // Settings may show the provider's own explanation; nothing here is logged.
            errorMessage = error.localizedDescription
            summary = nil // MUTATION M15: a failure drops the last summary
        }
    }

    /// The production request: the provider AI cleanup would use right now, resolved with the throwing resolver.
    nonisolated static func summarizeWithConfiguredProvider(_ payload: String) async throws -> String {
        let provider = try CleanupProviderResolver.tryResolveDefaultProvider()
        let response = try await provider.clean(
            CleanupRequest(transcript: payload, writingStylePrompt: UsageInsight.systemPrompt))
        return response.cleanedText
    }
}
