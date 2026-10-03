import AppKit
import XCTest

@testable import Scribe

@MainActor
final class OverlayPreviewTests: XCTestCase {
    func testIndicatorCannotBecomeTheKeyOrMainWindow() {
        _ = NSApplication.shared
        let panel = RecordingIndicatorPanel(
            contentRect: .zero, styleMask: [.nonactivatingPanel, .borderless],
            backing: .buffered, defer: true)
        XCTAssertFalse(panel.canBecomeKey)
        XCTAssertFalse(panel.canBecomeMain)
        XCTAssertTrue(panel.styleMask.contains(.nonactivatingPanel))
    }

    func testHiddenSettingSuppressesAllPresentationsWithoutLosingEngineOwnership() throws {
        let suite = try SettingsTestDefaults()
        defer { suite.remove() }
        suite.defaults.set(false, forKey: OverlayAnchorSelection.showDefaultsKey)
        let controller = OverlayPanelController(defaults: suite.defaults, presentsPanel: false)
        for (index, state) in [
            OverlayState.listening(level: 0.9), .processing, .startingLocalModel, .notice(.typed),
            .notice(.partlyInserted), .hidden,
        ].enumerated() {
            XCTAssertTrue(controller.render(state, revision: UInt64(index + 1)))
            XCTAssertEqual(controller.displayedState, .hidden)
            XCTAssertEqual(controller.lastRenderedRevision, UInt64(index + 1))
            if state != .hidden { XCTAssertFalse(controller.previewAnchor(.center)) }
        }
        controller.showIndicator = true
        XCTAssertEqual(controller.displayedState, .hidden)
        XCTAssertTrue(controller.render(.processing, revision: 7))
        controller.showIndicator = false
        XCTAssertEqual(controller.displayedState, .hidden)
        controller.showIndicator = true
        XCTAssertEqual(controller.displayedState, .processing)
    }

    func testEveryEnginePresentationSupersedesPreviewAndItsLateEnd() {
        for state in [
            OverlayState.listening(level: 0.4), .processing, .startingLocalModel, .notice(.typed), .hidden,
        ] {
            var gate = OverlayPreviewGate()
            let token = gate.begin(at: .topLeft)!
            gate.render(state)
            XCTAssertFalse(gate.end(token))
            XCTAssertNil(gate.candidate)
            XCTAssertEqual(gate.state, state)
        }
    }

    func testBusyAndOutcomeStatesRefusePreviewEvenWhenDisabled() {
        var gate = OverlayPreviewGate()
        gate.enabled = false
        for state in [
            OverlayState.listening(level: 0), .processing, .startingLocalModel, .notice(.typed),
            .notice(.textKept),
        ] {
            gate.render(state)
            XCTAssertNil(gate.begin(at: .center))
            XCTAssertEqual(gate.engineState, state)
            XCTAssertEqual(gate.state, .hidden)
        }
    }

    func testDisabledIndicatorStillPreviewsAndStaleCompletionCannotEndNewPreview() {
        var gate = OverlayPreviewGate()
        gate.enabled = false
        let first = gate.begin(at: .topLeft)!
        let second = gate.begin(at: .bottomRight)!
        XCTAssertFalse(gate.end(first))
        XCTAssertEqual(gate.candidate, .bottomRight)
        XCTAssertEqual(gate.state, .listening(level: 0.65))
        XCTAssertTrue(gate.end(second))
        XCTAssertEqual(gate.state, .hidden)
    }

    func testPreviewExpiryRestoresCommittedAnchorWithoutChangingEngineRevision() async throws {
        let suite = try SettingsTestDefaults()
        defer { suite.remove() }
        let clock = SettingsTestGate()
        let controller = OverlayPanelController(
            defaults: suite.defaults, presentsPanel: false, previewWait: { await clock.pass() })
        controller.anchor = .bottomRight
        controller.showIndicator = false
        XCTAssertTrue(controller.previewAnchor(.topLeft))
        let completion = controller.previewTask
        await clock.waitForArrival()
        XCTAssertEqual(controller.displayedAnchor, .topLeft)
        XCTAssertEqual(controller.lastRenderedRevision, 0)
        await clock.open()
        await completion?.value
        XCTAssertEqual(controller.displayedState, .hidden)
        XCTAssertEqual(controller.displayedAnchor, .bottomRight)
        XCTAssertFalse(controller.isPreviewing)
    }

    func testNewDictationRestoresAnchorAndLatePreviewEndCannotHideProcessing() async throws {
        let suite = try SettingsTestDefaults()
        defer { suite.remove() }
        let clock = SettingsTestGate()
        let controller = OverlayPanelController(
            defaults: suite.defaults, presentsPanel: false, previewWait: { await clock.pass() })
        XCTAssertTrue(controller.previewAnchor(.center))
        let completion = controller.previewTask
        await clock.waitForArrival()
        XCTAssertTrue(controller.render(.listening(level: 0.2), revision: 1))
        XCTAssertEqual(controller.displayedAnchor, .bottomCenter)
        XCTAssertTrue(controller.render(.processing, revision: 2))
        await clock.open()
        await completion?.value
        XCTAssertEqual(controller.displayedState, .processing)
        XCTAssertFalse(controller.render(.hidden, revision: 1))
        XCTAssertEqual(controller.displayedState, .processing)
    }

    func testSettingsCloseAndDiscardCancelWithoutChangingPreferences() async throws {
        let suite = try SettingsTestDefaults()
        defer { suite.remove() }
        for close in [true, false] {
            let clock = SettingsTestGate()
            let controller = OverlayPanelController(
                defaults: suite.defaults, presentsPanel: false, previewWait: { await clock.pass() })
            let drafts = SettingsDrafts()
            drafts.configureIndicator(controller: controller, defaults: suite.defaults)
            drafts.indicator?.select(.topLeft)
            drafts.indicator?.preview()
            let completion = controller.previewTask
            await clock.waitForArrival()
            if close { drafts.windowClosed() } else { drafts.discard() }
            XCTAssertEqual(controller.displayedAnchor, .bottomCenter)
            XCTAssertEqual(controller.displayedState, .hidden)
            XCTAssertNil(suite.defaults.string(forKey: OverlayAnchorSelection.defaultsKey))
            await clock.open()
            await completion?.value
            XCTAssertEqual(controller.displayedState, .hidden)
        }
    }
}
