import Foundation

/// Runs a callback on the main actor each time one notification is posted, until it is released.
///
/// Settings tabs use it to re-read what the tray or another window stored while they were open
/// (`UserDefaults.didChangeNotification`) and to re-check system state when Scribe becomes active again
/// (`NSApplication.didBecomeActiveNotification`). Foundation posts both on the thread that caused them. A post on
/// the main thread, which is where every writer in this app runs, reaches the callback before `post` returns; a
/// post from any other thread is handed to the main actor rather than run where it arrived.
final class SettingsNotificationObservation {
    private let center: NotificationCenter
    private let token: any NSObjectProtocol

    init(
        _ name: Notification.Name,
        object: AnyObject? = nil,
        center: NotificationCenter = .default,
        onPost: @escaping @MainActor @Sendable () -> Void
    ) {
        self.center = center
        token = center.addObserver(forName: name, object: object, queue: nil) { _ in
            if Thread.isMainThread {
                MainActor.assumeIsolated { onPost() }
            } else {
                Task { @MainActor in onPost() }
            }
        }
    }

    deinit {
        center.removeObserver(token)
    }
}
