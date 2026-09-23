import AppKit
import ApplicationServices
import Foundation
import OSLog

/// What reached the target application.
enum InjectionDelivery: String, Equatable, Sendable {
    /// Inserted through the Accessibility API.
    case accessibility
    /// Command-V was posted while the pasteboard held Scribe's text. That proves the keystrokes were
    /// posted, not that the target read the pasteboard.
    case pasted
    /// Every keystroke was posted.
    case typed
    /// Typing stopped part way, because focus moved or an event could not be created. Part of the text
    /// may have arrived, so it must never be typed again.
    case typedPartially
    /// The text was empty.
    case nothingToInsert
    /// The expected application or element no longer had focus. Nothing was delivered.
    case targetChanged
    /// The focused application did not answer an Accessibility request within the messaging timeout.
    /// Nothing else was tried: a request that timed out may still be carried out when it recovers.
    case targetUnresponsive
    /// Nothing had keyboard focus. Nothing was delivered.
    case noFocusedElement
    /// Scribe is not trusted for Accessibility. Nothing was delivered.
    case accessibilityDenied
    /// No keystroke could be created. Nothing was delivered.
    case failed
}

/// How the pasteboard path ended; `.notUsed` whenever the pasteboard was never touched.
enum ClipboardPasteOutcome: String, Equatable, Sendable {
    case notUsed
    /// Command-V was posted while the pasteboard still held Scribe's write.
    case pasted
    /// The pasteboard held content a plain-text restore cannot reproduce, so it was left alone and the
    /// text was typed.
    case nonTextContent
    /// The pasteboard held plain text Scribe could not read silently, so the text was typed.
    case unreadable
    /// Another application wrote while Scribe was reading the pasteboard, so the text was typed.
    case contended
    /// Scribe's text could not be written, so the text was typed.
    case writeFailed
    /// Another application replaced Scribe's text before Command-V, so the text was typed.
    case superseded
    /// The Command-V events could not be created, so the text was typed.
    case chordUnavailable
    /// Focus moved, or the target stopped answering, after the borrow. No Command-V was sent.
    case withheld

    init(_ refusal: PasteboardBorrowRefusal) {
        switch refusal {
        case .nonTextContent:
            self = .nonTextContent
        case .unreadable:
            self = .unreadable
        case .contended:
            self = .contended
        case .writeFailed:
            self = .writeFailed
        case .superseded:
            self = .superseded
        }
    }
}

/// The outcome of one delivery. It holds enum values only, never text, so it is safe to log.
struct InjectionResult: Equatable, Sendable {
    let delivery: InjectionDelivery
    let clipboard: ClipboardPasteOutcome
    let restore: ClipboardRestoreOutcome

    init(
        delivery: InjectionDelivery,
        clipboard: ClipboardPasteOutcome = .notUsed,
        restore: ClipboardRestoreOutcome = .notApplicable
    ) {
        self.delivery = delivery
        self.clipboard = clipboard
        self.restore = restore
    }

    /// Whether any of the text may have reached the target. When true the text must not be delivered
    /// again, even if `isComplete` is false, because that could insert it twice.
    var textReachedTarget: Bool {
        switch delivery {
        case .accessibility, .pasted, .typed, .typedPartially:
            return true
        case .nothingToInsert, .targetChanged, .targetUnresponsive, .noFocusedElement, .accessibilityDenied,
            .failed:
            return false
        }
    }

    /// Whether all of the text was delivered, or there was none. When false, keep the transcript so the
    /// user can recover it.
    var isComplete: Bool {
        switch delivery {
        case .accessibility, .pasted, .typed, .nothingToInsert:
            return true
        case .typedPartially, .targetChanged, .targetUnresponsive, .noFocusedElement, .accessibilityDenied,
            .failed:
            return false
        }
    }

    /// Enum names only: no text, lengths or pasteboard types.
    var logSummary: String {
        "delivery=\(delivery.rawValue) clipboard=\(clipboard.rawValue) restore=\(restore.rawValue)"
    }
}

/// The application, and when known the element, a dictation is meant for. Capture it when the recording
/// starts with `TextInjector.captureTarget()` and pass it to `inject`, which then refuses to deliver
/// anywhere else.
///
/// `@unchecked Sendable` because `AXUIElement` is not declared Sendable. The invariant holds because the
/// struct is immutable, an `AXUIElement` is an immutable reference to a remote element whose CF retain and
/// release are thread-safe, and the element is private to this file, where only `TextInjector` messages
/// it, on the main actor.
struct InjectionTarget: @unchecked Sendable {
    let processIdentifier: pid_t?
    let bundleIdentifier: String?
    fileprivate let focusedElement: AXUIElement?

    init(processIdentifier: pid_t?, bundleIdentifier: String?) {
        self.init(processIdentifier: processIdentifier, bundleIdentifier: bundleIdentifier, focusedElement: nil)
    }

    init(application: NSRunningApplication) {
        self.init(
            processIdentifier: application.processIdentifier,
            bundleIdentifier: application.bundleIdentifier,
            focusedElement: nil)
    }

    fileprivate init(processIdentifier: pid_t?, bundleIdentifier: String?, focusedElement: AXUIElement?) {
        // No focused element ever belongs to a process number of zero or below. `NSRunningApplication`
        // reports -1 for an application without a process of its own, and matching on that would withhold
        // every delivery, so such a target is identified by its bundle alone.
        self.processIdentifier = processIdentifier.flatMap { $0 > 0 ? $0 : nil }
        self.bundleIdentifier = bundleIdentifier
        self.focusedElement = focusedElement
    }

    /// Whether `inject` also requires this exact element to still have focus.
    var hasFocusedElement: Bool {
        focusedElement != nil
    }
}

/// An application as the workspace reports it.
struct InjectionApplication: Equatable, Sendable {
    let processIdentifier: pid_t
    let bundleIdentifier: String?
}

/// What the Accessibility API reports as focused.
enum InjectionFocusLookup {
    case element(AXUIElement, processIdentifier: pid_t)
    case nothingFocused
    /// The focused application did not answer within the messaging timeout.
    case unresponsive
}

enum AccessibilityInsertionOutcome: Equatable, Sendable {
    case inserted
    case notInserted
    /// A request timed out. A write may still be applied once the application recovers.
    case unresponsive
}

/// The platform calls `TextInjector` makes, so its decisions can be tested without Accessibility
/// permission, a focused application or real keyboard events.
@MainActor
protocol InjectionSystem {
    func isAccessibilityTrusted() -> Bool
    func focusedElement() -> InjectionFocusLookup
    func frontmostApplication() -> InjectionApplication?
    func bundleIdentifier(ofProcess processIdentifier: pid_t) -> String?
    func insertViaAccessibility(_ text: String, into element: AXUIElement) -> AccessibilityInsertionOutcome
    /// Posts every event for `keystroke` to the process, or none of them, and reports which.
    func post(_ keystroke: InjectionKeystroke, to processIdentifier: pid_t) -> Bool
}

/// The points where a delivery waits.
enum InjectionPause: Equatable, Sendable {
    /// After Scribe's text is on the pasteboard, before Command-V.
    case beforePaste
    /// After Command-V, so the target reads the pasteboard before it is restored.
    case afterPaste
    /// Between two typed keystrokes, so a long dictation does not flood the target's event queue.
    case betweenKeystrokes
}

@MainActor
protocol InjectionPacing {
    func pause(_ pause: InjectionPause) async
}

/// Real waits. They suspend rather than block, so the main actor keeps serving the menu, the pill and the
/// hotkey while a paste settles.
@MainActor
struct SystemInjectionPacing: InjectionPacing {
    var beforePaste: Duration = .milliseconds(50)
    /// A slow target (an Electron app under load, a virtual machine, a remote session) can read the
    /// pasteboard well after Command-V arrives, and a restore that runs first makes it paste the user's
    /// previous clipboard instead of the dictation. Waiting longer only delays the next delivery.
    var afterPaste: Duration = .milliseconds(250)
    var betweenKeystrokes: Duration = .milliseconds(4)

    func pause(_ pause: InjectionPause) async {
        let duration: Duration
        switch pause {
        case .beforePaste:
            duration = beforePaste
        case .afterPaste:
            duration = afterPaste
        case .betweenKeystrokes:
            duration = betweenKeystrokes
        }

        // An unstructured task does not inherit the caller's cancellation, so a cancelled dictation still
        // gives the target its full settle time before the pasteboard is restored.
        let wait = Task<Void, Never> {
            _ = try? await Task.sleep(for: duration)
        }
        await wait.value
    }
}

/// Places dictated text into the focused application: through the Accessibility API when the focused
/// element accepts it, otherwise by pasting through a briefly borrowed pasteboard, otherwise by typing it
/// as Unicode keystrokes. The target is confirmed again before each of those steps.
///
/// Runs on the main actor with the AppKit and Accessibility calls it makes. Its waits suspend rather than
/// block, and `LiveInjectionSystem` bounds every Accessibility request, so a delivery never freezes the
/// app for long.
@MainActor
final class TextInjector {
    private let system: any InjectionSystem
    private let pacer: any InjectionPacing
    private let borrower: PasteboardBorrower
    private let logger = Logger(subsystem: "com.scribe.macos", category: "TextInjection")
    private let logSink: (String) -> Void
    private var isDelivering = false
    private var waitingDeliveries: [CheckedContinuation<Void, Never>] = []

    convenience init(logSink: @escaping (String) -> Void) {
        self.init(
            system: LiveInjectionSystem(),
            pacer: SystemInjectionPacing(),
            pasteboard: .general,
            logSink: logSink)
    }

    init(
        system: any InjectionSystem,
        pacer: any InjectionPacing,
        pasteboard: NSPasteboard,
        logSink: @escaping (String) -> Void
    ) {
        self.system = system
        self.pacer = pacer
        self.borrower = PasteboardBorrower(pasteboard: pasteboard)
        self.logSink = logSink
    }

    /// Deliveries waiting behind the one in progress.
    var queuedDeliveryCount: Int {
        waitingDeliveries.count
    }

    func promptForAccessibilityAccessIfNeeded() -> Bool {
        let trusted: Bool
        if ProcessInfo.processInfo.environment["SCRIBE_FORCE_ACCESSIBILITY_DENIED"] == "1" {
            trusted = false
        } else {
            let options = ["AXTrustedCheckOptionPrompt": true] as CFDictionary
            trusted = AXIsProcessTrustedWithOptions(options)
        }

        if !trusted {
            let message = "Accessibility permission is not granted. Scribe can capture audio, but text injection "
                + "is unavailable until System Settings > Privacy & Security > Accessibility allows it."
            logger.warning("\(message, privacy: .public)")
            logSink(message)
        }
        return trusted
    }

    /// The application and element that have keyboard focus now, for a later `inject`. Call it when the
    /// recording starts. Falls back to the frontmost application alone when Accessibility cannot name the
    /// focused element; nil when there is no frontmost application either.
    func captureTarget() -> InjectionTarget? {
        if case .element(let element, let processIdentifier) = system.focusedElement() {
            return InjectionTarget(
                processIdentifier: processIdentifier,
                bundleIdentifier: system.bundleIdentifier(ofProcess: processIdentifier),
                focusedElement: element)
        }

        guard let application = system.frontmostApplication() else {
            return nil
        }
        return InjectionTarget(
            processIdentifier: application.processIdentifier,
            bundleIdentifier: application.bundleIdentifier)
    }

    /// Delivers `text` to `target`, or to whatever has focus when `target` is nil, and reports how it
    /// went. A delivered paste is never reported as failed and never followed by typing.
    ///
    /// Deliveries run one at a time in call order: two interleaved borrows would each snapshot the other's
    /// text as the user's clipboard. A delivery is not cancellable once it starts, because stopping between
    /// Command-V and the restore would either strand Scribe's text on the pasteboard or restore the old
    /// content before the target has read the new.
    func inject(
        text: String,
        into target: InjectionTarget? = nil,
        shiftReturnLineBreaks: Bool = true
    ) async -> InjectionResult {
        await beginDelivery()
        defer { endDelivery() }

        let result = await deliver(text, to: target, shiftReturnLineBreaks: shiftReturnLineBreaks)
        record(result)
        return result
    }

    /// Returns once every delivery requested before this call has finished, for example before quitting.
    func waitUntilIdle() async {
        await beginDelivery()
        endDelivery()
    }

    /// Expected outcomes are informational: the text arrived, or the user moved on before it could. A
    /// partial or failed delivery, an unresponsive target and a failed restore are errors.
    static func logLevel(for result: InjectionResult) -> OSLogType {
        if result.restore == .failed {
            return .error
        }

        switch result.delivery {
        case .accessibility, .pasted, .typed, .nothingToInsert:
            return .info
        case .targetChanged, .noFocusedElement:
            return .default
        case .typedPartially, .targetUnresponsive, .accessibilityDenied, .failed:
            return .error
        }
    }

    private func beginDelivery() async {
        guard isDelivering else {
            isDelivering = true
            return
        }
        await withCheckedContinuation { waitingDeliveries.append($0) }
    }

    /// Hands the turn straight to the next waiting delivery, so a later arrival can never slip ahead.
    private func endDelivery() {
        if waitingDeliveries.isEmpty {
            isDelivering = false
        } else {
            waitingDeliveries.removeFirst().resume()
        }
    }

    private func record(_ result: InjectionResult) {
        let line = "Text injection finished: \(result.logSummary)."
        logger.log(level: Self.logLevel(for: result), "\(line, privacy: .public)")
        logSink(line)
    }

    private func deliver(
        _ text: String,
        to target: InjectionTarget?,
        shiftReturnLineBreaks: Bool
    ) async -> InjectionResult {
        guard !text.isEmpty else {
            return InjectionResult(delivery: .nothingToInsert)
        }

        guard system.isAccessibilityTrusted() else {
            return InjectionResult(delivery: .accessibilityDenied)
        }

        let requested = TargetExpectation(target)
        let element: AXUIElement
        let processIdentifier: pid_t
        switch confirmTarget(requested) {
        case .confirmed(let focused, let focusedProcess):
            element = focused
            processIdentifier = focusedProcess
        case .changed:
            return InjectionResult(delivery: .targetChanged)
        case .nothingFocused:
            return InjectionResult(delivery: .noFocusedElement)
        case .unresponsive:
            return InjectionResult(delivery: .targetUnresponsive)
        }

        switch system.insertViaAccessibility(text, into: element) {
        case .inserted:
            return InjectionResult(delivery: .accessibility)
        case .unresponsive:
            // A write that timed out can still be applied once the target catches up, so pasting or typing
            // now could insert the text twice.
            return InjectionResult(delivery: .targetUnresponsive)
        case .notInserted:
            break
        }

        // Every later check pins the process just confirmed, so the text can only go where it looked.
        return await pasteOrType(
            text,
            to: requested.pinned(to: processIdentifier),
            processIdentifier: processIdentifier,
            shiftReturnLineBreaks: shiftReturnLineBreaks)
    }

    private func pasteOrType(
        _ text: String,
        to pinned: TargetExpectation,
        processIdentifier: pid_t,
        shiftReturnLineBreaks: Bool
    ) async -> InjectionResult {
        func typeInstead() async -> InjectionDelivery {
            await typeText(
                text,
                to: pinned,
                processIdentifier: processIdentifier,
                shiftReturnLineBreaks: shiftReturnLineBreaks)
        }

        let lease: PasteboardLease
        switch borrower.borrow(for: text) {
        case .borrowed(let borrowed):
            lease = borrowed
        case .refused(let refusal, let rollback):
            // The pasteboard is as the user or another application left it, and nothing was pasted, so
            // typing cannot duplicate anything.
            let delivery = await typeInstead()
            return InjectionResult(delivery: delivery, clipboard: ClipboardPasteOutcome(refusal), restore: rollback)
        }

        await pacer.pause(.beforePaste)

        // Focus can move while the write settles. Nothing has been sent yet, so the borrow is just undone.
        let withheld: InjectionDelivery?
        switch confirmTarget(pinned) {
        case .confirmed:
            withheld = nil
        case .unresponsive:
            withheld = .targetUnresponsive
        case .changed, .nothingFocused:
            withheld = .targetChanged
        }
        if let withheld {
            return InjectionResult(delivery: withheld, clipboard: .withheld, restore: borrower.restore(lease))
        }

        // Another application may have copied since Scribe's write, and Command-V would paste its content.
        // Nothing has been sent, so typing instead cannot duplicate anything, and the newer copy is left
        // alone. A copy made after this check and before the target reads the pasteboard cannot be seen:
        // NSPasteboard has no lock to hold across the paste, and the target must be free to read.
        guard borrower.stillHolds(lease) else {
            let restore = borrower.restore(lease)
            return InjectionResult(delivery: await typeInstead(), clipboard: .superseded, restore: restore)
        }

        guard system.post(.paste, to: processIdentifier) else {
            let restore = borrower.restore(lease)
            return InjectionResult(delivery: await typeInstead(), clipboard: .chordUnavailable, restore: restore)
        }

        // Delivered. Whatever becomes of the restore, typing now would insert the text twice.
        await pacer.pause(.afterPaste)
        return InjectionResult(delivery: .pasted, clipboard: .pasted, restore: borrower.restore(lease))
    }

    /// Types `text` as Unicode keystrokes, confirming before each one that the pinned target still has
    /// focus: typing is paced, and whatever is typed after focus moves lands somewhere else.
    private func typeText(
        _ text: String,
        to pinned: TargetExpectation,
        processIdentifier: pid_t,
        shiftReturnLineBreaks: Bool
    ) async -> InjectionDelivery {
        var posted = 0
        for keystroke in KeystrokePlan.keystrokes(for: text, shiftReturnLineBreaks: shiftReturnLineBreaks) {
            if posted > 0 {
                await pacer.pause(.betweenKeystrokes)
            }

            switch confirmTarget(pinned) {
            case .confirmed:
                break
            case .unresponsive:
                return posted == 0 ? .targetUnresponsive : .typedPartially
            case .changed, .nothingFocused:
                return posted == 0 ? .targetChanged : .typedPartially
            }

            guard system.post(keystroke, to: processIdentifier) else {
                return posted == 0 ? .failed : .typedPartially
            }
            posted += 1
        }

        return .typed
    }

    private func confirmTarget(_ expected: TargetExpectation) -> TargetConfirmation {
        switch system.focusedElement() {
        case .unresponsive:
            return .unresponsive

        case .nothingFocused:
            // Losing the expected element, or another application coming forward, is focus moving.
            if expected.element != nil {
                return .changed
            }
            guard !expected.isEmpty else {
                return .nothingFocused
            }
            guard
                let frontmost = system.frontmostApplication(),
                matches(frontmost.processIdentifier, bundleIdentifier: frontmost.bundleIdentifier, expected)
            else {
                return .changed
            }
            return .nothingFocused

        case .element(let element, let processIdentifier):
            guard
                matches(
                    processIdentifier,
                    bundleIdentifier: system.bundleIdentifier(ofProcess: processIdentifier),
                    expected)
            else {
                return .changed
            }
            if let expectedElement = expected.element, !CFEqual(expectedElement, element) {
                return .changed
            }
            return .confirmed(element, processIdentifier)
        }
    }

    private func matches(
        _ processIdentifier: pid_t,
        bundleIdentifier: @autoclosure () -> String?,
        _ expected: TargetExpectation
    ) -> Bool {
        if let expectedProcess = expected.processIdentifier, expectedProcess != processIdentifier {
            return false
        }
        if let expectedBundle = expected.bundleIdentifier, expectedBundle != bundleIdentifier() {
            return false
        }
        return true
    }
}

private struct TargetExpectation {
    var processIdentifier: pid_t?
    var bundleIdentifier: String?
    var element: AXUIElement?

    init(_ target: InjectionTarget?) {
        processIdentifier = target?.processIdentifier
        bundleIdentifier = target?.bundleIdentifier
        element = target?.focusedElement
    }

    var isEmpty: Bool {
        processIdentifier == nil && bundleIdentifier == nil && element == nil
    }

    func pinned(to processIdentifier: pid_t) -> TargetExpectation {
        var pinned = self
        pinned.processIdentifier = processIdentifier
        return pinned
    }
}

private enum TargetConfirmation {
    case confirmed(AXUIElement, pid_t)
    case changed
    case nothingFocused
    case unresponsive
}

/// The real Accessibility, workspace and event-posting calls behind `TextInjector`.
@MainActor
struct LiveInjectionSystem: InjectionSystem {
    /// How long any Accessibility request from Scribe waits for an answer, in seconds. The system default
    /// is several seconds, and these requests run on the main actor, so a hung target would otherwise
    /// freeze the menu, the pill and the hotkey for that long. A responsive application answers far sooner.
    static let accessibilityMessagingTimeout: Float = 1.0

    private let systemWide = AXUIElementCreateSystemWide()

    init() {
        // Set on the system-wide element, which makes it the timeout for every element Scribe messages.
        AXUIElementSetMessagingTimeout(systemWide, Self.accessibilityMessagingTimeout)
    }

    func isAccessibilityTrusted() -> Bool {
        if ProcessInfo.processInfo.environment["SCRIBE_FORCE_ACCESSIBILITY_DENIED"] == "1" {
            return false
        }
        return AXIsProcessTrusted()
    }

    func focusedElement() -> InjectionFocusLookup {
        var value: CFTypeRef?
        let result = AXUIElementCopyAttributeValue(systemWide, kAXFocusedUIElementAttribute as CFString, &value)
        if result == .cannotComplete {
            return .unresponsive
        }

        guard result == .success, let value, CFGetTypeID(value) == AXUIElementGetTypeID() else {
            return .nothingFocused
        }

        let element = value as! AXUIElement
        var processIdentifier: pid_t = 0
        guard AXUIElementGetPid(element, &processIdentifier) == .success, processIdentifier > 0 else {
            return .nothingFocused
        }
        return .element(element, processIdentifier: processIdentifier)
    }

    func frontmostApplication() -> InjectionApplication? {
        guard let application = NSWorkspace.shared.frontmostApplication else {
            return nil
        }
        return InjectionApplication(
            processIdentifier: application.processIdentifier,
            bundleIdentifier: application.bundleIdentifier)
    }

    func bundleIdentifier(ofProcess processIdentifier: pid_t) -> String? {
        NSRunningApplication(processIdentifier: processIdentifier)?.bundleIdentifier
    }

    func insertViaAccessibility(_ text: String, into element: AXUIElement) -> AccessibilityInsertionOutcome {
        switch settability(of: kAXSelectedTextAttribute as CFString, on: element) {
        case .unresponsive:
            return .unresponsive
        case .settable:
            let result = AXUIElementSetAttributeValue(
                element,
                kAXSelectedTextAttribute as CFString,
                text as CFTypeRef)
            if result == .success {
                return .inserted
            }
            if result == .cannotComplete {
                return .unresponsive
            }
        case .notSettable:
            break
        }

        return replaceSelectionInValue(of: element, with: text)
    }

    func post(_ keystroke: InjectionKeystroke, to processIdentifier: pid_t) -> Bool {
        guard
            let source = CGEventSource(stateID: .hidSystemState),
            let events = KeystrokeEvents.events(for: keystroke, source: source)
        else {
            return false
        }

        for event in events {
            event.postToPid(processIdentifier)
        }
        return true
    }

    /// For elements whose selected text cannot be set directly: splices the text into the whole value at
    /// the selection, then puts the caret after it.
    private func replaceSelectionInValue(of element: AXUIElement, with text: String) -> AccessibilityInsertionOutcome {
        switch settability(of: kAXValueAttribute as CFString, on: element) {
        case .unresponsive:
            return .unresponsive
        case .notSettable:
            return .notInserted
        case .settable:
            break
        }

        var valueReference: CFTypeRef?
        let valueResult = AXUIElementCopyAttributeValue(element, kAXValueAttribute as CFString, &valueReference)
        if valueResult == .cannotComplete {
            return .unresponsive
        }
        guard valueResult == .success, let currentValue = valueReference as? String else {
            return .notInserted
        }

        var rangeReference: CFTypeRef?
        let rangeResult = AXUIElementCopyAttributeValue(
            element,
            kAXSelectedTextRangeAttribute as CFString,
            &rangeReference)
        if rangeResult == .cannotComplete {
            return .unresponsive
        }
        guard
            rangeResult == .success,
            let rangeReference,
            CFGetTypeID(rangeReference) == AXValueGetTypeID()
        else {
            return .notInserted
        }

        let rangeValue = rangeReference as! AXValue
        var selection = CFRange()
        guard AXValueGetType(rangeValue) == .cfRange, AXValueGetValue(rangeValue, .cfRange, &selection) else {
            return .notInserted
        }

        let current = currentValue as NSString
        let location = max(0, min(selection.location, current.length))
        let length = max(0, min(selection.length, current.length - location))
        let updated = current.replacingCharacters(in: NSRange(location: location, length: length), with: text)

        let setResult = AXUIElementSetAttributeValue(element, kAXValueAttribute as CFString, updated as CFTypeRef)
        if setResult == .cannotComplete {
            return .unresponsive
        }
        guard setResult == .success else {
            return .notInserted
        }

        // Best effort: the text is in, and only the caret position is left to put right.
        var caret = CFRange(location: location + (text as NSString).length, length: 0)
        if let caretValue = AXValueCreate(.cfRange, &caret) {
            AXUIElementSetAttributeValue(element, kAXSelectedTextRangeAttribute as CFString, caretValue)
        }
        return .inserted
    }

    private enum Settability {
        case settable
        case notSettable
        case unresponsive
    }

    private func settability(of attribute: CFString, on element: AXUIElement) -> Settability {
        var settable: DarwinBoolean = false
        let result = AXUIElementIsAttributeSettable(element, attribute, &settable)
        if result == .cannotComplete {
            return .unresponsive
        }
        return result == .success && settable.boolValue ? .settable : .notSettable
    }
}
