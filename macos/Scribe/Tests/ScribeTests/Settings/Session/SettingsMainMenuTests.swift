import AppKit
import XCTest

@testable import Scribe

@MainActor
final class SettingsMainMenuTests: XCTestCase {
    func testStandardEditCommandsUseResponderChainAndNoEscapeCommandExists() {
        let target = makeTarget()
        let menu = target.makeMenu()
        let items = menu.items.flatMap { $0.submenu?.items ?? [] }
        for key in ["c", "v", "x", "a", "z"] {
            let item = items.first { $0.keyEquivalent == key }
            XCTAssertNotNil(item)
            XCTAssertNil(item?.target)
        }
        XCTAssertFalse(items.contains { $0.keyEquivalent == "\u{1B}" })
        XCTAssertNotNil(items.first { $0.keyEquivalent == "," })
        XCTAssertNotNil(items.first { $0.keyEquivalent == "s" })
        XCTAssertNotNil(items.first { $0.keyEquivalent == "w" })
    }

    func testSaveAndCloseAreDisabledOutsideSettingsOrDuringCapture() throws {
        let state = MainMenuTestState()
        let target = makeTarget(state)
        let items = target.makeMenu().items.flatMap { $0.submenu?.items ?? [] }
        let save = try XCTUnwrap(items.first { $0.keyEquivalent == "s" })
        let close = try XCTUnwrap(items.first { $0.keyEquivalent == "w" })
        XCTAssertFalse(target.validateMenuItem(save))
        state.settingsIsKey = true
        XCTAssertTrue(target.validateMenuItem(save))
        state.capturing = true
        XCTAssertFalse(target.validateMenuItem(save))
        XCTAssertFalse(target.validateMenuItem(close))
    }

    private func makeTarget(_ state: MainMenuTestState = MainMenuTestState()) -> SettingsMainMenu {
        SettingsMainMenu(
            commands: SettingsMainMenuCommands(
                settingsIsKey: { state.settingsIsKey },
                ownership: { SettingsKeyboardOwnership(shortcutCapture: state.capturing) },
                openSettings: {},
                save: {},
                close: {},
                quit: {}))
    }
}

@MainActor
private final class MainMenuTestState {
    var settingsIsKey = false
    var capturing = false
}
