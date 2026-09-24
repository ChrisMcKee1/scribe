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
    func testChoosingAnAnchorMovesThePillAndStoresIt() {
        let controller = OverlayPanelController()
        let selection = OverlayAnchorSelection(controller: controller, defaults: suite.defaults)

        selection.select(.topLeft)

        XCTAssertEqual(controller.anchor, .topLeft)
        XCTAssertEqual(
            suite.defaults.string(forKey: OverlayAnchorSelection.defaultsKey), OverlayAnchor.topLeft.rawValue)
        XCTAssertEqual(selection.anchor, .topLeft)
    }

    /// The tray's Overlay Position menu moves the pill, then stores the anchor; an open Overlay tab follows it.
    @MainActor
    func testATrayChangeIsShownWhileTheTabIsOpen() {
        let controller = OverlayPanelController()
        let selection = OverlayAnchorSelection(controller: controller, defaults: suite.defaults)
        XCTAssertEqual(selection.anchor, .bottomCenter)

        controller.anchor = .bottomRight
        suite.defaults.set(OverlayAnchor.bottomRight.rawValue, forKey: OverlayAnchorSelection.defaultsKey)

        XCTAssertEqual(selection.anchor, .bottomRight)
    }
}
