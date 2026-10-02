import XCTest

@testable import Scribe

final class OverlayAnchorSelectionTests: XCTestCase {
    private var suite: SettingsTestDefaults!

    override func setUpWithError() throws {
        try super.setUpWithError()
        suite = try SettingsTestDefaults()
    }

    override func tearDown() {
        suite.remove()
        suite = nil
        super.tearDown()
    }

    @MainActor
    func testChoosingAnAnchorStagesUntilSaved() {
        let controller = OverlayPanelController(defaults: suite.defaults, presentsPanel: false)
        let selection = OverlayAnchorSelection(controller: controller, defaults: suite.defaults)

        selection.select(.topLeft)

        XCTAssertEqual(controller.anchor, .bottomCenter)
        XCTAssertNil(suite.defaults.string(forKey: OverlayAnchorSelection.defaultsKey))
        XCTAssertTrue(selection.hasUnsavedChanges)
        selection.save()
        XCTAssertEqual(controller.anchor, .topLeft)
        XCTAssertEqual(
            suite.defaults.string(forKey: OverlayAnchorSelection.defaultsKey), OverlayAnchor.topLeft.rawValue)
        XCTAssertEqual(selection.anchor, .topLeft)
        XCTAssertFalse(selection.hasUnsavedChanges)
    }

    /// The tray's Overlay Position menu moves the pill, then stores the anchor; an open Overlay tab follows it.
    @MainActor
    func testATrayChangeIsShownWhileTheTabIsOpen() {
        let controller = OverlayPanelController(defaults: suite.defaults, presentsPanel: false)
        let selection = OverlayAnchorSelection(controller: controller, defaults: suite.defaults)
        XCTAssertEqual(selection.anchor, .bottomCenter)

        controller.anchor = .bottomRight
        suite.defaults.set(OverlayAnchor.bottomRight.rawValue, forKey: OverlayAnchorSelection.defaultsKey)

        XCTAssertEqual(selection.anchor, .bottomRight)
    }

    @MainActor
    func testVisibilityDefaultsOnAndPersistsAcrossControllers() {
        let controller = OverlayPanelController(defaults: suite.defaults, presentsPanel: false)
        XCTAssertTrue(controller.showIndicator)
        let selection = OverlayAnchorSelection(controller: controller, defaults: suite.defaults)
        selection.showIndicator = false
        XCTAssertTrue(controller.showIndicator)
        XCTAssertTrue(selection.hasUnsavedChanges)
        selection.save()
        XCTAssertFalse(controller.showIndicator)
        XCTAssertFalse(OverlayPanelController(defaults: suite.defaults, presentsPanel: false).showIndicator)
    }

    @MainActor
    func testDirtySelectionSurvivesTrayChangesAndDiscardUsesCommittedPosition() {
        let controller = OverlayPanelController(defaults: suite.defaults, presentsPanel: false)
        let selection = OverlayAnchorSelection(controller: controller, defaults: suite.defaults)
        selection.select(.topRight)
        controller.anchor = .bottomRight
        suite.defaults.set(OverlayAnchor.bottomRight.rawValue, forKey: OverlayAnchorSelection.defaultsKey)
        XCTAssertEqual(selection.anchor, .topRight)
        selection.discard()
        XCTAssertEqual(selection.anchor, .bottomRight)
        XCTAssertFalse(selection.hasUnsavedChanges)
    }
}
