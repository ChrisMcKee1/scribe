import Security
import XCTest

@testable import Scribe

/// The Keychain store against the runner's real login Keychain, each test under a service of its own whose items are
/// deleted when it ends, so no test touches a real credential.
final class KeychainSecretStoreTests: XCTestCase {
    private let account = "unit-test-account"

    private func makeStore() -> KeychainSecretStore {
        KeychainSecretStore(service: makeUniqueKeychainService(label: "secret-store"))
    }

    func testASavedSecretReadsBack() throws {
        let store = makeStore()

        try store.save("s3cr3t-value", for: account)

        XCTAssertEqual(try store.secret(for: account), "s3cr3t-value")
    }

    func testNothingSavedReadsAsNil() throws {
        XCTAssertNil(try makeStore().secret(for: "no-such-account"))
    }

    func testSavingAgainReplacesTheSecret() throws {
        let store = makeStore()

        try store.save("first", for: account)
        try store.save("second", for: account)

        XCTAssertEqual(try store.secret(for: account), "second")
    }

    /// A save over an existing item updates it in place, so the item keeps what it was created with. Deleting and
    /// adding again lost the item for a moment and failed a concurrent writer with `errSecDuplicateItem`.
    func testSavingOverAnExistingItemUpdatesItInPlace() throws {
        let service = makeUniqueKeychainService(label: "secret-store")
        let item: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecAttrLabel as String: "created-by-the-test",
            kSecValueData as String: Data("first".utf8),
        ]
        XCTAssertEqual(SecItemAdd(item as CFDictionary, nil), errSecSuccess)

        try KeychainSecretStore(service: service).save("second", for: account)

        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecReturnAttributes as String: true,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]
        var result: AnyObject?
        XCTAssertEqual(SecItemCopyMatching(query as CFDictionary, &result), errSecSuccess)
        let found = try XCTUnwrap(result as? [String: Any])
        XCTAssertEqual(found[kSecAttrLabel as String] as? String, "created-by-the-test")
        XCTAssertEqual((found[kSecValueData as String] as? Data).map { String(decoding: $0, as: UTF8.self) }, "second")
    }

    func testRemovingIsIdempotent() throws {
        let store = makeStore()
        try store.save("to-remove", for: account)

        try store.removeSecret(for: account)
        try store.removeSecret(for: account)
        try store.removeSecret(for: "never-saved")

        XCTAssertNil(try store.secret(for: account))
    }

    func testTwoServicesKeepTheirItemsApart() throws {
        let first = makeStore()
        let second = makeStore()

        try first.save("first-value", for: account)

        XCTAssertNil(try second.secret(for: account))
        XCTAssertEqual(try first.secret(for: account), "first-value")
    }

    /// Earlier builds saved a client secret under the client id exactly as typed. The Keychain matches an account
    /// exactly, so the trimmed account alone never finds that item; the settings store reads it from its own account,
    /// moves it to the trimmed one, and removes it.
    func testAClientSecretSavedUnderAnUntrimmedClientIdMovesToTheTrimmedAccount() throws {
        let service = makeUniqueKeychainService(label: "legacy-client-secret")
        try KeychainStore.set("legacy-secret", service: service, account: " client-1 ")
        XCTAssertNil(try KeychainStore.get(service: service, account: "client-1"))
        let isolated = makeIsolatedDefaults(label: "legacy-client-secret")
        let store = CleanupSettingsStore(
            domain: .suite(isolated.suiteName), apiKeys: InMemorySecretStore(),
            clientSecrets: KeychainSecretStore(service: service))

        XCTAssertEqual(try store.readAzureClientSecret(clientId: " client-1 "), "legacy-secret")

        XCTAssertEqual(try KeychainStore.get(service: service, account: "client-1"), "legacy-secret")
        XCTAssertNil(try KeychainStore.get(service: service, account: " client-1 "))
    }

    /// The move's rename is one Keychain operation, and the Keychain refuses it, changing nothing, when the new account
    /// already has an item or the old one is gone. That refusal is what lets a Save or a Clear win against a move.
    func testARenameIsRefusedWhenTheNewAccountIsTakenOrTheOldItemIsGone() throws {
        let store = makeStore()
        try store.save("earlier", for: " client-1 ")
        try store.save("replacement", for: "client-1")

        XCTAssertEqual(try store.renameAccount(" client-1 ", to: "client-1"), .destinationTaken)
        XCTAssertEqual(try store.secret(for: "client-1"), "replacement")
        XCTAssertEqual(try store.secret(for: " client-1 "), "earlier")

        try store.removeSecret(for: " client-1 ")
        XCTAssertEqual(try store.renameAccount(" client-1 ", to: "client-1"), .sourceGone)
        XCTAssertEqual(try store.secret(for: "client-1"), "replacement")

        try store.save("moved", for: "client-2 ")
        XCTAssertEqual(try store.renameAccount("client-2 ", to: "client-2"), .renamed)
        XCTAssertEqual(try store.secret(for: "client-2"), "moved")
        XCTAssertNil(try store.secret(for: "client-2 "))
    }
}
