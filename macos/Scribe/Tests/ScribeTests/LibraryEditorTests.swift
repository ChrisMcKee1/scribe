import XCTest

@testable import Scribe

final class LibraryEditorTests: XCTestCase {
    func testCommitSpokenTrimsAndCollapsesWhitespace() {
        XCTAssertEqual(LibraryEditor.commitSpoken("  get   hub "), "get hub")
        XCTAssertEqual(LibraryEditor.commitSpoken("get\u{00A0}\thub"), "get hub")
        XCTAssertEqual(LibraryEditor.commitSpoken("Get Hub"), "Get Hub")
        XCTAssertEqual(LibraryEditor.commitSpoken(nil), "")
        XCTAssertEqual(LibraryEditor.commitSpoken("   "), "")
    }

    func testCommitWrittenOnlyTrimsOuterWhitespace() {
        XCTAssertEqual(LibraryEditor.commitWritten("  Line one\r\n  Line  two \t"), "Line one\r\n  Line  two")
        XCTAssertEqual(LibraryEditor.commitWritten(nil), "")
        XCTAssertEqual(
            LibraryEditor.commit(TermValues(" get  hub", " GitHub ", false, false)),
            TermValues("get hub", "GitHub", false, false))
    }

    func testCommitChangesPreservesUntouchedDisplayedFields() {
        let displayed = TermValues("get  hub", " GitHub", true, true)

        XCTAssertEqual(
            LibraryEditor.commitChanges(displayed: displayed, typed: displayed),
            displayed)
        XCTAssertEqual(
            LibraryEditor.commitChanges(
                displayed: displayed,
                typed: TermValues("get  hub", " GH ", true, true)),
            TermValues("get  hub", "GH", true, true))
        XCTAssertEqual(
            LibraryEditor.commitChanges(
                displayed: displayed,
                typed: TermValues(" get hub ", " GitHub", true, true)),
            TermValues("get hub", " GitHub", true, true))
    }

    func testIsWellFormedRejectsUnpairedSurrogates() {
        func utf16(_ units: [UInt16]) -> String {
            String(utf16CodeUnits: units, count: units.count)
        }

        XCTAssertTrue(LibraryEditor.isWellFormed("plain"))
        XCTAssertTrue(LibraryEditor.isWellFormed("rocket \u{1F680} launch"))
        XCTAssertTrue(LibraryEditor.isWellFormed(nil))
        XCTAssertFalse(LibraryEditor.isWellFormed(utf16([0xD83D])))
        XCTAssertFalse(LibraryEditor.isWellFormed(utf16([0x0068, 0xD83D, 0x0020])))
        XCTAssertFalse(LibraryEditor.isWellFormed(utf16([0xDE80])))
    }

    func testAvailableCommandsMatchRowOriginAndEditingState() {
        let shipped = TermValues("get hub", "GitHub")
        let custom = LibraryRow.custom(shipped)
        let builtIn = LibraryRow(values: shipped, origin: .shipped, shipped: shipped)
        let edited = LibraryRow(values: TermValues("get hub", "GH"), origin: .edited, shipped: shipped)
        let off = LibraryRow(values: TermValues("get hub", "GitHub", true, false), origin: .off, shipped: shipped)
        let added = LibraryRow(values: shipped, origin: .added)
        let copies: TermCommands = [.copy, .copyToDictionary]

        XCTAssertEqual(
            LibraryEditor.availableCommands(for: custom, editingText: false),
            copies.union(.turnOff).union(.delete))
        XCTAssertEqual(
            LibraryEditor.availableCommands(for: builtIn, editingText: false),
            copies.union(.turnOff))
        XCTAssertEqual(
            LibraryEditor.availableCommands(for: edited, editingText: false),
            copies.union(.turnOff).union(.restoreBuiltIn))
        XCTAssertEqual(
            LibraryEditor.availableCommands(for: off, editingText: false),
            copies.union(.turnOn).union(.showOtherSources))
        XCTAssertEqual(
            LibraryEditor.availableCommands(for: added, editingText: false),
            copies.union(.turnOff).union(.delete))

        for row in [custom, builtIn, edited, off, added] {
            let commands = LibraryEditor.availableCommands(for: row, editingText: true)
            XCTAssertFalse(commands.contains(.delete))
            XCTAssertFalse(commands.contains(.turnOff))
            XCTAssertFalse(commands.contains(.turnOn))
            XCTAssertTrue(commands.contains(.copy))
        }
    }

    func testMessagesMatchExpectedWording() {
        func issue(_ kind: LibraryValidationKind, _ metadata: LibraryMetadataField = .none) -> LibraryValidationIssue {
            LibraryValidationIssue(libraryID: "team-terms", rowID: 1, kind: kind, metadata: metadata)
        }

        XCTAssertEqual(
            LibraryEditor.message(for: issue(.emptyWrittenWithoutIntent), spoken: " get  hub "),
            "Type how \"get hub\" should be written.")
        XCTAssertEqual(
            LibraryEditor.message(for: issue(.duplicateSpoken), spoken: "get hub", otherSpoken: "git  hub"),
            "\"get hub\" is already in this word pack as the term you changed to \"git hub\".")
        XCTAssertEqual(
            LibraryEditor.message(for: issue(.fieldTooLong)),
            "This is longer than 2,000 characters. Shorten it to save.")
        XCTAssertEqual(
            LibraryEditor.message(for: issue(.tooManyTerms)),
            "A word pack can hold up to 50,000 terms.")
        XCTAssertEqual(
            LibraryEditor.message(for: issue(.metadataDoubleQuote, .name)),
            LibraryEditor.metadataDoubleQuoteMessage)
        XCTAssertTrue(
            LibraryEditor.message(for: issue(.contentNotSaveable), state: .partlyReadable)
                .contains("Import the file again"))
        XCTAssertTrue(
            LibraryEditor.message(for: issue(.contentNotSaveable), state: .awaitingRelease)
                .contains("open in another app"))
    }
}
