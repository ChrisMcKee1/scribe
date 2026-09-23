import AppKit
import ServiceManagement
import XCTest
@testable import Scribe

/// Stands in for macOS. Each call reads the state as it is when the call arrives, which is what lets a test hold a
/// read at a gate while a flip changes the state underneath it.
actor LoginItemServiceFake: LoginItemService {
    private(set) var current: LoginItemState
    private(set) var requests: [Bool] = []
    private(set) var reads = 0
    private var refusal: LoginItemRefusal?
    private var stateAfterAcceptedRequest: LoginItemState?
    private var heldRead: SettingsTestGate?
    private var heldRequest: SettingsTestGate?
    private var heldReadAfterRequest: SettingsTestGate?

    init(_ state: LoginItemState) {
        current = state
    }

    func state() async -> LoginItemState {
        reads += 1
        let read = current
        if let gate = heldRead {
            heldRead = nil
            await gate.pass()
        }
        return read
    }

    func request(enabled: Bool) async -> LoginItemRefusal? {
        requests.append(enabled)
        if let gate = heldReadAfterRequest {
            heldReadAfterRequest = nil
            heldRead = gate
        }
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

    /// Holds the first read after the next request: the flip's own read of what macOS did.
    func holdReadAfterNextRequest(at gate: SettingsTestGate) {
        heldReadAfterRequest = gate
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

    /// Registered but waiting for approval is already a successful registration, and registering again is not an
    /// approval: turning the switch on sends the user to Login Items and asks macOS for nothing.
    @MainActor
    func testTurningOnARegistrationThatAwaitsApprovalOpensLoginItemsAndRegistersNothing() async {
        let service = LoginItemServiceFake(.requiresApproval)
        let opened = SettingsTestCounter()
        let loginItem = makeSwitch(service, openLoginItems: { opened.increment() })
        await loginItem.refresh()

        let outcome = await loginItem.setEnabled(true)

        XCTAssertEqual(outcome, .needsApproval)
        XCTAssertEqual(opened.count, 1)
        let requests = await service.requests
        XCTAssertEqual(requests, [])
        XCTAssertFalse(loginItem.isOn)
        XCTAssertTrue(loginItem.showsOpenLoginItems)
        XCTAssertTrue(loginItem.message.contains("Login Items"), loginItem.message)
    }

    @MainActor
    func testANeverRegisteredCopyThatReportsNotFoundCanBeTurnedOn() async {
        let service = LoginItemServiceFake(.notFound)
        let loginItem = makeSwitch(service)
        await loginItem.refresh()
        XCTAssertEqual(loginItem.message, "Open Scribe automatically when you log in.")

        let outcome = await loginItem.setEnabled(true)

        XCTAssertEqual(outcome, .applied)
        let requests = await service.requests
        XCTAssertEqual(requests, [true])
    }

    @MainActor
    func testAnUnrecognizedStateCannotBeFlipped() async {
        let service = LoginItemServiceFake(.unrecognized)
        let loginItem = makeSwitch(service)
        await loginItem.refresh()

        XCTAssertFalse(loginItem.canFlip)
        XCTAssertTrue(loginItem.showsOpenLoginItems)
        let outcome = await loginItem.setEnabled(true)

        XCTAssertEqual(outcome, .unchanged)
        let requests = await service.requests
        XCTAssertEqual(requests, [])
    }

    /// The user turns Scribe off in System Settings while a read that already saw it on is still out; coming back
    /// to Scribe must end on the new state, not the one that read brings back.
    @MainActor
    func testAnActivationDuringAReadGetsAReadOfItsOwn() async {
        let center = NotificationCenter()
        let service = LoginItemServiceFake(.enabled)
        let loginItem = makeSwitch(service, center: center)

        let gate = SettingsTestGate()
        await service.holdNextRead(at: gate)
        let heldRead = Task { await loginItem.refresh() }
        await gate.waitForArrival()

        await service.set(.notRegistered)
        center.post(name: NSApplication.didBecomeActiveNotification, object: nil)
        await loginItem.refreshTask?.value
        await gate.open()
        await heldRead.value

        XCTAssertEqual(loginItem.state, .notRegistered)
        XCTAssertFalse(loginItem.isOn)
        let reads = await service.reads
        XCTAssertEqual(reads, 2, "the overtaken read and exactly one read after it")
    }

    /// The flip's own read of what macOS did saw Scribe on, the user turned it off in System Settings while that
    /// read was out, and came back: the switch must end on what macOS reports now, with no refusal, since the flip
    /// itself worked.
    @MainActor
    func testAnActivationDuringAFlipsFinalReadEndsOnTheNewerState() async {
        let center = NotificationCenter()
        let service = LoginItemServiceFake(.notRegistered)
        let loginItem = makeSwitch(service, center: center)
        await loginItem.refresh()

        let gate = SettingsTestGate()
        await service.holdReadAfterNextRequest(at: gate)
        let flip = Task { await loginItem.setEnabled(true) }
        await gate.waitForArrival()

        await service.set(.notRegistered)
        center.post(name: NSApplication.didBecomeActiveNotification, object: nil)
        await loginItem.refreshTask?.value
        await gate.open()
        let outcome = await flip.value

        XCTAssertEqual(outcome, .applied)
        let current = await service.current
        XCTAssertEqual(loginItem.state, current)
        XCTAssertEqual(loginItem.state, .notRegistered)
        XCTAssertFalse(loginItem.isOn)
        XCTAssertNil(loginItem.refusal)
        XCTAssertTrue(loginItem.canFlip)
        let reads = await service.reads
        XCTAssertEqual(reads, 3, "the first read, the flip's own read, and exactly one read after the activation")
    }
}

/// Which native call each state permits: the part of the login item below `LoginItemService`, where a wrong
/// answer would register again, unregister nothing or act on a state Scribe cannot read.
final class LoginItemOperationTests: XCTestCase {
    func testEveryStateAndRequestPermitsExactlyTheExpectedCall() {
        let expected: [(LoginItemState, Bool, LoginItemOperation)] = [
            (.enabled, true, .nothingToDo),
            (.enabled, false, .unregister),
            (.notRegistered, true, .register),
            (.notRegistered, false, .nothingToDo),
            (.requiresApproval, true, .awaitApproval),
            (.requiresApproval, false, .unregister),
            (.notFound, true, .register),
            (.notFound, false, .nothingToDo),
            (.unrecognized, true, .unavailable),
            (.unrecognized, false, .unavailable),
        ]
        for (state, enabling, operation) in expected {
            XCTAssertEqual(
                LoginItemOperation(enabling: enabling, from: state), operation, "\(state), enabling: \(enabling)")
        }
    }
}

/// Stands in for `SMAppService.mainApp`, recording which calls `LoginItemManager.apply` makes.
final class LoginItemControlFake: LoginItemControl {
    var status: SMAppService.Status
    private(set) var calls: [String] = []
    var registerError: NSError?

    init(_ status: SMAppService.Status) {
        self.status = status
    }

    func register() throws {
        calls.append("register")
        if let registerError {
            throw registerError
        }
        status = .enabled
    }

    func unregister() throws {
        calls.append("unregister")
        status = .notRegistered
    }
}

final class LoginItemApplyTests: XCTestCase {
    private func apply(_ enabled: Bool, from status: SMAppService.Status) -> ([String], LoginItemRefusal?) {
        let control = LoginItemControlFake(status)
        let refusal = LoginItemManager.apply(enabled: enabled, control: control)
        return (control.calls, refusal)
    }

    func testEnablingAnApprovalPendingRegistrationDoesNotRegisterAgain() {
        let (calls, refusal) = apply(true, from: .requiresApproval)
        XCTAssertEqual(calls, [])
        XCTAssertNil(refusal)
    }

    func testDisablingAnApprovalPendingRegistrationUnregistersIt() {
        XCTAssertEqual(apply(false, from: .requiresApproval).0, ["unregister"])
    }

    func testNotFoundIsNotTreatedAsRegistered() {
        XCTAssertEqual(apply(false, from: .notFound).0, [], "nothing is registered, so nothing is unregistered")
        XCTAssertEqual(apply(true, from: .notFound).0, ["register"])
    }

    func testTheCurrentStateIsNeverRequestedAgain() {
        XCTAssertEqual(apply(true, from: .enabled).0, [])
        XCTAssertEqual(apply(false, from: .notRegistered).0, [])
    }

    func testRegisteringAndUnregisteringFromTheOtherState() {
        XCTAssertEqual(apply(true, from: .notRegistered).0, ["register"])
        XCTAssertEqual(apply(false, from: .enabled).0, ["unregister"])
    }

    func testAnUnknownStateChangesNothing() throws {
        let unknown = try XCTUnwrap(SMAppService.Status(rawValue: 99))
        XCTAssertEqual(LoginItemState(unknown), .unrecognized)

        let (calls, refusal) = apply(true, from: unknown)

        XCTAssertEqual(calls, [])
        XCTAssertEqual(refusal, .unrecognizedState)
    }

    func testARefusedRegistrationReportsWhy() {
        let control = LoginItemControlFake(.notRegistered)
        control.registerError = NSError(domain: "SMAppServiceErrorDomain", code: Int(kSMErrorLaunchDeniedByUser))

        XCTAssertEqual(LoginItemManager.apply(enabled: true, control: control), .deniedByUser)
        XCTAssertEqual(control.calls, ["register"])
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
