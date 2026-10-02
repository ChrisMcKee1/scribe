import AppKit

struct SettingsMainMenuCommands {
    var settingsIsKey: @MainActor () -> Bool
    var ownership: @MainActor () -> SettingsKeyboardOwnership = { SettingsKeyboardOwnership() }
    var openSettings: @MainActor () -> Void
    var save: @MainActor () -> Void
    var close: @MainActor () -> Void
    var quit: @MainActor () -> Void
}

/// Retain this target beside the app delegate; NSMenuItem does not own its target.
@MainActor
final class SettingsMainMenu: NSObject, NSMenuItemValidation {
    let commands: SettingsMainMenuCommands

    init(commands: SettingsMainMenuCommands) {
        self.commands = commands
    }

    func install(in application: NSApplication) {
        application.mainMenu = makeMenu()
    }

    func makeMenu() -> NSMenu {
        let root = NSMenu()
        let app = NSMenu(title: "Scribe")
        add("Settings...", action: #selector(openSettings), key: ",", to: app, target: self)
        app.addItem(.separator())
        add("Quit Scribe", action: #selector(quit), key: "q", to: app, target: self)
        append(app, to: root)

        let edit = NSMenu(title: "Edit")
        add("Undo", action: NSSelectorFromString("undo:"), key: "z", to: edit)
        let redo = add("Redo", action: NSSelectorFromString("redo:"), key: "z", to: edit)
        redo.keyEquivalentModifierMask = [.command, .shift]
        edit.addItem(.separator())
        add("Cut", action: NSSelectorFromString("cut:"), key: "x", to: edit)
        add("Copy", action: NSSelectorFromString("copy:"), key: "c", to: edit)
        add("Paste", action: NSSelectorFromString("paste:"), key: "v", to: edit)
        add("Select all", action: NSSelectorFromString("selectAll:"), key: "a", to: edit)
        append(edit, to: root)

        let file = NSMenu(title: "File")
        add("Save", action: #selector(save), key: "s", to: file, target: self)
        add("Close", action: #selector(close), key: "w", to: file, target: self)
        append(file, to: root)

        let window = NSMenu(title: "Window")
        add("Minimize", action: NSSelectorFromString("performMiniaturize:"), key: "m", to: window)
        add("Zoom", action: NSSelectorFromString("performZoom:"), key: "", to: window)
        append(window, to: root)
        return root
    }

    func validateMenuItem(_ menuItem: NSMenuItem) -> Bool {
        if menuItem.action == #selector(save) || menuItem.action == #selector(close) {
            return commands.settingsIsKey() && SettingsCloseGuard.canRunCommand(commands.ownership())
        }
        return true
    }

    @objc private func openSettings() {
        commands.openSettings()
    }

    @objc private func save() {
        guard commands.settingsIsKey(), SettingsCloseGuard.canRunCommand(commands.ownership()) else { return }
        commands.save()
    }

    @objc private func close() {
        guard commands.settingsIsKey(), SettingsCloseGuard.canRunCommand(commands.ownership()) else { return }
        commands.close()
    }

    @objc private func quit() {
        if commands.settingsIsKey() && !SettingsCloseGuard.canRunCommand(commands.ownership()) { return }
        commands.quit()
    }

    private func append(_ menu: NSMenu, to root: NSMenu) {
        let item = NSMenuItem(title: menu.title, action: nil, keyEquivalent: "")
        item.submenu = menu
        root.addItem(item)
    }

    @discardableResult
    private func add(
        _ title: String, action: Selector, key: String, to menu: NSMenu, target: AnyObject? = nil
    ) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: action, keyEquivalent: key)
        item.keyEquivalentModifierMask = .command
        item.target = target
        menu.addItem(item)
        return item
    }
}
