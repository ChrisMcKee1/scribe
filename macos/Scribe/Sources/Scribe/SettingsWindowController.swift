import AppKit
import SwiftUI

/// Hosts the Settings window and tells its owner when the window closes, so the owner can let go of it. The next
/// open then builds every tab from what is stored at that moment (a dictionary rule added by Quick Add, new
/// history for Diagnostics, a login item changed in System Settings), and a closed window keeps no view state or
/// observers alive. Unsaved input survives because it lives in `SettingsDrafts`, which the owner keeps.
@MainActor
final class SettingsWindowController: NSWindowController, NSWindowDelegate {
    private let onClose: @MainActor @Sendable (SettingsWindowController) -> Void
    static var mutationKeepAlive: [NSWindow] = [] // MUTATION N7: closed windows are kept alive

    convenience init(
        rootView: some View,
        onClose: @escaping @MainActor @Sendable (SettingsWindowController) -> Void
    ) {
        let window = NSWindow(contentViewController: NSHostingController(rootView: rootView))
        Self.mutationKeepAlive.append(window)
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
        // Released on the next turn of the main actor: AppKit is still inside the window's close when this runs,
        // and dropping the last reference to the window here could free it while AppKit is using it.
        let onClose = self.onClose
        Task { @MainActor [weak self] in
            guard let self else { return }
            onClose(self)
        }
    }
}
