import Foundation

enum SettingsSearchRequirementKind: String, Sendable {
    case checkbox
    case radio
    case view
    case action
}

struct SettingsSearchRequirement: Equatable, Sendable {
    let controlName: String
    let label: String
    let kind: SettingsSearchRequirementKind
}

struct SettingsSearchEntry: Identifiable, Equatable, Sendable {
    let id: String
    let section: SettingsSection
    let targetID: String
    let label: String
    let context: String?
    let keywords: [String]
    let requirements: [SettingsSearchRequirement]

    var pageLabel: String { section.label }

    var displayLabel: String {
        guard let context, !context.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return label
        }
        return "\(label) (\(context))"
    }
}

struct SettingsSearchResult: Equatable, Sendable {
    let entry: SettingsSearchEntry
    let displayText: String

    var section: SettingsSection { entry.section }
    var targetID: String { entry.targetID }
    var label: String { entry.label }
    var context: String? { entry.context }
    var requirements: [SettingsSearchRequirement] { entry.requirements }
}

enum SettingsSearchIndex {
    static let maxResults = 8

    private static let requiresAI = SettingsSearchRequirement(
        controlName: "ai.enabled", label: "Use AI cleanup", kind: .checkbox)
    private static let requiresLocal = SettingsSearchRequirement(
        controlName: "ai.provider", label: "On this PC", kind: .radio)
    private static let requiresScribeModel = SettingsSearchRequirement(
        controlName: "ai.provider", label: "Let Scribe manage it", kind: .radio)
    private static let requiresOllama = SettingsSearchRequirement(
        controlName: "ai.local.ollama", label: "Ollama", kind: .radio)
    private static let requiresLMStudio = SettingsSearchRequirement(
        controlName: "ai.local.lmstudio", label: "LM Studio", kind: .radio)
    private static let requiresFoundry = SettingsSearchRequirement(
        controlName: "ai.provider", label: "Microsoft Foundry", kind: .radio)
    private static let requiresOtherService = SettingsSearchRequirement(
        controlName: "ai.provider", label: "Another AI service", kind: .radio)
    private static let requiresServicePrincipal = SettingsSearchRequirement(
        controlName: "ai.azure.auth", label: "Service principal", kind: .radio)

    static let entries: [SettingsSearchEntry] = [
        entry("dictation.microphone", .dictation, "dictation.microphone", "Microphone", ["input", "device", "sound"]),
        entry(
            "dictation.shortcut", .dictation, "dictation.shortcut", "Dictation shortcut",
            ["hotkey", "shortcut", "key", "keyboard", "push to talk", "push-to-talk", "hold", "toggle", "press"]),
        entry(
            "dictation.input-monitoring", .dictation, "dictation.shortcut", "Input Monitoring access",
            ["permission", "privacy", "keyboard", "system settings"]),
        entry(
            "dictation.silence-stop", .dictation, "dictation.silence-stop", "Stop when I stop talking",
            ["vad", "silence", "automatic stop", "toggle"]),
        entry(
            "dictation.space", .dictation, "dictation.space", "Add a space after each dictation",
            ["typing", "trailing space", "spacing"]),
        entry(
            "dictation.indicator", .dictation, "dictation.indicator", "Show the recording indicator",
            ["overlay", "pill", "recording", "indicator", "hide", "show", "visibility"]),
        entry(
            "dictation.indicator.position", .dictation, "dictation.indicator.position", "Where it appears",
            ["overlay", "pill", "position", "anchor", "recording", "indicator"]),
        entry(
            "dictation.indicator.preview", .dictation, "dictation.indicator.position", "Preview on screen",
            ["overlay", "pill", "position", "anchor", "recording", "indicator", "preview"]),
        entry(
            "dictation.startup", .dictation, "dictation.startup", "Start Scribe when you log in",
            ["startup", "boot", "launch", "login", "sign in"]),

        entry("try.page", .tryDictation, "try.input", "Try dictation", ["playground", "test", "sample", "try"]),
        entry(
            "try.result", .tryDictation, "try.result", "Result", ["pipeline", "timing", "changes", "raw", "typed"]),

        entry("ai.enabled", .aiCleanup, "ai.enabled", "Use AI cleanup", ["polish", "grammar", "punctuation"]),
        entry(
            "ai.local", .aiCleanup, "ai.provider", "On this PC", ["provider", "offline", "local", "private"], nil,
            [requiresAI]),
        entry(
            "ai.foundry", .aiCleanup, "ai.provider", "Microsoft Foundry", ["provider", "azure", "cloud"], nil,
            [requiresAI]),
        entry(
            "ai.custom", .aiCleanup, "ai.provider", "Another AI service",
            ["provider", "openrouter", "openai", "server"], nil, [requiresAI]),
        entry(
            "ai.local.scribe", .aiCleanup, "ai.provider", "Let Scribe manage it",
            ["foundry local", "download", "local model", "scribe"], "On this PC", [requiresAI, requiresLocal]),
        entry(
            "ai.local.ollama", .aiCleanup, "ai.provider", "Ollama", ["local model", "gemma", "llama"],
            "On this PC", [requiresAI, requiresLocal]),
        entry(
            "ai.local.lmstudio", .aiCleanup, "ai.provider", "LM Studio",
            ["lm studio", "lmstudio", "local model"], "On this PC", [requiresAI, requiresLocal]),
        entry(
            "ai.model", .aiCleanup, "ai.provider", "Model alias",
            ["foundry local", "download", "load", "free memory"], "On this PC",
            [requiresAI, requiresLocal, requiresScribeModel]),
        entry(
            "ai.local.scribe.vocabulary", .aiCleanup, "ai.provider", LocalModelTuningText.wholeVocabularyTitle,
            ["vocabulary", "dictionary", "word packs", "context", "foundry local"], "On this PC",
            [requiresAI, requiresLocal, requiresScribeModel]),
        entry(
            "ai.local.ollama.context", .aiCleanup, "ai.provider", LocalModelTuningText.contextSizeTitle,
            ["context", "context window", "context length", "num_ctx", "tokens", "memory"], "Ollama",
            [requiresAI, requiresLocal, requiresOllama]),
        entry(
            "ai.local.ollama.idle", .aiCleanup, "ai.provider", "Free local model memory after",
            ["idle", "release", "memory", "minutes", "never"], "Ollama",
            [requiresAI, requiresLocal, requiresOllama]),
        entry(
            "ai.local.ollama.vocabulary", .aiCleanup, "ai.provider", LocalModelTuningText.wholeVocabularyTitle,
            ["vocabulary", "dictionary", "word packs", "context"], "Ollama",
            [requiresAI, requiresLocal, requiresOllama]),
        entry(
            "ai.local.lmstudio.context", .aiCleanup, "ai.provider", LocalModelTuningText.contextSizeTitle,
            ["context", "context window", "context length", "tokens", "memory"], "LM Studio",
            [requiresAI, requiresLocal, requiresLMStudio]),
        entry(
            "ai.local.lmstudio.idle", .aiCleanup, "ai.provider", "Free local model memory after",
            ["idle", "release", "memory", "minutes", "never"], "LM Studio",
            [requiresAI, requiresLocal, requiresLMStudio]),
        entry(
            "ai.local.lmstudio.vocabulary", .aiCleanup, "ai.provider", LocalModelTuningText.wholeVocabularyTitle,
            ["vocabulary", "dictionary", "word packs", "context"], "LM Studio",
            [requiresAI, requiresLocal, requiresLMStudio]),
        entry(
            "ai.custom.endpoint", .aiCleanup, "ai.provider", "Base URL", ["url", "openrouter", "address"],
            "Another AI service", [requiresAI, requiresOtherService]),
        entry(
            "ai.custom.api", .aiCleanup, "ai.provider", "API", ["chat completions", "responses", "openai"],
            "Another AI service", [requiresAI, requiresOtherService]),
        entry(
            "ai.custom.model", .aiCleanup, "ai.provider", "Model", ["model", "openrouter"],
            "Another AI service", [requiresAI, requiresOtherService]),
        entry(
            "ai.custom.key", .aiCleanup, "ai.provider", "API key (optional)", ["secret", "token"],
            "Another AI service", [requiresAI, requiresOtherService]),
        entry(
            "ai.azure.endpoint", .aiCleanup, "ai.provider", "Endpoint", ["address", "url", "foundry"],
            "Microsoft Foundry", [requiresAI, requiresFoundry]),
        entry(
            "ai.azure.deployment", .aiCleanup, "ai.provider", "Deployment name", ["model", "foundry"],
            "Microsoft Foundry", [requiresAI, requiresFoundry]),
        entry(
            "ai.azure.cache", .aiCleanup, "ai.provider", "Let Microsoft Foundry cache what Scribe sends",
            ["cache", "caching", "prompt cache", "privacy", "retention"], "Microsoft Foundry",
            [requiresAI, requiresFoundry]),
        entry(
            "ai.azure.auth", .aiCleanup, "ai.provider", "Authentication", ["sign in", "azure cli", "az login"],
            "Microsoft Foundry", [requiresAI, requiresFoundry]),
        entry(
            "ai.azure.api-key", .aiCleanup, "ai.provider", "Microsoft Foundry API key",
            ["secret", "token", "keychain", "authentication"], "Microsoft Foundry",
            [requiresAI, requiresFoundry]),
        entry(
            "ai.azure.sp.tenant", .aiCleanup, "ai.provider", "Tenant ID", ["service principal", "entra"],
            "Microsoft Foundry", [requiresAI, requiresFoundry, requiresServicePrincipal]),
        entry(
            "ai.azure.sp.client", .aiCleanup, "ai.provider", "Client ID", ["service principal", "app registration"],
            "Microsoft Foundry", [requiresAI, requiresFoundry, requiresServicePrincipal]),
        entry(
            "ai.azure.sp.secret", .aiCleanup, "ai.provider", "Client secret", ["service principal", "password"],
            "Microsoft Foundry", [requiresAI, requiresFoundry, requiresServicePrincipal]),
        entry(
            "ai.writing-style", .aiCleanup, "ai.writing-style", "Writing style", ["prompt", "tone"], nil, [requiresAI]),
        entry(
            "ai.restore-writing-style", .aiCleanup, "ai.writing-style", "Restore default writing style",
            ["reset", "restore", "default prompt"], nil, [requiresAI]),
        entry(
            "ai.guardrails.detailed", .aiCleanup, "ai.guardrails", "Detailed guardrail prompt",
            ["system prompt", "instructions", "cloud", "frontier"], nil, [requiresAI]),
        entry(
            "ai.guardrails.local", .aiCleanup, "ai.guardrails", "Local guardrail prompt",
            ["system prompt", "instructions", "on this pc"], nil, [requiresAI]),
        entry(
            "ai.guardrails.restore", .aiCleanup, "ai.guardrails", "Restore default guardrail prompts",
            ["reset", "restore", "default prompt"], nil, [requiresAI]),

        entry(
            "dictionary.words", .dictionary, "dictionary.words", "Your words",
            ["dictionary", "vocabulary", "words", "replacement", "spelling"]),
        entry(
            "dictionary.word-packs", .dictionary, "dictionary.word-packs", "Word packs",
            ["library", "libraries", "vocabulary", "packs", "terms"]),

        entry(
            "snippets.page", .voiceSnippets, "snippets.page", "Voice snippets",
            ["snippet", "template", "phrase", "trigger", "expand", "email", "sign-off"]),
        entry(
            "profiles.page", .appProfiles, "profiles.page", "App profiles",
            ["profile", "per app", "program", "process", "writing style", "line breaks"]),

        entry("history.keep", .history, "history.keep", "Keep dictations", ["retention", "delete", "days"]),
        entry(
            "history.delete", .history, "history.delete", "Delete all history",
            ["clear", "remove", "saved", "dictations"]),
        entry("history.list", .history, "history.list", "History list", ["search", "copy", "feedback", "table"]),

        entry("usage.period", .usage, "usage.period", "Period", ["usage", "range", "statistics"]),
        entry("usage.totals", .usage, "usage.totals", "Totals", ["dictations", "words", "speaking time"]),
        entry(
            "usage.terms", .usage, "usage.terms", "Words you could add",
            ["dictionary", "suggestions", "recurring terms"]),
        entry("usage.summary", .usage, "usage.summary", "AI summary", ["summary", "insights", "cleanup"]),

        entry(
            "advanced.speech-model", .advanced, "advanced.speech-model", "Speech model",
            ["model", "recognition", "parakeet", "foundry local"]),
        entry("advanced.threads", .advanced, "advanced.threads", "Processor threads", ["cpu", "decode", "advanced"]),
        entry(
            "advanced.free-memory", .advanced, "advanced.free-memory", "Free memory when Scribe is not used",
            ["idle", "release", "model", "memory"]),
        entry(
            "advanced.trim-silence", .advanced, "advanced.trim-silence", "Trim silence",
            ["vad", "voice activity detection", "silence"]),
        entry(
            "advanced.longest-recording", .advanced, "advanced.longest-recording", "Longest recording",
            ["duration", "limit", "minutes"]),
        entry(
            "advanced.typing-method", .advanced, "advanced.typing-method", "Typing method",
            ["paste", "clipboard", "type", "accessibility"]),
        entry(
            "advanced.accessibility", .advanced, "advanced.typing-method", "Accessibility insertion",
            ["permission", "privacy", "typing", "macos"]),
        entry(
            "advanced.line-breaks", .advanced, "advanced.line-breaks", "Line breaks", ["newline", "enter", "terminal"]),
        entry(
            "advanced.chat-lines", .advanced, "advanced.chat-lines", "Do not send chat messages early",
            ["teams", "slack", "enter", "shift return"]),
        entry(
            "advanced.text-changes", .advanced, "advanced.text-changes", "Apply your dictionary and snippets",
            ["dictionary", "snippets", "post processing", "vocabulary"]),

        entry(
            "diagnostics.help", .diagnostics, "diagnostics.help", "Report a problem", ["support", "github", "issue"]),
        entry(
            "diagnostics.save", .diagnostics, "diagnostics.data", "Save diagnostics",
            ["logs", "zip", "support", "data"]),
        entry("diagnostics.logs", .diagnostics, "diagnostics.data", "Logs", ["unified logging", "console"]),
        entry(
            "diagnostics.speed", .diagnostics, "diagnostics.speed", "How long each step takes",
            ["p50", "p95", "latency", "rtf", "performance"]),
        entry("diagnostics.window", .diagnostics, "diagnostics.speed", "Window", ["period", "range", "speed"]),
        entry(
            "diagnostics.mac", .diagnostics, "diagnostics.mac", "This Mac",
            ["system", "memory", "processor", "computer"]),
        entry(
            "diagnostics.data-file", .diagnostics, "diagnostics.data-file", "Scribe data file",
            ["database", "folder", "path", "finder"]),
    ]

    static func search(_ query: String?, maxResults: Int = maxResults) -> [SettingsSearchResult] {
        guard maxResults > 0, let query, !query.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return []
        }
        let terms = words(query)
        guard !terms.isEmpty else { return [] }

        let entryWords = EntryWordsIndex.all
        return entries.enumerated()
            .compactMap { index, entry -> (entry: SettingsSearchEntry, index: Int, rank: Int)? in
                let rank = rank(entryWords[index], terms: terms)
                return rank == Int.max ? nil : (entry, index, rank)
            }
            .sorted { left, right in
                if left.rank != right.rank { return left.rank < right.rank }
                let leftPosition = SettingsSection.allCases.firstIndex(of: left.entry.section) ?? 0
                let rightPosition = SettingsSection.allCases.firstIndex(of: right.entry.section) ?? 0
                if leftPosition != rightPosition { return leftPosition < rightPosition }
                return left.index < right.index
            }
            .prefix(min(maxResults, Self.maxResults))
            .map { candidate in
                SettingsSearchResult(
                    entry: candidate.entry,
                    displayText: "\(candidate.entry.displayLabel) on \(candidate.entry.pageLabel)")
            }
    }

    static func words(_ value: String?) -> [String] {
        guard let value, !value.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return []
        }

        var result: [String] = []
        var current = ""
        for scalar in value.decomposedStringWithCanonicalMapping.unicodeScalars {
            if CharacterSet.nonBaseCharacters.contains(scalar) {
                continue
            }
            if CharacterSet.alphanumerics.contains(scalar) {
                current += String(scalar).lowercased(with: Locale(identifier: "en_US_POSIX"))
            } else if !current.isEmpty {
                result.append(current)
                current.removeAll(keepingCapacity: true)
            }
        }
        if !current.isEmpty {
            result.append(current)
        }
        return result
    }

    private static func entry(
        _ id: String,
        _ section: SettingsSection,
        _ targetID: String,
        _ label: String,
        _ keywords: [String],
        _ context: String? = nil,
        _ requirements: [SettingsSearchRequirement] = []
    ) -> SettingsSearchEntry {
        SettingsSearchEntry(
            id: id,
            section: section,
            targetID: targetID,
            label: label,
            context: context,
            keywords: keywords,
            requirements: requirements)
    }

    private static func rank(_ words: EntryWords, terms: [String]) -> Int {
        if allTermsMatch(terms, words.label) { return 0 }
        if allTermsMatch(terms, words.labelAndKeywords) { return 2 }
        if allTermsMatch(terms, words.page) { return 3 }
        return Int.max
    }

    private static func allTermsMatch(_ terms: [String], _ words: [String]) -> Bool {
        terms.allSatisfy { term in words.contains { $0.hasPrefix(term) } }
    }

    private struct EntryWords {
        let label: [String]
        let labelAndKeywords: [String]
        let page: [String]
    }

    private enum EntryWordsIndex {
        static let all: [EntryWords] = build()

        private static func build() -> [EntryWords] {
            var pages: [SettingsSection: [String]] = [:]
            return entries.map { entry in
                let labelWords = words(entry.displayLabel)
                let keywordWords = entry.keywords.flatMap(words)
                let pageWords = pages[entry.section] ?? words(entry.pageLabel)
                pages[entry.section] = pageWords
                return EntryWords(label: labelWords, labelAndKeywords: labelWords + keywordWords, page: pageWords)
            }
        }
    }
}
