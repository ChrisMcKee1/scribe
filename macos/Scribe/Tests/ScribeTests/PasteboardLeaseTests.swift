import AppKit
import XCTest
@testable import Scribe

/// Exercises `PasteboardBorrower` against real, privately named pasteboards, never the general one.
final class PasteboardLeaseTests: XCTestCase {
    private let dictation = "Dictated text headed for the target app."
    private let usersText = "The user's own clipboard text."

    @MainActor
    func testPlainTextCopiedTheUsualWayIsBorrowable() {
        let pasteboard = makePrivatePasteboard()
        defer { pasteboard.releaseGlobally() }
        copyAsAnotherApplication(usersText, to: pasteboard)

        let itemTypes = (pasteboard.pasteboardItems ?? []).map(\.types)
        XCTAssertEqual(
            PasteboardBorrower.classify(itemTypes, discountingOwnMarkers: false),
            .plainText,
            "A plain string copy exposed types outside the plain-text set: \(itemTypes)")
    }

    @MainActor
    func testBorrowWritesTheDictationWithPrivacyMarkers() throws {
        let pasteboard = makePrivatePasteboard()
        defer { pasteboard.releaseGlobally() }
        let borrower = PasteboardBorrower(pasteboard: pasteboard)

        let lease = try borrowed(borrower.borrow(for: dictation, contentReadable: true))

        XCTAssertEqual(pasteboard.string(forType: .string), dictation)
        let types = pasteboard.pasteboardItems?.first?.types ?? []
        XCTAssertTrue(types.contains(PasteboardBorrower.transientType))
        XCTAssertTrue(types.contains(PasteboardBorrower.concealedType))
        XCTAssertEqual(pasteboard.changeCount, lease.ownedChangeCount)
        XCTAssertTrue(borrower.stillHolds(lease))
    }

    @MainActor
    func testRestorePutsTheUsersTextBackPrivately() throws {
        let pasteboard = makePrivatePasteboard()
        defer { pasteboard.releaseGlobally() }
        copyAsAnotherApplication(usersText, to: pasteboard)
        let borrower = PasteboardBorrower(pasteboard: pasteboard)

        let lease = try borrowed(borrower.borrow(for: dictation, contentReadable: true))

        XCTAssertEqual(borrower.restore(lease), .restored)
        XCTAssertEqual(pasteboard.string(forType: .string), usersText)
        let types = pasteboard.pasteboardItems?.first?.types ?? []
        XCTAssertTrue(types.contains(PasteboardBorrower.transientType))
        XCTAssertTrue(types.contains(PasteboardBorrower.concealedType))
    }

    @MainActor
    func testRestoringAnEmptyPasteboardEmptiesItAgain() throws {
        let pasteboard = makePrivatePasteboard()
        defer { pasteboard.releaseGlobally() }
        let borrower = PasteboardBorrower(pasteboard: pasteboard)

        let lease = try borrowed(borrower.borrow(for: dictation, contentReadable: true))

        XCTAssertEqual(borrower.restore(lease), .restored)
        XCTAssertTrue((pasteboard.pasteboardItems ?? []).isEmpty)
        XCTAssertNil(pasteboard.string(forType: .string))
    }

    @MainActor
    func testRestoreLeavesANewerCopyAlone() throws {
        let pasteboard = makePrivatePasteboard()
        defer { pasteboard.releaseGlobally() }
        copyAsAnotherApplication(usersText, to: pasteboard)
        let borrower = PasteboardBorrower(pasteboard: pasteboard)
        let lease = try borrowed(borrower.borrow(for: dictation, contentReadable: true))

        copyAsAnotherApplication("A newer copy.", to: pasteboard)

        XCTAssertFalse(borrower.stillHolds(lease))
        XCTAssertEqual(borrower.restore(lease), .superseded)
        XCTAssertEqual(pasteboard.string(forType: .string), "A newer copy.")
    }

    @MainActor
    func testARestoreSettlesTheLeaseOnce() throws {
        let pasteboard = makePrivatePasteboard()
        defer { pasteboard.releaseGlobally() }
        copyAsAnotherApplication(usersText, to: pasteboard)
        let borrower = PasteboardBorrower(pasteboard: pasteboard)
        let lease = try borrowed(borrower.borrow(for: dictation, contentReadable: true))

        XCTAssertEqual(borrower.restore(lease), .restored)
        let afterRestore = pasteboard.changeCount

        XCTAssertEqual(borrower.restore(lease), .restored)
        XCTAssertEqual(pasteboard.changeCount, afterRestore)
        XCTAssertFalse(borrower.stillHolds(lease))
    }

    @MainActor
    func testRichTextIsNeverBorrowed() {
        let pasteboard = makePrivatePasteboard()
        defer { pasteboard.releaseGlobally() }
        let rtf = Data("{\\rtf1\\ansi Bold words}".utf8)
        writeItem(to: pasteboard) { item in
            item.setString("Bold words", forType: .string)
            item.setData(rtf, forType: .rtf)
        }
        let changeCount = pasteboard.changeCount

        let result = PasteboardBorrower(pasteboard: pasteboard).borrow(for: dictation, contentReadable: true)

        XCTAssertEqual(refusal(result), .nonTextContent)
        XCTAssertEqual(pasteboard.changeCount, changeCount)
        XCTAssertEqual(pasteboard.data(forType: .rtf), rtf)
    }

    @MainActor
    func testSeveralItemsAreNeverBorrowed() {
        let pasteboard = makePrivatePasteboard()
        defer { pasteboard.releaseGlobally() }
        pasteboard.clearContents()
        let first = NSPasteboardItem()
        first.setString("first", forType: .string)
        let second = NSPasteboardItem()
        second.setString("second", forType: .string)
        pasteboard.writeObjects([first, second])
        let changeCount = pasteboard.changeCount

        let result = PasteboardBorrower(pasteboard: pasteboard).borrow(for: dictation, contentReadable: true)

        XCTAssertEqual(refusal(result), .nonTextContent)
        XCTAssertEqual(pasteboard.changeCount, changeCount)
        XCTAssertEqual(pasteboard.pasteboardItems?.count, 2)
    }

    @MainActor
    func testAnotherApplicationsConcealedCopyIsNeverBorrowed() {
        let pasteboard = makePrivatePasteboard()
        defer { pasteboard.releaseGlobally() }
        writeItem(to: pasteboard) { item in
            item.setString("hunter2", forType: .string)
            item.setData(Data(), forType: PasteboardBorrower.concealedType)
        }
        let changeCount = pasteboard.changeCount

        let result = PasteboardBorrower(pasteboard: pasteboard).borrow(for: dictation, contentReadable: true)

        XCTAssertEqual(refusal(result), .nonTextContent)
        XCTAssertEqual(pasteboard.changeCount, changeCount)
        XCTAssertEqual(pasteboard.string(forType: .string), "hunter2")
    }

    @MainActor
    func testScribesOwnRestoreCanBeBorrowedAgainButAForeignMarkerCannot() throws {
        let pasteboard = makePrivatePasteboard()
        defer { pasteboard.releaseGlobally() }
        copyAsAnotherApplication(usersText, to: pasteboard)
        let borrower = PasteboardBorrower(pasteboard: pasteboard)

        let first = try borrowed(borrower.borrow(for: dictation, contentReadable: true))
        XCTAssertEqual(borrower.restore(first), .restored)

        let second = try borrowed(borrower.borrow(for: "The next dictation.", contentReadable: true))
        XCTAssertEqual(borrower.restore(second), .restored)
        XCTAssertEqual(pasteboard.string(forType: .string), usersText)

        writeItem(to: pasteboard) { item in
            item.setString("A password manager's copy", forType: .string)
            item.setData(Data(), forType: PasteboardBorrower.transientType)
            item.setData(Data(), forType: PasteboardBorrower.concealedType)
        }
        XCTAssertEqual(refusal(borrower.borrow(for: dictation, contentReadable: true)), .nonTextContent)
    }

    @MainActor
    func testPlainTextThatCannotBeReadSilentlyIsNotBorrowed() {
        let pasteboard = makePrivatePasteboard()
        defer { pasteboard.releaseGlobally() }
        copyAsAnotherApplication(usersText, to: pasteboard)
        let changeCount = pasteboard.changeCount

        let result = PasteboardBorrower(pasteboard: pasteboard).borrow(for: dictation, contentReadable: false)

        XCTAssertEqual(refusal(result), .unreadable)
        XCTAssertEqual(pasteboard.changeCount, changeCount)
        XCTAssertEqual(pasteboard.string(forType: .string), usersText)
    }

    @MainActor
    func testAnEmptyPasteboardNeedsNoReadToBorrow() throws {
        let pasteboard = makePrivatePasteboard()
        defer { pasteboard.releaseGlobally() }
        let borrower = PasteboardBorrower(pasteboard: pasteboard)

        let lease = try borrowed(borrower.borrow(for: dictation, contentReadable: false))

        XCTAssertEqual(pasteboard.string(forType: .string), dictation)
        XCTAssertEqual(borrower.restore(lease), .restored)
    }

    @MainActor
    func testAFailedItemReadIsRefusedWithoutWritingAnything() {
        let pasteboard = makePrivatePasteboard()
        defer { pasteboard.releaseGlobally() }
        writeItem(to: pasteboard) { item in
            item.setString("Bold words", forType: .string)
            item.setData(Data("<b>Bold words</b>".utf8), forType: .html)
        }
        let changeCount = pasteboard.changeCount
        var operations = PasteboardOperations.live
        operations.itemTypes = { _ in nil }
        let borrower = PasteboardBorrower(pasteboard: pasteboard, operations: operations)

        let result = borrower.borrow(for: dictation, contentReadable: true)

        XCTAssertEqual(refusal(result), .unreadable)
        XCTAssertEqual(pasteboard.changeCount, changeCount)
        XCTAssertNotNil(pasteboard.data(forType: .html))
    }

    @MainActor
    func testAFailedWriteIsRolledBackAndSaysSo() {
        let pasteboard = makePrivatePasteboard()
        defer { pasteboard.releaseGlobally() }
        copyAsAnotherApplication(usersText, to: pasteboard)
        let failures = InjectionWriteFailures(1)
        let borrower = PasteboardBorrower(pasteboard: pasteboard, operations: failures.operations())

        let result = borrower.borrow(for: dictation, contentReadable: true)

        XCTAssertEqual(refusal(result), .writeFailed)
        XCTAssertEqual(rollback(result), .restored)
        XCTAssertEqual(pasteboard.string(forType: .string), usersText)
    }

    @MainActor
    func testAFailedRollbackIsReportedAsFailedNotAsUntouched() {
        let pasteboard = makePrivatePasteboard()
        defer { pasteboard.releaseGlobally() }
        copyAsAnotherApplication(usersText, to: pasteboard)
        let failures = InjectionWriteFailures(2)
        let borrower = PasteboardBorrower(pasteboard: pasteboard, operations: failures.operations())

        let result = borrower.borrow(for: dictation, contentReadable: true)

        XCTAssertEqual(refusal(result), .writeFailed)
        XCTAssertEqual(rollback(result), .failed)
        XCTAssertTrue((pasteboard.pasteboardItems ?? []).isEmpty, "The clear before the failed write took it.")
    }

    @MainActor
    func testACopyBetweenTheSnapshotAndTheWriteIsNotOverwritten() {
        let pasteboard = makePrivatePasteboard()
        defer { pasteboard.releaseGlobally() }
        copyAsAnotherApplication(usersText, to: pasteboard)
        let borrower = PasteboardBorrower(pasteboard: pasteboard)
        guard case .captured(let snapshot) = borrower.snapshot(contentReadable: true) else {
            return XCTFail("Expected the plain-text pasteboard to be captured.")
        }

        copyAsAnotherApplication("Copied while Scribe was reading.", to: pasteboard)
        let changeCount = pasteboard.changeCount

        XCTAssertEqual(refusal(borrower.replace(snapshot, with: dictation)), .contended)
        XCTAssertEqual(pasteboard.changeCount, changeCount)
        XCTAssertEqual(pasteboard.string(forType: .string), "Copied while Scribe was reading.")
    }

    @MainActor
    func testAUserCopyStaysOnThisMacWithoutTransientMarkers() {
        let pasteboard = makePrivatePasteboard()
        defer { pasteboard.releaseGlobally() }

        XCTAssertTrue(PasteboardBorrower.copyForUser("Recovered dictation.", to: pasteboard))

        XCTAssertEqual(pasteboard.string(forType: .string), "Recovered dictation.")
        let types = pasteboard.pasteboardItems?.first?.types ?? []
        XCTAssertFalse(types.contains(PasteboardBorrower.transientType))
        XCTAssertFalse(types.contains(PasteboardBorrower.concealedType))
    }

    @MainActor
    func testPrivatePasteboardsCanBeReadWithoutAPrompt() {
        let pasteboard = makePrivatePasteboard()
        defer { pasteboard.releaseGlobally() }

        XCTAssertTrue(PasteboardBorrower.allowsReadingWithoutPrompt(pasteboard))
    }

    @MainActor
    func testClassificationAllowsOnlyAnEmptyPasteboardOrOnePlainTextItem() {
        let transient = PasteboardBorrower.transientType
        let concealed = PasteboardBorrower.concealedType
        let legacyString = NSPasteboard.PasteboardType("NSStringPboardType")
        let remote = NSPasteboard.PasteboardType("com.apple.is-remote-clipboard")

        func classify(_ itemTypes: [[NSPasteboard.PasteboardType]], ownMarkers: Bool = false) -> PasteboardContentKind {
            PasteboardBorrower.classify(itemTypes, discountingOwnMarkers: ownMarkers)
        }

        XCTAssertEqual(classify([]), .empty)
        XCTAssertEqual(classify([[.string]]), .plainText)
        XCTAssertEqual(classify([[.string, legacyString]]), .plainText)
        XCTAssertEqual(classify([[NSPasteboard.PasteboardType("public.utf16-external-plain-text")]]), .plainText)
        XCTAssertEqual(classify([[.string, .html]]), .other)
        XCTAssertEqual(classify([[.string, .rtf]]), .other)
        XCTAssertEqual(classify([[.fileURL]]), .other)
        XCTAssertEqual(classify([[.string, remote]]), .other)
        XCTAssertEqual(classify([[.string], [.string]]), .other)
        XCTAssertEqual(classify([[]]), .other)
        XCTAssertEqual(classify([[.string, transient, concealed]]), .other)
        XCTAssertEqual(classify([[.string, transient, concealed]], ownMarkers: true), .plainText)
        XCTAssertEqual(classify([[transient, concealed]], ownMarkers: true), .other)
    }

    @MainActor
    private func makePrivatePasteboard() -> NSPasteboard {
        NSPasteboard(name: NSPasteboard.Name("com.scribe.macos.tests.pasteboard-lease.\(UUID().uuidString)"))
    }

    @MainActor
    private func copyAsAnotherApplication(_ text: String, to pasteboard: NSPasteboard) {
        pasteboard.clearContents()
        pasteboard.setString(text, forType: .string)
    }

    @MainActor
    private func writeItem(to pasteboard: NSPasteboard, _ fill: (NSPasteboardItem) -> Void) {
        pasteboard.clearContents()
        let item = NSPasteboardItem()
        fill(item)
        pasteboard.writeObjects([item])
    }

    private func borrowed(
        _ result: PasteboardBorrowResult,
        file: StaticString = #filePath,
        line: UInt = #line
    ) throws -> PasteboardLease {
        guard case .borrowed(let lease) = result else {
            let reason = refusal(result)?.rawValue ?? "unknown"
            return try XCTUnwrap(
                nil as PasteboardLease?,
                "Expected the pasteboard to be lent, but it was refused as \(reason).",
                file: file,
                line: line)
        }
        return lease
    }

    private func refusal(_ result: PasteboardBorrowResult) -> PasteboardBorrowRefusal? {
        guard case .refused(let refusal, _) = result else {
            return nil
        }
        return refusal
    }

    private func rollback(_ result: PasteboardBorrowResult) -> ClipboardRestoreOutcome? {
        guard case .refused(_, let rollback) = result else {
            return nil
        }
        return rollback
    }
}
