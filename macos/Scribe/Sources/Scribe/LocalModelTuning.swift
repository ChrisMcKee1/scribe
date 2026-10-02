import Foundation

struct LocalModelTuning: Sendable, Equatable {
    let contextTokens: Int
    let sendWholeVocabulary: Bool

    static let none = LocalModelTuning(contextTokens: 0, sendWholeVocabulary: false)

    static func forSettings(_ settings: CleanupSettingsSnapshot) -> LocalModelTuning {
        switch settings.providerKind {
        case .foundryLocal:
            return LocalModelTuning(
                contextTokens: 0,
                sendWholeVocabulary: settings.foundryLocalSendWholeVocabulary)
        case .ollama:
            return LocalModelTuning(
                contextTokens: settings.ollamaContextTokens,
                sendWholeVocabulary: settings.ollamaSendWholeVocabulary)
        case .openAICompatible:
            switch settings.selectedLocalApp {
            case .ollama:
                return LocalModelTuning(
                    contextTokens: settings.ollamaContextTokens,
                    sendWholeVocabulary: settings.ollamaSendWholeVocabulary)
            case .lmStudio:
                return LocalModelTuning(
                    contextTokens: settings.lmStudioContextTokens,
                    sendWholeVocabulary: settings.lmStudioSendWholeVocabulary)
            case .none:
                return .none
            }
        case .microsoftFoundry:
            return .none
        }
    }
}
