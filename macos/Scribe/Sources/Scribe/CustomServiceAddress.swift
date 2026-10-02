import Foundation

enum CustomServiceAddress {
    private static let chatCompletionsPath = "/chat/completions"
    private static let responsesPath = "/responses"
    private static let completionsPath = "/completions"

    static func namedStyle(_ address: String?) -> CustomAPIStyle? {
        guard let uri = normalizedURL(address) else {
            return nil
        }
        switch split(uri).named {
        case .chatCompletions:
            return .chatCompletions
        case .responses:
            return .responses
        case .none:
            return nil
        }
    }

    static func baseURL(_ address: URL) -> URL {
        split(address).base
    }

    static func namesOldCompletions(_ address: String?) -> Bool {
        guard let uri = normalizedURL(address) else {
            return false
        }
        let path = uri.path.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        return path.lowercased().hasSuffix(completionsPath.trimmingCharacters(in: CharacterSet(charactersIn: "/")))
            && !path.lowercased().hasSuffix(chatCompletionsPath.trimmingCharacters(in: CharacterSet(charactersIn: "/")))
    }

    static func effective(_ address: String?, chosen: CustomAPIStyle) -> CustomAPIStyle {
        if LocalAiServer.appAt(address) != .none {
            return .chatCompletions
        }
        return namedStyle(address) ?? chosen
    }

    private static func split(_ address: URL) -> (base: URL, named: CustomAPIStyle?) {
        guard var components = URLComponents(url: address, resolvingAgainstBaseURL: false) else {
            return (address, nil)
        }
        let path = components.percentEncodedPath.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        if path.lowercased().hasSuffix(chatCompletionsPath.trimmingCharacters(in: CharacterSet(charactersIn: "/"))) {
            components.percentEncodedPath = String(
                components.percentEncodedPath.dropLast(chatCompletionsPath.count))
            return (components.url ?? address, .chatCompletions)
        }
        if path.lowercased().hasSuffix(responsesPath.trimmingCharacters(in: CharacterSet(charactersIn: "/"))) {
            components.percentEncodedPath = String(components.percentEncodedPath.dropLast(responsesPath.count))
            return (components.url ?? address, .responses)
        }
        return (address, nil)
    }

    private static func normalizedURL(_ address: String?) -> URL? {
        guard let address else {
            return nil
        }
        let trimmed = address.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else {
            return nil
        }
        return URL(string: trimmed)
    }
}
