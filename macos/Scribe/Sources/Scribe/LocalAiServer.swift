import Foundation

/// An app on this Mac that serves AI models and that Scribe knows how to inspect and unload.
enum LocalServerApp: String, CaseIterable, Codable, Sendable {
    case none
    case ollama
    case lmStudio

    var displayName: String {
        switch self {
        case .none:
            return "Scribe"
        case .ollama:
            return "Ollama"
        case .lmStudio:
            return "LM Studio"
        }
    }
}

/// Whether an OpenAI-compatible address is a server on this Mac, such as Ollama or LM Studio answering on loopback.
enum LocalAiServer {
    static let prewarmAfterIdleSeconds = 30
    static let ollamaAddress = "http://localhost:11434/v1"
    static let lmStudioAddress = "http://localhost:1234/v1"

    static func isOnThisMac(_ endpoint: String?) -> Bool {
        guard let uri = normalizedURL(endpoint) else {
            return false
        }
        guard let scheme = uri.scheme?.lowercased(), scheme == "http" || scheme == "https" else {
            return false
        }
        guard let host = uri.host(percentEncoded: false)?.lowercased() else {
            return false
        }
        return host == "localhost" || host == "::1" || host.hasPrefix("127.") || host.hasSuffix(".localhost")
    }

    static func appAt(_ endpoint: String?) -> LocalServerApp {
        guard let uri = normalizedURL(endpoint) else {
            return .none
        }
        guard uri.scheme?.lowercased() == "http",
            let components = URLComponents(url: uri, resolvingAgainstBaseURL: false),
            components.user == nil || components.user?.isEmpty == true,
            components.password == nil || components.password?.isEmpty == true,
            components.query == nil || components.query?.isEmpty == true,
            components.fragment == nil
        else {
            return .none
        }

        let path = components.percentEncodedPath.trimmingCharacters(in: .whitespacesAndNewlines)
        guard path.trimmingCharacters(in: CharacterSet(charactersIn: "/")) == "v1" else {
            return .none
        }

        guard let host = uri.host(percentEncoded: false)?.lowercased(),
            host == "localhost" || host == "127.0.0.1" || host == "::1"
        else {
            return .none
        }

        switch uri.port {
        case 11434:
            return .ollama
        case 1234:
            return .lmStudio
        default:
            return .none
        }
    }

    static func address(of app: LocalServerApp) -> String? {
        switch app {
        case .none:
            return nil
        case .ollama:
            return ollamaAddress
        case .lmStudio:
            return lmStudioAddress
        }
    }

    static func appForChatCompletionsURL(_ url: URL) -> LocalServerApp {
        guard var components = URLComponents(url: url, resolvingAgainstBaseURL: false) else {
            return .none
        }
        components.percentEncodedPath = "/v1"
        components.query = nil
        components.fragment = nil
        return appAt(components.url?.absoluteString)
    }

    private static func normalizedURL(_ endpoint: String?) -> URL? {
        guard let endpoint, !endpoint.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return nil
        }
        return URL(string: endpoint.trimmingCharacters(in: .whitespacesAndNewlines))
    }
}
