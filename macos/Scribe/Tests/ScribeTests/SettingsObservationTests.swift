import AppKit
import XCTest

@testable import Scribe

final class SettingsNotificationObservationTests: XCTestCase {
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

    /// The Settings tabs rely on Foundation posting a defaults change on the writing thread before `set` returns,
    /// which is what makes a tray write show in an open tab at once. This pins that on the runner's macOS.
    @MainActor
    func testAMainThreadWriteReachesTheCallbackBeforeTheWriteReturns() {
        let counter = SettingsTestCounter()
        let observation = SettingsNotificationObservation(UserDefaults.didChangeNotification) {
            counter.increment()
        }

        suite.defaults.set(true, forKey: "ScribeAiCleanupEnabled")

        XCTAssertGreaterThanOrEqual(counter.count, 1)
        withExtendedLifetime(observation) {}
    }

    @MainActor
    func testAWriteFromAnotherThreadIsHandedToTheMainActor() async {
        let delivered = expectation(description: "callback on the main actor")
        delivered.assertForOverFulfill = false
        let signal = SettingsTestSignal(delivered)
        let observation = SettingsNotificationObservation(UserDefaults.didChangeNotification) {
            XCTAssertTrue(Thread.isMainThread)
            signal.fulfill()
        }
        let suiteName = suite.suiteName

        await Task.detached {
            let other = UserDefaults(suiteName: suiteName)
            other?.set(true, forKey: "ScribeAiCleanupEnabled")
        }.value

        await fulfillment(of: [delivered], timeout: 10)
        withExtendedLifetime(observation) {}
    }

    @MainActor
    func testAReleasedObservationStopsCallingBack() {
        let counter = SettingsTestCounter()
        do {
            let observation = SettingsNotificationObservation(UserDefaults.didChangeNotification) {
                counter.increment()
            }
            suite.defaults.set(1, forKey: "ScribeTestValue")
            withExtendedLifetime(observation) {}
        }
        let seen = counter.count
        XCTAssertGreaterThanOrEqual(seen, 1)

        suite.defaults.set(2, forKey: "ScribeTestValue")

        XCTAssertEqual(counter.count, seen)
    }

    @MainActor
    func testOnlyTheObservedNotificationCallsBack() {
        let center = NotificationCenter()
        let counter = SettingsTestCounter()
        let observation = SettingsNotificationObservation(
            NSApplication.didBecomeActiveNotification, center: center
        ) {
            counter.increment()
        }

        center.post(name: UserDefaults.didChangeNotification, object: nil)
        XCTAssertEqual(counter.count, 0)

        center.post(name: NSApplication.didBecomeActiveNotification, object: nil)
        XCTAssertEqual(counter.count, 1)
        withExtendedLifetime(observation) {}
    }
}
