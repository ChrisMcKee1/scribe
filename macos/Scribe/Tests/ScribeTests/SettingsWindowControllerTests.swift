import AppKit
import SwiftUI
import XCTest

@testable import Scribe

@MainActor
final class SettingsWindowCloseRecorder {
    private(set) var closed: [SettingsWindowController] = []

    func record(_ controller: SettingsWindowController) {
        closed.append(controller)
    }
}

/// A window-scoped model like the ones the Settings tabs create, which must be freed with the window.
@MainActor
final class SettingsWindowProbeModel: ObservableObject {
    @Published var text = "probe"
    private let watcher: SettingsDeallocationWatcher

    init(watcher: SettingsDeallocationWatcher) {
        self.watcher = watcher
    }
}

private struct SettingsWindowProbeView: View {
    @ObservedObject var model: SettingsWindowProbeModel
    @ObservedObject var drafts: SettingsDrafts

    var body: some View {
        VStack {
            Text(model.text)
            TextField("Spoken form", text: $drafts.dictionaryPattern)
        }
    }
}

/// Stands in for `AppDelegate`, which lets go of the controller when it is told the window closed.
@MainActor
final class SettingsWindowOwnerProbe {
    var controller: SettingsWindowController?

    func release(_ closed: SettingsWindowController) {
        if controller === closed {
            controller = nil
        }
    }
}

final class SettingsWindowControllerTests: XCTestCase {
    @MainActor
    func testInitialWindowReservesTheFullContentHeightBelowTheTitleBar() throws {
        _ = NSApplication.shared
        let controller = SettingsWindowController(
            rootView: Color.clear.frame(minWidth: 860, minHeight: 600), onClose: { _ in })
        let window = try XCTUnwrap(controller.window)
        let content = window.contentRect(forFrameRect: window.frame)
        XCTAssertEqual(content.width, 1000, accuracy: 1)
        XCTAssertEqual(content.height, 760, accuracy: 1)
        XCTAssertEqual(window.contentMinSize, NSSize(width: 860, height: 600))
        XCTAssertGreaterThan(window.minSize.height, window.contentMinSize.height)
        window.close()
    }

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

    /// Closing Settings frees the window, its hosting controller and the models its views hold, while the drafts
    /// the app owns keep what was typed. Headless: the window is created and closed but never shown.
    @MainActor
    func testClosingReleasesTheWindowAndWhatItHostsButKeepsTheDrafts() async {
        _ = NSApplication.shared
        let drafts = SettingsDrafts()
        drafts.dictionaryPattern = "sherpa onnx"
        let owner = SettingsWindowOwnerProbe()
        let told = expectation(description: "owner told about the close")
        let modelFreed = expectation(description: "the window's model freed")
        let signal = SettingsTestSignal(told)
        weak var weakController: SettingsWindowController?
        weak var weakWindow: NSWindow?
        weak var weakHosting: NSViewController?

        autoreleasepool {
            let model = SettingsWindowProbeModel(watcher: SettingsDeallocationWatcher(modelFreed))
            let controller = SettingsWindowController(
                rootView: SettingsWindowProbeView(model: model, drafts: drafts)
            ) { closed in
                owner.release(closed)
                signal.fulfill()
            }
            owner.controller = controller
            weakController = controller
            weakWindow = controller.window
            weakHosting = controller.window?.contentViewController
            XCTAssertNotNil(weakHosting)
            controller.window?.close()
        }

        await fulfillment(of: [told, modelFreed], timeout: 10)
        XCTAssertNil(owner.controller)
        XCTAssertNil(weakController)
        XCTAssertNil(weakWindow)
        XCTAssertNil(weakHosting)
        XCTAssertEqual(drafts.dictionaryPattern, "sherpa onnx")
    }
}
