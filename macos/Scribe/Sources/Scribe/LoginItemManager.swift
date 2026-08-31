import ServiceManagement

/// "Open at Login" via `SMAppService`, the modern replacement for a hand-rolled `~/Library/
/// LaunchAgents` plist (macOS 13+, matching this app's `LSMinimumSystemVersion`). Mirrors Windows'
/// `StartupRegistration` (`src/Scribe.App/Infrastructure/StartupRegistration.cs`), which manages
/// the `HKCU...\Run` key; the macOS equivalent needs no such manual bookkeeping because
/// `SMAppService` itself is the durable, system-tracked source of truth; there is no separate
/// preference to keep in sync or self-heal.
enum LoginItemManager {
    /// True when the app is currently registered to launch at login. `.enabled` is the only status
    /// that means "will actually launch"; `.requiresApproval` means the user must flip it on in
    /// System Settings > General > Login Items, and `.notFound`/`.notRegistered` both mean off.
    static var isEnabled: Bool {
        SMAppService.mainApp.status == .enabled
    }

    /// True when registered but the user still needs to approve it in System Settings, so the
    /// Settings UI can show an actionable hint instead of a silently-ignored toggle.
    static var requiresApproval: Bool {
        SMAppService.mainApp.status == .requiresApproval
    }

    /// Registers or unregisters the app as a login item. Returns true on success; failures (no
    /// bundle identifier outside a packaged .app, a user declining approval, etc.) are caught and
    /// reported as false rather than thrown, matching `StartupRegistration.Set`'s
    /// swallow-and-report-false contract.
    @discardableResult
    static func setEnabled(_ enabled: Bool) -> Bool {
        do {
            if enabled {
                if SMAppService.mainApp.status != .enabled {
                    try SMAppService.mainApp.register()
                }
            } else {
                if SMAppService.mainApp.status != .notRegistered {
                    try SMAppService.mainApp.unregister()
                }
            }
            return true
        } catch {
            return false
        }
    }
}
