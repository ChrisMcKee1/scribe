import XCTest

@testable import Scribe

final class SettingsEffectiveCleanupTests: XCTestCase {
    func testDormantEnvironmentKeysDoNotOverrideSettings() {
        let saved = SettingsPreferences().cleanupSnapshot
        let resolved = SettingsEffectiveCleanup.resolve(
            snapshot: saved,
            environment: ["SCRIBE_OLLAMA_MODEL": "other"],
            purpose: .serving)
        XCTAssertEqual(resolved.snapshot, saved)
        XCTAssertEqual(resolved.source, .savedSettings)
        XCTAssertNil(resolved.explanation)
    }

    func testServingAndCandidateUsePipelineEnvironmentPrecedenceAndNeverBlessAnUntestedDraft() {
        var saved = SettingsPreferences().cleanupSnapshot
        saved.openAIBaseURL = "https://saved.example/v1"
        saved.openAIModel = "saved"
        saved.providerKind = .openAICompatible
        let environment = [
            "SCRIBE_CLEANUP_PROVIDER": "openai-compatible",
            "SCRIBE_CLEANUP_BASE_URL": "https://outside.example/v1",
            "SCRIBE_CLEANUP_MODEL": "outside",
            "SCRIBE_CLEANUP_API_KEY": "never-copy-this-secret",
        ]
        let serving = SettingsEffectiveCleanup.resolve(snapshot: saved, environment: environment, purpose: .serving)
        let candidate = SettingsEffectiveCleanup.resolve(snapshot: saved, environment: environment, purpose: .candidate)
        XCTAssertEqual(serving.snapshot.openAIModel, "outside")
        XCTAssertEqual(candidate.snapshot, serving.snapshot)
        XCTAssertNotEqual(candidate.snapshot.openAIBaseURL, saved.openAIBaseURL)
        XCTAssertEqual(
            candidate.explanation,
            "This test uses AI cleanup set outside Settings, not the unsaved details shown here.")
        XCTAssertFalse(serving.environmentOverrides.contains("never-copy-this-secret"))
        XCTAssertEqual(candidate.source, .environment)
    }

    func testCandidateWithoutAnActiveProviderOverrideUsesTheImmutableDraft() {
        var draft = SettingsPreferences().cleanupSnapshot
        draft.providerKind = .openAICompatible
        draft.openAIBaseURL = "https://draft.example/v1"
        draft.openAIModel = "draft"
        let candidate = SettingsEffectiveCleanup.resolve(snapshot: draft, environment: [:], purpose: .candidate)
        XCTAssertEqual(candidate.snapshot, draft)
        XCTAssertEqual(candidate.source, .draftCandidate)
        XCTAssertTrue(candidate.environmentOverrides.isEmpty)
        XCTAssertNil(candidate.explanation)
    }

    func testUnknownProviderKeepsLegacyFoundryFallbackAndAzureUsesOnlySupportedAuthModes() {
        let saved = SettingsPreferences().cleanupSnapshot
        let unknown = SettingsEffectiveCleanup.resolve(
            snapshot: saved, environment: ["SCRIBE_CLEANUP_PROVIDER": "future"], purpose: .serving)
        XCTAssertEqual(unknown.snapshot.providerKind, .foundryLocal)
        let azure = SettingsEffectiveCleanup.resolve(
            snapshot: saved,
            environment: [
                "SCRIBE_CLEANUP_PROVIDER": "microsoft-foundry",
                "SCRIBE_AZURE_AUTH_MODE": "service-principal",
                "SCRIBE_AZURE_CLIENT_ID": "client",
            ],
            purpose: .serving)
        XCTAssertEqual(azure.snapshot.azureAuthMode, .servicePrincipal)
        XCTAssertEqual(azure.snapshot.azureClientId, "client")
    }
}
