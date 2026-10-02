import Foundation
import XCTest

@testable import Scribe

final class SettingsMigrationCompatibilityTests: XCTestCase {
    func testEveryLegacyDefaultsKeyRoundTripsItsStoredTypeAndIntroducedKeysAreDistinct() throws {
        let fixture = try SettingsTestDefaults()
        defer { fixture.remove() }
        for field in SettingsMigrationLedger.draftDefaults where !field.introduced {
            let value = field.missingValue ?? .string("kept")
            fixture.defaults.set(SettingsMigrationLedger.propertyList(value), forKey: field.key)
        }
        let captured = SettingsMigrationLedger.capture(fixture.defaults)
        for field in SettingsMigrationLedger.draftDefaults where !field.introduced {
            let value = field.missingValue ?? .string("kept")
            XCTAssertEqual(captured[field.key], value, field.key)
        }
        XCTAssertNil(captured["ScribeHasCompletedFirstRun"])
        XCTAssertNil(captured["ScribeIsPaused"])
    }

    func testShortcutMigrationPreservesEveryRepresentableSavedCodeAndLegacyInvalidFallback() throws {
        let fixture = try SettingsTestDefaults()
        defer { fixture.remove() }
        let legacy = HotkeySettingsStore(defaults: fixture.defaults)
        for code in [0, 1, 57, 61, 105, 126, 65_535, -1, 65_536] {
            fixture.defaults.set(code, forKey: "ScribePushToTalkKeyCode")
            let captured = SettingsMigrationLedger.capture(fixture.defaults)
            XCTAssertEqual(captured.shortcutKeyCode, Int(legacy.keyCode))
        }
        let samples: [Any] = [true, false, "61", 3.5]
        for raw in samples {
            fixture.defaults.set(raw, forKey: "ScribePushToTalkKeyCode")
            XCTAssertEqual(
                SettingsMigrationLedger.capture(fixture.defaults).shortcutKeyCode,
                Int(legacy.keyCode))
        }
    }

    func testAllMissingCleanupValuesReadExactlyAsTheExistingStore() throws {
        let fixture = try SettingsTestDefaults()
        defer { fixture.remove() }
        let store = CleanupSettingsStore(
            domain: .suite(fixture.suiteName), apiKeys: InMemorySecretStore(), clientSecrets: InMemorySecretStore())
        XCTAssertEqual(SettingsMigrationLedger.capture(fixture.defaults).cleanupSnapshot, store.snapshot())
        for (key, value) in [
            ("ScribeCleanupAzurePromptCaching", SettingsValue.bool(false)),
            ("ScribeCleanupOpenAIApiStyle", .string("responses")),
            ("ScribeCleanupProviderKind", .string("microsoftFoundry")),
            ("ScribeCleanupAzureAuthMode", .string("servicePrincipal")),
            ("ScribeCleanupAzureTenantId", .string("tenant")),
            ("ScribeCleanupAzureClientId", .string("client")),
            ("ScribeCleanupOtherServiceApiStyle", .string("responses")),
            ("ScribeCleanupOllamaContextTokens", .integer(12_345)),
            ("ScribeCleanupLmStudioSendWholeVocabulary", .bool(true)),
        ] {
            fixture.defaults.set(SettingsMigrationLedger.propertyList(value), forKey: key)
        }
        XCTAssertEqual(SettingsMigrationLedger.capture(fixture.defaults).cleanupSnapshot, store.snapshot())
    }

    func testBoolAndIntegerCoercionMatchesLegacyGetterBehaviorWithoutRewritingKeys() throws {
        let fixture = try SettingsTestDefaults()
        defer { fixture.remove() }
        let store = CleanupSettingsStore(
            domain: .suite(fixture.suiteName), apiKeys: InMemorySecretStore(), clientSecrets: InMemorySecretStore())
        let samples: [Any] = [false, true, 0, 1, 2, "YES", "NO", "true", "bad", 3.5]
        for raw in samples {
            fixture.defaults.set(raw, forKey: "ScribeAiCleanupEnabled")
            fixture.defaults.set(raw, forKey: "ScribeCleanupAzurePromptCaching")
            fixture.defaults.set(raw, forKey: "ScribeCleanupOllamaContextTokens")
            fixture.defaults.set(raw, forKey: "ScribeAddSpaceAfterDictation")
            let captured = SettingsMigrationLedger.capture(fixture.defaults)
            XCTAssertEqual(captured.cleanupSnapshot, store.snapshot())
            XCTAssertEqual(
                captured.addSpaceAfterDictation,
                TypingSettingsStore(defaults: fixture.defaults).addSpaceAfterDictation)
        }
    }
}
