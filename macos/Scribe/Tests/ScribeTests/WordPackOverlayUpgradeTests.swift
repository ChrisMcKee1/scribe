import XCTest

@testable import Scribe

final class WordPackOverlayUpgradeTests: XCTestCase {
    func testEditedFieldsInheritOnlyUntouchedFieldsAndReviewAsksOncePerShippedChange() throws {
        let original = TermValues("get hub", "GitHub")
        let initial = Self.shipped(original)
        let row = BuiltInLibraryOverlay.applyRows(shipped: initial, edits: nil)[0]
        let edited = BuiltInLibraryOverlay.edit(row, values: TermValues("git hub", "Enterprise"))
        let document = try BuiltInLibraryOverlay.collect(shipped: initial, committed: nil, rows: [edited])
        let updated = Self.shipped(TermValues("get hub", "GitHub, Inc.", false))
        let changed = BuiltInLibraryOverlay.applyRows(shipped: updated, edits: document)[0]
        XCTAssertEqual(changed.values, TermValues("git hub", "Enterprise", false))
        XCTAssertEqual(changed.review?.differing, [.spoken, .written])
        let kept = BuiltInLibraryOverlay.resolveReview(changed, choice: .keepMine)
        XCTAssertNil(kept.review)
        let acknowledged = try BuiltInLibraryOverlay.collect(shipped: updated, committed: document, rows: [kept])
        XCTAssertNil(BuiltInLibraryOverlay.applyRows(shipped: updated, edits: acknowledged)[0].review)
        let latest = Self.shipped(TermValues("get hub", "GitHub Corp", false))
        XCTAssertNotNil(BuiltInLibraryOverlay.applyRows(shipped: latest, edits: acknowledged)[0].review)
        let useUpdated = BuiltInLibraryOverlay.resolveReview(
            BuiltInLibraryOverlay.applyRows(shipped: latest, edits: acknowledged)[0], choice: .useUpdated)
        XCTAssertEqual(useUpdated.values, latest.entries.map { TermValues(entry: $0) }[0])
        XCTAssertEqual(useUpdated.origin, .pinned)
    }

    func testEditingAnOffRowWhileShippedOffNeverRevivesItWhenShippedOnAgain() throws {
        let on = Self.shipped(TermValues("term", "Term"))
        let off = BuiltInLibraryOverlay.setEnabled(
            BuiltInLibraryOverlay.applyRows(shipped: on, edits: nil)[0], enabled: false)
        let first = try BuiltInLibraryOverlay.collect(shipped: on, committed: nil, rows: [off])
        let shipsOff = Self.shipped(TermValues("term", "Term", true, false))
        let displayed = BuiltInLibraryOverlay.applyRows(shipped: shipsOff, edits: first)[0]
        let edited = BuiltInLibraryOverlay.edit(displayed, values: TermValues("term", "Mine", true, false))
        let second = try BuiltInLibraryOverlay.collect(shipped: shipsOff, committed: first, rows: [edited])
        let back = BuiltInLibraryOverlay.applyRows(shipped: on, edits: second)[0]
        XCTAssertFalse(back.values.enabled)
        XCTAssertEqual(back.values.written, "Mine")
        XCTAssertNil(back.review)
    }

    func testInertOffEntrySurvivesDisappearanceCollectionAndReturn() throws {
        let original = Self.shipped(TermValues("term", "Term"))
        let off = BuiltInLibraryOverlay.setEnabled(
            BuiltInLibraryOverlay.applyRows(shipped: original, edits: nil)[0], enabled: false)
        let first = try BuiltInLibraryOverlay.collect(shipped: original, committed: nil, rows: [off])
        let empty = DictionaryLibrary(
            id: "github", name: "GitHub", category: "Built-in", description: nil, builtIn: true, entries: [])
        let hidden = BuiltInLibraryOverlay.applyRows(shipped: empty, edits: first)
        XCTAssertTrue(hidden.isEmpty)
        let second = try BuiltInLibraryOverlay.collect(shipped: empty, committed: first, rows: hidden)
        XCTAssertEqual(second, first)
        XCTAssertFalse(BuiltInLibraryOverlay.applyRows(shipped: original, edits: second)[0].values.enabled)
    }

    func testLayoutPlannerBoundsRowsAndFallsBackForLargeText() {
        let ordinary = LibraryLayoutPlanner.plan(LibraryLayoutInput(contentWidth: 672, height: 660))
        XCTAssertTrue(ordinary.sideBySide)
        XCTAssertTrue(ordinary.short)
        let compact = LibraryLayoutPlanner.plan(LibraryLayoutInput(contentWidth: 500, height: 400, textScale: 2.25))
        XCTAssertFalse(compact.sideBySide)
        XCTAssertTrue(compact.horizontalOverflow)
        XCTAssertTrue(compact.verticalOverflow)
        XCTAssertEqual(compact.visibleTermRows, 4)
    }

    private static func shipped(_ values: TermValues) -> DictionaryLibrary {
        DictionaryLibrary(
            id: "github", name: "GitHub", category: "Built-in", description: nil,
            builtIn: true, entries: [values.dictionaryEntry])
    }
}
