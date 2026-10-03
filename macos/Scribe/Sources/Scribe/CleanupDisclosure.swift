import Foundation

enum CleanupDisclosure {
    static let whatCleanupSends: String = {
        let cloudTerms = count(CleanupPrompt.maxGlossaryTermsCloud)
        let characters = count(CleanupPrompt.maxGlossaryChars)
        let localTerms = count(CleanupPrompt.maxGlossaryTermsLocal)
        let termLength = count(CleanupPrompt.maxGlossaryTermChars)
        let parts: [String] = [
            "Foundry Local runs cleanup on this Mac, so your text stays on it. Microsoft Foundry and any other AI ",
            "service you set up receive, with every cleanup request, the text Scribe recognized for that dictation, ",
            "the cleanup instructions with your writing style (or the matching app profile's), and, as vocabulary, ",
            "the words from your dictionary plus the word packs the dictation appears to mention, including ones ",
            "Scribe heard slightly differently: up to \(cloudTerms) words or phrases and \(characters) characters, ",
            "or \(localTerms) words or phrases with the short instructions. A word from your dictionary or a word ",
            "pack is not vocabulary, and is not sent, when what Scribe writes for it spans more than one line, runs ",
            "past \(termLength) characters, or needs formatting Scribe applies only on this Mac, such as dash or ",
            "spacing fixes.",
        ]
        return parts.joined()
    }()

    static let whatCleanupNeverSends =
        "Test Connection sends a short request holding the word \"ok\" and the current writing style and guardrails, "
        + "with none of your "
        + "vocabulary. When a recording starts, Ollama or LM Studio on this Mac is asked whether it holds the selected "
        + "model at the needed size; if not, Scribe sends a fixed readying request with only \"ok\" and fixed "
        + "instructions to that local app. With a context size chosen for Ollama, each native request first asks "
        + "Ollama for the model's context limit, using only its model name and any key saved for that address. "
        + "After the answer, Scribe asks Ollama which model it holds and its loaded size, without sending text. "
        + "Neither request contains dictated text or vocabulary. LM Studio's loaded size is checked before text is sent; "
        + "if nothing is held at its own size, only the fixed readying request loads it before the size is checked again. "
        + "Cleanup never sends "
        + "your snippet templates, and audio never leaves this Mac."

    static func summary(for kind: CleanupProviderKind, endpoint: String?, forceLocal: Bool? = nil) -> String {
        let local = forceLocal ?? (kind == .ollama || LocalAiServer.appAt(endpoint) != .none)
        switch kind {
        case .foundryLocal, .ollama:
            return "Your text, writing style and vocabulary stay on this Mac. Audio never leaves it."
        case .openAICompatible:
            if local {
                return "Your text, writing style and vocabulary stay on this Mac. Audio never leaves it."
            }
            return "Each cleanup sends the text Scribe heard, your writing style, and the dictionary and word pack "
                + "words it mentions to the address you enter. Audio never leaves this Mac."
        case .microsoftFoundry:
            return "Each cleanup sends the text Scribe heard, your writing style, and the dictionary and word pack "
                + "words it mentions to your Microsoft Foundry deployment. Audio never leaves this Mac."
        }
    }

    private static func count(_ value: Int) -> String {
        let formatter = NumberFormatter()
        formatter.numberStyle = .decimal
        return formatter.string(from: NSNumber(value: value)) ?? String(value)
    }
}
