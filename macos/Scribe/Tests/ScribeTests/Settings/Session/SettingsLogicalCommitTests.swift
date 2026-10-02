import Foundation
import XCTest

@testable import Scribe

@MainActor
final class SettingsLogicalCommitTests: XCTestCase {
    func testLegacyMigrationReadsExistingPreferencesWithoutWritingOrRebinding() async throws {
        let defaults = try SettingsTestDefaults()
        defer { defaults.remove() }
        defaults.defaults.set(0, forKey: "ScribePushToTalkKeyCode")
        defaults.defaults.set(false, forKey: "ScribeAddSpaceAfterDictation")
        defaults.defaults.set("unknown-future-kind", forKey: "ScribeCleanupProviderKind")
        defaults.defaults.set("custom model name", forKey: "ScribeCleanupOllamaModel")
        defaults.defaults.set(12_345, forKey: "ScribeCleanupOllamaContextTokens")
        let before = defaults.defaults.dictionaryRepresentation() as NSDictionary
        let preferences = SettingsMigrationLedger.capture(defaults.defaults)
        let legacy = CleanupSettingsStore(
            domain: .suite(defaults.suiteName), apiKeys: InMemorySecretStore(), clientSecrets: InMemorySecretStore())
        XCTAssertEqual(preferences.shortcutKeyCode, Int(HotkeySettingsStore(defaults: defaults.defaults).keyCode))
        let oldSpacing = TypingSettingsStore(defaults: defaults.defaults).addSpaceAfterDictation
        XCTAssertEqual(preferences.addSpaceAfterDictation, oldSpacing)
        XCTAssertEqual(preferences.cleanupSnapshot, legacy.snapshot())
        XCTAssertEqual(preferences["ScribeCleanupProviderKind"], .string("unknown-future-kind"))
        XCTAssertEqual(before, defaults.defaults.dictionaryRepresentation() as NSDictionary)
    }

    func testLedgerKeysAreUniqueAndSecretsAndSystemChoicesAreNotDefaults() {
        XCTAssertEqual(Set(SettingsMigrationLedger.fields.map(\.key)).count, SettingsMigrationLedger.fields.count)
        XCTAssertFalse(SettingsMigrationLedger.draftDefaults.contains { $0.immediate })
        XCTAssertFalse(SettingsMigrationLedger.draftDefaults.contains { $0.location != .userDefaults })
        XCTAssertTrue(SettingsMigrationLedger.fields.contains { $0.location == .keychain })
        XCTAssertTrue(SettingsMigrationLedger.fields.contains { $0.location == .system })
    }

    func testDocumentRowsAndWordPackMetadataCommitTogetherAndKeepRollbackDefaults() async throws {
        let fixture = try SessionStorageFixture()
        defer { fixture.remove() }
        let baseline = try await fixture.adapter.load()
        var changed = baseline
        changed.preferences.aiCleanupEnabled = true
        changed.preferences.addSpaceAfterDictation = false
        changed.dictionary = [
            SettingsDictionaryRow(DictionaryEntry(id: -1, pattern: "dot net", replacement: ".NET"))
        ]
        changed.snippets = [SettingsSnippetRow(Snippet(id: -2, phrase: "sign off", template: "Thanks"))]
        var submission = SettingsSubmission(
            id: UUID(), revision: 1, baseline: baseline, document: changed,
            intents: [
                .aiCleanup: SettingsExternalIntent(
                    revision: SettingsIntentRevision.next(), values: ["ScribeAiCleanupEnabled": .bool(true)])
            ])
        submission.attachment.values["word_pack_state_v1"] = "{\"generation\":2}"
        let receipt = try await fixture.store.commitSettingsSession(submission)
        let read = try await fixture.adapter.load()
        XCTAssertTrue(read.preferences.aiCleanupEnabled)
        XCTAssertFalse(read.preferences.addSpaceAfterDictation)
        XCTAssertEqual(read.dictionary?.first?.replacement, ".NET")
        XCTAssertEqual(read.snippets?.first?.template, "Thanks")
        XCTAssertGreaterThan(try XCTUnwrap(receipt.document.dictionary?.first?.id), 0)
        XCTAssertEqual(try fixture.store.readStringSetting(key: "word_pack_state_v1"), "{\"generation\":2}")
        XCTAssertNil(fixture.defaults.defaults.object(forKey: "ScribeAiCleanupEnabled"))
        XCTAssertTrue(CleanupSettingsStore(
            domain: .suite(fixture.defaults.suiteName), apiKeys: InMemorySecretStore(),
            clientSecrets: InMemorySecretStore()).isEnabled == false)
    }

    func testParticipantConflictRollsBackAllRowsAndPreferences() async throws {
        let fixture = try SessionStorageFixture()
        defer { fixture.remove() }
        let baseline = try await fixture.adapter.load()
        var changed = baseline
        changed.preferences.addSpaceAfterDictation = false
        changed.dictionary = [SettingsDictionaryRow(DictionaryEntry(id: -1, pattern: "one", replacement: "One"))]
        var submission = SettingsSubmission(
            id: UUID(), revision: 1, baseline: baseline, document: changed, intents: [:])
        submission.attachment.expectedValues["word_pack_state_v1"] = "expected"
        do {
            _ = try await fixture.store.commitSettingsSession(submission)
            XCTFail("A stale participant was committed")
        } catch {
            XCTAssertEqual(error as? SettingsSaveFailure, .conflict)
        }
        let read = try await fixture.adapter.load()
        XCTAssertTrue(read.preferences.addSpaceAfterDictation)
        XCTAssertTrue(try XCTUnwrap(read.dictionary).isEmpty)
        XCTAssertNil(try fixture.store.readStringSetting(key: SettingsStoredDocument.key))
    }

    func testUnloadedCollectionsAreNeverSavedAsEmpty() async throws {
        let fixture = try SessionStorageFixture()
        defer { fixture.remove() }
        _ = try fixture.store.insertDictionaryEntry(DictionaryEntry(pattern: "kept", replacement: "Kept"))
        var baseline = try await fixture.adapter.load()
        baseline.dictionary = nil
        var changed = baseline
        changed.preferences.addSpaceAfterDictation = false
        let submission = SettingsSubmission(
            id: UUID(), revision: 1, baseline: baseline, document: changed, intents: [:])
        _ = try await fixture.store.commitSettingsSession(submission)
        XCTAssertEqual(try fixture.store.fetchAllDictionaryEntries().count, 1)
    }

    func testSavePreservesExternalAdditionRatherThanReplacingTheWholeTable() async throws {
        let fixture = try SessionStorageFixture()
        defer { fixture.remove() }
        let baseline = try await fixture.adapter.load()
        _ = try fixture.store.insertDictionaryEntry(DictionaryEntry(pattern: "outside", replacement: "Outside"))
        var changed = baseline
        changed.dictionary = [SettingsDictionaryRow(DictionaryEntry(id: -1, pattern: "inside", replacement: "Inside"))]
        let submission = SettingsSubmission(
            id: UUID(), revision: 1, baseline: baseline, document: changed, intents: [:])
        _ = try await fixture.store.commitSettingsSession(submission)
        XCTAssertEqual(Set(try fixture.store.fetchAllDictionaryEntries().map(\.pattern)), ["inside", "outside"])
    }

    func testNoIntentSaveKeepsAStoredSwitchItsWindowNeverChanged() async throws {
        let fixture = try SessionStorageFixture()
        defer { fixture.remove() }
        let baseline = try await fixture.adapter.load()
        let tray = SettingsIntentRevision.next()
        _ = try await fixture.adapter.updateExternal(
            .aiCleanup, values: ["ScribeAiCleanupEnabled": .bool(true)], revision: tray)
        var changed = baseline
        changed.preferences.addSpaceAfterDictation = false
        let receipt = try await fixture.store.commitSettingsSession(
            SettingsSubmission(id: UUID(), revision: 1, baseline: baseline, document: changed, intents: [:]))
        XCTAssertTrue(receipt.document.preferences.aiCleanupEnabled)
    }

    func testSaveSupersedesAnOlderTrayWriteThatArrivesAfterIt() async throws {
        let fixture = try SessionStorageFixture()
        defer { fixture.remove() }
        let baseline = try await fixture.adapter.load()
        let olderTray = SettingsIntentRevision.next()
        let window = SettingsIntentRevision.next()
        var changed = baseline
        changed.preferences.aiCleanupEnabled = false
        changed.preferences.addSpaceAfterDictation = false
        let submission = SettingsSubmission(
            id: UUID(), revision: 1, baseline: baseline, document: changed,
            intents: [
                .aiCleanup: SettingsExternalIntent(
                    revision: window, values: ["ScribeAiCleanupEnabled": .bool(false)])
            ])
        _ = try await fixture.store.commitSettingsSession(submission)
        let stored = try await fixture.adapter.updateExternal(
            .aiCleanup, values: ["ScribeAiCleanupEnabled": .bool(true)], revision: olderTray)
        XCTAssertFalse(stored.preferences.aiCleanupEnabled)
    }

    func testNewerTrayIntentWinsWhileSavePreparationIsHeldAtABarrier() async throws {
        let fixture = try SessionStorageFixture()
        defer { fixture.remove() }
        let gate = SettingsTestGate()
        let baseline = try await fixture.adapter.load()
        let session = SettingsSession(
            initial: baseline,
            access: fixture.adapter.access(
                prepare: {
                    await gate.pass()
                    return $0
                },
                apply: { _ in .applied }))
        session.edit {
            $0.preferences.aiCleanupEnabled = true
            $0.preferences.addSpaceAfterDictation = false
        }
        let save = Task { await session.save() }
        await gate.waitForArrival()
        _ = try await fixture.adapter.updateExternal(
            .aiCleanup, values: ["ScribeAiCleanupEnabled": .bool(false)], revision: SettingsIntentRevision.next())
        await gate.open()
        _ = await save.value
        XCTAssertFalse(session.baseline.preferences.aiCleanupEnabled)
        XCTAssertFalse(session.draft.preferences.aiCleanupEnabled)
    }

    func testReceiptMakesRetryIdempotentEvenAfterAnExternalWrite() async throws {
        let fixture = try SessionStorageFixture()
        defer { fixture.remove() }
        let baseline = try await fixture.adapter.load()
        var changed = baseline
        changed.dictionary = [SettingsDictionaryRow(DictionaryEntry(id: -1, pattern: "one", replacement: "One"))]
        let submission = SettingsSubmission(
            id: UUID(), revision: 1, baseline: baseline, document: changed, intents: [:])
        let first = try await fixture.store.commitSettingsSession(submission)
        _ = try await fixture.adapter.updateExternal(
            .aiCleanup, values: ["ScribeAiCleanupEnabled": .bool(true)], revision: SettingsIntentRevision.next())
        let repeated = try await fixture.store.commitSettingsSession(submission)
        XCTAssertEqual(first, repeated)
        XCTAssertEqual(try fixture.store.fetchAllDictionaryEntries().count, 1)
    }
}

@MainActor
private final class SessionStorageFixture {
    let defaults: SettingsTestDefaults
    let directory: StorageTestDirectory
    let store: PersistenceStore
    let adapter: SettingsLegacyStore

    init() throws {
        defaults = try SettingsTestDefaults()
        directory = try StorageTestDirectory()
        store = PersistenceStore(databaseURL: directory.databaseURL)
        try store.initialize()
        adapter = SettingsLegacyStore(defaults: defaults.defaults, database: store)
    }

    func remove() {
        store.closeConnection()
        directory.remove()
        defaults.remove()
    }
}
