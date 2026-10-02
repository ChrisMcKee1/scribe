import Foundation

enum CleanupDisclosure {
    static let whatCleanupSends =
        "Foundry Local runs cleanup on this Mac, so your text stays on it. Microsoft Foundry and any other AI service "
        + "you set up receive, with every cleanup request, the text Scribe recognized for that dictation, the cleanup "
        + "instructions with your writing style (or the matching app profile's), and, as vocabulary, the words from "
        + "your dictionary plus the word packs the dictation appears to mention, including ones Scribe heard slightly "
        + "differently: up to \(count(CleanupPrompt.maxGlossaryTermsCloud)) words or phrases and "
        + "\(count(CleanupPrompt.maxGlossaryChars)) characters, or \(count(CleanupPrompt.maxGlossaryTermsLocal)) words "
        + "or phrases with the short instructions. A word from your dictionary or a word pack is not vocabulary, and "
        + "is not sent, when what Scribe writes for it spans more than one line, runs past "
        + "\(count(CleanupPrompt.maxGlossaryTermChars)) characters, or needs formatting Scribe applies only on this "
        + "Mac, such as dash or spacing fixes."

    static let whatCleanupNeverSends =
        "Each time cleanup connects, it first sends a short test request holding the word \"ok\" and the same "
        + "instructions, with none of your vocabulary. Cleanup never sends your snippet templates, and audio never "
        + "leaves this Mac."

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
