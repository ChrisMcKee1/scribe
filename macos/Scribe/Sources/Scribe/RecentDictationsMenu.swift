import AppKit

/// The tray's Recent Dictations submenu. It is its submenu's delegate, so the submenu is filled from the recovery ring
/// each time it is about to open (Windows' `PopulateRecentDictations`); the top-level menu's delegate never hears
/// about a submenu opening. Choosing an entry copies it to the clipboard, unless Clear history has run since the
/// submenu was filled: every entry carries the recovery generation it was shown in, and Clear refills the submenu at
/// once (`invalidate()`), even while it is open.
@MainActor
final class RecentDictationsMenu: NSObject, NSMenuDelegate {
    /// One entry: its transcript and the recovery generation it was shown in.
    private final class Entry: NSObject {
        let transcript: String
        let generation: UInt64

        init(transcript: String, generation: UInt64) {
            self.transcript = transcript
            self.generation = generation
        }
    }

    private let store: LastTranscriptStore
    private let pasteboard: NSPasteboard
    /// The item to add to the tray menu; its submenu has this object as its delegate.
    let item: NSMenuItem

    init(store: LastTranscriptStore, pasteboard: NSPasteboard = .general) {
        self.store = store
        self.pasteboard = pasteboard
        item = NSMenuItem(title: "Recent Dictations", action: nil, keyEquivalent: "")
        super.init()
        let submenu = NSMenu(title: "Recent Dictations")
        submenu.delegate = self
        item.submenu = submenu
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        populate(menu)
    }

    /// Clear history: the entries on show hold text the user deleted, so they are replaced now.
    func invalidate() {
        guard let submenu = item.submenu else { return }
        populate(submenu)
    }

    /// Replaces `menu`'s items with the retained transcripts, newest first, or a placeholder when there are none.
    func populate(_ menu: NSMenu) {
        menu.removeAllItems()
        let generation = store.generation
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
            entry.representedObject = Entry(transcript: transcript, generation: generation)
            menu.addItem(entry)
        }
    }

    @objc private func copyRecentDictation(_ sender: NSMenuItem) {
        guard let entry = sender.representedObject as? Entry else { return }
        guard entry.generation == store.generation else {
            ScribeLog.info(.app, "A recent dictation Clear history removed was not copied")
            return
        }
        pasteboard.clearContents()
        pasteboard.setString(entry.transcript, forType: .string)
        ScribeLog.info(
            .app, "Copied a recent dictation to the clipboard", .count("characters", entry.transcript.count))
    }
}
