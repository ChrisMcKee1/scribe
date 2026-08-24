import Foundation
import OSLog

/// A cleanup provider that talks to any OpenAI-compatible `/v1/chat/completions` endpoint. This
/// is the shared transport used by Foundry Local, managed Ollama, LM Studio, OpenRouter, and any
/// other compatible server; concrete providers configure the base URL, model, optional API key,
/// and how they discover the endpoint (see FoundryLocalCleanupProvider / ManagedOllamaCleanupProvider).
final class OpenAICompatibleCleanupProvider: CleanupProvider {
    let id: String
    let displayName: String

    private let baseURLProvider: () async -> URL?
    private let model: String
    private let apiKey: String?
    private let timeout: TimeInterval
    private let session: URLSession
    private let logger: Logger

    /// - Parameters:
    ///   - baseURLProvider: resolves the endpoint's base URL (e.g. `http://127.0.0.1:11434`) each
    ///     call, since Foundry Local's port is dynamic and Ollama's daemon may not be running yet.
    init(
        id: String,
        displayName: String,
        model: String,
        apiKey: String? = nil,
        timeout: TimeInterval = 30,
        session: URLSession = .shared,
        baseURLProvider: @escaping () async -> URL?
    ) {
        self.id = id
        self.displayName = displayName
        self.model = model
        self.apiKey = apiKey
        self.timeout = timeout
        self.session = session
        self.baseURLProvider = baseURLProvider
        self.logger = Logger(subsystem: "com.scribe.macos", category: "Cleanup.\(id)")
    }

    func healthSnapshot() async -> CleanupHealthSnapshot {
        guard let baseURL = await baseURLProvider() else {
            return CleanupHealthSnapshot(providerID: id, reachable: false, detail: "Endpoint not reachable")
        }

        var request = URLRequest(url: baseURL.appendingPathComponent("v1/models"))
        request.timeoutInterval = 5
        applyAuthHeader(to: &request)

        do {
            let (_, response) = try await session.data(for: request)
            let ok = (response as? HTTPURLResponse)?.statusCode == 200
            return CleanupHealthSnapshot(
                providerID: id,
                reachable: ok,
                detail: ok ? "Reachable at \(baseURL.absoluteString)" : "Unexpected response from \(baseURL.absoluteString)")
        } catch {
            return CleanupHealthSnapshot(providerID: id, reachable: false, detail: error.localizedDescription)
        }
    }

    func clean(_ cleanupRequest: CleanupRequest) async throws -> CleanupResponse {
        guard let baseURL = await baseURLProvider() else {
            throw CleanupProviderError.notConfigured("\(displayName) endpoint is not reachable")
        }

        var request = URLRequest(url: baseURL.appendingPathComponent("v1/chat/completions"))
        request.httpMethod = "POST"
        request.timeoutInterval = timeout
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        applyAuthHeader(to: &request)

        let body = ChatCompletionRequest(
            model: model,
            messages: [
                .init(role: "system", content: cleanupRequest.writingStylePrompt),
                .init(role: "user", content: cleanupRequest.transcript)
            ],
            temperature: 0.2,
            stream: false)
        request.httpBody = try JSONEncoder().encode(body)

        let started = Date()
        let (data, response): (Data, URLResponse)
        do {
            (data, response) = try await session.data(for: request)
        } catch let urlError as URLError where urlError.code == .timedOut {
            throw CleanupProviderError.timedOut
        } catch {
            throw CleanupProviderError.requestFailed(error.localizedDescription)
        }

        guard let httpResponse = response as? HTTPURLResponse else {
            throw CleanupProviderError.invalidResponse("No HTTP response")
        }
        guard (200..<300).contains(httpResponse.statusCode) else {
            let bodyText = String(data: data, encoding: .utf8) ?? "<undecodable body>"
            throw CleanupProviderError.requestFailed("HTTP \(httpResponse.statusCode): \(bodyText)")
        }

        let decoded: ChatCompletionResponse
        do {
            decoded = try JSONDecoder().decode(ChatCompletionResponse.self, from: data)
        } catch {
            throw CleanupProviderError.invalidResponse("Could not decode chat completion: \(error.localizedDescription)")
        }

        guard let text = decoded.choices.first?.message.content, !text.isEmpty else {
            throw CleanupProviderError.invalidResponse("Empty completion from \(displayName)")
        }

        let latency = Date().timeIntervalSince(started)
        logger.debug("Cleanup via \(self.model, privacy: .public) completed in \(latency, format: .fixed(precision: 3))s")

        return CleanupResponse(
            cleanedText: text.trimmingCharacters(in: .whitespacesAndNewlines),
            latency: latency,
            providerID: id,
            modelID: model)
    }

    private func applyAuthHeader(to request: inout URLRequest) {
        guard let apiKey, !apiKey.isEmpty else { return }
        request.setValue("Bearer \(apiKey)", forHTTPHeaderField: "Authorization")
    }
}

// MARK: - Wire format

private struct ChatCompletionRequest: Encodable {
    struct Message: Encodable {
        let role: String
        let content: String
    }

    let model: String
    let messages: [Message]
    let temperature: Double
    let stream: Bool
}

private struct ChatCompletionResponse: Decodable {
    struct Choice: Decodable {
        struct Message: Decodable {
            let content: String
        }
        let message: Message
    }
    let choices: [Choice]
}
