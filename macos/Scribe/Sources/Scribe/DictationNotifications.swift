import AppKit
import Foundation
import UserNotifications

/// A System Settings privacy pane a notice can open.
enum PrivacyPane: String, Sendable, Equatable {
    case accessibility = "Privacy_Accessibility"
    case inputMonitoring = "Privacy_ListenEvent"
    case microphone = "Privacy_Microphone"

    var settingsURL: URL? {
        URL(string: "x-apple.systempreferences:com.apple.preference.security?\(rawValue)")
    }
}

/// Something that stopped part of Scribe from working when it started.
enum StartupProblem: String, Sendable, Equatable, CaseIterable {
    /// The database could not be opened or migrated, or the first rule read failed: dictation runs without the
    /// user's dictionary rules, snippets and app profiles until a later load succeeds.
    case rulesUnavailable
    /// Input Monitoring is not granted, so the push-to-talk key cannot be heard.
    case inputMonitoringMissing
    /// Accessibility is not granted, so dictated text cannot be inserted.
    case accessibilityMissing
}

/// A non-modal notice, posted as a local notification: it never takes focus from the app the user is typing in.
struct DictationNotice: Equatable, Sendable {
    enum Kind: String, Equatable, Sendable {
        case notInserted
        case partlyInserted
        case mayNotBeInserted
        case accessibilityNeeded
        case microphoneAccessNeeded
        case microphoneUnavailable
        case recognizerMissing
        case cleanupFellBack
        case transcriptionFailed
        case startup
    }

    let kind: Kind
    let title: String
    let body: String
    /// The dictation the notice is about, for its Copy Transcript action. Kept in memory only.
    let recoveryText: String?
    /// `LastTranscriptStore.generation` when `recoveryText` was kept for recovery. After a Clear the notice is
    /// obsolete: it is not posted, and its Copy Transcript action copies nothing.
    let recoveryGeneration: UInt64?
    /// The privacy pane its Open System Settings action opens.
    let settingsPane: PrivacyPane?

    init(
        kind: Kind, title: String, body: String, recoveryText: String?, recoveryGeneration: UInt64? = nil,
        settingsPane: PrivacyPane?
    ) {
        self.kind = kind
        self.title = title
        self.body = body
        self.recoveryText = recoveryText
        self.recoveryGeneration = recoveryGeneration
        self.settingsPane = settingsPane
    }

    static func notInserted(_ transcript: String, recoveryGeneration: UInt64) -> DictationNotice {
        DictationNotice(
            kind: .notInserted,
            title: "Dictation could not be inserted",
            body: "Use \u{201C}Copy Transcript\u{201D} below or the Recent Dictations menu to recover it.",
            recoveryText: transcript,
            recoveryGeneration: recoveryGeneration,
            settingsPane: nil)
    }

    static func partlyInserted(_ transcript: String, recoveryGeneration: UInt64) -> DictationNotice {
        DictationNotice(
            kind: .partlyInserted,
            title: "Dictation was only partly inserted",
            body: "Scribe stopped part way through typing it. Use \u{201C}Copy Transcript\u{201D} below or the "
                + "Recent Dictations menu to recover the full text.",
            recoveryText: transcript,
            recoveryGeneration: recoveryGeneration,
            settingsPane: nil)
    }

    static func mayNotBeInserted(_ transcript: String, recoveryGeneration: UInt64) -> DictationNotice {
        DictationNotice(
            kind: .mayNotBeInserted,
            title: "Dictation may not have been inserted",
            body: "The app stopped responding while Scribe was inserting it, so the text may still appear. If it "
                + "does not, use \u{201C}Copy Transcript\u{201D} below or the Recent Dictations menu.",
            recoveryText: transcript,
            recoveryGeneration: recoveryGeneration,
            settingsPane: nil)
    }

    static func accessibilityNeeded(_ transcript: String, recoveryGeneration: UInt64) -> DictationNotice {
        DictationNotice(
            kind: .accessibilityNeeded,
            title: "Scribe needs Accessibility access",
            body: "Allow Scribe in System Settings > Privacy & Security > Accessibility to insert dictations. This "
                + "one is kept: use \u{201C}Copy Transcript\u{201D} below or the Recent Dictations menu.",
            recoveryText: transcript,
            recoveryGeneration: recoveryGeneration,
            settingsPane: .accessibility)
    }

    /// Posted only when the pill could not say so at the time (`OverlayNotice.notifiesWhenThePillIsBusy`), and
    /// before the dictation's delivery, so it speaks about cleanup alone: whether the text went in is the delivery's
    /// own outcome to report.
    static let cleanupFellBack = DictationNotice(
        kind: .cleanupFellBack,
        title: "AI cleanup could not be used",
        body: "AI cleanup failed or gave a reply that could not be used for this dictation.",
        recoveryText: nil,
        settingsPane: nil)

    /// Posted only when the pill could not say so at the time (`OverlayNotice.notifiesWhenThePillIsBusy`).
    static let transcriptionFailed = DictationNotice(
        kind: .transcriptionFailed,
        title: "A dictation could not be transcribed",
        body: "The speech recognizer failed, so nothing was inserted. Please dictate it again.",
        recoveryText: nil,
        settingsPane: nil)

    static let microphoneAccessNeeded = DictationNotice(
        kind: .microphoneAccessNeeded,
        title: "Scribe needs the microphone",
        body: "Allow Scribe in System Settings > Privacy & Security > Microphone, then dictate again.",
        recoveryText: nil,
        settingsPane: .microphone)

    static let microphoneUnavailable = DictationNotice(
        kind: .microphoneUnavailable,
        title: "The microphone could not be opened",
        body: "Check the microphone chosen in Scribe's Settings > Input, then dictate again.",
        recoveryText: nil,
        settingsPane: nil)

    static func recognizerMissing(_ issue: TranscriptionBackendIssue) -> DictationNotice {
        DictationNotice(
            kind: .recognizerMissing,
            title: "No speech recognizer found",
            body: TranscriptionError.backendMissing(issue).errorDescription
                ?? "Install Foundry Local, then dictate again.",
            recoveryText: nil,
            settingsPane: nil)
    }

    /// One notice for everything that went wrong at startup, or nil when nothing did. It opens the first privacy
    /// pane that needs a change.
    static func startup(_ problems: [StartupProblem]) -> DictationNotice? {
        guard !problems.isEmpty else { return nil }
        var lines: [String] = []
        if problems.contains(.inputMonitoringMissing) {
            lines.append("Allow Scribe under Input Monitoring so it can hear the push-to-talk key.")
        }
        if problems.contains(.accessibilityMissing) {
            lines.append("Allow Scribe under Accessibility so it can insert what you dictate.")
        }
        if problems.contains(.rulesUnavailable) {
            lines.append(
                "Your dictionary rules, snippets and app profiles could not be read, so dictation works without them "
                    + "for now. Restarting Scribe tries again.")
        }
        let pane: PrivacyPane?
        if problems.contains(.inputMonitoringMissing) {
            pane = .inputMonitoring
        } else if problems.contains(.accessibilityMissing) {
            pane = .accessibility
        } else {
            pane = nil
        }
        return DictationNotice(
            kind: .startup,
            title: pane == nil ? "Scribe started without your rules" : "Scribe needs your permission",
            body: lines.joined(separator: " "),
            recoveryText: nil,
            settingsPane: pane)
    }
}

/// Collects what went wrong at startup and posts it once, as one notice, after every startup step that can report
/// a problem has settled: the storage and rule load, and the notification permission request (a notice posted
/// before macOS answers it is dropped).
@MainActor
final class StartupNotices {
    enum Step: Hashable, Sendable {
        case storage
        case notifications
    }

    private var problems: [StartupProblem] = []
    private var pending: Set<Step> = [.storage, .notifications]
    private let post: @MainActor (DictationNotice) -> Void
    private(set) var hasPosted = false

    init(post: @escaping @MainActor (DictationNotice) -> Void) {
        self.post = post
    }

    var reportedProblems: [StartupProblem] {
        problems
    }

    /// Adds a problem, unless the notice has already gone out.
    func report(_ problem: StartupProblem) {
        guard !hasPosted, !problems.contains(problem) else { return }
        problems.append(problem)
    }

    func settle(_ step: Step) {
        pending.remove(step)
        guard pending.isEmpty, !hasPosted else { return }
        hasPosted = true
        if let notice = DictationNotice.startup(problems) {
            post(notice)
        }
    }
}

/// The Copy Transcript texts of recent notices, kept in memory only and never in the notification itself, which
/// macOS stores on disk. Bounded, so an old notice's text is let go. Each text carries the recovery generation it was
/// kept in, and a Copy Transcript after Clear history finds nothing.
struct NotificationRecoveryTexts: Sendable {
    static let capacity = 5

    private var entries: [(identifier: String, text: String, generation: UInt64?)] = []

    mutating func remember(_ text: String, generation: UInt64?, for identifier: String) {
        entries.append((identifier, text, generation))
        if entries.count > Self.capacity {
            entries.removeFirst(entries.count - Self.capacity)
        }
    }

    /// The text of the notice `identifier`, unless a Clear has started a generation other than the one it was kept
    /// in.
    func text(for identifier: String, currentGeneration: UInt64) -> String? {
        guard let entry = entries.last(where: { $0.identifier == identifier }) else { return nil }
        if let generation = entry.generation, generation != currentGeneration {
            return nil
        }
        return entry.text
    }

    mutating func removeAll() {
        entries.removeAll()
    }
}

/// What the user did with a notice.
struct NotificationAnswer: Sendable, Equatable {
    let actionIdentifier: String
    let requestIdentifier: String
    let paneRawValue: String?
}

/// Posts `DictationNotice`s through `UNUserNotificationCenter` and handles their actions. Best-effort, like Windows'
/// tray balloons: a denied permission or a failed post is logged by its shape and nothing else happens. Created only
/// in the app bundle; the notification center needs one.
@MainActor
final class DictationNotificationCenter: DictationNotifying {
    static let recoveryCategoryIdentifier = "com.scribe.macos.injectionFailure"
    static let settingsCategoryIdentifier = "com.scribe.macos.openSettings"
    static let recoveryAndSettingsCategoryIdentifier = "com.scribe.macos.injectionFailureAndSettings"
    static let copyTranscriptActionIdentifier = "com.scribe.macos.copyTranscript"
    static let openSettingsActionIdentifier = "com.scribe.macos.openSystemSettings"
    private static let paneKey = "pane"

    private let center: UNUserNotificationCenter
    /// `LastTranscriptStore.generation` now, to refuse text a Clear has removed.
    private let recoveryGeneration: @MainActor () -> UInt64
    private var responder: NotificationResponder?
    private var recoveryTexts = NotificationRecoveryTexts()

    init(center: UNUserNotificationCenter, recoveryGeneration: @escaping @MainActor () -> UInt64) {
        self.center = center
        self.recoveryGeneration = recoveryGeneration
    }

    /// Registers the actions, becomes the delegate and asks for permission. `settled` runs on the main actor once
    /// macOS has answered, granted or not.
    func configure(settled: @escaping @MainActor @Sendable () -> Void) {
        let responder = NotificationResponder { [weak self] answer in
            self?.handle(answer)
        }
        self.responder = responder
        center.delegate = responder

        let copy = UNNotificationAction(
            identifier: Self.copyTranscriptActionIdentifier, title: "Copy Transcript", options: [])
        let openSettings = UNNotificationAction(
            identifier: Self.openSettingsActionIdentifier, title: "Open System Settings", options: [])
        center.setNotificationCategories([
            UNNotificationCategory(
                identifier: Self.recoveryCategoryIdentifier, actions: [copy], intentIdentifiers: [], options: []),
            UNNotificationCategory(
                identifier: Self.settingsCategoryIdentifier, actions: [openSettings], intentIdentifiers: [],
                options: []),
            UNNotificationCategory(
                identifier: Self.recoveryAndSettingsCategoryIdentifier, actions: [copy, openSettings],
                intentIdentifiers: [], options: []),
        ])

        // `@Sendable`, so the handler is not tied to the main actor: the center calls it on a queue of its own.
        center.requestAuthorization(options: [.alert, .sound]) { @Sendable granted, error in
            if let error {
                ScribeLog.warning(.app, "Notification permission could not be requested", .failure(error))
            } else if !granted {
                ScribeLog.warning(.app, "Notifications are not allowed, so dictation notices will not be shown")
            }
            Task { @MainActor in
                settled()
            }
        }
    }

    /// Forgets the transcripts earlier notices would copy, for Clear history: text the user asked to delete must not
    /// come back through a notice's Copy Transcript action either.
    func forgetRecoveryTexts() {
        recoveryTexts.removeAll()
    }

    func notify(_ notice: DictationNotice) {
        if let generation = notice.recoveryGeneration, generation != recoveryGeneration() {
            ScribeLog.info(.app, "A notice about text Clear history removed was not shown", .name("kind", notice.kind))
            return
        }
        let content = UNMutableNotificationContent()
        content.title = notice.title
        content.body = notice.body
        content.sound = .default
        switch (notice.recoveryText != nil, notice.settingsPane != nil) {
        case (true, true):
            content.categoryIdentifier = Self.recoveryAndSettingsCategoryIdentifier
        case (true, false):
            content.categoryIdentifier = Self.recoveryCategoryIdentifier
        case (false, true):
            content.categoryIdentifier = Self.settingsCategoryIdentifier
        case (false, false):
            break
        }
        if let pane = notice.settingsPane {
            content.userInfo = [Self.paneKey: pane.rawValue]
        }

        let identifier = UUID().uuidString
        if let text = notice.recoveryText {
            recoveryTexts.remember(text, generation: notice.recoveryGeneration, for: identifier)
        }
        let kind = notice.kind
        center.add(UNNotificationRequest(identifier: identifier, content: content, trigger: nil)) { @Sendable error in
            if let error {
                ScribeLog.warning(.app, "A notice could not be shown", .name("kind", kind), .failure(error))
            }
        }
    }

    private func handle(_ answer: NotificationAnswer) {
        switch answer.actionIdentifier {
        case Self.copyTranscriptActionIdentifier:
            guard
                let text = recoveryTexts.text(
                    for: answer.requestIdentifier, currentGeneration: recoveryGeneration())
            else {
                ScribeLog.info(.app, "Copy Transcript was chosen for a dictation Scribe no longer holds")
                return
            }
            let pasteboard = NSPasteboard.general
            pasteboard.clearContents()
            pasteboard.setString(text, forType: .string)
            ScribeLog.info(.app, "Copied a dictation from its notice", .count("characters", text.count))
        case Self.openSettingsActionIdentifier:
            guard let raw = answer.paneRawValue, let url = PrivacyPane(rawValue: raw)?.settingsURL else { return }
            NSWorkspace.shared.open(url)
        default:
            break
        }
    }
}

/// Stands in for the notification center when Scribe runs outside an app bundle (`swift run`), where macOS offers it
/// none: each notice is logged by its kind and goes no further.
@MainActor
final class SilentNotifier: DictationNotifying {
    func notify(_ notice: DictationNotice) {
        ScribeLog.info(.app, "A notice was not shown: notifications need the app bundle", .name("kind", notice.kind))
    }
}

/// The notification center's delegate. Not isolated to any actor, since the center calls it on a thread of its own
/// choosing: it reads what it needs from the response there and hands only those values to the main actor.
final class NotificationResponder: NSObject, UNUserNotificationCenterDelegate, Sendable {
    private let onAnswer: @MainActor @Sendable (NotificationAnswer) -> Void

    init(onAnswer: @escaping @MainActor @Sendable (NotificationAnswer) -> Void) {
        self.onAnswer = onAnswer
    }

    func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse) async {
        let request = response.notification.request
        let answer = NotificationAnswer(
            actionIdentifier: response.actionIdentifier,
            requestIdentifier: request.identifier,
            paneRawValue: request.content.userInfo["pane"] as? String)
        await onAnswer(answer)
    }

    // Shows a notice even while Scribe is the active app (Settings open), where macOS would otherwise hold it back.
    func userNotificationCenter(
        _ center: UNUserNotificationCenter, willPresent notification: UNNotification
    ) async -> UNNotificationPresentationOptions {
        [.banner, .list, .sound]
    }
}
