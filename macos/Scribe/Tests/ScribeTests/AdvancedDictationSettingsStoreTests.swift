import XCTest

@testable import Scribe

final class AdvancedDictationSettingsStoreTests: XCTestCase {
    private var suite: SettingsTestDefaults!
    private var store: AdvancedDictationSettingsStore!

    override func setUpWithError() throws {
        try super.setUpWithError()
        suite = try SettingsTestDefaults()
        store = AdvancedDictationSettingsStore(defaults: suite.defaults)
    }

    override func tearDown() {
        suite.remove()
        suite = nil
        store = nil
        super.tearDown()
    }

    func testLineBreaksDefaultToSmartFlatten() {
        XCTAssertEqual(store.newlineMode, .smartFlatten)
    }

    func testLineBreaksRoundTripThroughUserDefaults() {
        store.newlineMode = .keepNewlines
        XCTAssertEqual(AdvancedDictationSettingsStore(defaults: suite.defaults).newlineMode, .keepNewlines)

        store.newlineMode = .alwaysFlatten
        XCTAssertEqual(AdvancedDictationSettingsStore(defaults: suite.defaults).newlineMode, .alwaysFlatten)
    }

    func testInvalidStoredLineBreakModeFallsBackToSmartFlatten() {
        suite.defaults.set("paragraphParty", forKey: "ScribeNewlineMode")
        XCTAssertEqual(store.newlineMode, .smartFlatten)
    }

    @MainActor
    func testLiveConfigurationReadsStoredLineBreakMode() {
        store.newlineMode = .keepNewlines

        let configuration = DictationControllerConfigurationFactory.live(
            advanced: store,
            toggleKeyStopsOnSilence: { true })

        XCTAssertEqual(configuration.newlineMode, .keepNewlines)
        XCTAssertTrue(configuration.toggleKeyStopsOnSilence())
    }
}
