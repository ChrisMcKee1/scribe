import Foundation

/// A cleanup provider for any OpenAI-compatible `/v1/chat/completions` endpoint the user brings: LM Studio,
/// OpenRouter, a self-hosted server, or Ollama addressed by hand. The managed providers (Foundry Local, Ollama) and
/// Microsoft Foundry share its transport (`ChatCompletionsTransport`) and wire format.
final class OpenAICompatibleCleanupProvider: CleanupProvider {
    let id: String
    let displayName: String
    let model: String
    /// `{base}/v1/chat/completions`, from `OpenAICompatibleEndpoint.chatCompletionsURL(for:)`.
    let completionsURL: URL
    private let apiKey: String?
    private let timeout: TimeInterval
    private let transport: ChatCompletionsTransport

    init(
        id: String = "openai-compatible",
        displayName: String = "OpenAI-compatible endpoint",
        model: String,
        apiKey: String? = nil,
        completionsURL: URL,
        timeout: TimeInterval = 30,
        session: URLSession = CleanupProviderFactory.cleanupSession
    ) {
        self.id = id
        self.displayName = displayName
        self.model = model
        self.apiKey = apiKey
        self.completionsURL = completionsURL
        self.timeout = timeout
        self.transport = ChatCompletionsTransport(session: session)
    }

    func clean(_ request: CleanupRequest) async throws -> CleanupResponse {
        // No temperature: a bring-your-own endpoint can serve a reasoning model, which rejects or ignores one.
        let completion = try await transport.complete(
            request, at: completionsURL, model: model, bearerToken: apiKey, temperature: nil,
            defaultTimeout: timeout, provider: .openAICompatible)
        return CleanupResponse(
            cleanedText: completion.text, latency: completion.latency, providerID: id, modelID: model)
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
}

/// One chat completions request and its answer, shared by every provider.
///
/// The body is `model`, the system and user messages, `stream: false` and, for on-device models only, `temperature`.
/// It never has a `store` field: Chat Completions keep nothing unless asked to with `store: true`, and some
/// deployments reject fields they do not know (AGENTS.md, "Cloud cleanup stores nothing"). A Responses route, if one is
/// ever added, has to send `store: false` and prove it with a wire test.
struct ChatCompletionsTransport: Sendable {
    struct Completion: Sendable {
        let text: String
        let latency: TimeInterval
    }

    let session: URLSession

    func complete(
        _ cleanupRequest: CleanupRequest,
        at url: URL,
        model: String,
        bearerToken: String?,
        temperature: Double?,
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
        request.httpBody = try JSONEncoder().encode(
            ChatCompletionRequest(
                model: model,
                messages: [
                    ChatCompletionRequest.Message(role: "system", content: cleanupRequest.writingStylePrompt),
                    ChatCompletionRequest.Message(role: "user", content: cleanupRequest.transcript),
                ],
                temperature: temperature,
                stream: false))

        let started = ContinuousClock.now
        let data: Data
        let response: URLResponse
        do {
            (data, response) = try await session.data(for: request)
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
        let text = CleanupPrompt.stripTranscriptTags(decoded.choices.first?.message.content ?? "")
        guard !text.isEmpty else {
            throw CleanupProviderError.invalidResponse(.emptyCompletion)
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
        if error is CancellationError {
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
    struct Message: Encodable, Sendable {
        let role: String
        let content: String
    }

    let model: String
    let messages: [Message]
    /// Left out of the body when `nil`.
    let temperature: Double?
    let stream: Bool
}

struct ChatCompletionResponse: Decodable {
    struct Choice: Decodable {
        struct Message: Decodable {
            let content: String?
        }
        let message: Message
    }
    let choices: [Choice]
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
