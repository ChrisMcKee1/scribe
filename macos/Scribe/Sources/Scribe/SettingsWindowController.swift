import AppKit
import SwiftUI

/// Hosts the Settings window and tells its owner when the window closes, so the owner can let go of it. The next
/// open then builds every tab from what is stored at that moment (a dictionary rule added by Quick Add, new
/// history for Diagnostics, a login item changed in System Settings), and a closed window keeps no view state or
/// observers alive.
@MainActor
final class SettingsWindowController: NSWindowController, NSWindowDelegate {
    private let onClose: @MainActor @Sendable (SettingsWindowController) -> Void

    convenience init(
        rootView: SettingsView,
        onClose: @escaping @MainActor @Sendable (SettingsWindowController) -> Void
    ) {
        let window = NSWindow(contentViewController: NSHostingController(rootView: rootView))
        window.title = "Scribe Settings"
        window.setContentSize(NSSize(width: 720, height: 520))
        window.styleMask.formUnion([.titled, .closable, .miniaturizable, .resizable])
        window.isReleasedWhenClosed = false
        window.center()
        self.init(window: window, onClose: onClose)
    }

    init(window: NSWindow?, onClose: @escaping @MainActor @Sendable (SettingsWindowController) -> Void) {
        self.onClose = onClose
        super.init(window: window)
        shouldCascadeWindows = false
        window?.delegate = self
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        return nil
    }

    func windowWillClose(_ notification: Notification) {
        onClose(self) // MUTATION M10: released inside AppKit's close
    }
}
