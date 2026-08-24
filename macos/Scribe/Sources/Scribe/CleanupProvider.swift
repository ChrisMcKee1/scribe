import Foundation

/// A request to clean up a raw transcript into well-punctuated prose. Mirrors the shape of
/// Windows Scribe's cleanup request (writing style + transcript + metadata), but this port only
/// carries what's actually consumed today; per-app writing style overrides and glossary injection
/// are tracked separately in PORTING-PLAN.md and not implemented yet.
struct CleanupRequest {
    let transcript: String
    let writingStylePrompt: String
    /// Single-line mode collapses paragraph breaks for targets that don't want multi-line text
    /// (e.g. a single text field). Not wired to any UI yet; defaults to multi-line.
    let singleLineMode: Bool

    init(transcript: String, writingStylePrompt: String = CleanupPrompt.defaultWritingStyle, singleLineMode: Bool = false) {
        self.transcript = transcript
        self.writingStylePrompt = writingStylePrompt
        self.singleLineMode = singleLineMode
    }
}

struct CleanupResponse {
    let cleanedText: String
    let latency: TimeInterval
    let providerID: String
    let modelID: String
}

enum CleanupProviderError: Error, LocalizedError {
    case notConfigured(String)
    case requestFailed(String)
    case invalidResponse(String)
    case timedOut

    var errorDescription: String? {
        switch self {
        case .notConfigured(let message): return "Cleanup provider not configured: \(message)"
        case .requestFailed(let message): return "Cleanup request failed: \(message)"
        case .invalidResponse(let message): return "Cleanup provider returned an invalid response: \(message)"
        case .timedOut: return "Cleanup request timed out"
        }
    }
}

struct CleanupHealthSnapshot {
    let providerID: String
    let reachable: Bool
    let detail: String
}

/// Common surface every AI cleanup backend implements: Foundry Local, managed Ollama, any
/// OpenAI-compatible endpoint (LM Studio, OpenRouter, user-hosted), and (future) Microsoft Foundry
/// cloud. See PORTING-PLAN.md "AI cleanup provider architecture" for the design rationale.
protocol CleanupProvider {
    var id: String { get }
    var displayName: String { get }

    /// Cheap reachability/config check; does not guarantee the model is loaded.
    func healthSnapshot() async -> CleanupHealthSnapshot

    func clean(_ request: CleanupRequest) async throws -> CleanupResponse
}

/// The default editorial writing-style prompt, shared across providers so cleanup quality doesn't
/// silently drift between them. Kept dash-free per repo convention (see AGENTS.md); this is shown
/// to the model on every dictation, so any dash here would teach the model to imitate it.
enum CleanupPrompt {
    static let defaultWritingStyle = """
    Write in the speaker's language using clear, natural, well-structured prose. Never translate \
    the dictation unless explicitly asked to. Use correct punctuation, meaning commas, periods, \
    semicolons, colons, question marks, and parentheses, according to sentence structure. Do not \
    use dash punctuation to join clauses; use a comma, colon, semicolon, or period instead. Break \
    long run-on speech into properly formed sentences, and start a new paragraph when the topic \
    shifts. Remove filler words and false starts (such as "um", "uh", "you know", and "like") and \
    fix small grammar slips, while keeping the meaning, intent, and vocabulary. When the speaker \
    corrects themselves mid-speech, keep only the corrected version and drop what it replaced. If \
    the same point is repeated, merge it into a single clear statement. Keep the identity of \
    technical terms, product names, model names, code, and URLs unchanged. Write numbers the way \
    they are normally written rather than spelled out. Only reformat what was actually spoken, and \
    never invent or change a value that was not said. Respond with only the cleaned transcript, no \
    preamble or commentary.
    """
}
