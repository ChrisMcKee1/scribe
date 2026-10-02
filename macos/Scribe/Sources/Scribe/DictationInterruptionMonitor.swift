import AppKit
import Foundation

struct DictationInterruptionSources {
    let addWorkspaceObserver:
        @MainActor (_ name: Notification.Name, _ onPost: @escaping @MainActor @Sendable () -> Void)
            -> NSObjectProtocol
    let removeWorkspaceObserver: @MainActor (_ token: NSObjectProtocol) -> Void
    let addDistributedObserver:
        @MainActor (_ name: Notification.Name, _ onPost: @escaping @MainActor @Sendable () -> Void) -> NSObjectProtocol
    let removeDistributedObserver: @MainActor (_ token: NSObjectProtocol) -> Void

    static func live(
        workspaceCenter: NotificationCenter = NSWorkspace.shared.notificationCenter,
        distributedCenter: DistributedNotificationCenter = .default()
    ) -> DictationInterruptionSources {
        DictationInterruptionSources(
            addWorkspaceObserver: { name, onPost in
                workspaceCenter.addObserver(forName: name, object: nil, queue: nil) { _ in
                    if Thread.isMainThread {
                        MainActor.assumeIsolated { onPost() }
                    } else {
                        Task { @MainActor in onPost() }
                    }
                }
            },
            removeWorkspaceObserver: { token in workspaceCenter.removeObserver(token) },
            addDistributedObserver: { name, onPost in
                distributedCenter.addObserver(forName: name, object: nil, queue: nil) { _ in
                    if Thread.isMainThread {
                        MainActor.assumeIsolated { onPost() }
                    } else {
                        Task { @MainActor in onPost() }
                    }
                }
            },
            removeDistributedObserver: { token in distributedCenter.removeObserver(token) })
    }

    static func testing(workspaceCenter: NotificationCenter = .init(), distributedCenter: NotificationCenter = .init())
        -> DictationInterruptionSources
    {
        DictationInterruptionSources(
            addWorkspaceObserver: { name, onPost in
                workspaceCenter.addObserver(forName: name, object: nil, queue: nil) { _ in
                    Task { @MainActor in onPost() }
                }
            },
            removeWorkspaceObserver: { token in workspaceCenter.removeObserver(token) },
            addDistributedObserver: { name, onPost in
                distributedCenter.addObserver(forName: name, object: nil, queue: nil) { _ in
                    Task { @MainActor in onPost() }
                }
            },
            removeDistributedObserver: { token in distributedCenter.removeObserver(token) })
    }
}

/// Ends a dictation when macOS locks the session or sleeps the displays, mirroring Windows 0.4.4's "locking your PC
/// ends a dictation" rule.
@MainActor
final class DictationInterruptionMonitor {
    private static let sessionDidResignActiveNotification =
        Notification.Name("NSWorkspaceSessionDidResignActiveNotification")
    private static let screenLockedNotification = Notification.Name("com.apple.screenIsLocked")

    private let sources: DictationInterruptionSources
    private let workspaceTokens: [NSObjectProtocol]
    private let distributedTokens: [NSObjectProtocol]

    init(
        sources: DictationInterruptionSources = .live(),
        onInterruption: @escaping @MainActor @Sendable () -> Void
    ) {
        self.sources = sources
        workspaceTokens = [
            sources.addWorkspaceObserver(NSWorkspace.screensDidSleepNotification, onInterruption),
            sources.addWorkspaceObserver(Self.sessionDidResignActiveNotification, onInterruption),
        ]
        distributedTokens = [sources.addDistributedObserver(Self.screenLockedNotification, onInterruption)]
    }
}
