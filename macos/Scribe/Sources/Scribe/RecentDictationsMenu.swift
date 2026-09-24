import AppKit

/// The tray's Recent Dictations submenu. It is its submenu's delegate, so the submenu is filled from the recovery ring
/// each time it is about to open (Windows' `PopulateRecentDictations`); the top-level menu's delegate never hears
/// about a submenu opening. Choosing an entry copies it to the clipboard.
@MainActor
final class RecentDictationsMenu: NSObject, NSMenuDelegate {
    private let store: LastTranscriptStore
    /// The item to add to the tray menu; its submenu has this object as its delegate.
    let item: NSMenuItem

    init(store: LastTranscriptStore) {
        self.store = store
        item = NSMenuItem(title: "Recent Dictations", action: nil, keyEquivalent: "")
        super.init()
        let submenu = NSMenu(title: "Recent Dictations")
        submenu.delegate = self
        item.submenu = submenu
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        populate(menu)
    }

    /// Replaces `menu`'s items with the retained transcripts, newest first, or a placeholder when there are none.
    func populate(_ menu: NSMenu) {
        menu.removeAllItems()
        let recent = store.recent()
        guard !recent.isEmpty else {
            let placeholder = NSMenuItem(title: "No recent dictations", action: nil, keyEquivalent: "")
            placeholder.isEnabled = false
            menu.addItem(placeholder)
            return
        }
        for transcript in recent {
            let entry = NSMenuItem(
                title: LastTranscriptStore.formatPreview(transcript),
                action: #selector(copyRecentDictation(_:)),
                keyEquivalent: "")
            entry.target = self
            entry.representedObject = transcript
            menu.addItem(entry)
        }
    }

    @objc private func copyRecentDictation(_ sender: NSMenuItem) {
        guard let transcript = sender.representedObject as? String else { return }
        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        pasteboard.setString(transcript, forType: .string)
        ScribeLog.info(.app, "Copied a recent dictation to the clipboard", .count("characters", transcript.count))
    }
}
