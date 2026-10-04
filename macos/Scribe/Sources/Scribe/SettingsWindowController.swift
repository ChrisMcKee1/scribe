import AppKit
import SwiftUI

/// Hosts the Settings window and tells its owner when the window closes, so the owner can let go of it. The next
/// open then builds every tab from what is stored at that moment (a dictionary rule added by Quick Add, new
/// history for Diagnostics, a login item changed in System Settings), and a closed window keeps no view state or
/// observers alive. Pending input lives in `SettingsDrafts`; normal close saves or discards it only after the
/// user chooses, and Keep editing is the default.
@MainActor
final class SettingsWindowController: NSWindowController, NSWindowDelegate {
    /// Posted on the controller's notification center as the Settings window starts to close, so work a tab started
    /// and would otherwise leave running, such as a Test Connection, can stop (`CleanupSettingsModel`).
    static let willCloseNotification = Notification.Name("ScribeSettingsWindowWillClose")

    private let onClose: @MainActor @Sendable (SettingsWindowController) -> Void
    private let center: NotificationCenter
    private var drafts: SettingsDrafts?
    private var closePromptShowing = false
    private var closeAccepted = false
    private var approvalOperation: Task<Bool, Never>?
    private let chooseClose: @MainActor (NSWindow, [String]) async -> SettingsCloseChoice
    private(set) var closeOperation: Task<Void, Never>?

    convenience init(
        rootView: some View,
        onClose: @escaping @MainActor @Sendable (SettingsWindowController) -> Void
    ) {
        let window = NSWindow()
        window.title = "Scribe Settings"
        window.styleMask.formUnion([.titled, .closable, .miniaturizable, .resizable])
        window.contentMinSize = NSSize(width: 860, height: 600)
        window.isReleasedWhenClosed = false
        self.init(window: window, drafts: (rootView as? SettingsView)?.drafts, onClose: onClose)
        window.contentViewController = NSHostingController(
            rootView: rootView.environment(\.settingsCloseWindow) { [weak self] in
                self?.window?.performClose(nil)
            })
        window.setContentSize(NSSize(width: 1000, height: 760))
        window.center()
    }

    init(
        window: NSWindow?,
        drafts: SettingsDrafts? = nil,
        onClose: @escaping @MainActor @Sendable (SettingsWindowController) -> Void,
        center: NotificationCenter = .default,
        chooseClose: @escaping @MainActor (NSWindow, [String]) async -> SettingsCloseChoice =
            SettingsWindowController.showClosePrompt
    ) {
        self.onClose = onClose
        self.center = center
        self.drafts = drafts
        self.chooseClose = chooseClose
        super.init(window: window)
        shouldCascadeWindows = false
        window?.delegate = self
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        return nil
    }

    func windowShouldClose(_ sender: NSWindow) -> Bool {
        guard !closeAccepted, let drafts else { return true }
        guard sender.attachedSheet == nil else { return false }
        guard !closePromptShowing else { return false }
        guard drafts.isBusy || drafts.hasUnsavedChanges else { return true }
        closePromptShowing = true
        closeOperation = Task { @MainActor [weak self] in
            guard let self else { return }
            let accepted = await self.approvePendingChanges(sender, drafts: drafts)
            if accepted {
                self.closeAccepted = true
                sender.performClose(nil)
            }
            self.closePromptShowing = false
            self.closeOperation = nil
        }
        return false
    }

    /// Must complete before the application starts shutdown or schedules a restart. False cancels that request,
    /// leaving Settings editable, including after a failed save. This does not close the window or start teardown.
    func prepareForApplicationTermination() async -> Bool {
        guard let drafts else { return true }
        guard let window else { return !drafts.isBusy && !drafts.hasUnsavedChanges }
        let accepted = await approvePendingChanges(window, drafts: drafts)
        if !accepted {
            showWindow(nil)
            window.makeKeyAndOrderFront(nil)
        }
        return accepted
    }

    private func approvePendingChanges(_ window: NSWindow, drafts: SettingsDrafts) async -> Bool {
        // A Quit during a Settings-close prompt joins that same choice, rather than displaying a second sheet.
        if let approvalOperation {
            let accepted = await approvalOperation.value
            return accepted && !drafts.isBusy && !drafts.hasUnsavedChanges
        }
        guard window.attachedSheet == nil else { return false }
        let operation = Task { @MainActor in
            await drafts.waitUntilIdle()
            guard !Task.isCancelled else { return false }
            guard window.attachedSheet == nil else { return false }
            guard drafts.hasUnsavedChanges else { return true }
            let choice = await self.chooseClose(window, drafts.unsavedSections)
            guard !Task.isCancelled else { return false }
            return await drafts.acceptClose(choice)
        }
        approvalOperation = operation
        let accepted = await operation.value
        approvalOperation = nil
        return accepted && !drafts.isBusy && !drafts.hasUnsavedChanges
    }

    private static func showClosePrompt(_ window: NSWindow, _ sections: [String]) async -> SettingsCloseChoice {
        let alert = NSAlert()
        alert.messageText = "Save changes before closing?"
        alert.informativeText =
            "You have unsaved changes to \(sections.joined(separator: ", "))."
        alert.addButton(withTitle: "Save")
        alert.addButton(withTitle: "Discard changes")
        alert.addButton(withTitle: "Keep editing")
        alert.buttons[0].keyEquivalent = ""
        alert.buttons[2].keyEquivalent = "\r"
        alert.window.initialFirstResponder = alert.buttons[2]
        return await withCheckedContinuation { continuation in
            alert.beginSheetModal(for: window) { response in
                let choice: SettingsCloseChoice =
                    response == .alertFirstButtonReturn
                    ? .save : response == .alertSecondButtonReturn ? .discard : .keepEditing
                continuation.resume(returning: choice)
            }
        }
    }

    func windowWillClose(_ notification: Notification) {
        drafts?.windowClosed()
        center.post(name: Self.willCloseNotification, object: nil)
        // Released on the next turn of the main actor: AppKit is still inside the window's close when this runs,
        // and dropping the last reference to the window here could free it while AppKit is using it.
        let onClose = self.onClose
        Task { @MainActor [weak self] in
            guard let self else { return }
            onClose(self)
        }

    }
}

private struct SettingsCloseWindowKey: EnvironmentKey {
    static let defaultValue: @MainActor () -> Void = {}
}

extension EnvironmentValues {
    var settingsCloseWindow: @MainActor () -> Void {
        get { self[SettingsCloseWindowKey.self] }
        set { self[SettingsCloseWindowKey.self] = newValue }
    }
}
