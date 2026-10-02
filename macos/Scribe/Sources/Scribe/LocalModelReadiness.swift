import Foundation

enum LocalModelPreparationResult: String, Sendable, Equatable {
    case notApplicable
    case resident
    case started
    case failed
    case timedOut
    case cancelled
    case configurationChanged

    var permitsCleanup: Bool {
        switch self {
        case .notApplicable, .resident, .started:
            return true
        case .failed, .timedOut, .cancelled, .configurationChanged:
            return false
        }
    }
}

enum LocalModelReadiness {
    static let request = CleanupRequest(
        transcript: CleanupPrompt.wrapTranscript("ok"),
        writingStylePrompt: CleanupPrompt.systemPrompt(
            writingStyle: "Return only OK.", useLocalPrompt: true),
        timeout: ChatCompletionsTransport.seconds(LocalModelDefaults.startWait),
        maxOutputTokens: 1)

    static func prepare(
        isResident: @escaping @Sendable () async throws -> Bool,
        isCurrent: @escaping @MainActor @Sendable () async -> Bool,
        onStarting: @escaping @MainActor @Sendable () async -> Void,
        start: @escaping @Sendable () async throws -> Void
    ) async throws -> LocalModelPreparationResult {
        try Task.checkCancellation()
        guard await isCurrent() else { return .configurationChanged }
        if try await isResident() {
            guard await isCurrent() else { return .configurationChanged }
            return .resident
        }

        guard await isCurrent() else { return .configurationChanged }
        await onStarting()
        guard await isCurrent() else { return .configurationChanged }
        try Task.checkCancellation()
        try await start()
        try Task.checkCancellation()
        guard await isCurrent() else { return .configurationChanged }
        return .started
    }
}

enum LocalModelReadinessError: Error, Sendable {
    case unavailable
}

@MainActor
final class LocalModelPreparation {
    enum State: Equatable {
        case checking
        case starting
        case finished(LocalModelPreparationResult)
    }

    private(set) var state: State = .checking
    private var task: Task<LocalModelPreparationResult, Never>?
    private let changed: @MainActor @Sendable () -> Void

    init(changed: @escaping @MainActor @Sendable () -> Void) {
        self.changed = changed
    }

    var isStarting: Bool {
        state == .starting
    }

    func start(using cleanup: any DictationCleaning) {
        guard task == nil else { return }
        task = Task { [weak self] in
            let result = await cleanup.prepareLocalModel(
                isCurrent: { [weak self] in
                    guard let self, self.task != nil, !Task.isCancelled else { return false }
                    return true
                },
                onStarting: { [weak self] in
                    guard let self, !Task.isCancelled else { return }
                    self.set(.starting)
                })
            self?.set(.finished(result))
            return result
        }
    }

    func wait() async -> LocalModelPreparationResult {
        guard let task else { return .notApplicable }
        return await task.value
    }

    func cancel() {
        guard let task, !task.isCancelled else { return }
        task.cancel()
        if case .finished = state { return }
        set(.finished(.cancelled))
    }

    private func set(_ state: State) {
        guard self.state != state else { return }
        self.state = state
        changed()
    }
}
