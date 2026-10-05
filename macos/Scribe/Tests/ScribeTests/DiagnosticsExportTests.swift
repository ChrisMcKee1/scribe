import Foundation
import XCTest

@testable import Scribe

final class DiagnosticsExportTests: XCTestCase {
    private func scratch() throws -> URL {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        addTeardownBlock { try FileManager.default.removeItem(at: root) }
        return root
    }

    private var event: ScribeLog.Rendering {
        ScribeLog.render(
            .info, .dictation, "Decoded",
            [.count("characters", 12), .sensitive("profile", PrivacyCanary.transcript)])
    }

    func testOnlyShapedRedactedEventsReachTheLog() async throws {
        let store = DiagnosticsLogStore(directory: try scratch())
        store.append(event)
        store.append(.init(line: PrivacyCanary.secret, publicText: nil, privateText: nil))
        let files = try await store.snapshot()
        XCTAssertEqual(files.count, 1)
        let text = String(decoding: try XCTUnwrap(files.first).contents, as: UTF8.self)
        XCTAssertTrue(text.contains("characters=12 profile=<private>"))
        PrivacyCanary.assertAbsent(from: text)
    }

    func testOldLogsAreRemovedButOtherFilesAreNeverExportedOrDeleted() async throws {
        let root = try scratch()
        let old = root.appendingPathComponent("scribe-20000101.log")
        let other = root.appendingPathComponent("scribe.db")
        try Data("old log".utf8).write(to: old)
        try Data(PrivacyCanary.transcript.utf8).write(to: other)
        let store = DiagnosticsLogStore(directory: root)
        store.append(event)
        let files = try await store.snapshot()
        XCTAssertEqual(files.count, 1)
        XCTAssertFalse(FileManager.default.fileExists(atPath: old.path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: other.path))
        PrivacyCanary.assertAbsent(from: String(decoding: files[0].contents, as: UTF8.self))
    }

    func testAnActiveDayAtItsBudgetTakesNoMoreData() async throws {
        let root = try scratch()
        let file = root.appendingPathComponent(DiagnosticsLogStore.fileName(at: Date()))
        let data = Data(repeating: 65, count: DiagnosticsLogStore.dayByteLimit)
        try data.write(to: file)
        let store = DiagnosticsLogStore(directory: root)
        store.append(event)
        await store.flush()
        XCTAssertEqual(try Data(contentsOf: file), data)
    }

    func testFailureToWriteDoesNotReachTheCaller() async throws {
        let root = try scratch()
        let file = root.appendingPathComponent("not-a-directory")
        try Data().write(to: file)
        let store = DiagnosticsLogStore(directory: file)
        store.append(event)
        await store.flush()
        do {
            _ = try await store.snapshot()
            XCTFail("An export must report the inaccessible log directory.")
        } catch {
            XCTAssertTrue(error is CocoaError)
        }
    }

    func testLogSymlinksAreNeitherWrittenNorExported() async throws {
        let root = try scratch()
        let outside = root.appendingPathComponent("private.txt")
        let original = Data(PrivacyCanary.secret.utf8)
        try original.write(to: outside)
        let link = root.appendingPathComponent(DiagnosticsLogStore.fileName(at: Date()))
        try FileManager.default.createSymbolicLink(at: link, withDestinationURL: outside)
        let store = DiagnosticsLogStore(directory: root)
        store.append(event)
        let files = try await store.snapshot()
        XCTAssertTrue(files.isEmpty)
        XCTAssertEqual(try Data(contentsOf: outside), original)
    }

    func testArchiveContainsOnlyReportAndRedactedLogFiles() async throws {
        let root = try scratch()
        let logs = root.appendingPathComponent("logs", isDirectory: true)
        try FileManager.default.createDirectory(at: logs, withIntermediateDirectories: true)
        try Data(PrivacyCanary.secret.utf8).write(to: logs.appendingPathComponent("scribe.db"))
        let store = DiagnosticsLogStore(directory: logs)
        store.append(event)
        let destination = root.appendingPathComponent("diagnostics.zip")
        try await DiagnosticsExport.create(at: destination, store: store)
        let unpacked = root.appendingPathComponent("unpacked", isDirectory: true)
        let result = try await ProcessRunner.run(
            URL(fileURLWithPath: "/usr/bin/ditto"),
            arguments: ["-x", "-k", destination.path, unpacked.path], timeout: .seconds(10))
        XCTAssertTrue(result.succeeded)
        let names = try FileManager.default.contentsOfDirectory(atPath: unpacked.path).sorted()
        XCTAssertEqual(names, ["report.txt", DiagnosticsLogStore.fileName(at: Date())].sorted())
        for name in names {
            let text = try String(contentsOf: unpacked.appendingPathComponent(name), encoding: .utf8)
            PrivacyCanary.assertAbsent(from: text)
        }
    }
}
