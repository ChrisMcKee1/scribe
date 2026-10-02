import XCTest

@testable import Scribe

final class LibraryAdmissionIntegrationTests: XCTestCase {
    func testRealCatalogPermissionWithdrawalAndRestorationAtOneGenerationReachTheWireGate() async throws {
        let directory = try makeTemporaryDirectory(label: "library-admission")
        let fixture = makeCleanupStore()
        let gate = CleanupSendGate()
        let settings = DictionaryLibrarySettings(defaults: fixture.defaults, sendGate: gate)
        settings.enabledLibraryIds = ["github"]
        let store = PersistenceStore(databaseURL: directory.appendingPathComponent("scribe.db"))
        defer { store.closeConnection() }
        try await store.prepare()
        let service = DictionaryLibraryService(
            librariesDirectory: directory, settings: settings, persistenceStore: store)
        let first = try await service.loadVocabulary()
        XCTAssertFalse(first.aiEntries.isEmpty)
        XCTAssertTrue(first.aiScope.permittedLibraryIds.contains("github"))
        XCTAssertEqual(gate.currentVocabularyScope, first.aiScope)

        let cleanupStore = fixture.store
        cleanupStore.isEnabled = true
        cleanupStore.providerKind = .openAICompatible
        cleanupStore.openAIBaseURL = "https://example.invalid/v1"
        cleanupStore.openAIModel = "model"
        let requests = RequestLog()
        let cache = CleanupProviderCache(
            store: cleanupStore, environment: [:],
            factory: .testing(
                session: makeStubSession { request in
                    requests.record(request)
                    return StubReply.completion(request, "GitHub.")
                }),
            sendGate: gate)
        let recipient = try cache.captureRecipient()
        let receipt = cache.receipt(for: recipient, scope: first.aiScope, kind: .dictation)
        let provider = try cache.provider(for: recipient)
        let glossary = CleanupVocabulary(glossaryEntries: first.aiEntries)
            .glossary(maxTerms: 80, mode: .all, dictation: nil)
        let request = CleanupRequest(
            transcript: "get hub",
            writingStylePrompt: CleanupPrompt.systemPrompt(
                writingStyle: CleanupPrompt.defaultWritingStyle, useLocalPrompt: false, glossary: glossary),
            receipt: receipt)
        _ = try await provider.clean(request)

        let saved = try await store.loadStringSetting(key: DictionaryLibraryService.libraryStateKey)
        let raw = try XCTUnwrap(saved)
        var state = try JSONDecoder().decode(LibraryLocalState.self, from: Data(raw.utf8))
        state.aiPermissions["github"] = false
        try write(state, to: store, gate: gate)
        XCTAssertEqual(gate.currentVocabularyScope, .none)
        let revoked = try await service.loadVocabulary()
        XCTAssertEqual(revoked.generation, first.generation)
        XCTAssertTrue(revoked.aiEntries.isEmpty)
        XCTAssertFalse(revoked.entries.isEmpty, "Local word-pack corrections remain available")
        XCTAssertEqual(gate.currentVocabularyScope, revoked.aiScope)
        do {
            _ = try await provider.clean(request)
            XCTFail("A real permission withdrawal must stop the previously composed request")
        } catch {
            XCTAssertEqual(error as? CleanupHoldback, .vocabularyChanged)
        }
        XCTAssertEqual(requests.count, 1)

        state.aiPermissions["github"] = true
        try write(state, to: store, gate: gate)
        let restored = try await service.loadVocabulary()
        XCTAssertEqual(gate.currentVocabularyScope, restored.aiScope)
        _ = try await provider.clean(request)
        XCTAssertEqual(requests.count, 2)
    }

    private func write(_ state: LibraryLocalState, to store: PersistenceStore, gate: CleanupSendGate) throws {
        let text = String(decoding: try JSONEncoder().encode(state), as: UTF8.self)
        try gate.changingVocabulary {
            try store.writeStringSetting(key: DictionaryLibraryService.libraryStateKey, value: text)
        }
    }
}
