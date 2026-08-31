import XCTest
@testable import Scribe

final class AppProfileMatcherTests: XCTestCase {
    private let terminalProfile = AppProfile(
        name: "Terminal",
        bundleIdentifiers: ["com.apple.Terminal"],
        processNames: ["Terminal"],
        writingStylePrompt: "Be terse.",
        newlineHandling: .alwaysFlatten)

    private let emailProfile = AppProfile(
        name: "Email",
        bundleIdentifiers: ["com.apple.mail"],
        processNames: ["Mail"],
        writingStylePrompt: "Be formal.",
        newlineHandling: .keepNewlines)

    func testMatchesByBundleIdentifier() {
        let matched = AppProfileMatcher.match(
            profiles: [terminalProfile, emailProfile],
            bundleIdentifier: "com.apple.mail",
            processName: nil)
        XCTAssertEqual(matched?.name, "Email")
    }

    func testMatchIsCaseInsensitive() {
        let matched = AppProfileMatcher.match(
            profiles: [terminalProfile],
            bundleIdentifier: "COM.APPLE.TERMINAL",
            processName: nil)
        XCTAssertEqual(matched?.name, "Terminal")
    }

    func testFallsBackToProcessNameWhenNoBundleMatch() {
        let matched = AppProfileMatcher.match(
            profiles: [terminalProfile],
            bundleIdentifier: "com.unknown.app",
            processName: "Terminal")
        XCTAssertEqual(matched?.name, "Terminal")
    }

    func testReturnsNilWhenNoMatch() {
        let matched = AppProfileMatcher.match(
            profiles: [terminalProfile, emailProfile],
            bundleIdentifier: "com.unknown.app",
            processName: "Unknown")
        XCTAssertNil(matched)
    }

    func testFirstMatchWinsInConfiguredOrder() {
        let duplicate = AppProfile(
            name: "Duplicate",
            bundleIdentifiers: ["com.apple.Terminal"],
            processNames: [],
            writingStylePrompt: nil,
            newlineHandling: nil)
        let matched = AppProfileMatcher.match(
            profiles: [terminalProfile, duplicate],
            bundleIdentifier: "com.apple.Terminal",
            processName: nil)
        XCTAssertEqual(matched?.name, "Terminal")
    }

    func testResolveNewlineModeUsesProfileOverride() {
        let mode = AppProfileMatcher.resolveNewlineMode(
            profile: emailProfile,
            globalDefault: .alwaysFlatten,
            bundleIdentifier: "com.apple.mail")
        XCTAssertEqual(mode, .keepNewlines)
    }

    func testResolveNewlineModeFallsBackToGlobalDefault() {
        let mode = AppProfileMatcher.resolveNewlineMode(
            profile: nil,
            globalDefault: .alwaysFlatten,
            bundleIdentifier: "com.unknown.app")
        XCTAssertEqual(mode, .alwaysFlatten)
    }

    func testApplyNewlineModeAlwaysFlattenReplacesLineBreaks() {
        let result = AppProfileMatcher.applyNewlineMode(.alwaysFlatten, to: "one\ntwo\r\nthree", bundleIdentifier: nil)
        XCTAssertEqual(result, "one two three")
    }

    func testApplyNewlineModeKeepNewlinesPreservesText() {
        let result = AppProfileMatcher.applyNewlineMode(.keepNewlines, to: "one\ntwo", bundleIdentifier: nil)
        XCTAssertEqual(result, "one\ntwo")
    }

    func testApplyNewlineModeSmartFlattenFlattensKnownTerminal() {
        let result = AppProfileMatcher.applyNewlineMode(.smartFlatten, to: "one\ntwo", bundleIdentifier: "com.apple.Terminal")
        XCTAssertEqual(result, "one two")
    }

    func testApplyNewlineModeSmartFlattenKeepsNewlinesForNonTerminal() {
        let result = AppProfileMatcher.applyNewlineMode(.smartFlatten, to: "one\ntwo", bundleIdentifier: "com.apple.mail")
        XCTAssertEqual(result, "one\ntwo")
    }
}
