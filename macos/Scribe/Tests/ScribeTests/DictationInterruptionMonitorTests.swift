import AppKit
import XCTest

@testable import Scribe

@MainActor
final class DictationInterruptionMonitorTests: XCTestCase {
    func testTheMonitorCallsBackForSleepLockAndSessionResignActive() async {
        let workspaceCenter = NotificationCenter()
        let distributedCenter = NotificationCenter()
        let sources = DictationInterruptionSources.testing(
            workspaceCenter: workspaceCenter,
            distributedCenter: distributedCenter)
        let callbacks = SettingsTestCounter()
        let monitor = DictationInterruptionMonitor(sources: sources) {
            callbacks.increment()
        }
        _ = monitor

        workspaceCenter.post(name: NSWorkspace.screensDidSleepNotification, object: nil)
        await waitUntil("sleep callback") { callbacks.count == 1 }
        workspaceCenter.post(name: Notification.Name("NSWorkspaceSessionDidResignActiveNotification"), object: nil)
        await waitUntil("session resign active callback") { callbacks.count == 2 }
        distributedCenter.post(name: Notification.Name("com.apple.screenIsLocked"), object: nil)
        await waitUntil("screen lock callback") { callbacks.count == 3 }
    }
}
