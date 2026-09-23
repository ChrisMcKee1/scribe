import AppKit
import Foundation

/// What happened to the pasteboard content Scribe borrowed for a paste. Reported apart from how the
/// text was delivered, because the two fail independently: a paste can land while its restore fails.
enum ClipboardRestoreOutcome: String, Equatable, Sendable {
    /// Nothing was borrowed, so there was nothing to put back.
    case notApplicable
    /// The user's previous plain text, or the empty pasteboard, was put back.
    case restored
    /// Another application wrote to the pasteboard after Scribe did, so its newer content was left alone.
    case superseded
    /// Putting the previous text back failed.
    case failed
}

/// Why `PasteboardBorrower` declined to lend the pasteboard. Every refusal leaves the pasteboard as the
/// user had it, or as another application has since made it, and the caller types the text instead.
enum PasteboardBorrowRefusal: String, Equatable, Sendable {
    /// The pasteboard held something a plain-text restore cannot reproduce: rich text, an image, files,
    /// several items, a Handoff item, or another application's transient or concealed marker.
    case nonTextContent
    /// The pasteboard held plain text that could not be read, or not without macOS asking the user.
    case unreadable
    /// Another application wrote to the pasteboard while Scribe was reading it, so nothing was replaced.
    case contended
    /// Scribe could not write its text. The user's content was put back where possible.
    case writeFailed
    /// Another application took the pasteboard between Scribe's clear and its write.
    case superseded
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
    fileprivate var restoreOutcome: ClipboardRestoreOutcome?

    fileprivate init(previous: PasteboardSnapshot.Contents, ownedChangeCount: Int) {
        self.previous = previous
        self.ownedChangeCount = ownedChangeCount
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
/// later put it back, but only while the pasteboard still holds Scribe's write.
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

    let pasteboard: NSPasteboard

    /// The change count right after Scribe's last restore. While the pasteboard still reports it, the
    /// markers on the pasteboard are Scribe's own and the user's text under them can be borrowed again;
    /// any other value means the markers belong to whoever wrote last.
    private var ownRestoreChangeCount: Int?

    init(pasteboard: NSPasteboard) {
        self.pasteboard = pasteboard
    }

    /// Snapshots the pasteboard and replaces it with `text`, reading it only when macOS lets Scribe do so
    /// without asking the user.
    func borrow(for text: String) -> PasteboardBorrowResult {
        borrow(for: text, contentReadable: Self.allowsReadingWithoutPrompt(pasteboard))
    }

    func borrow(for text: String, contentReadable: Bool) -> PasteboardBorrowResult {
        switch snapshot(contentReadable: contentReadable) {
        case .refused(let refusal):
            return .refused(refusal, rollback: .notApplicable)
        case .captured(let snapshot):
            return replace(snapshot, with: text)
        }
    }

    /// Records what a borrow would have to put back. The change count is read before the contents, so
    /// `replace` can tell whether anything was written after the snapshot began.
    func snapshot(contentReadable: Bool) -> PasteboardSnapshotResult {
        let changeCount = pasteboard.changeCount
        let itemTypes = (pasteboard.pasteboardItems ?? []).map(\.types)
        let kind = Self.classify(itemTypes, discountingOwnMarkers: changeCount == ownRestoreChangeCount)
        switch kind {
        case .other:
            return .refused(.nonTextContent)
        case .empty:
            return .captured(PasteboardSnapshot(contents: .empty, changeCount: changeCount))
        case .plainText:
            guard contentReadable, let text = pasteboard.string(forType: .string) else {
                return .refused(.unreadable)
            }
            return .captured(PasteboardSnapshot(contents: .plainText(text), changeCount: changeCount))
        }
    }

    /// Replaces the pasteboard with `text` if nothing has written since `snapshot` was taken, and records
    /// the change count that later proves the pasteboard is still Scribe's.
    func replace(_ snapshot: PasteboardSnapshot, with text: String) -> PasteboardBorrowResult {
        // The snapshot only describes what the clear below removes while the count is unchanged. A copy
        // made since then is the user's newest content, and it is left exactly as it is.
        guard pasteboard.changeCount == snapshot.changeCount else {
            return .refused(.contended, rollback: .notApplicable)
        }

        let write = Self.writePrivately(text, to: pasteboard)
        let observed = pasteboard.changeCount
        guard observed == write.changeCount else {
            // Another application took the pasteboard after Scribe's clear. What is there now is not
            // Scribe's to replace, and the user's earlier content is already gone.
            return .refused(.superseded, rollback: .superseded)
        }

        let lease = PasteboardLease(previous: snapshot.contents, ownedChangeCount: observed)
        guard write.succeeded else {
            // Scribe's clear has already removed the user's content and nothing has written since, so it
            // goes straight back.
            return .refused(.writeFailed, rollback: restore(lease))
        }

        return .borrowed(lease)
    }

    /// Whether the pasteboard still holds the write `lease` describes, judged by a fresh read.
    func stillHolds(_ lease: PasteboardLease) -> Bool {
        lease.restoreOutcome == nil && pasteboard.changeCount == lease.ownedChangeCount
    }

    /// Puts the user's previous text, or the empty pasteboard, back, but only while the pasteboard still
    /// holds Scribe's write. Settles the lease: later calls return the first outcome and change nothing.
    func restore(_ lease: PasteboardLease) -> ClipboardRestoreOutcome {
        if let outcome = lease.restoreOutcome {
            return outcome
        }

        let outcome: ClipboardRestoreOutcome
        if pasteboard.changeCount != lease.ownedChangeCount {
            outcome = .superseded
        } else {
            let previousText: String?
            switch lease.previous {
            case .empty:
                previousText = nil
            case .plainText(let text):
                previousText = text
            }

            let write = Self.writePrivately(previousText, to: pasteboard)
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

    /// Copies text the user explicitly asked for, such as a recovered dictation. It stays on this Mac
    /// (`.currentHostOnly` keeps it out of Universal Clipboard) but carries no transient or concealed
    /// marker: the user wants it on the pasteboard, so clipboard history may keep it like any other copy.
    static func copyForUser(_ text: String, to pasteboard: NSPasteboard) -> Bool {
        pasteboard.prepareForNewContents(with: .currentHostOnly)
        return pasteboard.setString(text, forType: .string)
    }

    /// Clears the pasteboard for a write that stays on this Mac and, when there is text, writes it as one
    /// item marked with the nspasteboard.org transient and concealed types. Clipboard history tools read
    /// those as "do not record" and "do not display". Only Scribe's own writes can carry them; whatever
    /// another application copies is outside Scribe's control.
    private static func writePrivately(
        _ text: String?,
        to pasteboard: NSPasteboard
    ) -> (changeCount: Int, succeeded: Bool) {
        let changeCount = pasteboard.prepareForNewContents(with: .currentHostOnly)
        guard let text else {
            return (changeCount, true)
        }

        let item = NSPasteboardItem()
        guard item.setString(text, forType: .string) else {
            return (changeCount, false)
        }

        // Best effort: a marker that cannot be attached must never fail the paste the user is waiting for.
        item.setData(Data(), forType: transientType)
        item.setData(Data(), forType: concealedType)
        return (changeCount, pasteboard.writeObjects([item]))
    }
}
