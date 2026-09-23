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
        PrivacyCanary.assertAbsent(from: rendering.publicText)
    }

    func testAFailureIsLoggedByItsShape() {
        let error = LeakyError.failed(body: PrivacyCanary.transcript, endpoint: URL(string: PrivacyCanary.url)!)

        let rendering = ScribeLog.render(.warning, .cleanup, "Cleanup failed, using the raw text", [.failure(error)])

        XCTAssertEqual(
            rendering.line, "[Cleanup] warning: Cleanup failed, using the raw text failure=[LeakyError.failed]")
        PrivacyCanary.assertAbsent(from: rendering.line)
        PrivacyCanary.assertAbsent(from: rendering.publicText)
    }

    func testObserversSeeEventsAtEveryLevelAndLegacyLinesUntilTheyStop() {
        let recorder = recordScribeLog()
        let marker = UUID().uuidString

        ScribeLog.debug(.process, "Observer probe", .label("marker", "debug"))
        ScribeLog.error(.process, "Observer probe", .label("marker", "error"))
        ScribeLog.legacyUnshapedLine("Observer probe legacy \(marker)")
        recorder.stop()
        ScribeLog.info(.process, "Observer probe", .label("marker", "after"))

        let probes = recorder.lines.filter { $0.contains("Observer probe") }
        XCTAssertEqual(
            probes,
            [
                "[Process] debug: Observer probe marker=debug",
                "[Process] error: Observer probe marker=error",
                "Observer probe legacy \(marker)",
            ])
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
