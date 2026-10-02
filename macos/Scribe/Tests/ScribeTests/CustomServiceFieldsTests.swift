import XCTest

@testable import Scribe

final class CustomServiceFieldsTests: XCTestCase {
    private func saved(
        endpoint: String?,
        model: String?,
        provider: CleanupProviderKind = .openAICompatible,
        otherEndpoint: String? = nil,
        otherModel: String? = nil
    ) -> CleanupSettingsValues {
        CleanupSettingsValues(
            isEnabled: true,
            providerKind: provider,
            foundryLocalModelAlias: "qwen2.5-1.5b",
            ollamaModel: "qwen2.5:3b",
            lmStudioModel: "google/gemma-4-e2b",
            selectedLocalApp: .none,
            openAIBaseURL: endpoint ?? "",
            openAIModel: model ?? "",
            ollamaContextTokens: 0,
            lmStudioContextTokens: 0,
            foundryLocalSendWholeVocabulary: false,
            ollamaSendWholeVocabulary: false,
            lmStudioSendWholeVocabulary: false,
            otherServiceBaseURL: otherEndpoint ?? "",
            otherServiceModel: otherModel ?? "",
            azureEndpoint: "",
            azureDeployment: "",
            azureAuthMode: .azureCli,
            azureTenantId: "",
            azureClientId: "")
    }

    func testAnAppsOwnAddressSavedWithoutAKeyIsThatApp() {
        XCTAssertEqual(
            CustomServiceFields.savedApp(
                values: saved(endpoint: "http://localhost:11434/v1", model: "model"),
                hasSavedAPIKey: false),
            .ollama)
        XCTAssertEqual(
            CustomServiceFields.savedApp(
                values: saved(endpoint: "http://127.0.0.1:11434/v1/", model: "model"),
                hasSavedAPIKey: false),
            .ollama)
        XCTAssertEqual(
            CustomServiceFields.savedApp(
                values: saved(endpoint: "http://localhost:1234/v1", model: "model"),
                hasSavedAPIKey: false),
            .lmStudio)
        XCTAssertEqual(
            CustomServiceFields.savedApp(
                values: saved(endpoint: "http://localhost:1234/v1", model: "model"),
                hasSavedAPIKey: true),
            .none)
        XCTAssertEqual(
            CustomServiceFields.savedApp(
                values: saved(endpoint: "https://openrouter.ai/api/v1", model: "model"),
                hasSavedAPIKey: false),
            .none)
    }

    func testASelectedLocalAppStaysThatAppEvenWhenAnotherServiceHasASavedKey() {
        var values = saved(endpoint: LocalAiServer.ollamaAddress, model: "gemma4:e4b")
        values.selectedLocalApp = .ollama

        XCTAssertEqual(CustomServiceFields.savedApp(values: values, hasSavedAPIKey: true), .ollama)
        XCTAssertEqual(CustomServiceFields.savedAppModel(values: values, hasSavedAPIKey: true), "gemma4:e4b")
    }

    func testOnlyTheOpenAICompatibleServiceIsEverAnApp() {
        XCTAssertEqual(
            CustomServiceFields.savedApp(
                values: saved(
                    endpoint: "http://localhost:11434/v1",
                    model: "gemma4:e4b",
                    provider: .foundryLocal),
                hasSavedAPIKey: false),
            .none)
    }

    func testChoosingOllamaRemembersAnotherAIServiceAndChoosingItAgainBringsItBack() {
        let original = saved(endpoint: "https://openrouter.ai/api/v1", model: "openai/gpt-5-mini")
        let boxes = CustomServiceFields.otherService(values: original, hasSavedAPIKey: true)

        let savedFields = CustomServiceFields.forSave(
            app: .ollama,
            appModel: "gemma4:e4b",
            otherService: boxes,
            saved: original)

        XCTAssertEqual(
            savedFields.stored,
            CustomServiceFields.Fields(endpoint: LocalAiServer.ollamaAddress, model: "gemma4:e4b"))
        XCTAssertEqual(savedFields.remembered, boxes)

        let next = saved(
            endpoint: savedFields.stored.endpoint,
            model: savedFields.stored.model,
            otherEndpoint: savedFields.remembered.endpoint,
            otherModel: savedFields.remembered.model)
        XCTAssertEqual(CustomServiceFields.savedApp(values: next, hasSavedAPIKey: false), .ollama)
        XCTAssertEqual(CustomServiceFields.savedAppModel(values: next, hasSavedAPIKey: false), "gemma4:e4b")
        XCTAssertEqual(CustomServiceFields.otherService(values: next, hasSavedAPIKey: false), boxes)

        let back = CustomServiceFields.forSave(
            app: .none,
            appModel: "gemma4:e4b",
            otherService: CustomServiceFields.otherService(values: next, hasSavedAPIKey: false),
            saved: next)
        XCTAssertEqual(back.stored, boxes)
        XCTAssertEqual(back.remembered, .none)
    }

    func testAnAppKeepsTheAddressItWasSavedAt() {
        let original = saved(endpoint: "http://127.0.0.1:11434/v1/", model: "llama3.2")
        let savedFields = CustomServiceFields.forSave(
            app: .ollama,
            appModel: "llama3.2",
            otherService: CustomServiceFields.otherService(values: original, hasSavedAPIKey: false),
            saved: original)

        XCTAssertEqual(
            savedFields.stored,
            CustomServiceFields.Fields(endpoint: "http://127.0.0.1:11434/v1/", model: "llama3.2"))
        XCTAssertEqual(savedFields.remembered, .none)
        XCTAssertEqual(
            CustomServiceFields.forSave(
                app: .lmStudio,
                appModel: "google/gemma-4-e2b",
                otherService: .none,
                saved: original
            ).stored.endpoint,
            LocalAiServer.lmStudioAddress)
    }

    func testSaveTrimsWhatItStoresAndStoresBlankAsNothing() {
        let original = saved(endpoint: nil, model: nil, provider: .foundryLocal)
        let savedFields = CustomServiceFields.forSave(
            app: .lmStudio,
            appModel: "  ",
            otherService: CustomServiceFields.Fields(endpoint: " https://api.example.com/v1 ", model: " my-model "),
            saved: original)

        XCTAssertEqual(
            savedFields.stored,
            CustomServiceFields.Fields(endpoint: LocalAiServer.lmStudioAddress, model: nil))
        XCTAssertEqual(
            savedFields.remembered,
            CustomServiceFields.Fields(endpoint: "https://api.example.com/v1", model: "my-model"))
        XCTAssertEqual(
            CustomServiceFields.forSave(app: .none, appModel: nil, otherService: .none, saved: original).stored,
            .none)
    }

    func testNothingIsRememberedForAServiceThatRunsCleanupItself() {
        let original = saved(
            endpoint: "https://openrouter.ai/api/v1",
            model: "openai/gpt-5-mini",
            otherEndpoint: "https://stale.example.com/v1")

        XCTAssertEqual(
            CustomServiceFields.otherService(values: original, hasSavedAPIKey: true),
            CustomServiceFields.Fields(endpoint: "https://openrouter.ai/api/v1", model: "openai/gpt-5-mini"))
        XCTAssertNil(CustomServiceFields.savedAppModel(values: original, hasSavedAPIKey: true))
    }
}
