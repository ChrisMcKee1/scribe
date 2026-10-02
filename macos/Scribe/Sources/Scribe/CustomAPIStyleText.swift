import Foundation

enum CustomAPIStyleText {
    static let choices = CustomAPIStyle.allCases
    static let responsesStoreNotice = "With Responses, Scribe asks the service not to store responses."
    static let addressHint =
        "The service's OpenAI-compatible address, usually ending in /v1. An address that ends in /chat/completions "
        + "or /responses is used as it is. For Ollama or LM Studio on this Mac, choose On this PC instead."

    static func name(of style: CustomAPIStyle) -> String {
        style == .responses ? "Responses" : "Chat Completions"
    }

    static func canChoose(_ address: String?) -> Bool {
        CustomServiceAddress.namedStyle(address) == nil && LocalAiServer.appAt(address) == .none
    }

    static func hint(_ address: String?) -> String {
        let app = LocalAiServer.appAt(address)
        if app == .ollama {
            return "Ollama at its own address uses Chat Completions."
        }
        if app == .lmStudio {
            return "LM Studio at its own address uses Chat Completions."
        }

        switch CustomServiceAddress.namedStyle(address) {
        case .responses:
            return "This address ends in /responses, so Scribe uses Responses. " + responsesStoreNotice
        case .chatCompletions:
            return "This address ends in /chat/completions, so Scribe uses Chat Completions."
        case nil:
            return "Most services take Chat Completions. Choose Responses only if your service needs it. "
                + responsesStoreNotice
        }
    }
}
