import Security
import XCTest
import os

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

    func testConcurrentSavesAllSucceed() throws {
        let store = makeStore()
        let account = self.account
        let failures = OSAllocatedUnfairLock<[String]>(initialState: [])

        DispatchQueue.concurrentPerform(iterations: 8) { index in
            do {
                try store.save("value-\(index)", for: account)
            } catch {
                let shape = FailureShape(error).description
                failures.withLock { $0.append(shape) }
            }
        }

        XCTAssertEqual(failures.withLock { $0 }, [])
        let saved = try XCTUnwrap(try store.secret(for: account))
        XCTAssertTrue(saved.hasPrefix("value-"), saved)
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
}
