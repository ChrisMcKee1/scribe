import AppKit
import XCTest
@testable import Scribe

@MainActor
final class SettingsWindowCloseRecorder {
    private(set) var closed: [SettingsWindowController] = []

    func record(_ controller: SettingsWindowController) {
        closed.append(controller)
    }
}

final class SettingsWindowControllerTests: XCTestCase {
    /// The owner lets go of the window when told, so it must be told after AppKit has finished closing it, never
    /// from inside the close.
    @MainActor
    func testClosingTellsTheOwnerOnTheNextMainActorTurn() async {
        let told = expectation(description: "owner told about the close")
        let signal = SettingsTestSignal(told)
        let recorder = SettingsWindowCloseRecorder()
        let controller = SettingsWindowController(window: nil) { closed in
            recorder.record(closed)
            signal.fulfill()
        }

        controller.windowWillClose(Notification(name: NSWindow.willCloseNotification))
        XCTAssertTrue(recorder.closed.isEmpty, "the owner must not release the window inside AppKit's close")

        await fulfillment(of: [told], timeout: 10)
        XCTAssertEqual(recorder.closed.count, 1)
        XCTAssertTrue(recorder.closed.first === controller)
    }
}
