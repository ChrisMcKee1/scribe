import AppKit
import Foundation

/// What happened to the pasteboard content Scribe borrowed for a paste. Reported apart from how the
/// text was delivered, because the two fail independently: a paste can land while its restore fails.
enum ClipboardRestoreOutcome: String, Equatable, Sendable {
    /// Nothing was borrowed, so there was nothing to put back.
    case notApplicable
    /// The user's previous content (plain text, everything a Markdown paste kept, or the empty pasteboard) was
    /// put back.
    case restored
    /// Another application wrote to the pasteboard after Scribe did, so its newer content was left alone.
    case superseded
    /// Putting the previous text back failed, so the user's previous content is gone. After a refused
    /// write (`PasteboardBorrowRefusal.writeFailed`) this is the rollback that failed.
    case failed
}

/// Why `PasteboardBorrower` declined to lend the pasteboard; the caller types the text instead. Every
/// refusal but `writeFailed` leaves the pasteboard untouched. `writeFailed` happens after Scribe's clear,
/// and the rollback outcome that comes with it says whether the user's content went back.
enum PasteboardBorrowRefusal: String, Equatable, Sendable {
    /// The pasteboard held something a plain-text restore cannot reproduce: rich text, an image, files,
    /// several items, a Handoff item, or another application's transient or concealed marker.
    case nonTextContent
    /// The pasteboard could not report its items, or held plain text that could not be read, or not
    /// without macOS asking the user.
    case unreadable
    /// Another application wrote to the pasteboard while Scribe was reading it, so nothing was replaced.
    case contended
    /// Scribe could not write its text after clearing the pasteboard. The rollback outcome reports
    /// whether the user's content was put back (`restored`) or lost (`failed`).
    case writeFailed
    /// Another application took the pasteboard between Scribe's clear and its write.
    case superseded
    /// A Markdown paste would have kept more than `PasteboardBorrower.maximumKeptBytes`.
    case tooLarge
    /// A Markdown paste found another application's transient or concealed marker: that content (a password
    /// manager's, say) asked not to be kept, so it is left alone.
    case markedPrivate
}

/// The two pasteboard calls whose failure changes what a borrow may do. Injectable so tests can make them
/// fail on a real, private pasteboard.
struct PasteboardOperations {
    /// The types of every item, or nil when the pasteboard could not report its items. Apple documents a
    /// nil `pasteboardItems` as an error retrieving them, not as an empty pasteboard.
    var itemTypes: @MainActor (NSPasteboard) -> [[NSPasteboard.PasteboardType]]?
    /// Writes items after a clear, reporting whether the pasteboard accepted them.
    var write: @MainActor (NSPasteboard, [NSPasteboardItem]) -> Bool
    /// The data of every type of every item, in the pasteboard's order, or nil when it could not report its items.
    /// A type whose data could not be read has nil data.
    var itemData: @MainActor (NSPasteboard) -> [[(type: NSPasteboard.PasteboardType, data: Data?)]]?

    static var live: PasteboardOperations {
        PasteboardOperations(
            itemTypes: { pasteboard in pasteboard.pasteboardItems.map { items in items.map(\.types) } },
            write: { pasteboard, items in pasteboard.writeObjects(items) },
            itemData: { pasteboard in
                pasteboard.pasteboardItems.map { items in
                    items.map { item in item.types.map { type in (type: type, data: item.data(forType: type)) } }
                }
            })
    }
}

/// Whether a pasteboard can be borrowed, judged from its item types alone.
enum PasteboardContentKind: Equatable, Sendable {
    case empty
    case plainText
    case other
}

/// The user's pasteboard as it was just before a borrow. A class rather than a struct so that string
/// interpolation or `print` can never render the text it holds.
final class PasteboardSnapshot {
    fileprivate enum Contents {
        case empty
        case plainText(String)
        /// Every item, each with every type's data in the order the pasteboard listed them: what a Markdown paste
        /// keeps (`PasteboardBorrower.borrow(for:keepingWholePasteboard:html:)`).
        case items([[(type: NSPasteboard.PasteboardType, data: Data)]])
    }

    fileprivate let contents: Contents
    fileprivate let changeCount: Int

    fileprivate init(contents: Contents, changeCount: Int) {
        self.contents = contents
        self.changeCount = changeCount
    }
}

/// One borrow of the pasteboard: what to put back, and the change count that proves the pasteboard still
/// holds Scribe's write. A class for the same reason as `PasteboardSnapshot`.
final class PasteboardLease {
    fileprivate let previous: PasteboardSnapshot.Contents
    /// Read right after Scribe's write, with no other owner in between.
    let ownedChangeCount: Int
    /// Whether Scribe's write carried the HTML rendering it was given beside the text.
    let htmlPlaced: Bool
    fileprivate var restoreOutcome: ClipboardRestoreOutcome?

    fileprivate init(previous: PasteboardSnapshot.Contents, ownedChangeCount: Int, htmlPlaced: Bool = false) {
        self.previous = previous
        self.ownedChangeCount = ownedChangeCount
        self.htmlPlaced = htmlPlaced
    }

    /// How many items the borrow kept beyond plain text, for the log: zero unless a Markdown paste kept the whole
    /// pasteboard. A count only.
    var keptItemCount: Int {
        if case .items(let items) = previous {
            return items.count
        }
        return 0
    }
}

enum PasteboardSnapshotResult {
    case captured(PasteboardSnapshot)
    case refused(PasteboardBorrowRefusal)
}

enum PasteboardBorrowResult {
    case borrowed(PasteboardLease)
    case refused(PasteboardBorrowRefusal, rollback: ClipboardRestoreOutcome)
}

/// Lends the pasteboard for one paste: remember the user's plain text, replace it with Scribe's, and
/// later put it back, but only while the pasteboard still holds Scribe's write. A Markdown paste keeps everything
/// instead (`keepingWholePasteboard`), as Windows' whole-clipboard borrow does.
///
/// NSPasteboard has no exclusive lease like the Windows clipboard's OpenClipboard. The only proof of
/// ownership is `changeCount`, which Apple documents as incrementing on every change of owner and as the
/// value to record when taking ownership and compare later. Every decision here re-reads it immediately
/// before the mutation it authorizes, which narrows the unprotected gap to a single call but cannot close
/// it: a copy that lands between a check and the clear that follows it is lost, and one that lands after
/// the check before Command-V is what the target pastes.
@MainActor
final class PasteboardBorrower {
    /// nspasteboard.org marker: the content is momentary and clipboard history should not record it.
    static let transientType = NSPasteboard.PasteboardType("org.nspasteboard.TransientType")
    /// nspasteboard.org marker: the content is confidential and clipboard tools should not display it.
    static let concealedType = NSPasteboard.PasteboardType("org.nspasteboard.ConcealedType")

    /// The item types a plain-text restore reproduces. Anything else on the pasteboard (HTML, RTF,
    /// images, file URLs, application-private types, privacy markers) would be lost by putting back only
    /// the text, so such a pasteboard is never borrowed.
    static let plainTextTypes: Set<NSPasteboard.PasteboardType> = [
        .string,
        NSPasteboard.PasteboardType("NSStringPboardType"),
        NSPasteboard.PasteboardType("public.plain-text"),
        NSPasteboard.PasteboardType("public.utf16-plain-text"),
        NSPasteboard.PasteboardType("public.utf16-external-plain-text"),
        NSPasteboard.PasteboardType("com.apple.traditional-mac-plain-text"),
    ]

    private static let ownMarkerTypes: Set<NSPasteboard.PasteboardType> = [transientType, concealedType]

    /// The most a Markdown paste keeps, the bound Windows' whole-clipboard borrow uses (`MaxKeptBytes`). A larger
    /// pasteboard is left alone and the text typed.
    static let maximumKeptBytes = 128 * 1024 * 1024

    let pasteboard: NSPasteboard
    private let operations: PasteboardOperations
    private let maximumKeptBytes: Int

    /// The change count right after Scribe's last restore. While the pasteboard still reports it, the
    /// markers on the pasteboard are Scribe's own and the user's text under them can be borrowed again;
    /// any other value means the markers belong to whoever wrote last.
    private var ownRestoreChangeCount: Int?

    init(
        pasteboard: NSPasteboard,
        operations: PasteboardOperations = .live,
        maximumKeptBytes: Int = PasteboardBorrower.maximumKeptBytes
    ) {
        self.pasteboard = pasteboard
        self.operations = operations
        self.maximumKeptBytes = maximumKeptBytes
    }

    /// Snapshots the pasteboard and replaces it with `text`, reading it only when macOS lets Scribe do so
    /// without asking the user.
    func borrow(for text: String) -> PasteboardBorrowResult {
        borrow(for: text, contentReadable: Self.allowsReadingWithoutPrompt(pasteboard))
    }

    /// A Markdown paste's borrow (`DictationFormatPlan.keepsWholePasteboard`): with `keepingWholePasteboard`,
    /// a pasteboard holding more than plain text (an image, formatted text, files) is kept whole and put back
    /// rather than refused, and `html`, when given, is written beside the text as the pasteboard's HTML.
    func borrow(for text: String, keepingWholePasteboard: Bool, html: String?) -> PasteboardBorrowResult {
        borrow(
            for: text, contentReadable: Self.allowsReadingWithoutPrompt(pasteboard),
            keepingWholePasteboard: keepingWholePasteboard, html: html)
    }

    func borrow(
        for text: String,
        contentReadable: Bool,
        keepingWholePasteboard: Bool = false,
        html: String? = nil
    ) -> PasteboardBorrowResult {
        switch snapshot(contentReadable: contentReadable, keepingWholePasteboard: keepingWholePasteboard) {
        case .refused(let refusal):
            return .refused(refusal, rollback: .notApplicable)
        case .captured(let snapshot):
            return replace(snapshot, with: text, html: html)
        }
    }

    /// Records what a borrow would have to put back. The change count is read before the contents, so
    /// `replace` can tell whether anything was written after the snapshot began.
    func snapshot(contentReadable: Bool, keepingWholePasteboard: Bool = false) -> PasteboardSnapshotResult {
        let changeCount = pasteboard.changeCount
        // Only an item list that was actually retrieved may say the pasteboard is empty. Taking a failed
        // read for an empty one would let the borrow clear content it never looked at, then "restore" it
        // to nothing.
        guard let itemTypes = operations.itemTypes(pasteboard) else {
            return .refused(.unreadable)
        }

        let discountingOwnMarkers = changeCount == ownRestoreChangeCount
        let kind = Self.classify(itemTypes, discountingOwnMarkers: discountingOwnMarkers)
        switch kind {
        case .other:
            guard keepingWholePasteboard else {
                return .refused(.nonTextContent)
            }
            return snapshotWholePasteboard(
                itemTypes, discountingOwnMarkers: discountingOwnMarkers, contentReadable: contentReadable,
                changeCount: changeCount)
        case .empty:
            return .captured(PasteboardSnapshot(contents: .empty, changeCount: changeCount))
        case .plainText:
            guard contentReadable, let text = pasteboard.string(forType: .string) else {
                return .refused(.unreadable)
            }
            return .captured(PasteboardSnapshot(contents: .plainText(text), changeCount: changeCount))
        }
    }

    /// Every item with every type's data, refused when another application marked it transient or concealed, when
    /// a type cannot be read, or when it is larger than `maximumKeptBytes`. Reading a type an application promised
    /// asks that application to provide it, and how long it takes is the application's: nothing bounds it here.
    /// A "live" copy that keeps a link to its source comes back as the data it provided.
    private func snapshotWholePasteboard(
        _ itemTypes: [[NSPasteboard.PasteboardType]],
        discountingOwnMarkers: Bool,
        contentReadable: Bool,
        changeCount: Int
    ) -> PasteboardSnapshotResult {
        // A password manager's copy carries these so that nothing keeps it; putting it back would keep it on the
        // pasteboard past the moment it meant to clear itself.
        let othersMarkers = itemTypes.contains { types in
            types.contains { Self.ownMarkerTypes.contains($0) } && !discountingOwnMarkers
        }
        guard !othersMarkers else {
            return .refused(.markedPrivate)
        }
        guard contentReadable, let items = operations.itemData(pasteboard) else {
            return .refused(.unreadable)
        }

        var kept: [[(type: NSPasteboard.PasteboardType, data: Data)]] = []
        var bytes = 0
        for item in items {
            var entries: [(type: NSPasteboard.PasteboardType, data: Data)] = []
            for entry in item where !Self.ownMarkerTypes.contains(entry.type) {
                guard let data = entry.data else {
                    return .refused(.unreadable)
                }
                bytes += data.count
                guard bytes <= maximumKeptBytes else {
                    return .refused(.tooLarge)
                }
                entries.append((type: entry.type, data: data))
            }
            if !entries.isEmpty {
                kept.append(entries)
            }
        }
        return .captured(PasteboardSnapshot(contents: kept.isEmpty ? .empty : .items(kept), changeCount: changeCount))
    }

    /// Replaces the pasteboard with `text`, and `html` beside it when given, if nothing has written since
    /// `snapshot` was taken, and records the change count that later proves the pasteboard is still Scribe's.
    func replace(_ snapshot: PasteboardSnapshot, with text: String, html: String? = nil) -> PasteboardBorrowResult {
        // The snapshot only describes what the clear below removes while the count is unchanged. A copy
        // made since then is the user's newest content, and it is left exactly as it is.
        guard pasteboard.changeCount == snapshot.changeCount else {
            return .refused(.contended, rollback: .notApplicable)
        }

        let write = writePrivately(text, html: html)
        let observed = pasteboard.changeCount
        guard observed == write.changeCount else {
            // Another application took the pasteboard after Scribe's clear. What is there now is not
            // Scribe's to replace, and the user's earlier content is already gone.
            return .refused(.superseded, rollback: .superseded)
        }

        let lease = PasteboardLease(
            previous: snapshot.contents, ownedChangeCount: observed, htmlPlaced: write.htmlPlaced)
        guard write.succeeded else {
            // Scribe's clear has already removed the user's content and nothing has written since, so it
            // goes straight back. That write can fail too, and the rollback outcome then says so: the
            // user's previous content is gone.
            return .refused(.writeFailed, rollback: restore(lease))
        }

        return .borrowed(lease)
    }

    /// Whether the pasteboard still holds the write `lease` describes, judged by a fresh read.
    func stillHolds(_ lease: PasteboardLease) -> Bool {
        lease.restoreOutcome == nil && pasteboard.changeCount == lease.ownedChangeCount
    }

    /// Puts the user's previous content (plain text, every item a Markdown paste kept, or the empty pasteboard)
    /// back, but only while the pasteboard still holds Scribe's write. Settles the lease: later calls return the
    /// first outcome and change nothing.
    func restore(_ lease: PasteboardLease) -> ClipboardRestoreOutcome {
        if let outcome = lease.restoreOutcome {
            return outcome
        }

        let outcome: ClipboardRestoreOutcome
        if pasteboard.changeCount != lease.ownedChangeCount {
            outcome = .superseded
        } else {
            let write: (changeCount: Int, succeeded: Bool, htmlPlaced: Bool)
            switch lease.previous {
            case .empty:
                write = writePrivately(nil)
            case .plainText(let text):
                write = writePrivately(text)
            case .items(let items):
                let restored = writePrivately(items)
                write = (restored.changeCount, restored.succeeded, false)
            }

            if write.succeeded {
                outcome = .restored
                if pasteboard.changeCount == write.changeCount {
                    ownRestoreChangeCount = write.changeCount
                }
            } else {
                outcome = .failed
            }
        }

        lease.restoreOutcome = outcome
        return outcome
    }

    /// Classifies a pasteboard by the types of its items. Only an empty pasteboard or a single item made
    /// of plain-text types can be borrowed. Scribe's own markers are discounted only when the caller has
    /// proved, by change count, that they came from Scribe's own restore.
    static func classify(
        _ itemTypes: [[NSPasteboard.PasteboardType]],
        discountingOwnMarkers: Bool
    ) -> PasteboardContentKind {
        guard let first = itemTypes.first else {
            return .empty
        }

        guard itemTypes.count == 1 else {
            return .other
        }

        let types = discountingOwnMarkers ? first.filter { !ownMarkerTypes.contains($0) } : first
        guard !types.isEmpty, types.allSatisfy({ plainTextTypes.contains($0) }) else {
            return .other
        }

        return .plainText
    }

    /// Whether the pasteboard's contents can be read without macOS asking the user. macOS 15.4 added
    /// per-application pasteboard access control, which is still a developer preview in macOS 26. Where it
    /// applies, `.ask` would put a permission alert in front of a dictation and `.alwaysDeny` returns
    /// nothing, so Scribe types instead of reading. `.default` is treated as readable because that is how
    /// every release behaves today; Apple documents that `.default` asks on programmatic access to the
    /// general pasteboard once the feature is on, so this is the check to revisit when that ships.
    static func allowsReadingWithoutPrompt(_ pasteboard: NSPasteboard) -> Bool {
        guard #available(macOS 15.4, *) else {
            return true
        }

        switch pasteboard.accessBehavior {
        case .alwaysAllow, .default:
            return true
        case .ask, .alwaysDeny:
            return false
        @unknown default:
            return false
        }
    }

    /// Copies text the user asked for so that it stays on this Mac (`.currentHostOnly` keeps it out of Universal
    /// Clipboard), with no transient or concealed marker, so clipboard history may keep it like any other copy.
    /// The app does not call it: Copy Transcript (`DictationNotificationCenter`) and Recent Dictations
    /// (`RecentDictationsMenu`) make ordinary copies, which Universal Clipboard can share with other devices.
    static func copyForUser(_ text: String, to pasteboard: NSPasteboard) -> Bool {
        pasteboard.prepareForNewContents(with: .currentHostOnly)
        return pasteboard.setString(text, forType: .string)
    }

    /// Clears the pasteboard for a write that stays on this Mac and, when there is text, writes it as one
    /// item marked with the nspasteboard.org transient and concealed types. Clipboard history tools read
    /// those as "do not record" and "do not display". Only Scribe's own writes can carry them; whatever
    /// another application copies is outside Scribe's control.
    ///
    /// With `html`, the item also carries it as `public.html`, which apps built on a browser engine read as the
    /// formatted copy, behind a UTF-8 charset declaration as Chromium itself writes it. Best effort, like the
    /// markers: a formatted copy that cannot be attached never fails the paste, which keeps its text.
    private func writePrivately(
        _ text: String?, html: String? = nil
    ) -> (changeCount: Int, succeeded: Bool, htmlPlaced: Bool) {
        let changeCount = pasteboard.prepareForNewContents(with: .currentHostOnly)
        guard let text else {
            return (changeCount, true, false)
        }

        let item = NSPasteboardItem()
        guard item.setString(text, forType: .string) else {
            return (changeCount, false, false)
        }

        let htmlPlaced = html.map { item.setString(Self.htmlDocument($0), forType: .html) } ?? false
        // Best effort: a marker that cannot be attached must never fail the paste the user is waiting for.
        item.setData(Data(), forType: Self.transientType)
        item.setData(Data(), forType: Self.concealedType)
        return (changeCount, operations.write(pasteboard, [item]), htmlPlaced)
    }

    /// Puts back every item a Markdown paste kept, in their order and each with its types in their order, marked
    /// private like a restored text. A type that cannot be set again makes the restore a failure, though the rest
    /// is still written.
    private func writePrivately(
        _ items: [[(type: NSPasteboard.PasteboardType, data: Data)]]
    ) -> (changeCount: Int, succeeded: Bool) {
        let changeCount = pasteboard.prepareForNewContents(with: .currentHostOnly)
        var everyTypeSet = true
        let pasteboardItems = items.enumerated().map { index, entries in
            let item = NSPasteboardItem()
            for entry in entries {
                if !item.setData(entry.data, forType: entry.type) {
                    everyTypeSet = false
                }
            }
            if index == 0 {
                item.setData(Data(), forType: Self.transientType)
                item.setData(Data(), forType: Self.concealedType)
            }
            return item
        }
        let written = operations.write(pasteboard, pasteboardItems)
        return (changeCount, written && everyTypeSet)
    }

    /// The pasteboard's HTML for a fragment: a UTF-8 charset declaration, as Chromium writes its own copies, so the
    /// reader never guesses the encoding of the text, then the fragment.
    static func htmlDocument(_ fragment: String) -> String {
        "<meta charset=\"utf-8\">" + fragment
    }
}
