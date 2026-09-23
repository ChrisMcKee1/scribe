import AppKit
import OSLog
import ServiceManagement

/// What macOS reports for Scribe as a login item (`SMAppService.mainApp.status`).
enum LoginItemState: Equatable, Sendable {
    /// Scribe opens at login.
    case enabled
    /// Scribe is not a login item.
    case notRegistered
    /// Scribe is registered, but macOS will not open it at login until the user allows it in System Settings.
    case requiresApproval
    /// macOS cannot find this copy of Scribe as a login item, which is what a build run outside an app bundle sees.
    case notFound
    /// A status this build does not know.
    case unrecognized

    init(_ status: SMAppService.Status) {
        switch status {
        case .enabled: self = .enabled
        case .notRegistered: self = .notRegistered
        case .requiresApproval: self = .requiresApproval
        case .notFound: self = .notFound
        @unknown default: self = .unrecognized
        }
    }

    /// Only `.enabled` means Scribe will actually open at login, so it is the only state the switch shows as on.
    var isOn: Bool {
        self == .enabled
    }
}

/// Why a requested login item change did not happen.
enum LoginItemRefusal: Equatable, Sendable {
    /// The user turned Scribe off in System Settings, and only they can turn it back on there.
    case deniedByUser
    /// macOS did not allow the change, which usually means this copy is not an app in Applications.
    case notPermitted
    /// macOS accepted the request but still reports the old state.
    case noEffect
    /// Any other error, by its code, so a report can say which one.
    case failed(code: Int)
}

/// Reads and changes Scribe's login item. `SystemLoginItemService` talks to macOS; tests use a fake.
protocol LoginItemService: Sendable {
    func state() async -> LoginItemState

    /// Asks macOS to open Scribe at login, or to stop. Returns why macOS refused, or nil when it accepted.
    func request(enabled: Bool) async -> LoginItemRefusal?
}

/// `SMAppService.mainApp`, called off the main actor: every call is a round trip to a system service, and neither
/// Settings nor the hotkey tap on the main run loop should wait on one.
struct SystemLoginItemService: LoginItemService {
    func state() async -> LoginItemState {
        await Task.detached(priority: .userInitiated) { LoginItemManager.currentState }.value
    }

    func request(enabled: Bool) async -> LoginItemRefusal? {
        await Task.detached(priority: .userInitiated) { LoginItemManager.apply(enabled: enabled) }.value
    }
}

/// "Open at Login" through `SMAppService`, the replacement for a hand-written `~/Library/LaunchAgents` plist
/// (macOS 13 and later, this app's minimum). macOS keeps the only copy of the state, so Scribe saves no
/// preference of its own: the Settings switch shows what macOS reports and changes it directly. That is the macOS
/// side of Windows' `StartupToggle` (`src/Scribe.Core/Infrastructure/StartupToggle.cs`), without the saved
/// preference Windows needs for its launch-time reconcile.
enum LoginItemManager {
    static var currentState: LoginItemState {
        LoginItemState(SMAppService.mainApp.status)
    }

    /// True when Scribe will open at login. Used by the `--set-launch-at-login` CLI verb.
    static var isEnabled: Bool {
        currentState == .enabled
    }

    /// True when registered but waiting for the user to allow it in System Settings. Used by the CLI verb.
    static var requiresApproval: Bool {
        currentState == .requiresApproval
    }

    /// Registers or unregisters Scribe, blocking the caller. True when macOS accepted the change. The CLI verb's
    /// entry point; Settings goes through `SystemLoginItemService`, which also says why a change was refused.
    @discardableResult
    static func setEnabled(_ enabled: Bool) -> Bool {
        apply(enabled: enabled) == nil
    }

    /// Registers or unregisters Scribe, blocking the caller, and says why macOS refused. Asking for the state
    /// Scribe is already in makes no call, since macOS answers that with an error.
    static func apply(enabled: Bool) -> LoginItemRefusal? {
        let service = SMAppService.mainApp
        do {
            if enabled {
                guard service.status != .enabled else { return nil }
                try service.register()
            } else {
                guard service.status != .notRegistered else { return nil }
                try service.unregister()
            }
            return nil
        } catch {
            return refusal(for: error)
        }
    }

    /// Maps a ServiceManagement error to a reason the switch can show. The codes are the `kSMError` values in
    /// `<ServiceManagement/SMErrors.h>`, plus `EPERM`, which macOS reports as "Operation not permitted".
    static func refusal(for error: any Error) -> LoginItemRefusal? {
        let error = error as NSError
        guard error.domain == "SMAppServiceErrorDomain" else {
            return .failed(code: error.code)
        }
        switch error.code {
        case Int(kSMErrorAlreadyRegistered):
            return nil
        case Int(kSMErrorLaunchDeniedByUser):
            return .deniedByUser
        case Int(EPERM):
            return .notPermitted
        default:
            return .failed(code: error.code)
        }
    }

    /// Opens System Settings > General > Login Items.
    @MainActor
    static func openSystemSettings() {
        SMAppService.openSystemSettingsLoginItems()
    }
}

/// The "Open at Login" switch. It shows what macOS reports: read when the switch appears, again whenever Scribe
/// becomes active (after a visit to System Settings, say) and after every change it makes. A flip applies at once,
/// like Windows' Start with Windows switch, and there is nothing to save. A read that started before a flip is
/// dropped when it finishes, so it can never paint an older state over the one the flip produced, and a read
/// running while the user flips never blocks the flip.
@MainActor
final class LoginItemSwitch: ObservableObject {
    enum Outcome: Equatable {
        /// The switch already showed the requested state, or macOS has not answered the first read yet.
        case unchanged
        /// An earlier flip is still being applied, so this one was not started.
        case busy
        /// macOS now reports the requested state.
        case applied
        /// macOS accepted, but will only open Scribe at login once the user allows it in System Settings.
        case needsApproval
        /// macOS refused or kept the old state; `refusal` says why.
        case declined
    }

    @Published private(set) var state: LoginItemState?
    @Published private(set) var refusal: LoginItemRefusal?
    /// The value a flip in progress asked for, shown on the switch until macOS answers.
    @Published private(set) var pendingRequest: Bool?

    private let service: any LoginItemService
    private let openLoginItemsAction: @MainActor () -> Void
    private let logger = Logger(subsystem: "com.scribe.macos", category: "LoginItem")
    /// Advances when a flip starts, so a read that began before it can tell it has been overtaken.
    private var version = 0
    private var isReading = false
    private var observation: SettingsNotificationObservation?
    private(set) var refreshTask: Task<Void, Never>?

    init(
        service: any LoginItemService = SystemLoginItemService(),
        openLoginItems: @escaping @MainActor () -> Void = LoginItemManager.openSystemSettings,
        activation: Notification.Name? = NSApplication.didBecomeActiveNotification,
        center: NotificationCenter = .default
    ) {
        self.service = service
        openLoginItemsAction = openLoginItems
        if let activation {
            observation = SettingsNotificationObservation(activation, center: center) { [weak self] in
                self?.refreshInBackground()
            }
        }
    }

    var isOn: Bool {
        pendingRequest ?? (state?.isOn ?? false)
    }

    /// The switch can be flipped once macOS has answered the first read, and while no flip is being applied.
    var canFlip: Bool {
        state != nil && pendingRequest == nil
    }

    var showsOpenLoginItems: Bool {
        state == .requiresApproval || refusal == .deniedByUser
    }

    var message: String {
        if let refusal {
            switch refusal {
            case .deniedByUser:
                return "Scribe is turned off in System Settings. To open it at login, allow it in Login Items."
            case .notPermitted:
                return "macOS did not allow this change. Open Scribe from your Applications folder and try again."
            case .noEffect:
                return "macOS did not make this change. Check Login Items in System Settings."
            case .failed(let code):
                return "macOS did not make this change (error \(code)). Check Login Items in System Settings."
            }
        }
        switch state {
        case nil:
            return "Checking with macOS\u{2026}"
        case .enabled?, .notRegistered?:
            return "Open Scribe automatically when you log in."
        case .requiresApproval?:
            return "Waiting for your approval. Allow Scribe in System Settings > General > Login Items."
        case .notFound?:
            return "macOS cannot add this copy of Scribe. Open Scribe from your Applications folder and try again."
        case .unrecognized?:
            return "Scribe could not read this setting. Check Login Items in System Settings."
        }
    }

    /// Reads what macOS reports. Skipped while a flip is applied, since the flip reads the state itself when it
    /// finishes, and while another read is running.
    func refresh() async {
        guard pendingRequest == nil, !isReading else { return }
        isReading = true
        let ticket = version
        let read = await service.state()
        isReading = false
        guard pendingRequest == nil, ticket == version else { return }
        state = read
        refusal = nil
    }

    /// Starts a read without waiting for it. The switch stays usable while it runs.
    func refreshInBackground() {
        refreshTask = Task { [weak self] in
            await self?.refresh()
        }
    }

    /// Asks macOS for `requested`, then shows what macOS reports afterwards.
    @discardableResult
    func setEnabled(_ requested: Bool) async -> Outcome {
        guard pendingRequest == nil else { return .busy }
        guard let shown = state, shown.isOn != requested else { return .unchanged }

        pendingRequest = requested
        version += 1
        let refused = await service.request(enabled: requested)
        let after = await service.state()
        pendingRequest = nil
        state = after

        let outcome: Outcome
        if after.isOn == requested {
            refusal = nil
            outcome = .applied
        } else if requested, after == .requiresApproval, refused == nil {
            refusal = nil
            outcome = .needsApproval
        } else {
            refusal = refused ?? .noEffect
            outcome = .declined
        }
        // Enum names, a flag and an error code only, never a message from macOS.
        let refusalShape = refused.map { "\($0)" } ?? "none"
        let shape = "requested=\(requested) before=\(shown) after=\(after) refusal=\(refusalShape) outcome=\(outcome)"
        logger.info("Login item change: \(shape, privacy: .public)")
        return outcome
    }

    func openLoginItems() {
        openLoginItemsAction()
    }
}
