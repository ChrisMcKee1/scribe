import Foundation

/// One place an existing Mac string uses a retired name, counted exactly. The ratchet in
/// `GlossaryRatchetTests` fails when the count changes in either direction, so an entry is lowered when the text
/// goes, and a new retired word cannot be added without a reason here.
struct GlossaryAllowance {
    let file: String
    let rule: String
    let count: Int
    let reason: String
}

enum GlossaryAllowlist {
    private static let legacy =
        "Legacy text a person can read, rewritten when the page or error work that owns this file lands."
    private static let instructions =
        "The instructions the AI model is given; Windows excludes CleanupPrompt.cs for the same reason."
    private static let developer =
        "Developer-facing text (a command line tool, a log line or a log category), never shown in Settings."
    private static let command = "The name of a command the person types, which keeps its own name."
    private static let logReason = "A reason the log records when AI cleanup output is rejected; not shown in Settings."
    private static let csvTemplate =
        "The comment header of the CSV template a person downloads; reworded with the Dictionary page."
    private static let logFailure = "Failure descriptions the log records; not shown in Settings."
    private static let commandAround =
        "Names the command the person types; the sentence around it is reworded with AI cleanup."

    private static func entry(_ file: String, _ rule: String, _ count: Int, _ reason: String) -> GlossaryAllowance {
        GlossaryAllowance(file: file, rule: rule, count: count, reason: reason)
    }

    static let entries: [GlossaryAllowance] = [
        entry("AboutView.swift", "provider", 1, legacy),
        entry("AboutView.swift", "transcription", 1, legacy),
        entry("AzureCredential.swift", "az login", 4, command),
        entry("BuiltInDictionaryLibraries.swift", "library", 1, legacy),
        entry("CleanupProvider.swift", "endpoint", 3, legacy),
        entry("CleanupProvider.swift", "library", 1, instructions),
        entry("CleanupProvider.swift", "transcription", 5, instructions),
        entry("CleanupResponseGuard.swift", "transcription", 4, logReason),
        entry("CleanupSettingsStore.swift", "endpoint", 2, legacy),
        entry("CommandLineInspection.swift", "decode", 2, developer),
        entry("CommandLineInspection.swift", "real-time factor", 1, developer),
        entry("CommandLineTools.swift", "transcription", 2, developer),
        entry("CustomAPIStyleText.swift", "PC", 1, legacy),
        entry("DictationNotifications.swift", "recognizer", 2, legacy),
        entry("DictationNotifications.swift", "transcription", 6, legacy),
        entry("DictionaryCsv.swift", "transcription", 1, csvTemplate),
        entry("DictionaryLibraryService.swift", "library", 4, legacy),
        entry("LibraryCsvCodec.swift", "library", 1, legacy),
        entry("LocalAppSettingsSection.swift", "PC", 3, legacy),
        entry("LocalAppSettingsSection.swift", "az login", 2, commandAround),
        entry("LocalAppSettingsSection.swift", "endpoint", 1, legacy),
        entry("LocalAppSettingsSection.swift", "on-device", 1, legacy),
        entry("LocalAppSettingsSection.swift", "provider", 2, legacy),
        entry("OpenAICompatibleCleanupProvider.swift", "endpoint", 1, legacy),
        entry("OverlayAnchor.swift", "raw text", 1, legacy),
        entry("OverlayAnchor.swift", "recognizer", 1, legacy),
        entry("OverlayAnchor.swift", "transcription", 1, legacy),
        entry("PillOutcome.swift", "tray", 1, legacy),
        entry("ScribeLog.swift", "hotkey", 1, developer),
        entry("ScribeLog.swift", "overlay", 1, developer),
        entry("ScribeLog.swift", "transcription", 1, developer),
        entry("SettingsView.swift", "decode", 3, legacy),
        entry("SettingsView.swift", "glossary", 1, legacy),
        entry("SettingsView.swift", "hotkey", 1, legacy),
        entry("SettingsView.swift", "insight", 2, legacy),
        entry("SettingsView.swift", "latency", 3, legacy),
        entry("SettingsView.swift", "library", 4, legacy),
        entry("SettingsView.swift", "overlay", 1, legacy),
        entry("SettingsView.swift", "percentile", 4, legacy),
        entry("SettingsView.swift", "pill", 2, legacy),
        entry("SettingsView.swift", "playground", 2, legacy),
        entry("SettingsView.swift", "provider", 1, legacy),
        entry("SettingsView.swift", "raw text", 2, legacy),
        entry("SettingsView.swift", "real-time factor", 2, legacy),
        entry("SettingsView.swift", "transcription", 2, legacy),
        entry("SettingsView.swift", "trigger phrase", 1, legacy),
        entry("StorageMaintenance.swift", "Forever", 2, legacy),
        entry("TextInjector.swift", "text insertion", 2, logFailure),
        entry("TranscriptionEngine.swift", "recognizer", 8, legacy),
        entry("TranscriptionEngine.swift", "transcription", 2, legacy),
        entry("UsageSummaryModel.swift", "provider", 1, legacy),
        entry("WelcomeView.swift", "transcription", 2, legacy),
    ]
}
