import Foundation
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

        XCTAssertFalse(StandardErrorSink.emit("probe line", to: descriptors[1]))
        XCTAssertEqual(fcntl(descriptors[1], F_GETNOSIGPIPE), 1)

        ScribeLog.info(.process, "Standard error probe")
        XCTAssertEqual(fcntl(STDERR_FILENO, F_GETNOSIGPIPE), 1)
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
