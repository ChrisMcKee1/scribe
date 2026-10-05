import Foundation

/// Suppresses repeated cleanup-failure notifications until cleanup works again or its configuration changes.
struct CleanupNoticeEpisode: Sendable, Equatable {
    enum Event: Sendable {
        case failed
        case recovered
        case configurationChanged
    }

    private(set) var hasNotifiedFailure = false

    mutating func apply(_ event: Event) -> Bool {
        switch event {
        case .failed:
            guard !hasNotifiedFailure else { return false }
            hasNotifiedFailure = true
            return true
        case .recovered, .configurationChanged:
            hasNotifiedFailure = false
            return false
        }
    }
}
