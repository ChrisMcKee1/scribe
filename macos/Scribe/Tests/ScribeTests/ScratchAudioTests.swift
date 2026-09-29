import Darwin
import Foundation
import XCTest

@testable import Scribe

/// The scratch recordings the recognizer reads: private while they exist, gone when they are done, and swept
/// after a crash, without ever touching a file another live Scribe still uses or one Scribe did not name.
final class ScratchAudioTests: XCTestCase {
    private func mode(of url: URL) throws -> Int {
        let attributes = try FileManager.default.attributesOfItem(atPath: url.path(percentEncoded: false))
        return try XCTUnwrap(attributes[.posixPermissions] as? Int)
    }

    private func makeFile(named name: String, in directory: URL, age: TimeInterval, now: Date) throws -> URL {
        let url = directory.appendingPathComponent(name)
        try Data([1, 2, 3]).write(to: url)
        try FileManager.default.setAttributes(
            [.modificationDate: now.addingTimeInterval(-age)], ofItemAtPath: url.path(percentEncoded: false))
        return url
    }

    private func exists(_ url: URL) -> Bool {
        FileManager.default.fileExists(atPath: url.path(percentEncoded: false))
    }

    func testARecordingIsWrittenAsA32BitFloatWavInAPrivateDirectory() throws {
        let root = try makeTemporaryDirectory(label: "scratch")
        let scratch = ScratchAudioDirectory(url: root.appendingPathComponent("asr", isDirectory: true))
        let samples: [Float] = [0, 0.5, -0.5, 1, -1]

        let file = try scratch.writeRecording(samples: samples, sampleRate: 16_000)

        XCTAssertEqual(try mode(of: scratch.url), 0o700)
        XCTAssertEqual(try mode(of: file.url), 0o600)
        XCTAssertEqual(ScratchAudioDirectory.ownerPid(ofFileNamed: file.url.lastPathComponent), getpid())
        let data = try Data(contentsOf: file.url)
        XCTAssertEqual(data.count, 44 + samples.count * 4)
        XCTAssertEqual(Array(data.prefix(4)), Array("RIFF".utf8))
        XCTAssertEqual(Array(data[8..<16]), Array("WAVEfmt ".utf8))
        // Format 3 (IEEE float), one channel, 16 kHz, 64,000 bytes a second, 4-byte frames, 32 bits.
        XCTAssertEqual(
            Array(data[20..<36]),
            [3, 0, 1, 0, 0x80, 0x3E, 0, 0, 0x00, 0xFA, 0, 0, 4, 0, 32, 0])
        XCTAssertEqual(Array(data[36..<44]), Array("data".utf8) + [20, 0, 0, 0])
        let decoded: [Float] = data.dropFirst(44).withUnsafeBytes { raw in
            (0..<(raw.count / 4)).map { raw.loadUnaligned(fromByteOffset: $0 * 4, as: Float.self) }
        }
        XCTAssertEqual(decoded, samples)

        scratch.remove(file)
        XCTAssertFalse(exists(file.url))
        scratch.remove(file)
    }

    func testAnExistingDirectoryIsTightenedAndALinkInItsPlaceIsRefused() throws {
        let root = try makeTemporaryDirectory(label: "scratch")
        let loose = root.appendingPathComponent("loose", isDirectory: true)
        try FileManager.default.createDirectory(
            at: loose, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o755])
        let file = try ScratchAudioDirectory(url: loose).writeRecording(samples: [0.1], sampleRate: 16_000)
        XCTAssertEqual(try mode(of: loose), 0o700)
        XCTAssertTrue(exists(file.url))

        let target = root.appendingPathComponent("elsewhere", isDirectory: true)
        try FileManager.default.createDirectory(at: target, withIntermediateDirectories: false)
        let link = root.appendingPathComponent("link")
        try FileManager.default.createSymbolicLink(at: link, withDestinationURL: target)

        XCTAssertThrowsError(try ScratchAudioDirectory(url: link).writeRecording(samples: [0.1], sampleRate: 16_000)) {
            XCTAssertEqual(($0 as? ScratchAudioError)?.operation, .unsafeDirectory)
        }
        XCTAssertTrue(try FileManager.default.contentsOfDirectory(atPath: target.path(percentEncoded: false)).isEmpty)
    }

    func testAnImpossibleRecordingIsRefusedWithoutLeavingAFile() throws {
        let root = try makeTemporaryDirectory(label: "scratch")
        let scratch = ScratchAudioDirectory(url: root.appendingPathComponent("asr", isDirectory: true))

        XCTAssertThrowsError(try scratch.writeRecording(samples: [0.1], sampleRate: 0))
        XCTAssertThrowsError(try scratch.writeRecording(samples: [0.1], sampleRate: .infinity))
        XCTAssertFalse(exists(scratch.url))
    }

    func testTheSweepRemovesOnlyAbandonedRecordingsOfProcessesThatAreGone() throws {
        let root = try makeTemporaryDirectory(label: "scratch")
        let directory = root.appendingPathComponent("asr", isDirectory: true)
        let legacy = root.appendingPathComponent("asr-work", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: legacy, withIntermediateDirectories: true)
        let now = Date()
        let gone: pid_t = 999_991
        let alive: pid_t = 999_992
        let own = getpid()

        let abandoned = try makeFile(named: "scribe-asr-\(gone)-A.wav", in: directory, age: 600, now: now)
        let recent = try makeFile(named: "scribe-asr-\(gone)-B.wav", in: directory, age: 60, now: now)
        let otherLiveScribe = try makeFile(named: "scribe-asr-\(alive)-C.wav", in: directory, age: 7_200, now: now)
        let ours = try makeFile(named: "scribe-asr-\(own)-D.wav", in: directory, age: 7_200, now: now)
        let notOurs = try makeFile(named: "notes.wav", in: directory, age: 7_200, now: now)
        let malformed = try makeFile(named: "scribe-asr-abc-E.wav", in: directory, age: 7_200, now: now)
        let oldLegacy = try makeFile(named: "captured-1.wav", in: legacy, age: 7_200, now: now)
        let youngLegacy = try makeFile(named: "captured-2.wav", in: legacy, age: 600, now: now)
        let legacyStranger = try makeFile(named: "keep.txt", in: legacy, age: 7_200, now: now)

        let result = ScratchAudioDirectory(url: directory, legacyDirectory: legacy)
            .sweepAbandoned(now: now, isProcessAlive: { $0 == alive }, isAnotherScribeRunning: { false })

        XCTAssertFalse(exists(abandoned))
        XCTAssertTrue(exists(recent))
        XCTAssertTrue(exists(otherLiveScribe))
        XCTAssertTrue(exists(ours))
        XCTAssertTrue(exists(notOurs))
        XCTAssertTrue(exists(malformed))
        XCTAssertFalse(exists(oldLegacy))
        XCTAssertTrue(exists(youngLegacy))
        XCTAssertTrue(exists(legacyStranger))
        XCTAssertTrue(exists(legacy))
        XCTAssertEqual(
            result,
            ScratchAudioDirectory.SweepResult(removed: 1, keptInUse: 2, keptRecent: 2, failed: 0, legacyRemoved: 1))
    }

    func testTheSweepNeverRemovesTheEarlierBuildsDirectory() throws {
        let root = try makeTemporaryDirectory(label: "scratch")
        let legacy = root.appendingPathComponent("asr-work", isDirectory: true)
        try FileManager.default.createDirectory(at: legacy, withIntermediateDirectories: true)
        let now = Date()
        let old = try makeFile(named: "captured-9.wav", in: legacy, age: 7_200, now: now)

        let result = ScratchAudioDirectory(url: root.appendingPathComponent("missing"), legacyDirectory: legacy)
            .sweepAbandoned(now: now, isAnotherScribeRunning: { false })

        XCTAssertEqual(result.legacyRemoved, 1)
        XCTAssertFalse(exists(old))
        XCTAssertTrue(exists(legacy))
    }

    /// Earlier builds name their recordings without a process id and wait on the recognizer without a limit, so
    /// an older copy of Scribe that is still running may be reading one however old it is. While any other Scribe
    /// runs, neither the recordings nor their directory are touched; a later launch removes them.
    func testEarlierBuildsRecordingsAreKeptWhileAnotherCopyOfScribeRuns() throws {
        let root = try makeTemporaryDirectory(label: "scratch")
        let legacy = root.appendingPathComponent("asr-work", isDirectory: true)
        try FileManager.default.createDirectory(at: legacy, withIntermediateDirectories: true)
        let now = Date()
        let old = try makeFile(named: "captured-1.wav", in: legacy, age: 7 * 24 * 3_600, now: now)
        let young = try makeFile(named: "captured-2.wav", in: legacy, age: 600, now: now)
        let scratch = ScratchAudioDirectory(
            url: root.appendingPathComponent("asr", isDirectory: true), legacyDirectory: legacy)

        let whileRunning = scratch.sweepAbandoned(now: now, isAnotherScribeRunning: { true })

        XCTAssertTrue(exists(old))
        XCTAssertTrue(exists(young))
        XCTAssertEqual(whileRunning, ScratchAudioDirectory.SweepResult(legacyKeptWhileAnotherRuns: 2))

        let later = scratch.sweepAbandoned(now: now, isAnotherScribeRunning: { false })

        XCTAssertFalse(exists(old))
        XCTAssertTrue(exists(young))
        XCTAssertEqual(later, ScratchAudioDirectory.SweepResult(keptRecent: 1, legacyRemoved: 1))
    }

    /// The process check finds no other Scribe, and an older copy starts right after it. That build prepares
    /// `asr-work` (which succeeds, the directory exists) and then writes its recording into it atomically, as a
    /// separate step. The sweep removes the old recording in between, and the older copy's write must still
    /// succeed, so the directory is never removed, even when the sweep has just emptied it.
    func testAnOlderCopyThatStartsAfterTheProcessCheckStillWritesItsRecording() throws {
        let root = try makeTemporaryDirectory(label: "scratch")
        let legacy = root.appendingPathComponent("asr-work", isDirectory: true)
        try FileManager.default.createDirectory(at: legacy, withIntermediateDirectories: true)
        let now = Date()
        let old = try makeFile(named: "captured-1.wav", in: legacy, age: 7_200, now: now)
        let scratch = ScratchAudioDirectory(url: root.appendingPathComponent("missing"), legacyDirectory: legacy)
        var prepared = false

        let result = scratch.sweepAbandoned(
            now: now,
            isAnotherScribeRunning: {
                let noOtherScribe = false
                // The older copy starts after the snapshot above and prepares its directory.
                do {
                    try FileManager.default.createDirectory(at: legacy, withIntermediateDirectories: true)
                    prepared = true
                } catch {
                    prepared = false
                }
                return noOtherScribe
            })
        let written = legacy.appendingPathComponent("captured-2.wav")
        try Data([1, 2, 3]).write(to: written, options: .atomic)

        XCTAssertTrue(prepared)
        XCTAssertEqual(result.legacyRemoved, 1)
        XCTAssertFalse(exists(old))
        XCTAssertTrue(exists(legacy))
        XCTAssertTrue(exists(written))
    }

    /// The check the sweep relies on, against a real process: a copy of `sleep` under a name no other process
    /// has, which the kernel records as that process's short name.
    func testAnotherProcessIsFoundByItsExecutableNameOnlyWhileItRuns() throws {
        let name = "ScribeProbeTest"
        let directory = try makeTemporaryDirectory(label: "scratch")
        let executable = directory.appendingPathComponent(name)
        try FileManager.default.copyItem(at: URL(fileURLWithPath: "/bin/sleep"), to: executable)
        XCTAssertFalse(ScratchAudioDirectory.isAnotherProcessRunning(named: name))

        let child = Process()
        child.executableURL = executable
        child.arguments = ["30"]
        try child.run()
        defer {
            if child.isRunning {
                child.terminate()
            }
            child.waitUntilExit()
        }

        XCTAssertTrue(ScratchAudioDirectory.isAnotherProcessRunning(named: name))
        XCTAssertFalse(ScratchAudioDirectory.isAnotherProcessRunning(named: "ScribeProbeNone"))

        child.terminate()
        child.waitUntilExit()
        XCTAssertFalse(ScratchAudioDirectory.isAnotherProcessRunning(named: name))
    }

    func testQuittingRemovesOnlyThisProcesssRecordings() throws {
        let root = try makeTemporaryDirectory(label: "scratch")
        let scratch = ScratchAudioDirectory(url: root.appendingPathComponent("asr", isDirectory: true))
        let mine = try scratch.writeRecording(samples: [0.1], sampleRate: 16_000)
        let theirs = try makeFile(named: "scribe-asr-999993-F.wav", in: scratch.url, age: 0, now: Date())

        XCTAssertEqual(scratch.removeFilesOfThisProcess(), 1)

        XCTAssertFalse(exists(mine.url))
        XCTAssertTrue(exists(theirs))
    }

    func testOnlyNamesScribeWroteYieldAProcessID() {
        XCTAssertEqual(ScratchAudioDirectory.ownerPid(ofFileNamed: "scribe-asr-123-ABC.wav"), 123)
        XCTAssertNil(ScratchAudioDirectory.ownerPid(ofFileNamed: "scribe-asr-123-ABC.txt"))
        XCTAssertNil(ScratchAudioDirectory.ownerPid(ofFileNamed: "scribe-asr--ABC.wav"))
        XCTAssertNil(ScratchAudioDirectory.ownerPid(ofFileNamed: "scribe-asr-0-ABC.wav"))
        XCTAssertNil(ScratchAudioDirectory.ownerPid(ofFileNamed: "captured-ABC.wav"))
    }

    func testTheLiveDirectoryIsScribesOwnUnderTheTemporaryDirectory() {
        let live = ScratchAudioDirectory.live

        XCTAssertEqual(live.url.lastPathComponent, "com.scribe.macos.asr-scratch")
        XCTAssertTrue(
            live.url.path(percentEncoded: false)
                .hasPrefix(FileManager.default.temporaryDirectory.path(percentEncoded: false)))
        XCTAssertEqual(live.legacyDirectory?.lastPathComponent, "asr-work")
    }
}
