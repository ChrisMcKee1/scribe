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
        case tooQuick
        case noAudio
        case onlySilence
        case noWordsRecognized
        case fallbackMicrophone
        case microphoneDisconnected
        case durationLimit
        case copied
        case copyFailed
        case quickAdd
        case cleanupActivation
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
            title: "Couldn't type your dictation",
            body: "The window changed or didn't accept the text. Use \u{201C}Copy Transcript\u{201D} below or "
                + "open Scribe's menu bar icon and choose Recent Dictations, then paste it.",
            recoveryText: transcript,
            recoveryGeneration: recoveryGeneration,
            settingsPane: nil)
    }

    static func partlyInserted(_ transcript: String, recoveryGeneration: UInt64) -> DictationNotice {
        DictationNotice(
            kind: .partlyInserted,
            title: "Couldn't type all of your dictation",
            body: "This app didn't accept all of the text. Use \u{201C}Copy Transcript\u{201D} below or the "
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
        title: "AI cleanup isn't working",
        body: "Scribe types what it hears. To check AI cleanup, open Settings, AI cleanup.",
        recoveryText: nil,
        settingsPane: nil)

    static let transcriptionFailed = DictationNotice(
        kind: .transcriptionFailed,
        title: "Dictation didn't finish",
        body: "Something went wrong while Scribe turned your speech into text. Try again. If it keeps happening, "
            + "open Settings, Diagnostics.",
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
        title: "Couldn't start recording",
        body:
            "Scribe couldn't open your microphone. Check that it's connected, or choose another in Settings, Dictation.",
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

    static func tooQuick(_ trigger: DictationTrigger) -> DictationNotice {
        let instruction =
            trigger.gesture == .hold
            ? "That was too quick. Hold your shortcut while you speak, then let go."
            : "That was too quick. Start recording, speak, then stop it."
        return DictationNotice(
            kind: .tooQuick, title: "Nothing recorded", body: instruction, recoveryText: nil, settingsPane: nil)
    }

    static let noAudio = DictationNotice(
        kind: .noAudio, title: "No sound recorded",
        body: "Scribe didn't get any sound from your microphone. Check that it's connected, or choose another in "
            + "Settings, Dictation.", recoveryText: nil, settingsPane: nil)

    static let onlySilence = DictationNotice(
        kind: .onlySilence, title: "Only silence recorded",
        body: "Your microphone may be muted. Unmute it and try again.", recoveryText: nil, settingsPane: nil)

    static let noWordsRecognized = DictationNotice(
        kind: .noWordsRecognized, title: "No words recognized",
        body: "Scribe didn't catch any words. Try again, a little closer to the microphone.",
        recoveryText: nil, settingsPane: nil)

    static func fallbackMicrophone(_ result: MicrophoneSelectionOutcome.Result) -> DictationNotice {
        let body =
            result == .systemDefault
            ? "Your chosen microphone isn't available, so Scribe is recording from the system default microphone. "
                + "To choose another, open Settings, Dictation."
            : "Scribe couldn't confirm your chosen microphone. Check that it's connected, or choose another in "
                + "Settings, Dictation."
        return DictationNotice(
            kind: .fallbackMicrophone,
            title: result == .systemDefault ? "Using another microphone" : "Check your microphone",
            body: body, recoveryText: nil, settingsPane: nil)
    }

    static let microphoneDisconnected = DictationNotice(
        kind: .microphoneDisconnected, title: "Microphone stopped",
        body: "Your microphone stopped during the dictation. Check that it's connected, then try again.",
        recoveryText: nil, settingsPane: nil)

    static func durationLimit(_ duration: Duration) -> DictationNotice {
        let minutes = max(1, Int(duration.components.seconds / 60))
        let unit = minutes == 1 ? "minute" : "minutes"
        return DictationNotice(
            kind: .durationLimit, title: "Dictation stopped at \(minutes) \(unit)",
            body: "Scribe stopped recording and is processing what it heard. The recording limit is shown in "
                + "Settings, Advanced.", recoveryText: nil, settingsPane: nil)
    }

    static let copiedRecentDictation = DictationNotice(
        kind: .copied, title: "Copied",
        body: "That dictation is on the clipboard. Press Command+V to paste it.", recoveryText: nil, settingsPane: nil)

    static let copyFailed = DictationNotice(
        kind: .copyFailed, title: "Couldn't copy",
        body: "Another app may be using the clipboard. Try again in a moment.", recoveryText: nil, settingsPane: nil)

    static let quickAddOpenFailed = DictationNotice(
        kind: .quickAdd, title: "Couldn't open Add to dictionary",
        body: "Try again, or add the word in Settings, Dictionary.", recoveryText: nil, settingsPane: nil)

    static let quickAddSaved = DictationNotice(
        kind: .quickAdd, title: "Saved to your dictionary",
        body: "Scribe uses it from your next dictation.", recoveryText: nil, settingsPane: nil)

    static let quickAddNotInUse = DictationNotice(
        kind: .quickAdd, title: "Saved, but not in use yet",
        body: "Scribe saved your word but couldn't start using it. Quit and reopen Scribe to use it.",
        recoveryText: nil, settingsPane: nil)

    static func cleanupActivation(_ enabled: Bool) -> DictationNotice {
        DictationNotice(
            kind: .cleanupActivation, title: enabled ? "AI cleanup is on" : "AI cleanup is off",
            body: enabled
                ? "Scribe uses the AI cleanup service saved in Settings. Until it's ready, Scribe types what it hears."
                : "Scribe types what it hears, with your dictionary and voice snippets.",
            recoveryText: nil, settingsPane: nil)
    }

    var playsSound: Bool {
        switch kind {
        case .copied, .cleanupActivation, .fallbackMicrophone:
            return false
        case .quickAdd:
            return title != Self.quickAddSaved.title
        default:
            return true
        }
    }

    var opensScribeSettings: Bool {
        switch kind {
        case .transcriptionFailed, .microphoneUnavailable, .noAudio, .fallbackMicrophone,
            .microphoneDisconnected, .durationLimit, .cleanupFellBack, .quickAdd:
            return kind != .quickAdd || title != Self.quickAddSaved.title
        case .startup:
            return settingsPane == nil
        default:
            return false
        }
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

enum QuickAddNotice {
    static func forRefresh(applied: Bool) -> DictationNotice {
        applied ? .quickAddSaved : .quickAddNotInUse
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
    private var isClosed = false

    init(post: @escaping @MainActor (DictationNotice) -> Void) {
        self.post = post
    }

    var reportedProblems: [StartupProblem] {
        problems
    }

    /// The startup batch is sent once; a later fault starts a new episode only after actual recovery.
    func report(_ problem: StartupProblem) {
        guard !isClosed, !problems.contains(problem) else { return }
        problems.append(problem)
        if hasPosted, let notice = DictationNotice.startup([problem]) {
            post(notice)
        }
    }

    func recover(_ problem: StartupProblem) {
        problems.removeAll { $0 == problem }
    }

    func close() {
        isClosed = true
    }

    func settle(_ step: Step) {
        guard !isClosed else { return }
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
    static let appSettingsCategoryIdentifier = "com.scribe.macos.openScribeSettings"
    static let openAppSettingsActionIdentifier = "com.scribe.macos.showScribeSettings"
    private static let paneKey = "pane"

    private let center: UNUserNotificationCenter
    /// `LastTranscriptStore.generation` now, to refuse text a Clear has removed.
    private let recoveryGeneration: @MainActor () -> UInt64
    private var responder: NotificationResponder?
    private var recoveryTexts = NotificationRecoveryTexts()
    private var copyEpisode = TrayActionNoticeEpisode()
    private let openScribeSettings: @MainActor () -> Void

    init(
        center: UNUserNotificationCenter, recoveryGeneration: @escaping @MainActor () -> UInt64,
        openScribeSettings: @escaping @MainActor () -> Void = {}
    ) {
        self.center = center
        self.recoveryGeneration = recoveryGeneration
        self.openScribeSettings = openScribeSettings
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
        let openAppSettings = UNNotificationAction(
            identifier: Self.openAppSettingsActionIdentifier, title: "Open Scribe Settings", options: [.foreground])
        center.setNotificationCategories([
            UNNotificationCategory(
                identifier: Self.recoveryCategoryIdentifier, actions: [copy], intentIdentifiers: [], options: []),
            UNNotificationCategory(
                identifier: Self.settingsCategoryIdentifier, actions: [openSettings], intentIdentifiers: [],
                options: []),
            UNNotificationCategory(
                identifier: Self.recoveryAndSettingsCategoryIdentifier, actions: [copy, openSettings],
                intentIdentifiers: [], options: []),
            UNNotificationCategory(
                identifier: Self.appSettingsCategoryIdentifier, actions: [openAppSettings],
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

    static func content(for notice: DictationNotice) -> UNMutableNotificationContent {
        let content = UNMutableNotificationContent()
        content.title = notice.title
        content.body = notice.body
        if notice.playsSound { content.sound = .default }
        switch (notice.recoveryText != nil, notice.settingsPane != nil) {
        case (true, true):
            content.categoryIdentifier = Self.recoveryAndSettingsCategoryIdentifier
        case (true, false):
            content.categoryIdentifier = Self.recoveryCategoryIdentifier
        case (false, true):
            content.categoryIdentifier = Self.settingsCategoryIdentifier
        case (false, false):
            if notice.opensScribeSettings { content.categoryIdentifier = Self.appSettingsCategoryIdentifier }
        }
        if let pane = notice.settingsPane {
            content.userInfo = [Self.paneKey: pane.rawValue]
        }
        return content
    }

    func notify(_ notice: DictationNotice) {
        if let generation = notice.recoveryGeneration, generation != recoveryGeneration() {
            ScribeLog.info(.app, "A notice about text Clear history removed was not shown", .name("kind", notice.kind))
            return
        }
        let content = Self.content(for: notice)
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
            guard pasteboard.setString(text, forType: .string) else {
                ScribeLog.warning(.app, "Could not copy a dictation from its notice")
                if copyEpisode.failed() { notify(.copyFailed) }
                return
            }
            copyEpisode.recovered()
            ScribeLog.info(.app, "Copied a dictation from its notice", .count("characters", text.count))
            notify(.copiedRecentDictation)
        case Self.openAppSettingsActionIdentifier:
            openScribeSettings()
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
