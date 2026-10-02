import Foundation

enum SettingsSearchRequirementKind: Sendable {
    case checkBox
    case radio
    case view
    case action
}

/// A control that has to be on, chosen or open before a searched setting is visible.
struct SettingsSearchRequirement: Equatable, Sendable {
    let anchor: String
    let label: String
    let kind: SettingsSearchRequirementKind
}

/// One searchable setting. `id` is the Windows id where Windows has the same setting, so the two indexes can be
/// compared; `anchor` is what the page scrolls to.
struct SettingsSearchEntry: Equatable, Sendable {
    let id: String
    let page: SettingsPage
    let label: String
    let context: String?
    let keywords: [String]
    let requirements: [SettingsSearchRequirement]

    var displayLabel: String {
        guard let context, !context.trimmingCharacters(in: .whitespaces).isEmpty else { return label }
        return label + " (" + context + ")"
    }
}

struct SettingsSearchResult: Equatable, Sendable {
    let entry: SettingsSearchEntry
    let displayText: String
}

/// A Windows entry the Mac does not carry, and why.
struct SettingsSearchOmission: Equatable, Sendable {
    let id: String
    let because: CopyDeviation
}

/// Find a setting: a port of `SettingsSearchIndex.cs`. The ids, pages, ranking and keywords follow Windows, and the
/// keywords keep the names this window used to give things ("hotkey", "library", "playground") so a search for an old
/// word still lands. Labels come from the copy catalogs, so a label cannot differ from the page's.
enum SettingsSearchIndex {
    static let maxResults = 8

    /// Windows entries that have no Mac twin.
    static let omissions: [SettingsSearchOmission] = [
        SettingsSearchOmission(id: "ai.copilot.model", because: .copilotUnavailable),
        SettingsSearchOmission(id: "advanced.accent", because: .windowsOnly),
    ]

    /// Mac entries Windows does not have, with why.
    static let macOnly: [String: String] = [
        "dictation.permissions": "The Mac asks for Microphone, Accessibility and Input Monitoring access.",
        "diagnostics.page": "Windows has no entry for Diagnostics, so the page cannot be found by name there.",
        "about.page": "Windows has no entry for About, so the page cannot be found by name there.",
    ]

    private static let ai = SettingsSearchRequirement(
        anchor: "ai.enabled", label: SettingsCopy.aiCleanup.use.render(), kind: .checkBox)
    private static let local = SettingsSearchRequirement(
        anchor: "ai.local", label: SettingsCopy.aiCleanup.onThisMac.render(), kind: .radio)
    private static let foundryLocal = SettingsSearchRequirement(
        anchor: "ai.local.scribe", label: SettingsCopy.aiCleanup.foundryLocal.render(), kind: .radio)
    private static let foundry = SettingsSearchRequirement(
        anchor: "ai.foundry", label: SettingsCopy.aiCleanup.foundry.render(), kind: .radio)
    private static let custom = SettingsSearchRequirement(
        anchor: "ai.custom", label: SettingsCopy.aiCleanup.anotherService.render(), kind: .radio)
    private static let ollama = SettingsSearchRequirement(
        anchor: "ai.local.ollama", label: SettingsCopy.aiCleanup.ollama.render(), kind: .radio)
    private static let lmStudio = SettingsSearchRequirement(
        anchor: "ai.local.lmstudio", label: SettingsCopy.aiCleanup.lmStudio.render(), kind: .radio)
    private static let azureCli = SettingsSearchRequirement(
        anchor: "ai.azure.auth.cli", label: SettingsCopy.aiCleanup.signInAccount.render(), kind: .radio)
    private static let azureApp = SettingsSearchRequirement(
        anchor: "ai.azure.auth.sp", label: SettingsCopy.aiCleanup.signInApp.render(), kind: .radio)
    private static let azureKey = SettingsSearchRequirement(
        anchor: "ai.azure.auth.key", label: SettingsCopy.aiCleanup.signInKey.render(), kind: .radio)
    private static let azureSignIn = SettingsSearchRequirement(
        anchor: "ai.azure.signin", label: "Sign in to Azure", kind: .action)
    private static let azureManual = SettingsSearchRequirement(
        anchor: "ai.azure.manual", label: SettingsCopy.aiCleanup.enterManually.render(), kind: .view)

    private static func entry(
        _ id: String, _ page: SettingsPage, _ label: CopyItem, _ keywords: [String], context: CopyItem? = nil,
        requires: [SettingsSearchRequirement] = []
    ) -> SettingsSearchEntry {
        SettingsSearchEntry(
            id: id, page: page, label: label.render(), context: context?.render(), keywords: keywords,
            requirements: requires)
    }

    static let entries: [SettingsSearchEntry] = dictationEntries + aiEntries + otherEntries

    private static var dictationEntries: [SettingsSearchEntry] {
        let c = SettingsCopy.dictation
        return [
            entry("dictation.microphone", .dictation, c.microphone, ["input", "device", "sound"]),
            entry(
                "dictation.shortcut", .dictation, c.shortcut,
                [
                    "hotkey", "shortcut", "key", "keyboard", "push to talk", "push-to-talk", "hold", "toggle", "press",
                    "caps lock",
                ]),
            entry(
                "dictation.shortcut.raw", .dictation, c.plainShortcut,
                ["hotkey", "shortcut", "key", "raw", "dictation only", "hold", "toggle", "press"]),
            entry(
                "dictation.silence-stop", .dictation, c.silenceStop, ["vad", "silence", "automatic stop", "toggle"]),
            entry("dictation.space", .dictation, c.addSpace, ["typing", "trailing space", "spacing"]),
            entry(
                "dictation.indicator", .dictation, c.showIndicator, ["overlay", "pill", "recording", "indicator"]),
            entry(
                "dictation.startup", .dictation, c.openAtLogin,
                ["startup", "boot", "launch", "sign in", "login items", "open at login"]),
            entry(
                "dictation.permissions", .dictation, c.permissions,
                ["microphone", "accessibility", "input monitoring", "privacy", "allow", "system settings"]),
            entry("try.page", .tryDictation, SettingsCopy.tryDictation.tryIt, ["playground", "test", "sample", "try"]),
        ]
    }

    private static var aiEntries: [SettingsSearchEntry] {
        let c = SettingsCopy.aiCleanup
        let onMac = c.onThisMac
        return [
            entry("ai.enabled", .aiCleanup, c.use, ["polish", "grammar", "punctuation"]),
            entry("ai.local", .aiCleanup, c.onThisMac, ["provider", "offline", "local", "private"], requires: [ai]),
            entry("ai.copilot", .aiCleanup, c.copilot, ["provider", "github", "unavailable"], requires: [ai]),
            entry("ai.foundry", .aiCleanup, c.foundry, ["provider", "azure"], requires: [ai]),
            entry(
                "ai.custom", .aiCleanup, c.anotherService, ["provider", "openrouter", "openai", "server"],
                requires: [ai]),
            entry(
                "ai.local.scribe", .aiCleanup, c.foundryLocal, ["foundry local", "download", "local model", "scribe"],
                context: onMac, requires: [ai, local]),
            entry(
                "ai.local.ollama", .aiCleanup, c.ollama, ["local model", "gemma", "llama", "free memory"],
                context: onMac, requires: [ai, local]),
            entry(
                "ai.local.lmstudio", .aiCleanup, c.lmStudio, ["lm studio", "lmstudio", "local model", "free memory"],
                context: onMac, requires: [ai, local]),
            entry(
                "ai.model", .aiCleanup, c.model, ["foundry local", "download", "load", "free memory"],
                context: onMac, requires: [ai, local, foundryLocal]),
            entry(
                "ai.local.scribe.vocabulary", .aiCleanup, c.wholeVocabulary,
                ["vocabulary", "dictionary", "word packs", "context", "foundry local"],
                context: onMac, requires: [ai, local, foundryLocal]),
            entry(
                "ai.local.ollama.context", .aiCleanup, c.contextSize,
                ["context", "context window", "context length", "num_ctx", "tokens", "memory"],
                context: c.ollama, requires: [ai, local, ollama]),
            entry(
                "ai.local.ollama.vocabulary", .aiCleanup, c.wholeVocabulary,
                ["vocabulary", "dictionary", "word packs", "context"], context: c.ollama,
                requires: [ai, local, ollama]),
            entry(
                "ai.local.lmstudio.context", .aiCleanup, c.contextSize,
                ["context", "context window", "context length", "tokens", "memory"],
                context: c.lmStudio, requires: [ai, local, lmStudio]),
            entry(
                "ai.local.lmstudio.vocabulary", .aiCleanup, c.wholeVocabulary,
                ["vocabulary", "dictionary", "word packs", "context"], context: c.lmStudio,
                requires: [ai, local, lmStudio]),
        ] + azureEntries + [
            entry("ai.writing-style", .aiCleanup, c.writingStyle, ["prompt", "tone"], requires: [ai]),
            entry(
                "ai.prompt-style", .aiCleanup, c.instructionsTitle, ["prompt", "advanced"], requires: [ai]),
            entry(
                "ai.prompt.detailed", .aiCleanup, c.detailedInstructions, ["prompt", "frontier"], requires: [ai]),
            entry("ai.prompt.short", .aiCleanup, c.shortInstructions, ["prompt", "local"], requires: [ai]),
        ]
    }

    private static var azureEntries: [SettingsSearchEntry] {
        let c = SettingsCopy.aiCleanup
        let f = c.foundry
        return [
            entry(
                "ai.azure.auth.cli", .aiCleanup, c.signInAccount, ["sign in", "browser", "tenant"],
                context: f, requires: [ai, foundry]),
            entry(
                "ai.azure.auth.sp", .aiCleanup, c.signInApp, ["sign in", "entra", "client"],
                context: f, requires: [ai, foundry]),
            entry(
                "ai.azure.auth.key", .aiCleanup, c.signInKey, ["sign in", "resource key"],
                context: f, requires: [ai, foundry]),
            entry(
                "ai.azure.tenant", .aiCleanup, c.tenantOptional, ["directory", "azure cli"],
                context: f, requires: [ai, foundry, azureCli]),
            entry(
                "ai.azure.subscription", .aiCleanup, c.subscription, ["azure"],
                context: f, requires: [ai, foundry, azureCli, azureSignIn]),
            entry(
                "ai.azure.model", .aiCleanup, c.model, ["deployment", "foundry"],
                context: f, requires: [ai, foundry, azureCli, azureSignIn]),
            entry(
                "ai.azure.sp.tenant", .aiCleanup, c.directoryId, ["service principal", "entra"],
                context: f, requires: [ai, foundry, azureApp]),
            entry(
                "ai.azure.sp.client", .aiCleanup, c.clientId, ["service principal", "app registration"],
                context: f, requires: [ai, foundry, azureApp]),
            entry(
                "ai.azure.sp.secret", .aiCleanup, c.clientSecret, ["service principal", "password"],
                context: f, requires: [ai, foundry, azureApp]),
            entry(
                "ai.azure.endpoint", .aiCleanup, c.azureAddress, ["address", "url", "foundry"],
                context: f, requires: [ai, foundry, azureManual]),
            entry(
                "ai.azure.deployment", .aiCleanup, c.deploymentName, ["model", "foundry"],
                context: f, requires: [ai, foundry, azureManual]),
            entry(
                "ai.azure.key", .aiCleanup, c.apiKey, ["resource key"], context: f, requires: [ai, foundry, azureKey]),
            entry(
                "ai.azure.cache", .aiCleanup, c.promptCaching,
                ["cache", "caching", "prompt cache", "privacy", "retention"], context: f, requires: [ai, foundry]),
            entry(
                "ai.custom.endpoint", .aiCleanup, c.serverAddress, ["url", "openrouter", "address"],
                context: c.anotherService, requires: [ai, custom]),
            entry(
                "ai.custom.api", .aiCleanup, c.apiStyle, ["chat completions", "responses", "openai"],
                context: c.anotherService, requires: [ai, custom]),
            entry(
                "ai.custom.model", .aiCleanup, c.modelName, ["model", "openrouter"],
                context: c.anotherService, requires: [ai, custom]),
            entry(
                "ai.custom.key", .aiCleanup, c.serviceKey, ["secret", "token"],
                context: c.anotherService, requires: [ai, custom]),
        ]
    }

    private static var otherEntries: [SettingsSearchEntry] {
        let a = SettingsCopy.advanced
        return [
            entry(
                "dictionary.words", .dictionary, SettingsCopy.dictionary.yourWordsTab,
                ["dictionary", "vocabulary", "words", "replacement", "spelling"]),
            entry(
                "dictionary.word-packs", .dictionary, SettingsCopy.dictionary.wordPacksTab,
                ["library", "libraries", "vocabulary", "packs", "terms"]),
            entry(
                "snippets.page", .voiceSnippets, SettingsCopy.snippets.title,
                ["snippet", "template", "phrase", "trigger", "expand", "email", "sign-off"]),
            entry(
                "profiles.page", .appProfiles, SettingsCopy.appProfiles.title,
                ["profile", "per app", "program", "process", "writing style", "line breaks"]),
            entry("history.keep", .history, SettingsCopy.history.keep, ["retention", "delete", "days"]),
            entry(
                "history.recordings", .history, SettingsCopy.history.saveRecording, ["audio", "recording", "history"]),
            entry("usage.period", .usage, SettingsCopy.usage.period, ["usage", "range", "statistics"]),
            entry(
                "advanced.speech-model", .advanced, a.speechModel, ["model", "recognition", "parakeet", "moonshine"]),
            entry("advanced.threads", .advanced, a.threads, ["cpu", "decode", "advanced"]),
            entry("advanced.free-memory", .advanced, a.freeMemory, ["idle", "release", "model", "memory"]),
            entry("advanced.trim-silence", .advanced, a.trimSilence, ["vad", "voice activity detection", "silence"]),
            entry("advanced.longest-recording", .advanced, a.longest, ["duration", "limit", "minutes"]),
            entry("advanced.typing-method", .advanced, a.typingMethod, ["paste", "clipboard", "type"]),
            entry("advanced.line-breaks", .advanced, a.lineBreaks, ["newline", "enter", "terminal"]),
            entry(
                "advanced.chat-lines", .advanced, a.chatSafe, ["teams", "slack", "enter", "shift enter", "return"]),
            entry(
                "advanced.text-changes", .advanced, a.applyRules,
                ["dictionary", "snippets", "post processing", "vocabulary"]),
            entry(
                "diagnostics.page", .diagnostics, SettingsCopy.diagnostics.title,
                ["logs", "support", "speed", "latency", "console", "report", "problem"]),
            entry(
                "about.page", .about, SettingsCopy.about.title,
                ["version", "privacy", "license", "licence", "source", "github", "help", "feedback"]),
        ]
    }

    /// Settings that match what the person typed, best first, at most `maxResults`.
    static func search(_ query: String?, maxResults: Int = SettingsSearchIndex.maxResults) -> [SettingsSearchResult] {
        search(query, in: entries, maxResults: maxResults)
    }

    static func search(
        _ query: String?, in entries: [SettingsSearchEntry], maxResults: Int = SettingsSearchIndex.maxResults
    ) -> [SettingsSearchResult] {
        guard let query, maxResults > 0 else { return [] }
        let terms = words(query)
        guard !terms.isEmpty else { return [] }

        var ranked: [(entry: SettingsSearchEntry, index: Int, rank: Int)] = []
        for (index, entry) in entries.enumerated() {
            let rank = rank(entry, terms: terms)
            if rank < Int.max {
                ranked.append((entry, index, rank))
            }
        }
        ranked.sort { left, right in
            if left.rank != right.rank { return left.rank < right.rank }
            if left.entry.page.position != right.entry.page.position {
                return left.entry.page.position < right.entry.page.position
            }
            return left.index < right.index
        }
        return ranked.prefix(min(maxResults, SettingsSearchIndex.maxResults)).map { candidate in
            let text = SettingsCopy.search.resultOn.render([
                "setting": candidate.entry.displayLabel, "page": candidate.entry.page.title,
            ])
            return SettingsSearchResult(entry: candidate.entry, displayText: text)
        }
    }

    /// The pages whose name or keywords the query starts a word of, in sidebar order.
    static func matchingPages(_ query: String?) -> [SettingsPage] {
        guard let query else { return [] }
        let terms = words(query)
        guard !terms.isEmpty else { return [] }
        return SettingsPage.allCases.filter { page in
            let pageWords = words(page.title) + page.keywords.flatMap { words($0) }
            return allTermsMatch(terms, pageWords)
        }
    }

    /// What to tell a person whose result is not visible yet: the control to choose or turn on.
    static func requirementHint(_ requirement: SettingsSearchRequirement) -> String {
        let label = requirement.label.replacingOccurrences(of: " (recommended)", with: "")
        switch requirement.kind {
        case .checkBox: return SettingsCopy.search.turnOnToSee.render(["label": label])
        case .radio, .view: return SettingsCopy.search.chooseToSee.render(["label": label])
        case .action: return SettingsCopy.search.signInToSee.render()
        }
    }

    private static func rank(_ entry: SettingsSearchEntry, terms: [String]) -> Int {
        let label = words(entry.displayLabel)
        if allTermsMatch(terms, label) { return 0 }
        if !entry.keywords.isEmpty, allTermsMatch(terms, label + entry.keywords.flatMap { words($0) }) { return 2 }
        if allTermsMatch(terms, words(entry.page.title)) { return 3 }
        return Int.max
    }

    private static func allTermsMatch(_ terms: [String], _ words: [String]) -> Bool {
        terms.allSatisfy { term in words.contains { $0.hasPrefix(term) } }
    }

    /// Lower-case words of letters and digits, accents removed.
    static func words(_ value: String) -> [String] {
        var result: [String] = []
        var current = ""
        for scalar in value.decomposedStringWithCanonicalMapping.unicodeScalars {
            if scalar.properties.generalCategory == .nonspacingMark { continue }
            if CharacterSet.alphanumerics.contains(scalar) {
                current.append(String(scalar).lowercased())
            } else if !current.isEmpty {
                result.append(current)
                current = ""
            }
        }
        if !current.isEmpty { result.append(current) }
        return result
    }
}
