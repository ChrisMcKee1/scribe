import Foundation
import os

/// Hands a recording's events from the audio thread to a main-actor handler without flooding the main
/// queue. Meter readings coalesce: at most one delivery is waiting at any time, and it carries the newest
/// reading. Stop requests are never coalesced or dropped, and arrive in the order they were posted. A reading
/// posted in the same window as a stop request is delivered just before it; a recording posts nothing after
/// its stop request, so this only ever reorders events of different recordings, which carry their owner.
final class CaptureEventRelay: Sendable {
    private struct Pending: Sendable {
        var level: CaptureEvent?
        var stops: [CaptureEvent] = []
        var deliveryScheduled = false
    }

    private let pending = OSAllocatedUnfairLock(initialState: Pending())
    private let handler: @MainActor @Sendable (CaptureEvent) -> Void

    init(handler: @escaping @MainActor @Sendable (CaptureEvent) -> Void) {
        self.handler = handler
    }

    /// The closure to pass to `AudioCaptureEngine.start` as its `events`.
    var sink: @Sendable (CaptureEvent) -> Void {
        { [self] event in
            post(event)
        }
    }

    /// Callable from any thread; never blocks on the main actor.
    func post(_ event: CaptureEvent) {
        let schedule = pending.withLock { pending -> Bool in
            switch event.kind {
            case .level:
                pending.level = event
            case .stopRequested:
                pending.stops.append(event)
            }
            guard !pending.deliveryScheduled else { return false }
            pending.deliveryScheduled = true
            return true
        }
        guard schedule else { return }

        DispatchQueue.main.async { [self] in
            MainActor.assumeIsolated {
                deliver()
            }
        }
    }

    @MainActor
    private func deliver() {
        let (level, stops) = pending.withLock { pending -> (CaptureEvent?, [CaptureEvent]) in
            let taken = (pending.level, pending.stops)
            pending.level = nil
            pending.stops = []
            pending.deliveryScheduled = false
            return taken
        }
        if let level {
            handler(level)
        }
        for stop in stops {
            handler(stop)
        }
    }
}
