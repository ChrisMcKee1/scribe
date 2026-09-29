import Foundation
import XCTest

@testable import Scribe

final class TestSupportTests: XCTestCase {
    func testIsolatedDefaultsShareNothingWithStandardOrWithEachOther() {
        let first = IsolatedDefaults(label: "support")
        let second = IsolatedDefaults(label: "support")
        defer {
            first.removePersistentDomain()
            second.removePersistentDomain()
        }
        let key = "ScribeTestSupportProbe.\(UUID().uuidString)"

        first.defaults.set("first", forKey: key)

        XCTAssertNotEqual(first.suiteName, second.suiteName)
        XCTAssertTrue(first.suiteName.hasPrefix("com.scribe.macos.tests.support."))
        XCTAssertEqual(first.defaults.string(forKey: key), "first")
        XCTAssertNil(second.defaults.string(forKey: key))
        XCTAssertNil(UserDefaults.standard.string(forKey: key))
    }

    func testRemovingTheDomainDeletesWhatTheTestWrote() {
        let isolated = IsolatedDefaults(label: "support")
        isolated.defaults.set(true, forKey: "probe")

        isolated.removePersistentDomain()

        XCTAssertNil(isolated.defaults.object(forKey: "probe"))
        XCTAssertNil(UserDefaults(suiteName: isolated.suiteName)?.object(forKey: "probe"))
    }

    func testTheTeardownHelperHandsOutAnEmptySuite() {
        let isolated = makeIsolatedDefaults(label: "support")

        XCTAssertTrue(isolated.suiteName.hasPrefix("com.scribe.macos.tests.support."))
        XCTAssertTrue(isolated.defaults.persistentDomain(forName: isolated.suiteName)?.isEmpty ?? true)
        isolated.defaults.set(1, forKey: "probe")
        XCTAssertEqual(isolated.defaults.persistentDomain(forName: isolated.suiteName)?["probe"] as? Int, 1)
    }

    func testUniqueKeychainServicesKeepItemsApartAndCleanUpCompletely() throws {
        let service = makeUniqueKeychainService(label: "support")
        let other = TestKeychain.uniqueService(label: "support")
        XCTAssertNotEqual(service, other)
        XCTAssertTrue(service.hasPrefix("com.scribe.macos.tests.support."))

        try KeychainStore.set("value-a", service: service, account: "first")
        try KeychainStore.set("value-b", service: service, account: "second")
        XCTAssertEqual(try KeychainStore.get(service: service, account: "first"), "value-a")
        XCTAssertNil(try KeychainStore.get(service: other, account: "first"))

        TestKeychain.removeAllItems(service: service)

        XCTAssertNil(try KeychainStore.get(service: service, account: "first"))
        XCTAssertNil(try KeychainStore.get(service: service, account: "second"))
    }

    func testTemporaryDirectoriesAreFreshAndRemovable() throws {
        let directory = try TemporaryDirectory(label: "support")
        let path = directory.url.path(percentEncoded: false)
        XCTAssertTrue(FileManager.default.fileExists(atPath: path))
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: path), [])
        try Data("x".utf8).write(to: directory.url.appendingPathComponent("file"))

        directory.remove()

        XCTAssertFalse(FileManager.default.fileExists(atPath: path))
    }

    func testTheFileGateOpensWhenAnotherProcessCreatesTheFile() async throws {
        let marker = try makeTemporaryDirectory(label: "gate").appendingPathComponent("ready")

        async let opened = FileGate.waitForFile(at: marker, timeout: .seconds(30))
        let touched = try await ProcessRunner.run(
            URL(fileURLWithPath: "/usr/bin/touch"), arguments: [marker.path(percentEncoded: false)],
            timeout: .seconds(30))

        XCTAssertTrue(touched.succeeded)
        let didOpen = await opened
        XCTAssertTrue(didOpen)
    }

    func testTheFileGateGivesUpAtItsTimeout() async throws {
        let marker = try makeTemporaryDirectory(label: "gate").appendingPathComponent("never")

        let opened = await FileGate.waitForFile(at: marker, timeout: .milliseconds(100))

        XCTAssertFalse(opened)
    }

    func testTheRecorderStopsRecordingWhenStopped() {
        let recorder = recordScribeLog()
        let marker = UUID().uuidString

        ScribeLog.legacyUnshapedLine("recorder probe \(marker) one")
        recorder.stop()
        ScribeLog.legacyUnshapedLine("recorder probe \(marker) two")

        XCTAssertEqual(recorder.lines.filter { $0.contains(marker) }, ["recorder probe \(marker) one"])
    }

    func testThePrivacyCanariesAllCarryTheMarkerTheAssertionLooksFor() {
        for canary in PrivacyCanary.all {
            XCTAssertTrue(canary.lowercased().contains("canary"), canary)
        }
    }
}
