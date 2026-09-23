import AppKit
import ServiceManagement
import XCTest
@testable import Scribe

/// Stands in for macOS. Each call reads the state as it is when the call arrives, which is what lets a test hold a
/// read at a gate while a flip changes the state underneath it.
actor LoginItemServiceFake: LoginItemService {
    private(set) var current: LoginItemState
    private(set) var requests: [Bool] = []
    private var refusal: LoginItemRefusal?
    private var stateAfterAcceptedRequest: LoginItemState?
    private var heldRead: SettingsTestGate?
    private var heldRequest: SettingsTestGate?

    init(_ state: LoginItemState) {
        current = state
    }

    func state() async -> LoginItemState {
        let read = current
        if let gate = heldRead {
            heldRead = nil
            await gate.pass()
        }
        return read
    }

    func request(enabled: Bool) async -> LoginItemRefusal? {
        requests.append(enabled)
        if let gate = heldRequest {
            heldRequest = nil
            await gate.pass()
        }
        if let refusal {
            return refusal
        }
        current = stateAfterAcceptedRequest ?? (enabled ? .enabled : .notRegistered)
        return nil
    }

    func set(_ state: LoginItemState) {
        current = state
    }

    func refuse(with refusal: LoginItemRefusal) {
        self.refusal = refusal
    }

    func afterAcceptedRequest(report state: LoginItemState) {
        stateAfterAcceptedRequest = state
    }

    func holdNextRead(at gate: SettingsTestGate) {
        heldRead = gate
    }

    func holdNextRequest(at gate: SettingsTestGate) {
        heldRequest = gate
    }
}

final class LoginItemSwitchTests: XCTestCase {
    @MainActor
    private func makeSwitch(
        _ service: LoginItemServiceFake,
        center: NotificationCenter = NotificationCenter(),
        openLoginItems: @escaping @MainActor () -> Void = {}
    ) -> LoginItemSwitch {
        LoginItemSwitch(service: service, openLoginItems: openLoginItems, center: center)
    }

    @MainActor
    func testTheSwitchCannotBeFlippedUntilMacOSHasAnswered() async {
        let service = LoginItemServiceFake(.notRegistered)
        let loginItem = makeSwitch(service)

        XCTAssertFalse(loginItem.canFlip)
        let outcome = await loginItem.setEnabled(true)

        XCTAssertEqual(outcome, .unchanged)
        let requests = await service.requests
        XCTAssertEqual(requests, [])
    }

    @MainActor
    func testShowsWhatMacOSReports() async {
        let service = LoginItemServiceFake(.enabled)
        let loginItem = makeSwitch(service)

        await loginItem.refresh()

        XCTAssertTrue(loginItem.isOn)
        XCTAssertTrue(loginItem.canFlip)
        XCTAssertFalse(loginItem.showsOpenLoginItems)
        XCTAssertEqual(loginItem.message, "Open Scribe automatically when you log in.")
    }

    /// Registered but not approved reads as off, because Scribe will not open at login in that state, and points
    /// to the one place the user can finish it.
    @MainActor
    func testWaitingForApprovalShowsOffWithTheApprovalHintAndAnOpenAction() async {
        let service = LoginItemServiceFake(.requiresApproval)
        let opened = SettingsTestCounter()
        let loginItem = makeSwitch(service, openLoginItems: { opened.increment() })

        await loginItem.refresh()

        XCTAssertFalse(loginItem.isOn)
        XCTAssertTrue(loginItem.showsOpenLoginItems)
        XCTAssertTrue(loginItem.message.contains("Login Items"), loginItem.message)
        XCTAssertNil(loginItem.refusal)
        loginItem.openLoginItems()
        XCTAssertEqual(opened.count, 1)
    }

    @MainActor
    func testAFlipAppliesAtOnceAndShowsTheStateMacOSReportsAfterwards() async {
        let service = LoginItemServiceFake(.notRegistered)
        let loginItem = makeSwitch(service)
        await loginItem.refresh()

        let on = await loginItem.setEnabled(true)
        XCTAssertEqual(on, .applied)
        XCTAssertTrue(loginItem.isOn)

        let off = await loginItem.setEnabled(false)
        XCTAssertEqual(off, .applied)
        XCTAssertFalse(loginItem.isOn)

        let requests = await service.requests
        XCTAssertEqual(requests, [true, false])
    }

    @MainActor
    func testAnAcceptedRequestThatStillNeedsApprovalShowsOffWithoutAnError() async {
        let service = LoginItemServiceFake(.notRegistered)
        await service.afterAcceptedRequest(report: .requiresApproval)
        let loginItem = makeSwitch(service)
        await loginItem.refresh()

        let outcome = await loginItem.setEnabled(true)

        XCTAssertEqual(outcome, .needsApproval)
        XCTAssertFalse(loginItem.isOn)
        XCTAssertNil(loginItem.refusal)
        XCTAssertTrue(loginItem.showsOpenLoginItems)
    }

    @MainActor
    func testARefusedChangeShowsWhy() async {
        let service = LoginItemServiceFake(.notRegistered)
        await service.refuse(with: .deniedByUser)
        let loginItem = makeSwitch(service)
        await loginItem.refresh()

        let outcome = await loginItem.setEnabled(true)

        XCTAssertEqual(outcome, .declined)
        XCTAssertFalse(loginItem.isOn)
        XCTAssertEqual(loginItem.refusal, .deniedByUser)
        XCTAssertTrue(loginItem.message.contains("System Settings"), loginItem.message)
        XCTAssertTrue(loginItem.showsOpenLoginItems)
    }

    @MainActor
    func testAnUnexpectedErrorShowsItsCode() async {
        let service = LoginItemServiceFake(.enabled)
        await service.refuse(with: .failed(code: 5))
        let loginItem = makeSwitch(service)
        await loginItem.refresh()

        let outcome = await loginItem.setEnabled(false)

        XCTAssertEqual(outcome, .declined)
        XCTAssertTrue(loginItem.isOn)
        XCTAssertTrue(loginItem.message.contains("error 5"), loginItem.message)
    }

    @MainActor
    func testAnAcceptedRequestThatChangesNothingIsNotReportedAsApplied() async {
        let service = LoginItemServiceFake(.notRegistered)
        await service.afterAcceptedRequest(report: .notRegistered)
        let loginItem = makeSwitch(service)
        await loginItem.refresh()

        let outcome = await loginItem.setEnabled(true)

        XCTAssertEqual(outcome, .declined)
        XCTAssertEqual(loginItem.refusal, .noEffect)
    }

    /// Mirrors Windows' `StartupSwitchState`: a read that began before a flip must not paint the older state over
    /// the one the flip produced.
    @MainActor
    func testAReadThatStartedBeforeAFlipCannotPaintOverIt() async {
        let service = LoginItemServiceFake(.notRegistered)
        let loginItem = makeSwitch(service)
        await loginItem.refresh()

        let gate = SettingsTestGate()
        await service.holdNextRead(at: gate)
        let staleRead = Task { await loginItem.refresh() }
        await gate.waitForArrival()

        let outcome = await loginItem.setEnabled(true)
        XCTAssertEqual(outcome, .applied)
        XCTAssertTrue(loginItem.isOn)

        await gate.open()
        await staleRead.value

        XCTAssertTrue(loginItem.isOn)
        XCTAssertEqual(loginItem.state, .enabled)
    }

    @MainActor
    func testASecondFlipWhileTheFirstIsAppliedIsNotStarted() async {
        let service = LoginItemServiceFake(.notRegistered)
        let loginItem = makeSwitch(service)
        await loginItem.refresh()

        let gate = SettingsTestGate()
        await service.holdNextRequest(at: gate)
        let first = Task { await loginItem.setEnabled(true) }
        await gate.waitForArrival()

        XCTAssertTrue(loginItem.isOn, "the switch shows the requested state while macOS answers")
        XCTAssertFalse(loginItem.canFlip)
        let second = await loginItem.setEnabled(false)
        XCTAssertEqual(second, .busy)

        await gate.open()
        let firstOutcome = await first.value

        XCTAssertEqual(firstOutcome, .applied)
        XCTAssertTrue(loginItem.isOn)
        let requests = await service.requests
        XCTAssertEqual(requests, [true])
    }

    /// Coming back from System Settings activates Scribe, and the switch then shows what the user changed there.
    @MainActor
    func testBecomingActiveReadsTheStateAgain() async {
        let center = NotificationCenter()
        let service = LoginItemServiceFake(.enabled)
        let loginItem = makeSwitch(service, center: center)
        await loginItem.refresh()
        XCTAssertTrue(loginItem.isOn)

        await service.set(.notRegistered)
        center.post(name: NSApplication.didBecomeActiveNotification, object: nil)
        await loginItem.refreshTask?.value

        XCTAssertFalse(loginItem.isOn)
    }

    @MainActor
    func testAFreshReadClearsAnEarlierRefusal() async {
        let service = LoginItemServiceFake(.notRegistered)
        await service.refuse(with: .notPermitted)
        let loginItem = makeSwitch(service)
        await loginItem.refresh()
        await loginItem.setEnabled(true)
        XCTAssertEqual(loginItem.refusal, .notPermitted)

        await loginItem.refresh()

        XCTAssertNil(loginItem.refusal)
    }
}

final class LoginItemStateTests: XCTestCase {
    func testOnlyEnabledCountsAsOn() {
        XCTAssertTrue(LoginItemState.enabled.isOn)
        XCTAssertFalse(LoginItemState.notRegistered.isOn)
        XCTAssertFalse(LoginItemState.requiresApproval.isOn)
        XCTAssertFalse(LoginItemState.notFound.isOn)
        XCTAssertFalse(LoginItemState.unrecognized.isOn)
    }

    func testMapsEverySystemStatus() {
        XCTAssertEqual(LoginItemState(.enabled), .enabled)
        XCTAssertEqual(LoginItemState(.notRegistered), .notRegistered)
        XCTAssertEqual(LoginItemState(.requiresApproval), .requiresApproval)
        XCTAssertEqual(LoginItemState(.notFound), .notFound)
    }

    func testServiceManagementErrorsMapToReasons() {
        let domain = "SMAppServiceErrorDomain"
        XCTAssertNil(LoginItemManager.refusal(for: NSError(domain: domain, code: Int(kSMErrorAlreadyRegistered))))
        XCTAssertEqual(
            LoginItemManager.refusal(for: NSError(domain: domain, code: Int(kSMErrorLaunchDeniedByUser))),
            .deniedByUser)
        XCTAssertEqual(LoginItemManager.refusal(for: NSError(domain: domain, code: Int(EPERM))), .notPermitted)
        XCTAssertEqual(LoginItemManager.refusal(for: NSError(domain: domain, code: 99)), .failed(code: 99))
        XCTAssertEqual(
            LoginItemManager.refusal(for: NSError(domain: NSPOSIXErrorDomain, code: Int(EPERM))),
            .failed(code: Int(EPERM)))
    }
}
