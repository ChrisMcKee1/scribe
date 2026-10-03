import Foundation
import os

/// A cleanup provider for any OpenAI-compatible `/v1/chat/completions` endpoint the user brings: LM Studio,
/// OpenRouter, a self-hosted server, or Ollama addressed by hand. The managed providers (Foundry Local, Ollama) and
/// Microsoft Foundry share its transport (`ChatCompletionsTransport`) and wire format.
final class OpenAICompatibleCleanupProvider: CleanupProvider {
    let id: String
    let displayName: String
    let model: String
    let serviceURL: URL
    let apiStyle: CustomAPIStyle
    let usesLocalCleanupPrompt: Bool
    let localServerApp: LocalServerApp
    private let apiKey: String?
    private let keepAliveMinutes: Int
    private let localModelLane: AsyncLane
    private let localTuning: @Sendable () -> LocalModelTuning
    private let localServerEndpoint: String?
    private let readLocalServer: @Sendable (_ endpoint: String, _ apiKey: String?) async -> LocalServerState
    private let loadLocalContext: @Sendable (_ endpoint: String, _ model: String, _ contextTokens: Int) async -> String?
    private let timeout: TimeInterval
    private let transport: ChatCompletionsTransport
    private let lifecycle: LocalModelLifecycle
    private let plainRequests = OSAllocatedUnfairLock(initialState: false)
    var requiresOutputLimit: Bool {
        localServerApp != .none
    }

    init(
        id: String = "openai-compatible",
        displayName: String = "OpenAI-compatible endpoint",
        model: String,
        apiKey: String? = nil,
        completionsURL: URL? = nil,
        serviceURL: URL? = nil,
        apiStyle: CustomAPIStyle = .chatCompletions,
        localServerApp: LocalServerApp = .none,
        keepAliveMinutes: Int = LocalModelDefaults.keepAliveMinutes,
        localModelLane: AsyncLane = LocalModelDefaults.sharedLane,
        lifecycle: LocalModelLifecycle? = nil,
        localTuning: @escaping @Sendable () -> LocalModelTuning = { .none },
        loadLocalContext:
            (
                @Sendable (
                    _ endpoint: String,
                    _ model: String,
                    _ contextTokens: Int
                ) async -> String?
            )? = nil,
        readLocalServer: @escaping @Sendable (_ endpoint: String, _ apiKey: String?) async -> LocalServerState = {
            endpoint,
            apiKey in
            await LocalServerClient().read(endpoint, apiKey: apiKey)
        },
        timeout: TimeInterval = 30,
        session: URLSession = CleanupProviderFactory.cleanupSession
    ) {
        self.id = id
        self.displayName = displayName
        self.model = model
        self.apiKey = apiKey
        self.serviceURL = serviceURL ?? OpenAICompatibleEndpoint.serviceURL(for: completionsURL!) ?? completionsURL!
        self.apiStyle = apiStyle
        self.usesLocalCleanupPrompt = LocalAiServer.isOnThisMac(self.serviceURL.absoluteString)
        self.localServerApp =
            LocalAiServer.appAt(self.serviceURL.absoluteString) == localServerApp ? localServerApp : .none
        self.keepAliveMinutes = keepAliveMinutes
        self.localModelLane = localModelLane
        self.lifecycle = lifecycle ?? LocalModelLifecycle(idle: .zero, actions: .connected(to: session))
        self.localTuning = localTuning
        self.localServerEndpoint = self.serviceURL.absoluteString
        self.loadLocalContext =
            loadLocalContext
            ?? { endpoint, model, contextTokens in
                await LocalServerClient(session: session).loadWithContext(
                    endpoint, modelID: model, contextTokens: contextTokens, apiKey: apiKey)
            }
        self.readLocalServer = readLocalServer
        self.timeout = timeout
        self.transport = ChatCompletionsTransport(session: session)
    }

    private var lifecycleTarget: LocalModelTarget? {
        guard localServerApp != .none, let endpoint = localServerEndpoint else { return nil }
        return LocalModelTarget(endpoint: endpoint, model: model, app: localServerApp, apiKey: apiKey)
    }

    /// Every use of a model on this Mac, a dictation's cleanup, Test connection and one-off requests included, holds a
    /// lease for its whole length, so no release unloads the model under it.
    private func beginLease() async throws -> LocalModelLifecycle.Lease? {
        guard let target = lifecycleTarget else { return nil }
        do {
            return try await lifecycle.beginUse(target)
        } catch is LocalModelLifecycleError {
            throw CleanupProviderError.timedOut
        }
    }

    func clean(_ request: CleanupRequest) async throws -> CleanupResponse {
        let lease = try await beginLease()
        defer { lease?.end() }
        let local = usesLocalCleanupPrompt
        let plain = local && plainRequests.withLock { $0 }

        do {
            let completion = try await complete(
                request,
                plain: plain,
                local: local,
                lease: lease)
            return CleanupResponse(
                cleanedText: completion.text, latency: completion.latency, providerID: id, modelID: model)
        } catch let error as CleanupProviderError {
            guard local, !plain, case .rejected(let status, _, _) = error, status == 400 || status == 422 else {
                throw error
            }

            plainRequests.withLock { $0 = true }
            do {
                let completion = try await complete(
                    request,
                    plain: true,
                    local: local,
                    lease: lease)
                return CleanupResponse(
                    cleanedText: completion.text, latency: completion.latency, providerID: id, modelID: model)
            } catch {
                plainRequests.withLock { $0 = false }
                throw error
            }
        }
    }

    func prepareLocalModel(
        isCurrent: @escaping @MainActor @Sendable () async -> Bool,
        onStarting: @escaping @MainActor @Sendable () async -> Void
    ) async throws -> LocalModelPreparationResult {
        guard localServerApp != .none, let endpoint = localServerEndpoint else { return .notApplicable }
        let requestedContext = ContextBudget.sanitize(localTuning().contextTokens)
        let lease = try await beginLease()
        defer { lease?.end() }
        return try await localModelLane.run {
            try await LocalModelReadiness.prepare(
                isResident: {
                    let state = await self.readLocalServer(endpoint, self.apiKey)
                    guard state.reach == .reached else { throw LocalModelReadinessError.unavailable }
                    guard let loaded = state.loaded(for: self.model) else { return false }
                    let context =
                        self.localServerApp == .ollama
                        ? self.transport.ollamaContextLimit(requested: requestedContext) : requestedContext
                    return requestedContext == 0 || loaded.contextTokens == context
                },
                isCurrent: isCurrent,
                onStarting: onStarting,
                start: {
                    _ = try await self.complete(
                        LocalModelReadiness.request,
                        plain: false,
                        local: true,
                        acquireLane: false,
                        readying: true,
                        lease: lease)
                })
        }
    }

    private func complete(
        _ request: CleanupRequest,
        plain: Bool,
        local: Bool,
        acquireLane: Bool = true,
        readying: Bool = false,
        lease: LocalModelLifecycle.Lease? = nil
    ) async throws -> ChatCompletionsTransport.Completion {
        let tuning = localTuning()
        let keepAlive = localServerApp == .ollama && keepAliveMinutes > 0 ? "\(keepAliveMinutes)m" : nil
        let ttl = localServerApp == .lmStudio && keepAliveMinutes > 0 ? keepAliveMinutes * 60 : nil
        let contextTokens = ContextBudget.sanitize(tuning.contextTokens)
        let transport = self.transport
        let serviceURL = self.serviceURL
        let apiStyle = self.apiStyle
        let model = self.model
        let apiKey = self.apiKey
        let localServerApp = self.localServerApp
        let loadLocalContext = self.loadLocalContext
        let readLocalServer = self.readLocalServer
        let lifecycle = self.lifecycle
        let target = lifecycleTarget
        let timeout = self.timeout
        let requiresChosenContext = CleanupProviderCache.isConnectionTest
        let work: @Sendable () async throws -> ChatCompletionsTransport.Completion = {
            var request = request
            if localServerApp == .lmStudio, contextTokens > 0 || !lifecycle.ownedCopies.isEmpty {
                if let lease, let target {
                    let outcome = await lifecycle.reconcileLMStudio(
                        target: target, contextTokens: contextTokens, lease: lease,
                        read: readLocalServer, load: loadLocalContext)
                    try Task.checkCancellation()
                    if requiresChosenContext, contextTokens > 0, outcome != .ready {
                        throw CleanupProviderError.localContextUnavailable(outcome)
                    }
                }
            }
            if localServerApp == .lmStudio {
                var observed = await readLocalServer(serviceURL.absoluteString, apiKey)
                var warmed: ChatCompletionsTransport.Completion?
                try Task.checkCancellation()
                guard observed.reach == .reached else { throw CleanupProviderError.localContextUnknown }
                if contextTokens == 0, readying || observed.loaded(for: model) == nil {
                    warmed = try await transport.complete(
                        LocalModelReadiness.request,
                        at: serviceURL.appendingPathComponent("chat/completions"),
                        model: model, bearerToken: apiKey,
                        temperature: CleanupSampling.onDeviceTemperature,
                        reasoningEffort: CleanupReasoningEffort.none, includeLegacyMaxTokens: true,
                        ttl: ttl, defaultTimeout: timeout, provider: .openAICompatible)
                    observed = await readLocalServer(serviceURL.absoluteString, apiKey)
                    try Task.checkCancellation()
                }
                guard observed.reach == .reached, let held = observed.loaded(for: model),
                    held.contextTokens > 0
                else { throw CleanupProviderError.localContextUnknown }
                request = CleanupRequest(
                    transcript: request.transcript, writingStylePrompt: request.writingStylePrompt,
                    singleLineMode: request.singleLineMode, timeout: request.timeout,
                    maxOutputTokens: request.maxOutputTokens ?? 4096)
                guard ContextBudget.requestFits(request, contextTokens: held.contextTokens) else {
                    throw CleanupProviderError.localRequestTooLarge
                }
                if readying, let warmed { return warmed }
            }

            if localServerApp == .ollama, contextTokens > 0 {
                return try await transport.completeOllama(
                    request,
                    at: serviceURL,
                    model: model,
                    bearerToken: apiKey,
                    keepAlive: keepAlive,
                    contextTokens: contextTokens,
                    defaultTimeout: timeout)
            }
            if localServerApp == .ollama {
                return try await transport.completeOllamaAtOwnSize(
                    request, at: serviceURL, model: model, bearerToken: apiKey,
                    keepAlive: keepAlive, defaultTimeout: timeout, plain: plain,
                    readying: readying, read: readLocalServer)
            }

            if apiStyle == .responses {
                return try await transport.completeResponses(
                    request,
                    at: serviceURL.appendingPathComponent("responses"),
                    model: model,
                    bearerToken: apiKey,
                    defaultTimeout: timeout)
            }

            return try await transport.complete(
                request,
                at: serviceURL.appendingPathComponent("chat/completions"),
                model: model,
                bearerToken: apiKey,
                temperature: local ? CleanupSampling.onDeviceTemperature : nil,
                reasoningEffort: local && !plain ? CleanupReasoningEffort.none : nil,
                includeLegacyMaxTokens: local && !plain,
                keepAlive: keepAlive,
                ttl: ttl,
                defaultTimeout: timeout,
                provider: .openAICompatible)
        }
        return local && acquireLane ? try await localModelLane.run(work) : try await work()
    }
}

/// Where an OpenAI-compatible server takes chat completions.
enum OpenAICompatibleEndpoint {
    /// `{base}/v1/chat/completions` for a base URL given with or without its `/v1` segment (OpenRouter documents
    /// `https://openrouter.ai/api/v1`, LM Studio `http://localhost:1234`), so neither shape becomes `/v1/v1`. `nil`
    /// unless the base is an http or https URL with a host.
    static func chatCompletionsURL(for base: URL) -> URL? {
        guard var components = URLComponents(url: base, resolvingAgainstBaseURL: false),
            let scheme = components.scheme?.lowercased(), scheme == "http" || scheme == "https",
            let host = components.host, !host.isEmpty
        else {
            return nil
        }
        var path = components.percentEncodedPath
        while path.hasSuffix("/") {
            path.removeLast()
        }
        if path.lowercased().hasSuffix("/v1") {
            path.removeLast(3)
        }
        components.scheme = scheme
        components.percentEncodedPath = path + "/v1/chat/completions"
        components.fragment = nil
        return components.url
    }

    static func serviceURL(for completionsURL: URL) -> URL? {
        guard var components = URLComponents(url: completionsURL, resolvingAgainstBaseURL: false) else {
            return nil
        }
        let path = components.percentEncodedPath
        guard path.lowercased().hasSuffix("/v1/chat/completions") else {
            return nil
        }
        components.percentEncodedPath = String(path.dropLast("/chat/completions".count))
        components.fragment = nil
        return components.url
    }
}

/// One chat completions request and its answer, shared by every provider.
///
/// The body is `model`, the system and user messages, `stream: false`, for on-device models only `temperature`, and,
/// for Test Connection only, `max_completion_tokens`.
/// It never has a `store` field: Chat Completions keep nothing unless asked to with `store: true`, and some
/// deployments reject fields they do not know (AGENTS.md, "Cloud cleanup stores nothing"). A Responses route, if one is
/// ever added, has to send `store: false` and prove it with a wire test.
struct ChatCompletionsTransport: Sendable {
    private struct OllamaContext {
        var endpoint: URL?
        var model = ""
        var requested = 0
        var maximum = 0
        var runtimeCap = 0

        var limit: Int {
            let modelLimit = maximum > 0 ? min(requested, maximum) : requested
            return runtimeCap > 0 ? min(modelLimit, runtimeCap) : modelLimit
        }
    }
    private let learnedOllamaContext = OSAllocatedUnfairLock(initialState: OllamaContext())
    struct Completion: Sendable {
        let text: String
        let latency: TimeInterval
    }

    let session: URLSession

    func ollamaContextLimit(requested: Int) -> Int {
        learnedOllamaContext.withLock { $0.requested == requested ? $0.limit : requested }
    }

    func completeOllamaAtOwnSize(
        _ request: CleanupRequest,
        at url: URL, model: String, bearerToken: String?, keepAlive: String?,
        defaultTimeout: TimeInterval, plain: Bool, readying: Bool,
        read: @escaping @Sendable (String, String?) async -> LocalServerState
    ) async throws -> Completion {
        guard LocalAiServer.appAt(url.absoluteString) == .ollama else {
            throw CleanupProviderError.transport(URLError(.badURL))
        }
        let completionURL = url.appendingPathComponent("chat/completions")
        var state = await read(url.absoluteString, bearerToken)
        try Task.checkCancellation()
        guard state.reach == .reached else { throw CleanupProviderError.localContextUnknown }
        var warmed: Completion?
        if readying || state.loaded(for: model) == nil {
            warmed = try await complete(
                LocalModelReadiness.request, at: completionURL, model: model, bearerToken: bearerToken,
                temperature: CleanupSampling.onDeviceTemperature,
                reasoningEffort: plain ? nil : CleanupReasoningEffort.none,
                includeLegacyMaxTokens: true, keepAlive: keepAlive,
                defaultTimeout: defaultTimeout, provider: .ollama)
            state = await read(url.absoluteString, bearerToken)
            try Task.checkCancellation()
        }
        guard state.reach == .reached, let held = state.loaded(for: model), held.contextTokens > 0 else {
            throw CleanupProviderError.localContextUnknown
        }
        // A larger copy may belong to another app. A default-size request can replace it.
        let context = min(ContextBudget.assumedContextTokens, held.contextTokens)
        let bounded = CleanupRequest(
            transcript: request.transcript, writingStylePrompt: request.writingStylePrompt,
            singleLineMode: request.singleLineMode, timeout: request.timeout,
            maxOutputTokens: request.maxOutputTokens ?? 4096)
        guard ContextBudget.requestFits(bounded, contextTokens: context) else {
            throw CleanupProviderError.localRequestTooLarge
        }
        if readying, let warmed { return warmed }
        let answer = try await complete(
            bounded, at: completionURL, model: model, bearerToken: bearerToken,
            temperature: CleanupSampling.onDeviceTemperature,
            reasoningEffort: plain ? nil : CleanupReasoningEffort.none,
            includeLegacyMaxTokens: true, keepAlive: keepAlive,
            defaultTimeout: defaultTimeout, provider: .ollama)
        state = await read(url.absoluteString, bearerToken)
        try Task.checkCancellation()
        guard state.reach == .reached, let answered = state.loaded(for: model), answered.contextTokens > 0 else {
            throw CleanupProviderError.localContextUnknown
        }
        guard ContextBudget.requestFits(bounded, contextTokens: min(context, answered.contextTokens)) else {
            throw CleanupProviderError.localRequestTooLarge
        }
        return answer
    }

    func completeOllama(
        _ cleanupRequest: CleanupRequest,
        at url: URL,
        model: String,
        bearerToken: String? = nil,
        keepAlive: String?,
        contextTokens: Int,
        defaultTimeout: TimeInterval
    ) async throws -> Completion {
        guard LocalAiServer.appAt(url.absoluteString) == .ollama,
            var components = URLComponents(url: url, resolvingAgainstBaseURL: false)
        else {
            throw CleanupProviderError.transport(URLError(.badURL))
        }
        components.percentEncodedPath = "/api/chat"
        guard let chatURL = components.url else { throw CleanupProviderError.transport(URLError(.badURL)) }
        guard ContextBudget.requestFits(cleanupRequest, contextTokens: contextTokens) else {
            throw CleanupProviderError.localRequestTooLarge
        }
        let client = LocalServerClient(session: session)
        let maximum = await client.readMaxContext(
            url.absoluteString, modelID: model, apiKey: bearerToken)
        try Task.checkCancellation()
        let effectiveContext = learnedOllamaContext.withLock {
            if $0.endpoint != url || $0.model != model || $0.requested != contextTokens {
                $0 = OllamaContext(endpoint: url, model: model, requested: contextTokens)
            }
            $0.maximum = maximum
            return $0.limit
        }
        guard maximum > 0 else {
            throw CleanupProviderError.localContextUnknown
        }
        guard ContextBudget.requestFits(cleanupRequest, contextTokens: effectiveContext) else {
            throw CleanupProviderError.localRequestTooLarge
        }
        var request = URLRequest(url: chatURL)
        request.httpMethod = "POST"
        request.timeoutInterval = cleanupRequest.timeout ?? defaultTimeout
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        if let bearerToken, !bearerToken.isEmpty {
            request.setValue("Bearer \(bearerToken)", forHTTPHeaderField: "Authorization")
        }
        request.httpBody = try JSONEncoder().encode(
            OllamaChatRequest(
                model: model,
                messages: [
                    ChatCompletionRequest.Message(role: "system", content: cleanupRequest.writingStylePrompt),
                    ChatCompletionRequest.Message(role: "user", content: cleanupRequest.transcript),
                ],
                keepAlive: keepAlive,
                think: false,
                options: .init(
                    numContext: effectiveContext,
                    temperature: CleanupSampling.onDeviceTemperature,
                    numPredict: cleanupRequest.maxOutputTokens ?? 4096),
                stream: false))

        let started = ContinuousClock.now
        let data: Data
        let response: URLResponse
        do {
            (data, response) = try await CleanupSendHandoff.data(for: request, session: session)
        } catch {
            throw Self.transportFailure(error)
        }

        guard let httpResponse = response as? HTTPURLResponse else {
            throw CleanupProviderError.invalidResponse(.notHTTP)
        }
        guard (200..<300).contains(httpResponse.statusCode) else {
            throw CleanupProviderError.rejected(
                status: httpResponse.statusCode,
                provider: .openAICompatible,
                reply: CleanupServiceReply(errorBody: data))
        }
        guard let decoded = try? JSONDecoder().decode(OllamaChatResponse.self, from: data) else {
            throw CleanupProviderError.invalidResponse(.undecodable)
        }
        let text = CleanupPrompt.stripTranscriptTags(decoded.message.content ?? "")
        guard !text.isEmpty else {
            throw CleanupProviderError.invalidResponse(
                decoded.doneReason == "length" ? .outputLimitReachedBeforeText : .emptyCompletion)
        }

        let loadedContext = await client.readLoadedContext(
            url.absoluteString, modelID: model, apiKey: bearerToken)
        try Task.checkCancellation()
        guard loadedContext > 0 else { throw CleanupProviderError.localContextUnknown }
        learnedOllamaContext.withLock {
            guard $0.endpoint == url, $0.model == model, $0.requested == contextTokens else { return }
            let observedCap = min(effectiveContext, loadedContext)
            $0.runtimeCap = $0.runtimeCap > 0 ? min($0.runtimeCap, observedCap) : observedCap
        }
        guard ContextBudget.requestFits(cleanupRequest, contextTokens: min(effectiveContext, loadedContext)) else {
            throw CleanupProviderError.localRequestTooLarge
        }

        let elapsed = started.duration(to: .now)
        ScribeLog.debug(
            .cleanup,
            "Cleanup request finished",
            .name("provider", CleanupProviderKind.openAICompatible),
            .duration("elapsed", elapsed),
            .count("characters", text.count))
        return Completion(text: text, latency: Self.seconds(elapsed))
    }

    func completeResponses(
        _ cleanupRequest: CleanupRequest,
        at url: URL,
        model: String,
        bearerToken: String?,
        defaultTimeout: TimeInterval
    ) async throws -> Completion {
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.timeoutInterval = cleanupRequest.timeout ?? defaultTimeout
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        if let bearerToken, !bearerToken.isEmpty {
            request.setValue("Bearer \(bearerToken)", forHTTPHeaderField: "Authorization")
        }
        request.httpBody = try JSONEncoder().encode(
            ResponsesRequest(
                model: model,
                input: [
                    .init(role: "system", content: cleanupRequest.writingStylePrompt),
                    .init(role: "user", content: cleanupRequest.transcript),
                ],
                maxOutputTokens: cleanupRequest.maxOutputTokens,
                store: false))

        let started = ContinuousClock.now
        let data: Data
        let response: URLResponse
        do {
            (data, response) = try await CleanupSendHandoff.data(for: request, session: session)
        } catch {
            throw Self.transportFailure(error)
        }

        guard let httpResponse = response as? HTTPURLResponse else {
            throw CleanupProviderError.invalidResponse(.notHTTP)
        }
        guard (200..<300).contains(httpResponse.statusCode) else {
            throw CleanupProviderError.rejected(
                status: httpResponse.statusCode,
                provider: .openAICompatible,
                reply: CleanupServiceReply(errorBody: data))
        }
        guard let decoded = try? JSONDecoder().decode(ResponsesResponse.self, from: data) else {
            throw CleanupProviderError.invalidResponse(.undecodable)
        }

        let text = CleanupPrompt.stripTranscriptTags(decoded.text ?? "")
        guard !text.isEmpty else {
            throw CleanupProviderError.invalidResponse(.emptyCompletion)
        }

        let elapsed = started.duration(to: .now)
        ScribeLog.debug(
            .cleanup,
            "Cleanup request finished",
            .name("provider", CleanupProviderKind.openAICompatible),
            .duration("elapsed", elapsed),
            .count("characters", text.count))
        return Completion(text: text, latency: Self.seconds(elapsed))
    }

    func complete(
        _ cleanupRequest: CleanupRequest,
        at url: URL,
        model: String,
        bearerToken: String?,
        apiKey: String? = nil,
        temperature: Double?,
        reasoningEffort: String? = nil,
        promptCacheMode: String? = nil,
        includeLegacyMaxTokens: Bool = false,
        keepAlive: String? = nil,
        ttl: Int? = nil,
        defaultTimeout: TimeInterval,
        provider: CleanupProviderKind
    ) async throws -> Completion {
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.timeoutInterval = cleanupRequest.timeout ?? defaultTimeout
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        if let bearerToken, !bearerToken.isEmpty {
            request.setValue("Bearer \(bearerToken)", forHTTPHeaderField: "Authorization")
        }
        if let apiKey, !apiKey.isEmpty {
            request.setValue(apiKey, forHTTPHeaderField: "api-key")
        }
        request.httpBody = try JSONEncoder().encode(
            ChatCompletionRequest(
                model: model,
                messages: [
                    ChatCompletionRequest.Message(role: "system", content: cleanupRequest.writingStylePrompt),
                    ChatCompletionRequest.Message(role: "user", content: cleanupRequest.transcript),
                ],
                temperature: temperature,
                reasoningEffort: reasoningEffort,
                maxCompletionTokens: cleanupRequest.maxOutputTokens,
                promptCacheOptions: promptCacheMode.map { ChatCompletionRequest.PromptCacheOptions(mode: $0) },
                maxTokens: includeLegacyMaxTokens ? cleanupRequest.maxOutputTokens : nil,
                keepAlive: keepAlive,
                ttl: ttl,
                stream: false))

        let started = ContinuousClock.now
        let data: Data
        let response: URLResponse
        do {
            (data, response) = try await CleanupSendHandoff.data(for: request, session: session)
        } catch {
            throw Self.transportFailure(error)
        }

        guard let httpResponse = response as? HTTPURLResponse else {
            throw CleanupProviderError.invalidResponse(.notHTTP)
        }
        guard (200..<300).contains(httpResponse.statusCode) else {
            throw CleanupProviderError.rejected(
                status: httpResponse.statusCode, provider: provider, reply: CleanupServiceReply(errorBody: data))
        }
        guard let decoded = try? JSONDecoder().decode(ChatCompletionResponse.self, from: data) else {
            throw CleanupProviderError.invalidResponse(.undecodable)
        }
        let choice = decoded.choices.first
        let text = CleanupPrompt.stripTranscriptTags(choice?.message.content ?? "")
        guard !text.isEmpty else {
            // `length` with nothing visible: the output limit ran out before any text, which a reasoning model does
            // when a request caps its output tightly. Kept apart from an empty answer so Test Connection, which sends
            // such a cap, knows to ask once more without one; for a dictation either one is no text.
            throw CleanupProviderError.invalidResponse(
                choice?.finishReason == "length" ? .outputLimitReachedBeforeText : .emptyCompletion)
        }

        let elapsed = started.duration(to: .now)
        ScribeLog.debug(
            .cleanup, "Cleanup request finished", .name("provider", provider), .duration("elapsed", elapsed),
            .count("characters", text.count))
        return Completion(text: text, latency: Self.seconds(elapsed))
    }

    /// A failed `URLSession` call as a cleanup failure. A cancelled task stays a `CancellationError`, so a caller can
    /// tell a shutdown from a failure, and a URL error keeps only its code, never the failing URL its user info holds.
    static func transportFailure(_ error: any Error) -> any Error {
        if error is CancellationError || error is CleanupSendHandoff.Refusal {
            return error
        }
        guard let urlError = error as? URLError else {
            return Task.isCancelled ? CancellationError() : CleanupProviderError.transport(URLError(.unknown))
        }
        if urlError.code == .cancelled, Task.isCancelled {
            return CancellationError()
        }
        if urlError.code == .timedOut {
            return CleanupProviderError.timedOut
        }
        return CleanupProviderError.transport(URLError(urlError.code))
    }

    static func seconds(_ duration: Duration) -> TimeInterval {
        let (seconds, attoseconds) = duration.components
        return TimeInterval(seconds) + TimeInterval(attoseconds) / 1_000_000_000_000_000_000
    }
}

// MARK: - Wire format

struct ChatCompletionRequest: Encodable, Sendable {
    struct PromptCacheOptions: Encodable, Sendable {
        let mode: String
    }

    struct Message: Encodable, Sendable {
        let role: String
        let content: String
    }

    let model: String
    let messages: [Message]
    /// Left out of the body when `nil`.
    let temperature: Double?
    /// Left out of the body when `nil`.
    let reasoningEffort: String?
    /// Left out of the body when `nil`. `max_completion_tokens` rather than the older `max_tokens`, which reasoning
    /// deployments refuse; it is the field Windows' OpenAI client sends for the same limit.
    let maxCompletionTokens: Int?
    let promptCacheOptions: PromptCacheOptions?
    /// Left out of the body when `nil`. Local servers on this Mac still read the older field.
    let maxTokens: Int?
    /// Left out of the body when `nil`. Ollama's OpenAI-compatible endpoint keeps the model this long.
    let keepAlive: String?
    /// Left out of the body when `nil`. LM Studio's OpenAI-compatible endpoint keeps the model this long, in seconds.
    let ttl: Int?
    let stream: Bool

    enum CodingKeys: String, CodingKey {
        case model
        case messages
        case temperature
        case reasoningEffort = "reasoning_effort"
        case maxCompletionTokens = "max_completion_tokens"
        case promptCacheOptions = "prompt_cache_options"
        case maxTokens = "max_tokens"
        case keepAlive = "keep_alive"
        case ttl
        case stream
    }
}

struct ChatCompletionResponse: Decodable {
    struct Choice: Decodable {
        struct Message: Decodable {
            let content: String?
        }
        let message: Message
        /// Why the model stopped: `stop`, `length` when the output limit ran out, or another value some servers send.
        let finishReason: String?

        enum CodingKeys: String, CodingKey {
            case message
            case finishReason = "finish_reason"
        }
    }
    let choices: [Choice]
}

struct ResponsesRequest: Encodable, Sendable {
    struct Message: Encodable, Sendable {
        let role: String
        let content: String
    }

    let model: String
    let input: [Message]
    let maxOutputTokens: Int?
    let store: Bool

    enum CodingKeys: String, CodingKey {
        case model
        case input
        case maxOutputTokens = "max_output_tokens"
        case store
    }
}

struct OllamaChatRequest: Encodable, Sendable {
    struct Options: Encodable, Sendable {
        let numContext: Int
        let temperature: Double
        let numPredict: Int

        enum CodingKeys: String, CodingKey {
            case numContext = "num_ctx"
            case temperature
            case numPredict = "num_predict"
        }
    }

    let model: String
    let messages: [ChatCompletionRequest.Message]
    let keepAlive: String?
    let think: Bool
    let options: Options
    let stream: Bool

    enum CodingKeys: String, CodingKey {
        case model
        case messages
        case keepAlive = "keep_alive"
        case think
        case options
        case stream
    }
}

struct OllamaChatResponse: Decodable {
    struct Message: Decodable {
        let content: String?
    }
    let message: Message
    let doneReason: String?

    enum CodingKeys: String, CodingKey {
        case message
        case doneReason = "done_reason"
    }
}

struct ResponsesResponse: Decodable {
    struct Output: Decodable {
        struct Content: Decodable {
            let type: String?
            let text: String?
        }

        let type: String?
        let text: String?
        let content: [Content]?
    }

    let outputText: String?
    let output: [Output]?

    var text: String? {
        if let outputText, !outputText.isEmpty {
            return outputText
        }
        for item in output ?? [] {
            if let text = item.text, !text.isEmpty {
                return text
            }
            for content in item.content ?? [] {
                if let text = content.text, !text.isEmpty {
                    return text
                }
            }
        }
        return nil
    }

    enum CodingKeys: String, CodingKey {
        case outputText = "output_text"
        case output
    }
}

extension CleanupServiceReply {
    /// The error an OpenAI-style endpoint puts in its body: `{"error": {"code", "type", "message"}}` (OpenAI, Azure,
    /// LM Studio), `{"error": "text"}` (Ollama) or `{"code", "message"}`. Anything else, such as a proxy's HTML error
    /// page, yields no code and no message; the body itself is never kept.
    init(errorBody data: Data) {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            self.init(code: nil, message: nil)
            return
        }
        if let error = object["error"] as? [String: Any] {
            self.init(code: Self.code(in: error) ?? (error["type"] as? String), message: error["message"] as? String)
        } else if let text = object["error"] as? String {
            self.init(code: nil, message: text)
        } else {
            self.init(code: Self.code(in: object), message: object["message"] as? String)
        }
    }

    private static func code(in object: [String: Any]) -> String? {
        if let text = object["code"] as? String {
            return text
        }
        if let number = object["code"] as? Int {
            return String(number)
        }
        return nil
    }
}
