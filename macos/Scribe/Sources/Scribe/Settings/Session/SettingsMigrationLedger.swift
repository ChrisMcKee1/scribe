import CoreFoundation
import Foundation

enum SettingsStorageLocation: String, Sendable {
    case userDefaults
    case sqlite
    case keychain
    case system
}

struct SettingsMigrationField: Sendable {
    let key: String
    let location: SettingsStorageLocation
    let page: SettingsSessionPage?
    let missingValue: SettingsValue?
    let immediate: Bool
    let introduced: Bool

    init(
        _ key: String,
        _ page: SettingsSessionPage?,
        _ missingValue: SettingsValue? = nil,
        location: SettingsStorageLocation = .userDefaults,
        immediate: Bool = false,
        introduced: Bool = false
    ) {
        self.key = key
        self.location = location
        self.page = page
        self.missingValue = missingValue
        self.immediate = immediate
        self.introduced = introduced
    }
}

/// Inventory only: reading this ledger never writes defaults, creates a key, or reads a credential.
enum SettingsMigrationLedger {
    static let fields: [SettingsMigrationField] = [
        .init("ScribePushToTalkKeyCode", .dictation, .integer(57)),
        .init("ScribeAutoStopOnSilence", .dictation, .bool(false)),
        .init("ScribeAddSpaceAfterDictation", .dictation, .bool(true)),
        .init("ScribeInputDeviceUID", .dictation),
        .init("ScribeInputDeviceName", .dictation),
        .init("ScribeOverlayAnchor", .dictation, .string("bottomCenter")),
        .init("ScribeAiCleanupEnabled", .aiCleanup, .bool(false)),
        .init("ScribeCleanupProviderKind", .aiCleanup, .string("foundryLocal")),
        .init("ScribeCleanupFoundryLocalModelAlias", .aiCleanup, .string("qwen2.5-1.5b")),
        .init("ScribeCleanupOllamaModel", .aiCleanup, .string("qwen2.5:3b")),
        .init("ScribeCleanupLmStudioModel", .aiCleanup, .string("")),
        .init("ScribeCleanupSelectedLocalApp", .aiCleanup, .string("none")),
        .init("ScribeCleanupOpenAIBaseURL", .aiCleanup, .string("")),
        .init("ScribeCleanupOpenAIModel", .aiCleanup, .string("")),
        .init("ScribeCleanupOpenAIApiStyle", .aiCleanup, .string("chatCompletions")),
        .init("ScribeCleanupOllamaContextTokens", .aiCleanup, .integer(0)),
        .init("ScribeCleanupLmStudioContextTokens", .aiCleanup, .integer(0)),
        .init("ScribeCleanupFoundryLocalSendWholeVocabulary", .aiCleanup, .bool(false)),
        .init("ScribeCleanupOllamaSendWholeVocabulary", .aiCleanup, .bool(false)),
        .init("ScribeCleanupLmStudioSendWholeVocabulary", .aiCleanup, .bool(false)),
        .init("ScribeCleanupOtherServiceBaseURL", .aiCleanup, .string("")),
        .init("ScribeCleanupOtherServiceModel", .aiCleanup, .string("")),
        .init("ScribeCleanupOtherServiceApiStyle", .aiCleanup, .string("chatCompletions")),
        .init("ScribeCleanupAzureEndpoint", .aiCleanup, .string("")),
        .init("ScribeCleanupAzureDeployment", .aiCleanup, .string("")),
        .init("ScribeCleanupAzurePromptCaching", .aiCleanup, .bool(true)),
        .init("ScribeCleanupAzureAuthMode", .aiCleanup, .string("azureCli")),
        .init("ScribeCleanupAzureTenantId", .aiCleanup, .string("")),
        .init("ScribeCleanupAzureClientId", .aiCleanup, .string("")),
        .init("ScribeCleanupSecretRevision", nil, .string("")),
        .init("ScribeCleanupWritingStyle", .aiCleanup, .string(""), introduced: true),
        .init("ScribeCleanupPromptStyle", .aiCleanup, .string("automatic"), introduced: true),
        .init("ScribeCleanupDetailedInstructions", .aiCleanup, .string(""), introduced: true),
        .init("ScribeCleanupShortInstructions", .aiCleanup, .string(""), introduced: true),
        .init("ScribeNewlineMode", .advanced, .string("smartFlatten"), introduced: true),
        .init("ScribeApplyDictionaryAndSnippets", .advanced, .bool(true), introduced: true),
        .init("ScribeShiftReturnLineBreaks", .advanced, .bool(true), introduced: true),
        .init("ScribeShowRecordingIndicator", .dictation, .bool(true), introduced: true),
        .init("ScribeEnabledDictionaryLibraryIds", .dictionary, .strings([])),
        .init("ScribeHasCompletedFirstRun", nil, .bool(false), immediate: true),
        .init("ScribeIsPaused", nil, .bool(false), immediate: true),
        .init("history_retention_days", .history, location: .sqlite),
        .init("word_pack_state_v1", .dictionary, location: .sqlite),
        .init("com.scribe.macos.openai-compatible-api-key/default", .aiCleanup, location: .keychain),
        .init("com.scribe.macos.azure-client-secret/client-id", .aiCleanup, location: .keychain),
        .init("com.scribe.macos.azure-client-secret/legacy-untrimmed-client-id", .aiCleanup, location: .keychain),
        .init("SMAppService.mainApp", .dictation, location: .system, immediate: true),
    ]

    static var draftDefaults: [SettingsMigrationField] {
        fields.filter { $0.location == .userDefaults && !$0.immediate }
    }

    static func page(for key: String) -> SettingsSessionPage? {
        fields.first { $0.key == key }?.page
    }

    static func capture(_ defaults: UserDefaults) -> SettingsPreferences {
        var result = SettingsPreferences()
        for field in draftDefaults {
            guard let raw = defaults.object(forKey: field.key) else { continue }
            result[field.key] = value(raw)
        }
        return result
    }

    static func value(_ raw: Any) -> SettingsValue? {
        if let number = raw as? NSNumber {
            if CFGetTypeID(number) == CFBooleanGetTypeID() {
                return .bool(number.boolValue)
            }
            let type = String(cString: number.objCType)
            if type == "d" || type == "f" { return .number(number.doubleValue) }
            return .integer(number.intValue)
        }
        if let string = raw as? String { return .string(string) }
        if let strings = raw as? [String] { return .strings(strings) }
        if let data = raw as? Data { return .data(data) }
        return nil
    }

    static func propertyList(_ value: SettingsValue) -> Any {
        switch value {
        case .bool(let value): return value
        case .integer(let value): return value
        case .number(let value): return value
        case .string(let value): return value
        case .strings(let value): return value
        case .data(let value): return value
        }
    }
}

extension SettingsPreferences {
    var cleanupSnapshot: CleanupSettingsSnapshot {
        func text(_ key: String, _ fallback: String = "") -> String {
            values[key]?.string ?? fallback
        }
        func flag(_ key: String, _ fallback: Bool = false) -> Bool {
            values[key]?.legacyBool(default: fallback, strict: key == "ScribeCleanupAzurePromptCaching") ?? fallback
        }
        func number(_ key: String) -> Int {
            values[key]?.legacyInteger ?? 0
        }
        let otherStyle = CustomAPIStyle(rawValue: text("ScribeCleanupOtherServiceApiStyle")) ?? .chatCompletions
        return CleanupSettingsSnapshot(
            isEnabled: aiCleanupEnabled,
            providerKind: CleanupProviderKind(rawValue: text("ScribeCleanupProviderKind")) ?? .foundryLocal,
            foundryLocalModelAlias: text("ScribeCleanupFoundryLocalModelAlias", "qwen2.5-1.5b"),
            ollamaModel: text("ScribeCleanupOllamaModel", "qwen2.5:3b"),
            selectedLocalApp: LocalServerApp(rawValue: text("ScribeCleanupSelectedLocalApp")) ?? .none,
            openAIBaseURL: text("ScribeCleanupOpenAIBaseURL"),
            openAIModel: text("ScribeCleanupOpenAIModel"),
            openAIApiStyle: CustomAPIStyle(rawValue: text("ScribeCleanupOpenAIApiStyle")) ?? .chatCompletions,
            ollamaContextTokens: number("ScribeCleanupOllamaContextTokens"),
            lmStudioContextTokens: number("ScribeCleanupLmStudioContextTokens"),
            foundryLocalSendWholeVocabulary: flag("ScribeCleanupFoundryLocalSendWholeVocabulary"),
            ollamaSendWholeVocabulary: flag("ScribeCleanupOllamaSendWholeVocabulary"),
            lmStudioSendWholeVocabulary: flag("ScribeCleanupLmStudioSendWholeVocabulary"),
            azureEndpoint: text("ScribeCleanupAzureEndpoint"),
            azureDeployment: text("ScribeCleanupAzureDeployment"),
            azurePromptCaching: flag("ScribeCleanupAzurePromptCaching", true),
            azureAuthMode: AzureAuthMode(rawValue: text("ScribeCleanupAzureAuthMode")) ?? .azureCli,
            azureTenantId: text("ScribeCleanupAzureTenantId"),
            azureClientId: text("ScribeCleanupAzureClientId"),
            otherServiceApiStyle: otherStyle,
            secretRevision: text("ScribeCleanupSecretRevision"))
    }
}
