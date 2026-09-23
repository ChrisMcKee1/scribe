import AppKit
import OSLog
import ServiceManagement

/// What macOS reports for Scribe as a login item (`SMAppService.mainApp.status`).
enum LoginItemState: Equatable, Sendable {
    /// Scribe opens at login.
    case enabled
    /// Scribe is not a login item.
    case notRegistered
    /// Registered, which macOS counts as a successful registration even after the user revoked consent, but macOS
    /// will not open Scribe at login until the user allows it in System Settings. Registering again is not an
    /// approval.
    case requiresApproval
    /// macOS has no record of this copy as a login item. A copy that has never been registered can report this
    /// rather than `.notRegistered`, so it permits a registration, and like `.notRegistered` it has nothing to
    /// unregister.
    case notFound
    /// A status this build does not know, so Scribe changes nothing.
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
    /// macOS reported a state Scribe does not know, so Scribe asked for nothing.
    case unrecognizedState
    /// Any other error, by its code, so a report can say which one.
    case failed(code: Int)
}

/// The one `SMAppService` call a request permits, decided from the state macOS reported just before it.
enum LoginItemOperation: Equatable, Sendable {
    /// macOS already reports the requested state.
    case nothingToDo
    case register
    case unregister
    /// Registered and waiting for the user's approval, which only they can give, in System Settings > General >
    /// Login Items. Scribe does not register again.
    case awaitApproval
    /// A state Scribe does not know; it asks macOS for nothing.
    case unavailable

    init(enabling enabled: Bool, from state: LoginItemState) {
        switch (state, enabled) {
        case (.enabled, true), (.notRegistered, false):
            self = .nothingToDo
        case (.notFound, false):
            self = .unregister // MUTATION N2: notFound treated as registered
        case (.notRegistered, true), (.notFound, true):
            self = .register
        case (.enabled, false), (.requiresApproval, false):
            self = .unregister
        case (.requiresApproval, true):
            self = .awaitApproval
        case (.unrecognized, _):
            self = .unavailable
        }
    }
}

/// The `SMAppService` calls Scribe makes, behind a protocol so a test can see which call each state permits
/// without touching the system service.
protocol LoginItemControl {
    var status: SMAppService.Status { get }
    func register() throws
    func unregister() throws
}

extension SMAppService: LoginItemControl {}

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

    /// Registers or unregisters Scribe, blocking the caller. True when macOS accepted the change or had nothing to
    /// do. The CLI verb's entry point; Settings goes through `SystemLoginItemService`, which also says why a change
    /// was refused.
    @discardableResult
    static func setEnabled(_ enabled: Bool) -> Bool {
        apply(enabled: enabled) == nil
    }

    /// Makes the one call `LoginItemOperation` permits for the state macOS reports now, blocking the caller, and
    /// says why macOS refused. A request for the state Scribe is already in makes no call, and neither does a
    /// request to enable a registration that is waiting for approval.
    static func apply(enabled: Bool, control: any LoginItemControl = SMAppService.mainApp) -> LoginItemRefusal? {
        do {
            switch LoginItemOperation(enabling: enabled, from: LoginItemState(control.status)) {
            case .nothingToDo:
                return nil
            case .awaitApproval:
                try control.register() // MUTATION N3: registers again while approval is pending
            case .unavailable:
                return .unrecognizedState
            case .register:
                try control.register()
            case .unregister:
                try control.unregister()
            }
            return nil
        } catch {
            return refusal(for: error)
        }
    }

    /// Maps a ServiceManagement error to a reason the switch can show. The codes are the `kSMError` values in
    /// `<ServiceManagement/SMErrors.h>`, plus `EPERM`, which macOS reports as "Operation not permitted". An
    /// already-registered error is not a refusal: the state read afterwards says what macOS did.
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
/// like Windows' Start with Windows switch, and there is nothing to save. Turning on a registration that is waiting
/// for approval opens Login Items instead, since only the user can approve it. A read that something newer
/// overtook is dropped when it finishes (a flip, or another request to read, which then gets a read of its own),
/// so an older state is never painted over a newer one, and a read in flight never blocks a flip.
@MainActor
final class LoginItemSwitch: ObservableObject {
    enum Outcome: Equatable {
        /// The switch already showed the requested state, or macOS has not answered the first read yet.
        case unchanged
        /// An earlier flip is still being applied, so this one was not started.
        case busy
        /// macOS now reports the requested state.
        case applied
        /// macOS will only open Scribe at login once the user allows it in System Settings.
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
    /// Advances when a flip starts or a read is requested while another is running, so a read that began earlier
    /// can tell it has been overtaken.
    private var generation = 0
    private var isReading = false
    /// Set when a read was requested while one was running; the running one then reads again before it stops.
    private var readAgain = false
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

    /// The switch can be flipped once macOS has answered the first read with a state Scribe knows, and while no
    /// flip is being applied.
    var canFlip: Bool {
        guard let state, state != .unrecognized else { return false }
        return pendingRequest == nil
    }

    var showsOpenLoginItems: Bool {
        state == .requiresApproval || state == .unrecognized || refusal == .deniedByUser
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
            case .unrecognizedState:
                return "Scribe could not read this setting. Check Login Items in System Settings."
            case .failed(let code):
                return "macOS did not make this change (error \(code)). Check Login Items in System Settings."
            }
        }
        switch state {
        case nil:
            return "Checking with macOS\u{2026}"
        case .enabled?, .notRegistered?, .notFound?:
            return "Open Scribe automatically when you log in."
        case .requiresApproval?:
            return "Waiting for your approval. Allow Scribe in System Settings > General > Login Items."
        case .unrecognized?:
            return "Scribe could not read this setting. Check Login Items in System Settings."
        }
    }

    /// Reads what macOS reports. A request that arrives while a read is running overtakes it, because the user
    /// may have changed the state in System Settings after that read began, and the running read then reads again.
    /// Nothing is read while a flip is applied: the flip reads the state itself when it finishes.
    func refresh() async {
        guard pendingRequest == nil else { return }
        guard !isReading else {
            return // MUTATION N4: a request during a read is dropped
        }
        isReading = true
        defer { isReading = false }
        repeat {
            readAgain = false
            let ticket = generation
            let read = await service.state()
            if pendingRequest == nil, ticket == generation {
                state = read
                refusal = nil
            }
        } while readAgain && pendingRequest == nil
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
        switch LoginItemOperation(enabling: requested, from: shown) {
        case .awaitApproval:
            break // MUTATION N1: approval-pending registers again
        case .unavailable, .nothingToDo:
            return .unchanged
        case .register, .unregister:
            break
        }

        pendingRequest = requested
        generation += 1
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