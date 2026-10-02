import Foundation
import os

/// Hands a recording's events from the audio thread to a main-actor handler without flooding the main
/// queue. Meter readings coalesce: at most one delivery is waiting at any time, carrying the newest reading.
/// Stop requests and microphone selections never coalesce and arrive in the order posted. A late open can report
/// its selection after its stop, so each event carries its recording's owner.
final class CaptureEventRelay: Sendable {
    private struct Pending: Sendable {
        var level: CaptureEvent?
        var orderedEvents: [CaptureEvent] = []
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
            case .stopRequested, .microphoneSelection:
                pending.orderedEvents.append(event)
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
        let (level, events) = pending.withLock { pending -> (CaptureEvent?, [CaptureEvent]) in
            let taken = (pending.level, pending.orderedEvents)
            pending.level = nil
            pending.orderedEvents = []
            pending.deliveryScheduled = false
            return taken
        }
        if let level {
            handler(level)
        }
        for event in events {
            handler(event)
        }
    }
}
