import Darwin
import Foundation
import MachO
import XCTest

@testable import Scribe

final class ScribeLogTests: XCTestCase {
    private enum Backend {
        case foundryLocal
        case whisperCpp
    }

    private enum Delivery {
        case typed
        case failed(reason: String)
    }

    private enum LeakyError: Error {
        case failed(body: String, endpoint: URL)
    }

    private struct ServiceFailure: FailureShapeDetailing {
        let failureServiceCode: String?
    }

    func testEveryShapeFieldRendersItsValueAndNothingElse() {
        let rendering = ScribeLog.render(
            .info, .transcription, "Decoded",
            [
                .count("characters", 42),
                .integer("status", Int32(-25299)),
                .decimal("peak", -12.345),
                .decimal("ratio", 0.5, precision: 3),
                .milliseconds("decode", 812.44),
                .duration("wait", .milliseconds(1500)),
                .flag("cleaned", true),
                .name("backend", Backend.whisperCpp),
                .label("stage", "decode"),
            ])

        let fields =
            "characters=42 status=-25299 peak=-12.3 ratio=0.500 decode=812.4ms wait=1500.0ms cleaned=true"
            + " backend=whisperCpp stage=decode"
        XCTAssertEqual(rendering.line, "[Transcription] info: Decoded \(fields)")
        XCTAssertEqual(rendering.publicText, "Decoded \(fields)")
        XCTAssertNil(rendering.privateText)
    }

    func testANameIsTheCaseAloneAndNeverOtherText() {
        let fields: [ScribeLog.Field] = [
            .name("delivery", Delivery.failed(reason: PrivacyCanary.transcript)),
            .name("typed", Delivery.typed),
            .name("text", PrivacyCanary.secret),
            .name("number", 7),
            .name("present", Optional(Backend.foundryLocal)),
            .name("absent", Backend?.none),
        ]

        let rendering = ScribeLog.render(.notice, .injection, "Delivered", fields)

        XCTAssertEqual(
            rendering.line,
            "[TextInjection] notice: Delivered delivery=failed typed=typed text=? number=?"
                + " present=foundryLocal absent=none")
        PrivacyCanary.assertAbsent(from: rendering.line)
    }

    func testASensitiveValueReachesOnlyTheUnifiedLogsPrivatePart() {
        let rendering = ScribeLog.render(
            .notice, .audio, "Selected microphone",
            [.sensitive("device", "Dana's canary AirPods"), .count("channels", 2)])

        XCTAssertEqual(rendering.line, "[AudioCapture] notice: Selected microphone device=<private> channels=2")
        XCTAssertEqual(rendering.publicText, "Selected microphone channels=2")
        XCTAssertEqual(rendering.privateText, "device=Dana's canary AirPods")
        PrivacyCanary.assertAbsent(from: rendering.line)
        PrivacyCanary.assertAbsent(from: rendering.publicText ?? "")
    }

    func testAFailureIsLoggedByItsShape() {
        let error = LeakyError.failed(body: PrivacyCanary.transcript, endpoint: URL(string: PrivacyCanary.url)!)

        let rendering = ScribeLog.render(.warning, .cleanup, "Cleanup failed, using the raw text", [.failure(error)])

        XCTAssertEqual(
            rendering.line, "[Cleanup] warning: Cleanup failed, using the raw text failure=[LeakyError.failed]")
        PrivacyCanary.assertAbsent(from: rendering.line)
        PrivacyCanary.assertAbsent(from: rendering.publicText ?? "")
    }

    func testObserversSeeEventsAtEveryLevelAndLegacyLinesUntilTheyStop() {
        let recorder = recordScribeLog()
        let marker = UUID().uuidString

        ScribeLog.debug(.process, "Observer probe", .label("marker", "debug"))
        ScribeLog.error(.process, "Observer probe", .label("marker", "error"))
        ScribeLog.legacyUnshapedLine("Observer probe legacy \(marker)")
        recorder.stop()
        ScribeLog.info(.process, "Observer probe", .label("marker", "after"))

        let probes = recorder.renderings.filter { $0.line.contains("Observer probe") }
        XCTAssertEqual(
            probes.map(\.line),
            [
                "[Process] debug: Observer probe marker=debug",
                "[Process] error: Observer probe marker=error",
                "Observer probe legacy \(marker)",
            ])
        XCTAssertEqual(probes.map(\.publicText), ["Observer probe marker=debug", "Observer probe marker=error", nil])
    }

    /// Content can look exactly like an identifier: the fixture's API key does, and so does a phrase in
    /// snake case. Arriving as a service code or as an error domain, neither may reach standard error or
    /// the unified log's public argument, which this checks on the path every event takes.
    func testIdentifierShapedContentInAFailureReachesNeitherStandardErrorNorTheUnifiedLog() {
        let recorder = recordScribeLog()

        ScribeLog.error(.cleanup, "Cleanup failed", .failure(ServiceFailure(failureServiceCode: PrivacyCanary.secret)))
        ScribeLog.error(.cleanup, "Cleanup failed", .failure(NSError(domain: "canary_private_dictation", code: 1)))
        recorder.stop()

        let failures = recorder.renderings.filter { $0.line.contains("Cleanup failed") }
        XCTAssertEqual(
            failures.map(\.line),
            [
                "[Cleanup] error: Cleanup failed failure=[ServiceFailure service=other]",
                "[Cleanup] error: Cleanup failed failure=[NSError(other 1)]",
            ])
        XCTAssertEqual(
            failures.map(\.publicText),
            ["Cleanup failed failure=[ServiceFailure service=other]", "Cleanup failed failure=[NSError(other 1)]"])
        XCTAssertEqual(failures.map(\.privateText), [nil, nil])
        PrivacyCanary.assertAbsent(from: recorder.publicText)
    }

    /// Standard error whose reader has gone away (Scribe 2>&1 | head) must cost the line and nothing
    /// else. This drives the sink every line goes through at a pipe with no reader, then checks that
    /// logging marks the real standard error the same way: a descriptor without the mark would raise
    /// SIGPIPE, whose default action ends the app.
    func testALineToAStandardErrorWithoutAReaderIsLostWithoutSIGPIPE() {
        var descriptors: [Int32] = [-1, -1]
        XCTAssertEqual(pipe(&descriptors), 0)
        close(descriptors[0])
        defer { close(descriptors[1]) }

        // Ignored for this one write, so a sink that stopped marking its descriptor fails the assertions
        // below instead of ending the test process; the process test covers the default action.
        let previousAction = signal(SIGPIPE, SIG_IGN)
        XCTAssertFalse(StandardErrorSink.emit("probe line", to: descriptors[1]))
        signal(SIGPIPE, previousAction)
        XCTAssertEqual(fcntl(descriptors[1], F_GETNOSIGPIPE), 1)

        ScribeLog.info(.process, "Standard error probe")
        XCTAssertEqual(fcntl(STDERR_FILENO, F_GETNOSIGPIPE), 1)
    }

    /// The same situation as a whole process: `StandardErrorProbe` runs in a second copy of this test
    /// bundle, points its standard error at a pipe nobody reads, puts SIGPIPE back to its default action
    /// and logs. Only a separate process can show that nothing ended it.
    func testAProcessLoggingToAStandardErrorWithoutAReaderKeepsRunning() async throws {
        guard ProcessInfo.processInfo.environment[StandardErrorProbe.environmentKey] == nil else {
            throw XCTSkip("This is already the probe's process")
        }
        let runner = try XCTUnwrap(Self.runningExecutable())
        XCTAssertEqual(
            runner.lastPathComponent, "xctest", "Expected XCTest's runner, found \(runner.path(percentEncoded: false))")
        var environment = ProcessInfo.processInfo.environment
        environment[StandardErrorProbe.environmentKey] = "1"
        // Xcode's console mode copies unified log events to standard error with a writer Scribe does not
        // own, which is not what this checks.
        environment["OS_ACTIVITY_DT_MODE"] = nil
        if let runtime = Self.sanitizerRuntime() {
            environment["DYLD_INSERT_LIBRARIES"] = runtime
        }

        let bundlePath = Bundle(for: StandardErrorProbe.self).bundlePath
        let outcome = try await ProcessRunner.run(
            runner,
            arguments: ["-XCTest", StandardErrorProbe.testIdentifier, bundlePath],
            environment: environment,
            timeout: .seconds(180))

        XCTAssertNil(outcome.terminationSignal, "The probe was ended by signal \(outcome.terminationSignal ?? 0)")
        XCTAssertEqual(outcome.exitStatus, 0)
        XCTAssertTrue(
            outcome.standardOutput.text.contains(StandardErrorProbe.survivedMarker),
            "The probe did not finish; its standard error ends with: \(outcome.standardError.text.suffix(2_000))")
    }

    private static func runningExecutable() -> URL? {
        var path = [CChar](repeating: 0, count: 4 * Int(MAXPATHLEN))
        let length = proc_pidpath(getpid(), &path, UInt32(path.count))
        guard length > 0 else { return nil }
        let bytes = path.prefix(Int(length)).map { UInt8(bitPattern: $0) }
        return URL(fileURLWithPath: String(decoding: bytes, as: UTF8.self))
    }

    /// The sanitizer runtime this process runs with, if any. The runtime takes itself out of
    /// DYLD_INSERT_LIBRARIES once it has loaded, so a child test process has to be given it again, or the
    /// instrumented test bundle aborts as it loads.
    private static func sanitizerRuntime() -> String? {
        for index in 0..<_dyld_image_count() {
            guard let name = _dyld_get_image_name(index) else { continue }
            let path = String(cString: name)
            let file = URL(fileURLWithPath: path).lastPathComponent
            if file.hasPrefix("libclang_rt."), file.hasSuffix("_osx_dynamic.dylib") {
                return path
            }
        }
        return nil
    }

    func testEveryLevelAndCategoryCanBeLogged() {
        let recorder = recordScribeLog()

        for level in ScribeLog.Level.allCases {
            ScribeLog.log(level, .app, "Level probe", [.name("level", level)])
        }
        for category in ScribeLog.Category.allCases {
            ScribeLog.debug(category, "Category probe")
        }

        XCTAssertEqual(recorder.lines.filter { $0.contains("Level probe") }.count, ScribeLog.Level.allCases.count)
        XCTAssertEqual(
            recorder.lines.filter { $0.contains("Category probe") }.count, ScribeLog.Category.allCases.count)
    }
}

/// Not a test by itself: `ScribeLogTests` runs it in a child process, and anywhere else it is skipped.
final class StandardErrorProbe: XCTestCase {
    static let environmentKey = "SCRIBE_STANDARD_ERROR_PROBE"
    static let testIdentifier = "ScribeTests.StandardErrorProbe/testLoggingToAStandardErrorWithoutAReader"
    static let survivedMarker = "standard-error-probe-survived"

    func testLoggingToAStandardErrorWithoutAReader() throws {
        guard ProcessInfo.processInfo.environment[Self.environmentKey] == "1" else {
            throw XCTSkip("Runs only in the child process ScribeLogTests starts")
        }
        var descriptors: [Int32] = [-1, -1]
        XCTAssertEqual(pipe(&descriptors), 0)
        close(descriptors[0])
        let savedStandardError = dup(STDERR_FILENO)
        // The action the app runs with: Scribe never ignores SIGPIPE, so one unmarked write would end it.
        let previousAction = signal(SIGPIPE, SIG_DFL)
        dup2(descriptors[1], STDERR_FILENO)
        close(descriptors[1])

        ScribeLog.error(.process, "Standard error probe", .count("attempt", 1))
        ScribeLog.legacyUnshapedLine("Standard error probe")

        dup2(savedStandardError, STDERR_FILENO)
        close(savedStandardError)
        signal(SIGPIPE, previousAction)
        try FileHandle.standardOutput.write(contentsOf: Data((Self.survivedMarker + "\n").utf8))
    }
}
