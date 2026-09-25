import Foundation
import XCTest

/// A `UserDefaults` suite of its own for one settings test, so the test never reads or writes the developer's
/// preferences and never races another test process over `UserDefaults.standard`.
final class SettingsTestDefaults {
    let suiteName = "com.scribe.macos.tests.settings.\(UUID().uuidString)"
    let defaults: UserDefaults

    init() throws {
        defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
    }

    /// Removes the suite and its file; call from `tearDown`.
    func remove() {
        defaults.removePersistentDomain(forName: suiteName)
    }
}

/// Holds an async call at a known point until the test opens the gate, so a test orders two operations exactly
/// instead of sleeping and hoping.
actor SettingsTestGate {
    private var isOpen = false
    private var hasArrival = false
    private var held: [CheckedContinuation<Void, Never>] = []
    private var arrivalWaiters: [CheckedContinuation<Void, Never>] = []

    /// Called by the code under test: records the arrival, then waits until `open()`.
    func pass() async {
        hasArrival = true
        let waiters = arrivalWaiters
        arrivalWaiters.removeAll()
        for waiter in waiters {
            waiter.resume()
        }
        guard !isOpen else { return }
        await withCheckedContinuation { continuation in
            held.append(continuation)
        }
    }

    /// Returns once something has reached `pass()`.
    func waitForArrival() async {
        guard !hasArrival else { return }
        await withCheckedContinuation { continuation in
            arrivalWaiters.append(continuation)
        }
    }

    func open() {
        isOpen = true
        let released = held
        held.removeAll()
        for continuation in released {
            continuation.resume()
        }
    }
}

/// Counts callbacks on the main actor, for closures that must be `@Sendable`.
@MainActor
final class SettingsTestCounter {
    private(set) var count = 0

    func increment() {
        count += 1
    }
}

/// Fulfills an expectation from a main-actor `@Sendable` closure without the closure capturing the expectation.
@MainActor
final class SettingsTestSignal {
    private let expectation: XCTestExpectation

    init(_ expectation: XCTestExpectation) {
        self.expectation = expectation
    }

    func fulfill() {
        expectation.fulfill()
    }
}

/// Fulfills an expectation when it is freed, so a test can wait for the object that owns it to be released.
final class SettingsDeallocationWatcher {
    private let expectation: XCTestExpectation

    init(_ expectation: XCTestExpectation) {
        self.expectation = expectation
    }

    deinit {
        expectation.fulfill()
    }
}
