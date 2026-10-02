import Foundation

enum SettingsCloseTrigger: Equatable, Sendable {
    case close
    case quit
    case restart
    case systemTermination
}

enum SettingsCloseChoice: CaseIterable, Equatable, Sendable {
    case save
    case discard
    case keepEditing
}

struct SettingsCloseDecision: Equatable, Sendable {
    let ask: Bool
    let title: String
    let body: String
    let primaryButton: String
    let defaultChoice: SettingsCloseChoice
}

struct SettingsKeyboardOwnership: Equatable, Sendable {
    var shortcutCapture = false
    var composing = false
    var editing = false
    var sheetOrMenu = false
    var searchHasText = false
}

enum SettingsEscapeAction: Equatable, Sendable {
    case cancelCapture
    case nativeComposition
    case cancelEdit
    case dismissPresentation
    case clearSearch
    case none
}

enum SettingsCloseGuard {
    static func decide(dirty: Bool, saving: Bool = false, trigger: SettingsCloseTrigger) -> SettingsCloseDecision {
        let title: String
        let primary: String
        switch trigger {
        case .quit:
            title = "Save changes before quitting?"
            primary = "Save and quit"
        case .restart:
            title = "Save changes before restarting?"
            primary = "Save and restart"
        case .close, .systemTermination:
            title = "Save changes before closing?"
            primary = "Save and close"
        }
        let body = [
            "Your changes haven't been saved.",
            "Things that already happened, such as deleting history, aren't undone.",
        ].joined(separator: " ")
        return SettingsCloseDecision(
            ask: trigger != .systemTermination && (dirty || saving),
            title: title, body: body, primaryButton: primary, defaultChoice: .keepEditing)
    }

    static func escape(_ ownership: SettingsKeyboardOwnership) -> SettingsEscapeAction {
        if ownership.shortcutCapture { return .cancelCapture }
        if ownership.composing { return .nativeComposition }
        if ownership.editing { return .cancelEdit }
        if ownership.sheetOrMenu { return .dismissPresentation }
        if ownership.searchHasText { return .clearSearch }
        return .none
    }

    static func canRunCommand(_ ownership: SettingsKeyboardOwnership) -> Bool {
        !ownership.shortcutCapture && !ownership.composing
    }
}
