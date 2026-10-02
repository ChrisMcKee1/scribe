import Foundation
import XCTest

@testable import Scribe

final class SettingsFormatTests: XCTestCase {
    private let english = Locale(identifier: "en_US")
    private let german = Locale(identifier: "de_DE")

    func testNumbersFollowTheLocalesGrouping() {
        XCTAssertEqual(SettingsFormat.number(1_200, locale: english), "1,200")
        XCTAssertEqual(SettingsFormat.number(1_200, locale: german), "1.200")
    }

    func testACountAndItsNounAgree() {
        XCTAssertEqual(SettingsFormat.words(0, locale: english), "0 words")
        XCTAssertEqual(SettingsFormat.words(1, locale: english), "1 word")
        XCTAssertEqual(SettingsFormat.words(1_549, locale: english), "1,549 words")
        let one = SettingsFormat.quantity(1, one: "dictation", other: "dictations", locale: english)
        XCTAssertEqual(one, "1 dictation")
    }

    func testSizesUseTheLocalesDecimalSeparator() {
        XCTAssertTrue(SettingsFormat.byteSize(1_500_000, locale: english).contains("1.5"))
        XCTAssertTrue(SettingsFormat.byteSize(1_500_000, locale: german).contains("1,5"))
        XCTAssertTrue(SettingsFormat.byteSize(1_500_000, locale: english).contains("MB"))
    }

    func testDurationsAreWholeWords() {
        XCTAssertEqual(SettingsFormat.duration(seconds: 30, locale: english), "30 seconds")
        XCTAssertEqual(SettingsFormat.duration(seconds: 600, locale: english), "10 minutes")
        XCTAssertEqual(SettingsFormat.duration(seconds: 3_600, locale: english), "1 hour")
        XCTAssertTrue(SettingsFormat.duration(seconds: 90, locale: english).contains("1 minute"))
    }

    func testDatesReadAsTheRegionWritesThem() {
        let date = Date(timeIntervalSince1970: 1_700_000_000)
        let utc = TimeZone(identifier: "UTC") ?? .current
        let us = SettingsFormat.dateTime(date, locale: english, timeZone: utc)
        let de = SettingsFormat.dateTime(date, locale: german, timeZone: utc)
        XCTAssertTrue(us.contains("Nov"))
        XCTAssertNotEqual(us, de)
    }

    func testListsReadAsTheWindowsWindowWritesThemInEnglish() {
        XCTAssertTrue(SettingsFormat.list([], locale: english).isEmpty)
        XCTAssertEqual(SettingsFormat.list(["Dictionary"], locale: english), "Dictionary")
        XCTAssertEqual(SettingsFormat.list(["Word packs", "Dictionary"], locale: english), "Word packs and Dictionary")
        XCTAssertEqual(SettingsFormat.list(["A", "B", "C"], locale: english), "A, B and C")
        XCTAssertFalse(SettingsFormat.list(["A", "B", "C"], locale: german).isEmpty)
    }
}
