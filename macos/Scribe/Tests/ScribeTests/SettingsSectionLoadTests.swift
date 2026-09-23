import XCTest
@testable import Scribe

/// The Windows `SettingsSectionLoadTests` that apply on macOS: the ticket rules. Windows' saved-state snapshot and
/// `Invalidate` are not ported (see `SettingsSectionLoad`).
final class SettingsSectionLoadTests: XCTestCase {
    func testASectionStartsUnloadedAndLoadsThroughItsTicket() {
        var section = SettingsSectionLoad()
        XCTAssertEqual(section.state, .unloaded)
        XCTAssertFalse(section.isLoaded)

        let ticket = section.begin()
        XCTAssertEqual(section.state, .loading)
        XCTAssertTrue(section.canPublish(ticket))

        XCTAssertTrue(section.publish(ticket))
        XCTAssertTrue(section.isLoaded)
    }

    func testAFailedLoadCanBeRetried() {
        var section = SettingsSectionLoad()
        let ticket = section.begin()

        XCTAssertTrue(section.fail(ticket))
        XCTAssertEqual(section.state, .failed)
        XCTAssertFalse(section.isLoaded)

        let retry = section.begin()
        XCTAssertTrue(section.publish(retry))
        XCTAssertTrue(section.isLoaded)
    }

    func testOnlyTheNewestReadPublishesWhateverOrderReadsFinishIn() {
        var section = SettingsSectionLoad()
        let first = section.begin()
        let second = section.begin()

        XCTAssertTrue(section.publish(second))
        XCTAssertFalse(section.publish(first))
        XCTAssertFalse(section.fail(first))
        XCTAssertTrue(section.isLoaded)
    }

    func testAStaleFailureNeverOverridesTheNewerRead() {
        var section = SettingsSectionLoad()
        let first = section.begin()
        let second = section.begin()

        XCTAssertFalse(section.fail(first))
        XCTAssertEqual(section.state, .loading)
        XCTAssertTrue(section.canPublish(second))
    }

    func testASettledReadCannotPublishAgainButARefreshCan() {
        var section = SettingsSectionLoad()
        let ticket = section.begin()
        XCTAssertTrue(section.publish(ticket))

        XCTAssertFalse(section.publish(ticket))
        XCTAssertFalse(section.fail(ticket))

        let refresh = section.begin()
        XCTAssertFalse(section.isLoaded)
        XCTAssertTrue(section.publish(refresh))
        XCTAssertTrue(section.isLoaded)
    }
}
