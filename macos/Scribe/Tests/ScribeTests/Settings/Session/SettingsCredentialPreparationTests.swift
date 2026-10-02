import Foundation
import XCTest

@testable import Scribe

final class SettingsCredentialPreparationTests: XCTestCase {
    func testPreparationNeverReplacesTheLegacyAccountAndReferencesContainNoSecret() async throws {
        let store = InMemorySecretStore(["default": "old-secret"])
        let id = SettingsCredentialID(slot: .customApiKey, account: "default")
        let prepared = try await SettingsCredentialPreparer.prepare(
            edits: [id: .replace("new-secret")], existing: [:], stores: [.customApiKey: store])
        XCTAssertEqual(try store.secret(for: "default"), "old-secret")
        let selected = SettingsReferencedSecretStore(
            base: store, slot: .customApiKey, references: prepared.references)
        XCTAssertEqual(try selected.secret(for: "default"), "new-secret")
        let encoded = String(decoding: try JSONEncoder().encode(prepared.references), as: UTF8.self)
        XCTAssertFalse(encoded.contains("new-secret"))
        XCTAssertFalse(encoded.contains("old-secret"))
        for allocated in prepared.allocated {
            try store.removeSecret(for: allocated.account)
        }
    }

    func testRemoveIsAStagedTombstoneNotAKeychainDeletion() async throws {
        let store = InMemorySecretStore(["default": "old-secret"])
        let id = SettingsCredentialID(slot: .customApiKey, account: "default")
        let prepared = try await SettingsCredentialPreparer.prepare(
            edits: [id: .remove], existing: [:], stores: [.customApiKey: store])
        XCTAssertTrue(prepared.allocated.isEmpty)
        XCTAssertEqual(try store.secret(for: "default"), "old-secret")
        let selected = SettingsReferencedSecretStore(
            base: store, slot: .customApiKey, references: prepared.references)
        XCTAssertNil(try selected.secret(for: "default"))
    }

    func testKeychainFailureCannotChangeTheServingCredential() async throws {
        let store = InMemorySecretStore(["default": "old-secret"])
        store.failNextWrite(with: -25293)
        let id = SettingsCredentialID(slot: .customApiKey, account: "default")
        do {
            _ = try await SettingsCredentialPreparer.prepare(
                edits: [id: .replace("new-secret")], existing: [:], stores: [.customApiKey: store])
            XCTFail("Failed Keychain preparation succeeded")
        } catch {
            XCTAssertEqual(error as? SettingsSaveFailure, .credentials)
        }
        XCTAssertEqual(try store.secret(for: "default"), "old-secret")
    }

    func testDeadlineReturnsWhileNativeWriteIsStillHeldAndLateAccountIsDiscarded() async throws {
        let store = HoldingSessionSecretStore()
        let timer = SettingsTestGate()
        let id = SettingsCredentialID(slot: .customApiKey, account: "default")
        let task = Task {
            try await SettingsCredentialPreparer.prepare(
                edits: [id: .replace("private")], existing: [:], stores: [.customApiKey: store],
                sleep: { _ in await timer.pass() })
        }
        await store.pause.waitUntilReached()
        await timer.waitForArrival()
        await timer.open()
        do {
            _ = try await task.value
            XCTFail("The bounded preparation waited indefinitely")
        } catch {
            XCTAssertEqual(error as? SettingsSaveFailure, .preparationTimedOut)
        }
        // The caller has already returned while this native call is still held.
        store.pause.release()
        let removed = await store.removed.value
        XCTAssertTrue(removed)
        XCTAssertEqual(try store.base.secret(for: "default"), "original")
    }

    func testCancellingCallerDoesNotWaitForABlockedNativeWrite() async throws {
        let store = HoldingSessionSecretStore()
        let id = SettingsCredentialID(slot: .customApiKey, account: "default")
        let task = Task {
            try await SettingsCredentialPreparer.prepare(
                edits: [id: .replace("private")], existing: [:], stores: [.customApiKey: store])
        }
        await store.pause.waitUntilReached()
        task.cancel()
        do {
            _ = try await task.value
            XCTFail("Cancelled preparation succeeded")
        } catch {
            XCTAssertEqual(error as? SettingsSaveFailure, .cancelled)
        }
        store.pause.release()
        let removed = await store.removed.value
        XCTAssertTrue(removed)
    }

    func testUnchangedCredentialDoesNotReadOrWriteKeychain() async throws {
        let store = InMemorySecretStore(["default": "private"])
        let id = SettingsCredentialID(slot: .customApiKey, account: "default")
        let prepared = try await SettingsCredentialPreparer.prepare(
            edits: [id: .keep], existing: [:], stores: [.customApiKey: store])
        XCTAssertTrue(prepared.allocated.isEmpty)
        XCTAssertEqual(store.reads, 0)
        XCTAssertEqual(store.writes, 0)
        XCTAssertFalse(String(reflecting: SettingsCredentialEdit.replace("private")).contains("private"))
    }

    func testAzureReferenceUsesTheLegacyTrimmedClientAccount() async throws {
        let store = InMemorySecretStore(["client": "old"])
        let id = SettingsCredentialID(slot: .azureClientSecret, account: " client ")
        XCTAssertEqual(id.account, "client")
        let prepared = try await SettingsCredentialPreparer.prepare(
            edits: [id: .replace("new")], existing: [:], stores: [.azureClientSecret: store])
        let selected = SettingsReferencedSecretStore(
            base: store, slot: .azureClientSecret, references: prepared.references)
        XCTAssertEqual(try selected.secret(for: "client"), "new")
        for allocated in prepared.allocated {
            try store.removeSecret(for: allocated.account)
        }
    }
}

private final class HoldingSessionSecretStore: SecretStore {
    let base = InMemorySecretStore(["default": "original"])
    let pause = ReadPause()
    let removed = FirstOutcome()

    func secret(for account: String) throws -> String? {
        try base.secret(for: account)
    }

    func save(_ secret: String, for account: String) throws {
        pause.arrive()
        try base.save(secret, for: account)
    }

    func removeSecret(for account: String) throws {
        try base.removeSecret(for: account)
        removed.settle(true)
    }

    func renameAccount(_ account: String, to newAccount: String) throws -> SecretRename {
        try base.renameAccount(account, to: newAccount)
    }
}
