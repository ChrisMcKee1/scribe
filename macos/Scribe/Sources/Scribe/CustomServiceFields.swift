import Foundation

/// The OpenAI-compatible service fields, as Settings shows and stores them now that "On this PC" can run cleanup
/// with Ollama or LM Studio as well as with another AI service set up by hand.
enum CustomServiceFields {
    struct Fields: Sendable, Equatable {
        var endpoint: String?
        var model: String?

        static let none = Fields(endpoint: nil, model: nil)
    }

    static func savedApp(values: CleanupSettingsValues, hasSavedAPIKey: Bool) -> LocalServerApp {
        switch values.providerKind {
        case .foundryLocal:
            return .none
        case .ollama:
            return .ollama
        case .microsoftFoundry:
            return .none
        case .openAICompatible:
            if values.selectedLocalApp != .none {
                return values.selectedLocalApp
            }
            return hasSavedAPIKey ? .none : LocalAiServer.appAt(values.openAIBaseURL)
        }
    }

    static func savedAppModel(values: CleanupSettingsValues, hasSavedAPIKey: Bool) -> String? {
        if let current = trimmed(values.openAIModel),
            savedApp(values: values, hasSavedAPIKey: hasSavedAPIKey) != .none
        {
            return current
        }
        switch savedApp(values: values, hasSavedAPIKey: hasSavedAPIKey) {
        case .ollama:
            return trimmed(values.ollamaModel)
        case .lmStudio:
            return trimmed(values.lmStudioModel)
        case .none:
            return nil
        }
    }

    static func otherService(values: CleanupSettingsValues, hasSavedAPIKey: Bool) -> Fields {
        if savedApp(values: values, hasSavedAPIKey: hasSavedAPIKey) == .none {
            return Fields(endpoint: trimmed(values.openAIBaseURL), model: trimmed(values.openAIModel))
        }
        return Fields(endpoint: trimmed(values.otherServiceBaseURL), model: trimmed(values.otherServiceModel))
    }

    static func forSave(
        app: LocalServerApp,
        appModel: String?,
        otherService: Fields,
        saved: CleanupSettingsValues
    ) -> (stored: Fields, remembered: Fields) {
        let remembered = Fields(
            endpoint: trimmed(otherService.endpoint),
            model: trimmed(otherService.model))
        guard app != .none else {
            return (remembered, .none)
        }

        let storedEndpoint: String?
        if LocalAiServer.appAt(saved.openAIBaseURL) == app {
            storedEndpoint = trimmed(saved.openAIBaseURL)
        } else {
            storedEndpoint = LocalAiServer.address(of: app)
        }
        return (
            Fields(
                endpoint: storedEndpoint,
                model: trimmed(appModel)
            ),
            remembered
        )
    }

    private static func trimmed(_ value: String?) -> String? {
        guard let value else {
            return nil
        }
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? nil : trimmed
    }
}
