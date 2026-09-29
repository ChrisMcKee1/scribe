import AppKit
import ApplicationServices
import OSLog
import XCTest

@testable import Scribe

/// Stands in for Accessibility, the workspace and event posting, so each test decides what has focus and
/// sees every keystroke `TextInjector` posts.
@MainActor
final class ScriptedInjectionSystem: InjectionSystem {
    struct PostedKeystroke: Equatable {
        let keystroke: InjectionKeystroke
        let processIdentifier: pid_t
    }

    var trusted = true
    var focus: InjectionFocusLookup
    var frontmost: InjectionApplication?
    var bundleIdentifiers: [pid_t: String] = [:]
    var accessibilityOutcome: AccessibilityInsertionOutcome = .notInserted
    /// Cancels the task running the delivery while this focus question (counted from 1) is answered, as a
    /// pipeline would while the application is slow to reply.
    var cancelsTaskOnFocusQuery: Int?
    /// Keystrokes that fail to post, as if their events could not be created.
    var rejects: (InjectionKeystroke) -> Bool = { _ in false }
    /// Runs as each keystroke is posted, before it is recorded.
    var onPost: ((InjectionKeystroke) -> Void)?
    private(set) var posted: [PostedKeystroke] = []
    private(set) var accessibilityAttempts = 0
    private(set) var focusQueries = 0

    init(focus: InjectionFocusLookup) {
        self.focus = focus
    }

    var postedKeystrokes: [InjectionKeystroke] {
        posted.map(\.keystroke)
    }

    func isAccessibilityTrusted() -> Bool {
        trusted
    }

    func focusedElement() -> InjectionFocusLookup {
        focusQueries += 1
        if focusQueries == cancelsTaskOnFocusQuery {
            cancelTheCurrentInjectionTask()
        }
        return focus
    }

    func frontmostApplication() -> InjectionApplication? {
        frontmost
    }

    func bundleIdentifier(ofProcess processIdentifier: pid_t) -> String? {
        bundleIdentifiers[processIdentifier]
    }

    func insertViaAccessibility(_ text: String, into element: AXUIElement) -> AccessibilityInsertionOutcome {
        accessibilityAttempts += 1
        return accessibilityOutcome
    }

    func post(_ keystroke: InjectionKeystroke, to processIdentifier: pid_t) -> Bool {
        guard !rejects(keystroke) else {
            return false
        }
        onPost?(keystroke)
        posted.append(PostedKeystroke(keystroke: keystroke, processIdentifier: processIdentifier))
        return true
    }
}

/// Records every pause and lets a test act at an exact point in a delivery, or hold it there.
@MainActor
final class ScriptedInjectionPacer: InjectionPacing {
    private(set) var pauses: [InjectionPause] = []
    /// Runs inside each pause, before the delivery resumes: where a test makes another application copy
    /// or moves focus.
    var onPause: ((InjectionPause) -> Void)?
    /// The next pause of this kind suspends until `releaseHeldPause()`.
    var holdNext: InjectionPause?
    private var held: CheckedContinuation<Void, Never>?

    var isHolding: Bool {
        held != nil
    }

    func pause(_ pause: InjectionPause) async {
        pauses.append(pause)
        onPause?(pause)
        guard pause == holdNext else {
            return
        }
        holdNext = nil
        await withCheckedContinuation { held = $0 }
    }

    func releaseHeldPause() {
        let continuation = held
        held = nil
        continuation?.resume()
    }
}

final class InjectionLogCapture {
    var lines: [String] = []
}

/// Cancels whichever task is running the caller: a fake answering an Accessibility request uses it to
/// cancel the delivery while that request is in flight.
func cancelTheCurrentInjectionTask() {
    withUnsafeCurrentTask { task in
        if let task {
            task.cancel()
        }
    }
}

/// State the tests' callbacks and tasks write into, kept on the main actor with everything else.
@MainActor
final class InjectionObservations {
    var first: InjectionResult?
    var second: InjectionResult?
    var events: [String] = []
    var pastedTexts: [String?] = []
    var pastedTypes: [[NSPasteboard.PasteboardType]] = []
    /// The task running a delivery, so a hook inside that delivery can cancel it.
    var task: Task<Void, Never>?
}

/// How many pasteboard writes still fail, for tests that make a write or its rollback fail.
@MainActor
final class InjectionWriteFailures {
    var remaining: Int

    init(_ remaining: Int) {
        self.remaining = remaining
    }

    func operations() -> PasteboardOperations {
        var operations = PasteboardOperations.live
        operations.write = { pasteboard, items in
            guard self.remaining <= 0 else {
                self.remaining -= 1
                return false
            }
            return pasteboard.writeObjects(items)
        }
        return operations
    }
}

/// One injector wired to scripted focus, event posting and pacing, and a private pasteboard. The focused
/// element belongs to process 100, "com.example.editor", unless a test says otherwise.
@MainActor
final class InjectionHarness {
    static let editorProcess: pid_t = 100
    static let otherProcess: pid_t = 200
    static let editorBundle = "com.example.editor"
    static let otherBundle = "com.example.other"

    let pasteboard: NSPasteboard
    let system: ScriptedInjectionSystem
    let pacer: ScriptedInjectionPacer
    let log: InjectionLogCapture
    let injector: TextInjector
    /// Stand-ins for two different focused elements. Created for unrelated process numbers, which is all
    /// `CFEqual` compares; nothing ever messages them.
    let editorField: AXUIElement
    let otherField: AXUIElement

    init(operations: PasteboardOperations = .live) {
        let pasteboard = NSPasteboard(
            name: NSPasteboard.Name("com.scribe.macos.tests.injector.\(UUID().uuidString)"))
        let editorField = AXUIElementCreateApplication(4_001)
        let system = ScriptedInjectionSystem(
            focus: .element(editorField, processIdentifier: InjectionHarness.editorProcess))
        system.bundleIdentifiers = [
            InjectionHarness.editorProcess: InjectionHarness.editorBundle,
            InjectionHarness.otherProcess: InjectionHarness.otherBundle,
        ]
        system.frontmost = InjectionApplication(
            processIdentifier: InjectionHarness.editorProcess,
            bundleIdentifier: InjectionHarness.editorBundle)
        let pacer = ScriptedInjectionPacer()
        let log = InjectionLogCapture()

        self.pasteboard = pasteboard
        self.editorField = editorField
        self.otherField = AXUIElementCreateApplication(4_002)
        self.system = system
        self.pacer = pacer
        self.log = log
        self.injector = TextInjector(
            system: system,
            pacer: pacer,
            borrower: PasteboardBorrower(pasteboard: pasteboard, operations: operations),
            logSink: { line in log.lines.append(line) })
    }

    func releasePasteboard() {
        pasteboard.releaseGlobally()
    }

    func copyAsAnotherApplication(_ text: String) {
        pasteboard.clearContents()
        pasteboard.setString(text, forType: .string)
    }

    func copyRichTextAsAnotherApplication(_ text: String) {
        pasteboard.clearContents()
        let item = NSPasteboardItem()
        item.setString(text, forType: .string)
        item.setData(Data("<b>\(text)</b>".utf8), forType: .html)
        pasteboard.writeObjects([item])
    }

    func moveFocusToAnotherApplication() {
        system.focus = .element(otherField, processIdentifier: InjectionHarness.otherProcess)
        system.frontmost = InjectionApplication(
            processIdentifier: InjectionHarness.otherProcess,
            bundleIdentifier: InjectionHarness.otherBundle)
    }
}

private let dictation = "Dictated words for the editor, long enough to need several keystrokes."
private let usersText = "The user's own clipboard text."

final class TextInjectorTests: XCTestCase {
    @MainActor
    func testAccessibilityInsertionLeavesThePasteboardAlone() async {
        let harness = InjectionHarness()
        defer { harness.releasePasteboard() }
        harness.copyAsAnotherApplication(usersText)
        let changeCount = harness.pasteboard.changeCount
        harness.system.accessibilityOutcome = .inserted

        let result = await harness.injector.inject(text: dictation)

        XCTAssertEqual(result, InjectionResult(delivery: .accessibility))
        XCTAssertEqual(harness.pasteboard.changeCount, changeCount)
        XCTAssertTrue(harness.system.posted.isEmpty)
        XCTAssertTrue(harness.pacer.pauses.isEmpty)
    }

    @MainActor
    func testPasteBorrowsThePasteboardAndPutsTheUsersTextBack() async {
        let harness = InjectionHarness()
        defer { harness.releasePasteboard() }
        harness.copyAsAnotherApplication(usersText)
        let observed = InjectionObservations()
        harness.system.onPost = { _ in
            observed.pastedTexts.append(harness.pasteboard.string(forType: .string))
            observed.pastedTypes.append(harness.pasteboard.pasteboardItems?.first?.types ?? [])
        }

        let result = await harness.injector.inject(text: dictation)

        XCTAssertEqual(result, InjectionResult(delivery: .pasted, clipboard: .pasted, restore: .restored))
        XCTAssertEqual(
            harness.system.posted,
            [
                ScriptedInjectionSystem.PostedKeystroke(
                    keystroke: .paste,
                    processIdentifier: InjectionHarness.editorProcess)
            ])
        XCTAssertEqual(observed.pastedTexts, [dictation])
        let typesAtCommandV = observed.pastedTypes.first ?? []
        XCTAssertTrue(typesAtCommandV.contains(PasteboardBorrower.transientType))
        XCTAssertTrue(typesAtCommandV.contains(PasteboardBorrower.concealedType))
        XCTAssertEqual(harness.pacer.pauses, [.beforePaste, .afterPaste])
        XCTAssertEqual(harness.pasteboard.string(forType: .string), usersText)
    }

    @MainActor
    func testPasteIntoAnEmptyPasteboardLeavesItEmptyAgain() async {
        let harness = InjectionHarness()
        defer { harness.releasePasteboard() }

        let result = await harness.injector.inject(text: dictation)

        XCTAssertEqual(result, InjectionResult(delivery: .pasted, clipboard: .pasted, restore: .restored))
        XCTAssertTrue((harness.pasteboard.pasteboardItems ?? []).isEmpty)
    }

    @MainActor
    func testACopyBeforeThePasteCheckIsTypedAroundAndKept() async {
        let harness = InjectionHarness()
        defer { harness.releasePasteboard() }
        harness.copyAsAnotherApplication(usersText)
        harness.pacer.onPause = { pause in
            if pause == .beforePaste {
                harness.copyAsAnotherApplication("Copied by another app before Command-V.")
            }
        }

        let result = await harness.injector.inject(text: dictation)

        XCTAssertEqual(result, InjectionResult(delivery: .typed, clipboard: .superseded, restore: .superseded))
        XCTAssertFalse(harness.system.postedKeystrokes.contains(.paste))
        XCTAssertEqual(typedText(harness.system.postedKeystrokes), dictation)
        XCTAssertEqual(harness.pasteboard.string(forType: .string), "Copied by another app before Command-V.")
    }

    @MainActor
    func testACopyBeforeTheRestoreIsNeverOverwrittenAndThePasteIsNeverRetyped() async {
        let harness = InjectionHarness()
        defer { harness.releasePasteboard() }
        harness.copyAsAnotherApplication(usersText)
        harness.pacer.onPause = { pause in
            if pause == .afterPaste {
                harness.copyAsAnotherApplication("Copied by another app after Command-V.")
            }
        }

        let result = await harness.injector.inject(text: dictation)

        XCTAssertEqual(result, InjectionResult(delivery: .pasted, clipboard: .pasted, restore: .superseded))
        XCTAssertEqual(harness.system.postedKeystrokes, [.paste])
        XCTAssertEqual(harness.pasteboard.string(forType: .string), "Copied by another app after Command-V.")
    }

    @MainActor
    func testARichClipboardIsTypedAroundAndLeftUntouched() async {
        let harness = InjectionHarness()
        defer { harness.releasePasteboard() }
        harness.copyRichTextAsAnotherApplication(usersText)
        let changeCount = harness.pasteboard.changeCount

        let result = await harness.injector.inject(text: dictation)

        XCTAssertEqual(result, InjectionResult(delivery: .typed, clipboard: .nonTextContent))
        XCTAssertEqual(typedText(harness.system.postedKeystrokes), dictation)
        XCTAssertGreaterThan(harness.system.posted.count, 1)
        XCTAssertTrue(harness.system.posted.allSatisfy { $0.processIdentifier == InjectionHarness.editorProcess })
        XCTAssertEqual(harness.pasteboard.changeCount, changeCount)
        XCTAssertNotNil(harness.pasteboard.data(forType: .html))
    }

    @MainActor
    func testFocusInAnotherApplicationWithholdsTheText() async {
        let harness = InjectionHarness()
        defer { harness.releasePasteboard() }
        harness.copyAsAnotherApplication(usersText)
        let changeCount = harness.pasteboard.changeCount
        let target = InjectionTarget(processIdentifier: InjectionHarness.editorProcess, bundleIdentifier: nil)
        harness.moveFocusToAnotherApplication()

        let result = await harness.injector.inject(text: dictation, into: target)

        XCTAssertEqual(result, InjectionResult(delivery: .targetChanged))
        XCTAssertFalse(result.mayHaveReachedTarget)
        XCTAssertEqual(harness.system.accessibilityAttempts, 0)
        XCTAssertTrue(harness.system.posted.isEmpty)
        XCTAssertEqual(harness.pasteboard.changeCount, changeCount)
    }

    @MainActor
    func testFocusMovingBeforeCommandVUndoesTheBorrowAndWithholdsTheText() async {
        let harness = InjectionHarness()
        defer { harness.releasePasteboard() }
        harness.copyAsAnotherApplication(usersText)
        let target = harness.injector.captureTarget()
        harness.pacer.onPause = { pause in
            if pause == .beforePaste {
                harness.moveFocusToAnotherApplication()
            }
        }

        let result = await harness.injector.inject(text: dictation, into: target)

        XCTAssertEqual(result, InjectionResult(delivery: .targetChanged, clipboard: .withheld, restore: .restored))
        XCTAssertTrue(harness.system.posted.isEmpty)
        XCTAssertEqual(harness.pasteboard.string(forType: .string), usersText)
    }

    @MainActor
    func testFocusMovingBeforeCommandVIsCaughtEvenWithoutATarget() async {
        let harness = InjectionHarness()
        defer { harness.releasePasteboard() }
        harness.copyAsAnotherApplication(usersText)
        harness.pacer.onPause = { pause in
            if pause == .beforePaste {
                harness.moveFocusToAnotherApplication()
            }
        }

        let result = await harness.injector.inject(text: dictation)

        XCTAssertEqual(result, InjectionResult(delivery: .targetChanged, clipboard: .withheld, restore: .restored))
        XCTAssertTrue(harness.system.posted.isEmpty)
    }

    @MainActor
    func testAnotherElementInTheSameApplicationWithholdsTheText() async throws {
        let harness = InjectionHarness()
        defer { harness.releasePasteboard() }
        let target = try XCTUnwrap(harness.injector.captureTarget())
        XCTAssertTrue(target.hasFocusedElement)
        harness.system.focus = .element(harness.otherField, processIdentifier: InjectionHarness.editorProcess)

        let result = await harness.injector.inject(text: dictation, into: target)

        XCTAssertEqual(result, InjectionResult(delivery: .targetChanged))
        XCTAssertEqual(harness.system.accessibilityAttempts, 0)
    }

    @MainActor
    func testACapturedTargetThatKeepsFocusGetsTheText() async throws {
        let harness = InjectionHarness()
        defer { harness.releasePasteboard() }
        harness.system.accessibilityOutcome = .inserted

        let target = try XCTUnwrap(harness.injector.captureTarget())
        let result = await harness.injector.inject(text: dictation, into: target)

        XCTAssertEqual(target.processIdentifier, InjectionHarness.editorProcess)
        XCTAssertEqual(target.bundleIdentifier, InjectionHarness.editorBundle)
        XCTAssertEqual(result, InjectionResult(delivery: .accessibility))
    }

    @MainActor
    func testCapturingWhileTheFocusedAppIsUnresponsiveFallsBackToTheFrontmostApplication() throws {
        let harness = InjectionHarness()
        defer { harness.releasePasteboard() }
        harness.system.focus = .unresponsive

        let target = try XCTUnwrap(harness.injector.captureTarget())

        XCTAssertEqual(target.processIdentifier, InjectionHarness.editorProcess)
        XCTAssertEqual(target.bundleIdentifier, InjectionHarness.editorBundle)
        XCTAssertFalse(target.hasFocusedElement)
    }

    @MainActor
    func testAnotherBundleWithholdsTheText() async {
        let harness = InjectionHarness()
        defer { harness.releasePasteboard() }
        let target = InjectionTarget(processIdentifier: nil, bundleIdentifier: InjectionHarness.otherBundle)

        let result = await harness.injector.inject(text: dictation, into: target)

        XCTAssertEqual(result, InjectionResult(delivery: .targetChanged))
    }

    @MainActor
    func testAnUnresponsiveTargetGetsNothing() async {
        let harness = InjectionHarness()
        defer { harness.releasePasteboard() }
        harness.copyAsAnotherApplication(usersText)
        let changeCount = harness.pasteboard.changeCount
        harness.system.focus = .unresponsive

        let result = await harness.injector.inject(text: dictation)

        XCTAssertEqual(result, InjectionResult(delivery: .targetUnresponsive))
        XCTAssertEqual(harness.system.accessibilityAttempts, 0)
        XCTAssertTrue(harness.system.posted.isEmpty)
        XCTAssertEqual(harness.pasteboard.changeCount, changeCount)
    }

    @MainActor
    func testATimedOutAccessibilityWriteMayHaveLandedAndIsNeverFollowedByAPasteOrTyping() async {
        let harness = InjectionHarness()
        defer { harness.releasePasteboard() }
        harness.copyAsAnotherApplication(usersText)
        let changeCount = harness.pasteboard.changeCount
        harness.system.accessibilityOutcome = .unconfirmed

        let result = await harness.injector.inject(text: dictation)

        XCTAssertEqual(result, InjectionResult(delivery: .accessibilityUnconfirmed))
        XCTAssertTrue(result.mayHaveReachedTarget, "The write was sent, so the text may already be there.")
        XCTAssertFalse(result.isComplete)
        XCTAssertTrue(harness.system.posted.isEmpty)
        XCTAssertEqual(harness.pasteboard.changeCount, changeCount)
    }

    @MainActor
    func testATimedOutAccessibilityQuestionSendsNothingAndTriesNothingElse() async {
        let harness = InjectionHarness()
        defer { harness.releasePasteboard() }
        harness.copyAsAnotherApplication(usersText)
        let changeCount = harness.pasteboard.changeCount
        harness.system.accessibilityOutcome = .unresponsive

        let result = await harness.injector.inject(text: dictation)

        XCTAssertEqual(result, InjectionResult(delivery: .targetUnresponsive))
        XCTAssertFalse(result.mayHaveReachedTarget, "Nothing was written before the question timed out.")
        XCTAssertTrue(harness.system.posted.isEmpty)
        XCTAssertEqual(harness.pasteboard.changeCount, changeCount)
    }

    @MainActor
    func testWithoutAccessibilityTrustNothingIsTried() async {
        let harness = InjectionHarness()
        defer { harness.releasePasteboard() }
        harness.system.trusted = false

        let result = await harness.injector.inject(text: dictation)

        XCTAssertEqual(result, InjectionResult(delivery: .accessibilityDenied))
        XCTAssertEqual(harness.system.focusQueries, 0)
        XCTAssertTrue(harness.system.posted.isEmpty)
    }

    @MainActor
    func testNothingFocusedDeliversNothing() async {
        let harness = InjectionHarness()
        defer { harness.releasePasteboard() }
        harness.system.focus = .nothingFocused

        let result = await harness.injector.inject(text: dictation)

        XCTAssertEqual(result, InjectionResult(delivery: .noFocusedElement))
        XCTAssertTrue(harness.system.posted.isEmpty)
    }

    @MainActor
    func testNothingFocusedIsATargetChangeOnlyWhenAnotherApplicationIsFrontmost() async {
        let harness = InjectionHarness()
        defer { harness.releasePasteboard() }
        let target = InjectionTarget(processIdentifier: InjectionHarness.editorProcess, bundleIdentifier: nil)
        harness.system.focus = .nothingFocused

        let sameApplication = await harness.injector.inject(text: dictation, into: target)
        harness.system.frontmost = InjectionApplication(
            processIdentifier: InjectionHarness.otherProcess,
            bundleIdentifier: InjectionHarness.otherBundle)
        let otherApplication = await harness.injector.inject(text: dictation, into: target)

        XCTAssertEqual(sameApplication, InjectionResult(delivery: .noFocusedElement))
        XCTAssertEqual(otherApplication, InjectionResult(delivery: .targetChanged))
    }

    @MainActor
    func testEmptyTextHasNothingToInsert() async {
        let harness = InjectionHarness()
        defer { harness.releasePasteboard() }

        let result = await harness.injector.inject(text: "")

        XCTAssertEqual(result, InjectionResult(delivery: .nothingToInsert))
        XCTAssertTrue(result.isComplete)
        XCTAssertEqual(harness.system.focusQueries, 0)
    }

    @MainActor
    func testTypingStopsWhenFocusMovesPartWay() async {
        let harness = InjectionHarness()
        defer { harness.releasePasteboard() }
        harness.copyRichTextAsAnotherApplication(usersText)
        harness.pacer.onPause = { pause in
            if pause == .betweenKeystrokes {
                harness.moveFocusToAnotherApplication()
            }
        }

        let result = await harness.injector.inject(text: dictation)

        XCTAssertEqual(result, InjectionResult(delivery: .typedPartially, clipboard: .nonTextContent))
        XCTAssertTrue(result.mayHaveReachedTarget)
        XCTAssertFalse(result.isComplete)
        XCTAssertEqual(harness.system.posted.count, 1)
    }

    @MainActor
    func testTypingThatCannotPostItsFirstKeystrokeFails() async {
        let harness = InjectionHarness()
        defer { harness.releasePasteboard() }
        harness.copyRichTextAsAnotherApplication(usersText)
        harness.system.rejects = { _ in true }

        let result = await harness.injector.inject(text: dictation)

        XCTAssertEqual(result, InjectionResult(delivery: .failed, clipboard: .nonTextContent))
        XCTAssertFalse(result.mayHaveReachedTarget)
    }

    @MainActor
    func testAPasteChordThatCannotBeCreatedIsUndoneAndTyped() async {
        let harness = InjectionHarness()
        defer { harness.releasePasteboard() }
        harness.copyAsAnotherApplication(usersText)
        harness.system.rejects = { $0 == .paste }

        let result = await harness.injector.inject(text: dictation)

        XCTAssertEqual(result, InjectionResult(delivery: .typed, clipboard: .chordUnavailable, restore: .restored))
        XCTAssertEqual(typedText(harness.system.postedKeystrokes), dictation)
        XCTAssertEqual(harness.pasteboard.string(forType: .string), usersText)
    }

    @MainActor
    func testDeliveriesRunOneAtATimeInCallOrder() async {
        let harness = InjectionHarness()
        defer { harness.releasePasteboard() }
        harness.copyAsAnotherApplication(usersText)
        let observed = InjectionObservations()
        harness.system.onPost = { _ in
            observed.pastedTexts.append(harness.pasteboard.string(forType: .string))
        }
        harness.pacer.holdNext = .beforePaste

        Task { @MainActor in
            observed.first = await harness.injector.inject(text: "The first dictation.")
        }
        await yieldUntil { harness.pacer.isHolding }
        Task { @MainActor in
            observed.second = await harness.injector.inject(text: "The second dictation.")
        }
        await yieldUntil { harness.injector.queuedDeliveryCount == 1 }

        // The second delivery is queued, and has not touched the pasteboard while the first one owns it.
        XCTAssertEqual(harness.pacer.pauses, [.beforePaste])
        XCTAssertEqual(harness.pasteboard.string(forType: .string), "The first dictation.")

        harness.pacer.releaseHeldPause()
        await yieldUntil { observed.first != nil && observed.second != nil }

        let pasted = InjectionResult(delivery: .pasted, clipboard: .pasted, restore: .restored)
        XCTAssertEqual(observed.first, pasted)
        XCTAssertEqual(observed.second, pasted)
        XCTAssertEqual(observed.pastedTexts, ["The first dictation.", "The second dictation."])
        XCTAssertEqual(harness.pacer.pauses, [.beforePaste, .afterPaste, .beforePaste, .afterPaste])
        XCTAssertEqual(harness.pasteboard.string(forType: .string), usersText)
    }

    @MainActor
    func testWaitUntilIdleReturnsOnlyAfterTheDeliveryInProgress() async {
        let harness = InjectionHarness()
        defer { harness.releasePasteboard() }
        harness.copyAsAnotherApplication(usersText)
        let observed = InjectionObservations()
        harness.pacer.holdNext = .afterPaste

        Task { @MainActor in
            observed.first = await harness.injector.inject(text: dictation)
            observed.events.append("delivered")
        }
        await yieldUntil { harness.pacer.isHolding }
        Task { @MainActor in
            await harness.injector.waitUntilIdle()
            observed.events.append("idle")
        }
        await yieldUntil { harness.injector.queuedDeliveryCount == 1 }
        XCTAssertTrue(observed.events.isEmpty)

        harness.pacer.releaseHeldPause()
        await yieldUntil { observed.events.count == 2 }

        XCTAssertEqual(observed.events, ["delivered", "idle"])
        XCTAssertEqual(harness.pasteboard.string(forType: .string), usersText)
    }

    @MainActor
    func testConsecutivePastesKeepBorrowingAfterScribesOwnRestore() async {
        let harness = InjectionHarness()
        defer { harness.releasePasteboard() }
        harness.copyAsAnotherApplication(usersText)

        let first = await harness.injector.inject(text: "The first dictation.")
        let second = await harness.injector.inject(text: "The second dictation.")

        let pasted = InjectionResult(delivery: .pasted, clipboard: .pasted, restore: .restored)
        XCTAssertEqual(first, pasted)
        XCTAssertEqual(second, pasted)
        XCTAssertEqual(harness.system.postedKeystrokes, [.paste, .paste])
    }

    @MainActor
    func testTheLogCarriesOnlyOutcomeNames() async {
        let harness = InjectionHarness()
        defer { harness.releasePasteboard() }
        harness.copyAsAnotherApplication(usersText)

        _ = await harness.injector.inject(text: dictation)

        XCTAssertEqual(
            harness.log.lines,
            ["Text injection finished: delivery=pasted clipboard=pasted restore=restored."])
        for line in harness.log.lines {
            XCTAssertFalse(line.contains(dictation))
            XCTAssertFalse(line.contains(usersText))
            XCTAssertFalse(line.contains("public.utf8-plain-text"))
            XCTAssertNil(line.rangeOfCharacter(from: .decimalDigits), "A count or length reached the log.")
        }
    }

    @MainActor
    func testOnlyUnexpectedOutcomesAreLoggedAsErrors() {
        func level(_ result: InjectionResult) -> OSLogType {
            TextInjector.logLevel(for: result)
        }
        let delivered = InjectionResult(delivery: .pasted, clipboard: .pasted, restore: .restored)
        let restoreFailed = InjectionResult(delivery: .pasted, clipboard: .pasted, restore: .failed)

        XCTAssertEqual(level(delivered).rawValue, OSLogType.info.rawValue)
        XCTAssertEqual(level(InjectionResult(delivery: .targetChanged)).rawValue, OSLogType.default.rawValue)
        XCTAssertEqual(level(InjectionResult(delivery: .cancelled)).rawValue, OSLogType.default.rawValue)
        XCTAssertEqual(level(InjectionResult(delivery: .typedPartially)).rawValue, OSLogType.error.rawValue)
        XCTAssertEqual(level(InjectionResult(delivery: .accessibilityUnconfirmed)).rawValue, OSLogType.error.rawValue)
        XCTAssertEqual(level(InjectionResult(delivery: .targetUnknown)).rawValue, OSLogType.error.rawValue)
        XCTAssertEqual(level(restoreFailed).rawValue, OSLogType.error.rawValue)
    }

    func testOnlyDeliveriesThatMayHaveLandedCountAsReachingTheTarget() {
        let reached: Set<InjectionDelivery> = [
            .accessibility, .pasted, .typed, .typedPartially, .accessibilityUnconfirmed,
        ]
        let complete: Set<InjectionDelivery> = [.accessibility, .pasted, .typed, .nothingToInsert]
        let all: [InjectionDelivery] = [
            .accessibility, .pasted, .typed, .typedPartially, .accessibilityUnconfirmed, .nothingToInsert,
            .targetChanged, .targetUnknown, .targetUnresponsive, .noFocusedElement, .accessibilityDenied, .cancelled,
            .failed,
        ]

        for delivery in all {
            let result = InjectionResult(delivery: delivery)
            XCTAssertEqual(result.mayHaveReachedTarget, reached.contains(delivery), delivery.rawValue)
            XCTAssertEqual(result.isComplete, complete.contains(delivery), delivery.rawValue)
        }
    }

    func testEveryBorrowRefusalIsReportedUnderItsOwnName() {
        let refusals: [PasteboardBorrowRefusal] = [.nonTextContent, .unreadable, .contended, .writeFailed, .superseded]

        for refusal in refusals {
            XCTAssertEqual(ClipboardPasteOutcome(refusal).rawValue, refusal.rawValue)
        }
    }

    func testATargetBuiltFromARunningApplicationNamesThatApplication() {
        let application = NSRunningApplication.current
        let target = InjectionTarget(application: application)

        XCTAssertEqual(target.bundleIdentifier, application.bundleIdentifier)
        // The test runner is not a registered application and reports -1, which no element belongs to.
        if application.processIdentifier > 0 {
            XCTAssertEqual(target.processIdentifier, application.processIdentifier)
        } else {
            XCTAssertNil(target.processIdentifier)
        }
        XCTAssertFalse(target.hasFocusedElement)
    }

    func testAProcessNumberThatNoElementCanHaveIsDropped() {
        XCTAssertNil(InjectionTarget(processIdentifier: -1, bundleIdentifier: "com.example.editor").processIdentifier)
        XCTAssertNil(InjectionTarget(processIdentifier: 0, bundleIdentifier: nil).processIdentifier)
        XCTAssertEqual(InjectionTarget(processIdentifier: 42, bundleIdentifier: nil).processIdentifier, 42)
        XCTAssertNil(InjectionTarget(processIdentifier: 42, bundleIdentifier: "").bundleIdentifier)
    }

    func testOnlyATargetWithAProcessOrABundleIdentifiesAnApplication() {
        XCTAssertTrue(InjectionTarget(processIdentifier: 42, bundleIdentifier: nil).identifiesApplication)
        let bundleOnly = InjectionTarget(processIdentifier: -1, bundleIdentifier: "com.example.editor")
        XCTAssertTrue(bundleOnly.identifiesApplication)
        XCTAssertFalse(InjectionTarget(processIdentifier: -1, bundleIdentifier: nil).identifiesApplication)
        XCTAssertFalse(InjectionTarget(processIdentifier: nil, bundleIdentifier: nil).identifiesApplication)
        XCTAssertFalse(InjectionTarget(processIdentifier: 0, bundleIdentifier: "").identifiesApplication)
    }

    @MainActor
    func testATargetThatNamesNoApplicationIsRefusedRatherThanTakenAsNoTarget() async {
        let harness = InjectionHarness()
        defer { harness.releasePasteboard() }
        harness.copyAsAnotherApplication(usersText)
        let changeCount = harness.pasteboard.changeCount
        harness.system.accessibilityOutcome = .inserted
        let unidentifiable = [
            InjectionTarget(processIdentifier: -1, bundleIdentifier: nil),
            InjectionTarget(processIdentifier: nil, bundleIdentifier: nil),
            InjectionTarget(processIdentifier: 0, bundleIdentifier: ""),
        ]

        for target in unidentifiable {
            let result = await harness.injector.inject(text: dictation, into: target)
            XCTAssertEqual(result, InjectionResult(delivery: .targetUnknown))
            XCTAssertFalse(result.mayHaveReachedTarget)
        }

        XCTAssertEqual(harness.system.focusQueries, 0)
        XCTAssertEqual(harness.system.accessibilityAttempts, 0)
        XCTAssertTrue(harness.system.posted.isEmpty)
        XCTAssertEqual(harness.pasteboard.changeCount, changeCount)
    }

    @MainActor
    func testCapturingWhenNothingIdentifiesTheApplicationGivesNoTarget() {
        let harness = InjectionHarness()
        defer { harness.releasePasteboard() }
        harness.system.focus = .unresponsive
        harness.system.frontmost = InjectionApplication(processIdentifier: -1, bundleIdentifier: nil)

        XCTAssertNil(harness.injector.captureTarget())
    }

    @MainActor
    func testAFailedPasteboardReadIsNeverTakenForAnEmptyPasteboard() async {
        var operations = PasteboardOperations.live
        operations.itemTypes = { _ in nil }
        let harness = InjectionHarness(operations: operations)
        defer { harness.releasePasteboard() }
        harness.copyRichTextAsAnotherApplication(usersText)
        let changeCount = harness.pasteboard.changeCount

        let result = await harness.injector.inject(text: dictation)

        XCTAssertEqual(result, InjectionResult(delivery: .typed, clipboard: .unreadable))
        XCTAssertFalse(harness.system.postedKeystrokes.contains(.paste))
        XCTAssertEqual(typedText(harness.system.postedKeystrokes), dictation)
        XCTAssertEqual(harness.pasteboard.changeCount, changeCount, "Nothing may be written after a failed read.")
        XCTAssertNotNil(harness.pasteboard.data(forType: .html))
        XCTAssertEqual(harness.pasteboard.string(forType: .string), usersText)
    }

    @MainActor
    func testAFailedWriteWhoseRollbackAlsoFailsIsReportedAsLost() async {
        let failures = InjectionWriteFailures(2)
        let harness = InjectionHarness(operations: failures.operations())
        defer { harness.releasePasteboard() }
        harness.copyAsAnotherApplication(usersText)

        let result = await harness.injector.inject(text: dictation)

        XCTAssertEqual(result, InjectionResult(delivery: .typed, clipboard: .writeFailed, restore: .failed))
        XCTAssertEqual(
            harness.log.lines,
            ["Text injection finished: delivery=typed clipboard=writeFailed restore=failed."])
        XCTAssertEqual(TextInjector.logLevel(for: result).rawValue, OSLogType.error.rawValue)
        XCTAssertFalse(harness.system.postedKeystrokes.contains(.paste))
        XCTAssertTrue((harness.pasteboard.pasteboardItems ?? []).isEmpty, "The clear took the user's content.")
    }

    @MainActor
    func testAFailedWriteWhoseRollbackWorksPutsTheUsersTextBack() async {
        let failures = InjectionWriteFailures(1)
        let harness = InjectionHarness(operations: failures.operations())
        defer { harness.releasePasteboard() }
        harness.copyAsAnotherApplication(usersText)

        let result = await harness.injector.inject(text: dictation)

        XCTAssertEqual(result, InjectionResult(delivery: .typed, clipboard: .writeFailed, restore: .restored))
        XCTAssertEqual(harness.pasteboard.string(forType: .string), usersText)
    }

    @MainActor
    func testADeliveryCancelledBeforeItStartsSendsNothing() async {
        let harness = InjectionHarness()
        defer { harness.releasePasteboard() }
        harness.copyAsAnotherApplication(usersText)
        let changeCount = harness.pasteboard.changeCount
        harness.system.accessibilityOutcome = .inserted
        let observed = InjectionObservations()

        let delivery = Task { @MainActor in
            observed.first = await harness.injector.inject(text: dictation)
        }
        delivery.cancel()
        await delivery.value

        XCTAssertEqual(observed.first, InjectionResult(delivery: .cancelled))
        XCTAssertEqual(harness.system.focusQueries, 0)
        XCTAssertEqual(harness.system.accessibilityAttempts, 0)
        XCTAssertEqual(harness.pasteboard.changeCount, changeCount)
    }

    @MainActor
    func testCancellingBeforeCommandVUndoesTheBorrowAndSendsNothing() async {
        let harness = InjectionHarness()
        defer { harness.releasePasteboard() }
        harness.copyAsAnotherApplication(usersText)
        harness.pacer.holdNext = .beforePaste
        let observed = InjectionObservations()

        let delivery = Task { @MainActor in
            observed.first = await harness.injector.inject(text: dictation)
        }
        await yieldUntil { harness.pacer.isHolding }
        delivery.cancel()
        harness.pacer.releaseHeldPause()
        await delivery.value

        XCTAssertEqual(observed.first, InjectionResult(delivery: .cancelled, clipboard: .withheld, restore: .restored))
        XCTAssertTrue(harness.system.posted.isEmpty)
        XCTAssertEqual(harness.pasteboard.string(forType: .string), usersText)
    }

    @MainActor
    func testCancellingAfterCommandVStillSettlesAndRestores() async {
        let harness = InjectionHarness()
        defer { harness.releasePasteboard() }
        harness.copyAsAnotherApplication(usersText)
        harness.pacer.holdNext = .afterPaste
        let observed = InjectionObservations()

        let delivery = Task { @MainActor in
            observed.first = await harness.injector.inject(text: dictation)
        }
        await yieldUntil { harness.pacer.isHolding }
        delivery.cancel()
        harness.pacer.releaseHeldPause()
        await delivery.value

        XCTAssertEqual(observed.first, InjectionResult(delivery: .pasted, clipboard: .pasted, restore: .restored))
        XCTAssertEqual(harness.system.postedKeystrokes, [.paste])
        XCTAssertEqual(harness.pacer.pauses, [.beforePaste, .afterPaste])
        XCTAssertEqual(harness.pasteboard.string(forType: .string), usersText)
    }

    @MainActor
    func testCancellingWhileTypingStopsAtTheNextKeystroke() async {
        let harness = InjectionHarness()
        defer { harness.releasePasteboard() }
        harness.copyRichTextAsAnotherApplication(usersText)
        let observed = InjectionObservations()
        harness.pacer.onPause = { pause in
            if pause == .betweenKeystrokes {
                observed.task?.cancel()
            }
        }

        observed.task = Task { @MainActor in
            observed.first = await harness.injector.inject(text: dictation)
        }
        await yieldUntil { observed.first != nil }

        XCTAssertEqual(observed.first, InjectionResult(delivery: .typedPartially, clipboard: .nonTextContent))
        XCTAssertEqual(harness.system.posted.count, 1)
    }

    @MainActor
    func testACancelledDeliveryWaitingItsTurnSendsNothing() async {
        let harness = InjectionHarness()
        defer { harness.releasePasteboard() }
        harness.copyAsAnotherApplication(usersText)
        harness.pacer.holdNext = .beforePaste
        let observed = InjectionObservations()

        Task { @MainActor in
            observed.first = await harness.injector.inject(text: "The first dictation.")
        }
        await yieldUntil { harness.pacer.isHolding }
        let waiting = Task { @MainActor in
            observed.second = await harness.injector.inject(text: "The second dictation.")
        }
        await yieldUntil { harness.injector.queuedDeliveryCount == 1 }
        waiting.cancel()
        harness.pacer.releaseHeldPause()
        await waiting.value

        XCTAssertEqual(observed.first, InjectionResult(delivery: .pasted, clipboard: .pasted, restore: .restored))
        XCTAssertEqual(observed.second, InjectionResult(delivery: .cancelled))
        XCTAssertEqual(harness.system.postedKeystrokes, [.paste])
        XCTAssertEqual(harness.pasteboard.string(forType: .string), usersText)
    }

    @MainActor
    func testCancellingWhileTheFocusQuestionIsAnsweredStopsBeforeAccessibilityIsTried() async {
        let harness = InjectionHarness()
        defer { harness.releasePasteboard() }
        harness.copyAsAnotherApplication(usersText)
        let changeCount = harness.pasteboard.changeCount
        harness.system.accessibilityOutcome = .inserted
        harness.system.cancelsTaskOnFocusQuery = 1
        let observed = InjectionObservations()

        let delivery = Task { @MainActor in
            observed.first = await harness.injector.inject(text: dictation)
        }
        await delivery.value

        XCTAssertEqual(observed.first, InjectionResult(delivery: .cancelled))
        XCTAssertEqual(harness.system.accessibilityAttempts, 0)
        XCTAssertTrue(harness.system.posted.isEmpty)
        XCTAssertEqual(harness.pasteboard.changeCount, changeCount)
    }

    @MainActor
    func testAnInsertionCancelledBetweenItsQuestionsIsNotDelivered() async {
        let harness = InjectionHarness()
        defer { harness.releasePasteboard() }
        harness.copyAsAnotherApplication(usersText)
        let changeCount = harness.pasteboard.changeCount
        harness.system.accessibilityOutcome = .cancelled

        let result = await harness.injector.inject(text: dictation)

        XCTAssertEqual(result, InjectionResult(delivery: .cancelled))
        XCTAssertFalse(result.mayHaveReachedTarget)
        XCTAssertTrue(harness.system.posted.isEmpty)
        XCTAssertEqual(harness.pasteboard.changeCount, changeCount)
    }

    @MainActor
    func testCancellingWhileTheQuestionBeforeCommandVIsAnsweredUndoesTheBorrow() async {
        let harness = InjectionHarness()
        defer { harness.releasePasteboard() }
        harness.copyAsAnotherApplication(usersText)
        // The first focus question confirms the target; the second is the one asked before Command-V.
        harness.system.cancelsTaskOnFocusQuery = 2
        let observed = InjectionObservations()

        let delivery = Task { @MainActor in
            observed.first = await harness.injector.inject(text: dictation)
        }
        await delivery.value

        XCTAssertEqual(observed.first, InjectionResult(delivery: .cancelled, clipboard: .withheld, restore: .restored))
        XCTAssertTrue(harness.system.posted.isEmpty)
        XCTAssertEqual(harness.pasteboard.string(forType: .string), usersText)
    }

    @MainActor
    func testCancellingWhileAKeystrokeQuestionIsAnsweredStopsBeforeThatKeystroke() async {
        let harness = InjectionHarness()
        defer { harness.releasePasteboard() }
        harness.copyRichTextAsAnotherApplication(usersText)
        // Question one confirms the target, two comes before the first keystroke, three before the second.
        harness.system.cancelsTaskOnFocusQuery = 3
        let observed = InjectionObservations()

        let delivery = Task { @MainActor in
            observed.first = await harness.injector.inject(text: dictation)
        }
        await delivery.value

        XCTAssertEqual(observed.first, InjectionResult(delivery: .typedPartially, clipboard: .nonTextContent))
        XCTAssertEqual(harness.system.posted.count, 1)
    }

    @MainActor
    func testATargetWithoutAUsableProcessStillMatchesByBundle() async {
        let harness = InjectionHarness()
        defer { harness.releasePasteboard() }
        harness.system.accessibilityOutcome = .inserted
        let target = InjectionTarget(processIdentifier: -1, bundleIdentifier: InjectionHarness.editorBundle)

        let result = await harness.injector.inject(text: dictation, into: target)

        XCTAssertEqual(result, InjectionResult(delivery: .accessibility))
    }

    @MainActor
    func testACancelledDeliveryStillWaitsOutItsPause() async {
        let pacing = SystemInjectionPacing(beforePaste: .zero, afterPaste: .milliseconds(30), betweenKeystrokes: .zero)
        let clock = ContinuousClock()
        let start = clock.now

        let delivery = Task { @MainActor in
            await pacing.pause(.afterPaste)
        }
        delivery.cancel()
        await delivery.value

        XCTAssertGreaterThanOrEqual(clock.now - start, .milliseconds(30))
    }

    /// Yields the main actor until `condition` holds. Bounded by a count of yields rather than by time, so
    /// a broken ordering fails the test instead of hanging the run.
    @MainActor
    private func yieldUntil(
        _ condition: () -> Bool,
        file: StaticString = #filePath,
        line: UInt = #line
    ) async {
        var attempts = 0
        while !condition() && attempts < 10_000 {
            attempts += 1
            await Task.yield()
        }
        XCTAssertTrue(condition(), "The awaited state was never reached.", file: file, line: line)
    }

    private func typedText(_ keystrokes: [InjectionKeystroke]) -> String {
        keystrokes.map { keystroke -> String in
            switch keystroke {
            case .text(let units):
                return String(decoding: units, as: UTF16.self)
            case .lineBreak:
                return "\n"
            case .paste:
                return "<paste>"
            }
        }.joined()
    }
}
