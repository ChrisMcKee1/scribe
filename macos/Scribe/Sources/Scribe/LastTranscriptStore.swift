import Foundation

/// Keeps the last few finalized dictations available for explicit recovery from the tray menu.
/// A bounded ring of `capacity` transcripts, most recent first, so a dictation lost to a failed
/// injection stays recoverable. Direct port of Windows' `LastTranscriptStore`.
///
/// A recovery generation fences reads of durable history: it advances each time the ring is cleared for
/// Clear history, a read captures it before it starts, and a seed from an earlier generation is refused.
/// Without it, a startup or Quick Add read that began before a successful Clear could finish afterwards,
/// find the ring empty and put the deleted text back.
///
/// `@unchecked Sendable` because the compiler cannot see the locking: `entries` and `clearGeneration` are
/// only read or written while `lock` is held.
final class LastTranscriptStore: @unchecked Sendable {
    /// How many finalized transcripts are retained for recovery.
    static let capacity = 5

    /// Preview length budget for the tray submenu, including the trailing ellipsis.
    static let previewLength = 42

    private let lock = NSLock()
    // Most recent first. A plain array is fine at this size: inserts shift at most `capacity` items.
    private var entries: [String] = []
    private var clearGeneration: UInt64 = 0

    func set(_ text: String) {
        guard !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return
        }

        lock.lock()
        defer { lock.unlock() }

        // Re-dictating identical text must not burn ring slots on adjacent duplicates: the
        // transcript is already recoverable at the top of the list, so keep it there and
        // preserve the older, distinct entries beneath it.
        if let first = entries.first, first == text {
            return
        }

        entries.insert(text, at: 0)
        if entries.count > Self.capacity {
            entries.removeLast()
        }
    }

    /// Rewrites the retained transcripts that exactly match `original`.
    ///
    /// Keyed by content rather than by position on purpose: a dictation finishing while a
    /// correction UI is open shifts every index in the ring, and an index-based update would then
    /// silently overwrite somebody else's transcript. If the original has already been evicted,
    /// nothing happens.
    ///
    /// Every match is rewritten, not just the first, and no entry is ever removed, matching
    /// Windows' semantics: dropping a slot instead of updating it would cost the user a recovery
    /// slot as the price of a correction.
    @discardableResult
    func update(original: String?, updated: String?) -> Bool {
        guard let original, let updated,
            !original.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
            !updated.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
            original != updated
        else {
            return false
        }

        lock.lock()
        defer { lock.unlock() }

        var changed = false
        for index in entries.indices where entries[index] == original {
            entries[index] = updated
            changed = true
        }
        return changed
    }

    /// Fills an empty ring from durable history so the transcripts a user can act on are the same
    /// ones that can be repaired. Only ever fills a ring that is empty, so it can never displace
    /// live dictations. For transcripts already in hand; a read of durable history that has to
    /// wait for storage uses `seed(_:readAt:)` or `seed(from:)`, so a Clear in the meantime wins.
    func seed(_ transcripts: [String]) {
        lock.lock()
        defer { lock.unlock() }
        fill(with: transcripts)
    }

    /// The recovery generation: it advances each time the ring is cleared for Clear history.
    var generation: UInt64 {
        lock.lock()
        defer { lock.unlock() }
        return clearGeneration
    }

    /// `seed(_:)` for transcripts read from durable history while the ring was at `generation`. Refused
    /// when a Clear has happened since, because the read then holds text the user just deleted. Returns
    /// whether the ring was seeded.
    @discardableResult
    func seed(_ transcripts: [String], readAt generation: UInt64) -> Bool {
        lock.lock()
        defer { lock.unlock() }
        guard clearGeneration == generation, entries.isEmpty else {
            return false
        }
        fill(with: transcripts)
        return !entries.isEmpty
    }

    private func fill(with transcripts: [String]) {
        guard entries.isEmpty else {
            return
        }

        for text in transcripts {
            guard !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, entries.count < Self.capacity else {
                continue
            }
            entries.append(text)
        }
    }

    func get() -> String? {
        lock.lock()
        defer { lock.unlock() }
        return entries.first
    }

    /// Returns an immutable snapshot of the retained transcripts, most recent first.
    func recent() -> [String] {
        lock.lock()
        defer { lock.unlock() }
        return entries
    }

    /// Forgets every retained transcript and starts a new recovery generation, for Clear history: text
    /// the user asked to delete must not stay recoverable from the tray, and a read of durable history
    /// already running when the Clear succeeded must not bring it back.
    func removeAll() {
        lock.lock()
        defer { lock.unlock() }
        entries.removeAll()
        clearGeneration &+= 1
    }

    /// Renders a transcript as a single-line menu preview: all whitespace runs (including line
    /// breaks) collapse to single spaces, the result is trimmed, and anything longer than
    /// `maxLength` is truncated so the ellipsis fits inside the budget.
    static func formatPreview(_ text: String?, maxLength: Int = previewLength) -> String {
        precondition(maxLength >= 2, "maxLength must be at least 2.")

        guard let text, !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return ""
        }

        let collapsed =
            text
            .components(separatedBy: .whitespacesAndNewlines)
            .filter { !$0.isEmpty }
            .joined(separator: " ")

        if collapsed.count <= maxLength {
            return collapsed
        }

        let cut = collapsed.index(collapsed.startIndex, offsetBy: maxLength - 1)
        return collapsed[collapsed.startIndex..<cut] + "…"
    }
}

extension LastTranscriptStore {
    /// Fills an empty ring from a read of durable history. The recovery generation is captured before
    /// the read starts, so a Clear that succeeds while the read runs makes its result stale and it is
    /// dropped. A failed read seeds nothing. Returns whether the ring was seeded.
    @discardableResult
    func seed(from load: @Sendable () async throws -> [String]) async -> Bool {
        guard recent().isEmpty else {
            return false
        }
        let generation = self.generation
        guard let transcripts = try? await load() else {
            return false
        }
        return seed(transcripts, readAt: generation)
    }
}
