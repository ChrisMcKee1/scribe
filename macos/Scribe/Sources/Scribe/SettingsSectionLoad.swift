import Foundation

/// Load state for one Settings tab whose rows are read off the main actor. Every read takes a ticket, and only the
/// newest ticket may publish its rows or report its failure, so a read that finishes after a newer one can never put
/// older rows, or an error that no longer applies, on screen. The storage queue runs reads in the order they were
/// queued, but that orders the SQLite work, not the moments the waiting tasks resume, so the order results arrive in
/// is not something a tab can rely on.
///
/// The macOS port of Windows' `SettingsSectionLoad` (`SettingsWindow` publishes and fails its history load through
/// it). It leaves out Windows' saved-state snapshot, which only Windows' batch Save needs, and `Invalidate`, which
/// exists for writes made around a loaded grid: every macOS Settings edit is written the moment it is made, and the
/// tab reads its rows again afterwards, which starts a newer read than any already running.
///
/// Not thread-safe by design: its owner is a main-actor model, and the reads it tickets never touch it.
struct SettingsSectionLoad: Equatable, Sendable {
    enum State: Equatable, Sendable {
        /// Nothing has been read yet.
        case unloaded
        /// A read is running.
        case loading
        /// What the tab shows came from a completed read.
        case loaded
        /// The newest read failed.
        case failed
    }

    typealias Ticket = UInt64

    private(set) var state = State.unloaded
    private var newest: Ticket = 0

    var isLoaded: Bool { state == .loaded }

    /// Starts a read and returns its ticket. A read already running can no longer publish.
    mutating func begin() -> Ticket {
        newest &+= 1
        state = .loading
        return newest
    }

    /// Whether a finished read may still be shown: it is the newest read, and nothing has settled it yet.
    func canPublish(_ ticket: Ticket) -> Bool {
        ticket == newest && state == .loading
    }

    /// Records that the tab now shows the read `ticket` belongs to. Returns false, changing nothing, for a stale read.
    mutating func publish(_ ticket: Ticket) -> Bool {
        guard canPublish(ticket) else {
            return false
        }
        state = .loaded
        return true
    }

    /// Records that the read `ticket` belongs to failed. Returns false, changing nothing, for a stale read.
    mutating func fail(_ ticket: Ticket) -> Bool {
        guard canPublish(ticket) else {
            return false
        }
        state = .failed
        return true
    }
}
