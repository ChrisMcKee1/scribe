import XCTest

@testable import Scribe

final class TypingSettingsModelTests: XCTestCase {
    private var suite: SettingsTestDefaults!
    private var store: TypingSettingsStore!

    override func setUpWithError() throws {
        try super.setUpWithError()
        suite = try SettingsTestDefaults()
        store = TypingSettingsStore(defaults: suite.defaults)
    }

    override func tearDown() {
        suite.remove()
        suite = nil
        store = nil
        super.tearDown()
    }

    @MainActor
    func testTheTrailingSpaceSettingDefaultsToOnAndStoresChangesAtOnce() {
        let model = TypingSettingsModel(store: store)

        XCTAssertTrue(store.addSpaceAfterDictation)
        XCTAssertTrue(model.addSpaceAfterDictation)

        model.setAddSpaceAfterDictation(false)
        XCTAssertFalse(store.addSpaceAfterDictation)
        XCTAssertFalse(model.addSpaceAfterDictation)

        model.setAddSpaceAfterDictation(true)
        XCTAssertTrue(store.addSpaceAfterDictation)
        XCTAssertTrue(model.addSpaceAfterDictation)
    }

    @MainActor
    func testAppAwareFormattingDefaultsToOnWithPlainProfilesAndStoresChangesAtOnce() {
        let model = TypingSettingsModel(store: store)

        XCTAssertTrue(model.appAwareFormatting)
        XCTAssertEqual(model.defaultTextFormat, .plain)

        model.setAppAwareFormatting(false)
        model.setDefaultTextFormat(.markdownSource)
        XCTAssertEqual(
            store.formatting, DictationFormattingSettings(appAwareFormatting: false, defaultTextFormat: .markdownSource)
        )
        XCTAssertFalse(model.appAwareFormatting)
        XCTAssertEqual(model.defaultTextFormat, .markdownSource)

        store.defaultTextFormat = .markdownWithFormatting
        XCTAssertEqual(model.defaultTextFormat, .markdownWithFormatting)
    }

    @MainActor
    func testTheLiveConfigurationReadsTheFormattingSettingsForEachRecording() {
        let configuration = DictationControllerConfigurationFactory.live(
            advanced: AdvancedDictationSettingsStore(defaults: suite.defaults), typing: store)

        XCTAssertEqual(configuration.formatting(), DictationFormattingSettings())
        store.appAwareFormatting = false
        XCTAssertFalse(configuration.formatting().appAwareFormatting)
    }

    @MainActor
    func testAChangeStoredElsewhereIsShownWithoutReopeningTheSection() {
        let model = TypingSettingsModel(store: store)
        XCTAssertTrue(model.addSpaceAfterDictation)

        store.addSpaceAfterDictation = false

        XCTAssertFalse(model.addSpaceAfterDictation)
    }
}
