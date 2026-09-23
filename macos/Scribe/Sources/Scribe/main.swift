import AppKit
import ApplicationServices
import AVFoundation
import OSLog
import SwiftUI
@preconcurrency import UserNotifications
import os

if CommandLineTranscriptionTool.runIfRequested() {
    exit(EXIT_SUCCESS)
}

let application = NSApplication.shared
let delegate = AppDelegate()
application.setActivationPolicy(.accessory)
application.delegate = delegate
application.run()

/// How a dictation capture was stopped (menu-driven test dictation, or the real push-to-talk/toggle
/// hotkey). Shared with `PipelineReport` for the Playground's timing display.
enum CaptureStopSource {
    case menu
    case hotkey
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate, @preconcurrency UNUserNotificationCenterDelegate {
    private var statusItem: NSStatusItem?
    private var settingsWindowController: NSWindowController?
    /// Outlives the Settings window, which is released on close, so unsaved entries survive a close and reopen.
    private let settingsDrafts = SettingsDrafts()
    private var welcomeWindowController: NSWindowController?
    private var quickAddWindowController: NSWindowController?
    private let logger = Logger(subsystem: "com.scribe.macos", category: "App")
    private let persistenceStore = PersistenceStore()
    /// Commits history off the main actor, after the text is delivered, in dictation order.
    private lazy var historyWriter = HistoryWriter(recorder: persistenceStore)
    /// Held from capture start until that dictation's pipeline finishes, so storage housekeeping
    /// never competes with a dictation. The lifecycle owner is expected to take this over.
    private let foregroundActivity = ForegroundActivity()
    private var captureActivityLease: ForegroundActivity.Lease?
    /// Applies the history retention choice and reclaims space while the app is idle.
    private lazy var storageMaintenance = StorageMaintenance(
        store: persistenceStore, historyWriter: historyWriter, activity: foregroundActivity)
    /// Reads the rules every dictation applies off the main actor; the newest refresh wins.
    private lazy var ruleRefresher = RuleSetRefresher(
        load: { [persistenceStore] in try await persistenceStore.loadRuleSet() },
        apply: { [weak self] rules in self?.applyRules(rules) },
        onFailure: { [weak self] error in self?.reportRuleLoadFailure(error) })
    /// Opens once the database is migrated and the first rule load has finished; dictation post-processing and
    /// Quick Add wait for it. The lifecycle owner is expected to take this over.
    private let startupGate = StartupGate()
    private let audioCaptureEngine = AudioCaptureEngine()
    /// Looks the recognizer up again for every dictation, so installing Foundry Local while Scribe runs
    /// takes effect on the next one.
    private let transcriptionEngine = TranscriptionEngine()
    private lazy var hotkeyManager = HotkeyManager(
        logSink: { message in AppDelegate.writeLogLine(message) })
    private lazy var textInjector = TextInjector(
        logSink: { message in AppDelegate.writeLogLine(message) })
    private let textPostProcessor = TextPostProcessor()
    let dictionaryLibraryService = DictionaryLibraryService()
    private let lastTranscriptStore = LastTranscriptStore()
    let pipelineReportStore = PipelineReportStore()
    private var recentDictationsMenuItem: NSMenuItem?
    private var appProfiles: [AppProfile] = []
    /// Global default when no profile overrides it. Not yet Settings-driven (macos-overlay-ui);
    /// SmartFlatten matches Windows' default.
    private var globalNewlineMode: NewlineInjectionMode = .smartFlatten
    private let overlayPanelController = OverlayPanelController()
    private var dictationMenuItem: NSMenuItem?
    /// The recording that holds the microphone and what started it. Every start and stop names it, so a late
    /// event or a second stop can never act on another recording. The lifecycle owner is expected to take
    /// this over.
    private var activeRecording: (id: RecordingID, source: CaptureStopSource)?
    /// Brings capture events to the main actor, with meter readings coalesced.
    private lazy var captureEvents = CaptureEventRelay { [weak self] event in
        self?.handleCaptureEvent(event)
    }
    private static let hasCompletedFirstRunDefaultsKey = "ScribeHasCompletedFirstRun"
    private static let isPausedDefaultsKey = "ScribeIsPaused"
    private var pauseMenuItem: NSMenuItem?
    private var aiCleanupMenuItem: NSMenuItem?
    /// Persisted user intent for AI cleanup. Mirrors Windows' `AppSettings.EnableAiCleanup`. Wired
    /// into the live dictation pipeline in `transcribeAndInject`: when enabled, the provider comes
    /// from `CleanupProviderCache.shared` (built from `CleanupSettingsStore`, i.e. the Settings
    /// window's "AI Cleanup" tab, or an env var override, and reused until that configuration changes)
    /// and its cleaned output is injected instead of the raw post-processed text, falling back to
    /// the post-processed text on any resolution or request failure. A computed proxy over
    /// `CleanupSettingsStore.isEnabled` rather than its own cached flag, so the tray checkbox and
    /// the Settings tab's toggle always agree, however each one was last changed.
    private var isAiCleanupEnabled: Bool {
        get { CleanupSettingsStore.live.isEnabled }
        set {
            let store = CleanupSettingsStore.live
            store.isEnabled = newValue
        }
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        // First, before anything can reach the store and before the hotkeys start: the storage queue runs
        // operations in order, so every storage call made after this meets the migrated schema.
        let preparation = persistenceStore.beginPreparing()
        Task { await prepareStorage(after: preparation) }
        // A crash or forced quit during a decode can leave a scratch recording behind.
        Task.detached(priority: .utility) {
            _ = ScratchAudioDirectory.live.sweepAbandoned()
        }
        loadOverlayAnchorPreference()
        loadQuickTogglePreferences()
        setUpStatusItem()
        // The tray icon reflects paused state as a distinct glyph (mic.slash.fill), but the
        // status item doesn't exist until setUpStatusItem() runs above, so a persisted pause
        // from a prior session can only be reflected here, once the button is available. Without
        // this, relaunching into an already-paused state left the mic.fill icon showing even
        // though hotkeyManager.isPaused (and the menu checkmark) were correctly restored.
        updateStatusIcon(paused: hotkeyManager.isPaused)
        configureNotifications()
        promptForAccessibilityAccess()
        requestMicrophoneAccessIfNeeded()
        configureHotkeyManager()
        hotkeyManager.start()
        showWelcomeIfFirstRun()
    }

    func applicationWillTerminate(_ notification: Notification) {
        hotkeyManager.stop()
        // Both bounded, so quitting never hangs on storage. Maintenance stops first: a reclaim in
        // progress rolls back and frees the connection for the last history writes.
        storageMaintenance.stop(timeout: 2)
        historyWriter.complete(timeout: 3)
        ScratchAudioDirectory.live.removeFilesOfThisProcess()
    }

    @objc private func startTestDictation(_ sender: Any?) {
        if activeRecording != nil {
            stopActiveCapture(source: .menu)
        } else if hotkeyManager.isPaused {
            Self.writeLogLine("Ignoring Start Test Dictation while paused.")
        } else {
            startCapture(source: .menu)
        }
    }

    @objc private func openSettings(_ sender: Any?) {
        if settingsWindowController == nil {
            settingsWindowController = SettingsWindowController(
                rootView: SettingsView(
                    persistenceStore: persistenceStore,
                    overlayPanelController: overlayPanelController,
                    pipelineReportStore: pipelineReportStore,
                    dictionaryLibraryService: dictionaryLibraryService,
                    onProfilesOrRulesChanged: { [weak self] in self?.refreshPostProcessorRules() },
                    onHotkeyChanged: { [weak self] keyCode in self?.hotkeyManager.keyCode = keyCode },
                    historyAccess: .live(store: persistenceStore, maintenance: storageMaintenance),
                    onHistoryCleared: { [weak self] in self?.lastTranscriptStore.removeAll() },
                    drafts: settingsDrafts),
                onClose: { [weak self] closed in
                    if self?.settingsWindowController === closed {
                        self?.settingsWindowController = nil
                    }
                })
        }

        settingsWindowController?.showWindow(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    /// Fills `lastTranscriptStore` from durable history on launch, so the "Recent Dictations" tray
    /// submenu and the Quick Add popup both survive an app restart instead of starting empty, the
    /// same as Windows' history-backed recovery fallback. `LastTranscriptStore.seed(from:)` only ever
    /// fills an empty ring and refuses a read that a successful Clear overtook, so this is safe to call
    /// again before Quick Add opens. The read waits on the storage queue, not the main actor, and covers
    /// only the newest few rows.
    private func seedLastTranscriptStoreFromHistory() async {
        let store = persistenceStore
        await lastTranscriptStore.seed(from: {
            try await store.loadRecentTranscripts(limit: LastTranscriptStore.capacity)
        })
    }

    /// Opens the quick "Add to Dictionary" popup, mirroring Windows' `ShowQuickAdd()`. Seeds
    /// `LastTranscriptStore` from durable history the first time the ring is empty (e.g. right
    /// after launch, before any dictation has happened this run), so the popup has real transcripts
    /// to pick a word from rather than an empty list. It shows and changes rules, so it waits for
    /// startup's first rule load (`startupGate`).
    @objc private func openQuickAdd(_ sender: Any?) {
        Task { [weak self] in
            guard let self else { return }
            _ = await startupGate.wait()
            await seedLastTranscriptStoreFromHistory()
            let existing = (try? await persistenceStore.loadAllDictionaryEntries()) ?? []
            // The transcripts are taken only now, after the reads: a Clear that succeeded while they ran has
            // emptied the ring and made the read above stale, so the popup never shows text the user deleted.
            presentQuickAdd(recent: lastTranscriptStore.recent(), existing: existing)
        }
    }

    private func presentQuickAdd(recent: [String], existing: [DictionaryEntry]) {
        let hostingController = NSHostingController(
            rootView: QuickAddView(
                recentTranscripts: recent,
                existing: existing,
                onSave: { [weak self] result in self?.handleQuickAddSaved(result) },
                onClose: { [weak self] in self?.quickAddWindowController?.close() },
                persistAction: { [weak self] entry in
                    guard let self else {
                        throw QuickAddPersistError.noPersistAction
                    }
                    return try await self.persistQuickAddEntry(entry)
                }))
        let window = NSWindow(contentViewController: hostingController)
        window.title = "Add to Dictionary"
        window.styleMask.insert(.titled)
        window.styleMask.insert(.closable)
        window.isReleasedWhenClosed = false
        window.center()

        let controller = NSWindowController(window: window)
        controller.shouldCascadeWindows = false
        quickAddWindowController = controller

        quickAddWindowController?.showWindow(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    /// Writes the entry (insert for a new rule, update in place for an existing one, keyed by a
    /// non-zero id) and returns the row's id, mirroring Windows' `persist` delegate. The write waits on
    /// the storage queue, not the main actor.
    private func persistQuickAddEntry(_ entry: DictionaryEntry) async throws -> Int64 {
        if entry.id != 0 {
            try await persistenceStore.saveDictionaryEntry(entry)
            return entry.id
        }
        return try await persistenceStore.addDictionaryEntry(entry)
    }

    /// After a successful save: refreshes the post-processor so the new rule takes effect on the
    /// next dictation, repairs the retained copy of the transcript the correction came from (so
    /// "copy last dictation" hands back the corrected wording), and closes the popup. The log line
    /// says only that a rule was saved: rules are dictated content.
    private func handleQuickAddSaved(_ result: QuickAddView.SavedResult) {
        refreshPostProcessorRules()
        if let source = result.sourceTranscript, let corrected = result.correctedTranscript {
            lastTranscriptStore.update(original: source, updated: corrected)
        }
        Self.writeLogLine("Saved a dictionary rule from Quick Add.")
        quickAddWindowController?.close()
    }

    @objc private func quit(_ sender: Any?) {
        NSApp.terminate(nil)
    }

    private func setUpStatusItem() {
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        if let button = item.button {
            button.toolTip = "Scribe"
            let symbolName = hotkeyManager.isPaused ? "mic.slash.fill" : "mic.fill"
            button.image = NSImage(systemSymbolName: symbolName, accessibilityDescription: "Scribe")
            if button.image == nil {
                button.title = hotkeyManager.isPaused ? "Scribe (paused)" : "Scribe"
            }
        }

        let menu = NSMenu()
        menu.delegate = self
        let dictationItem = NSMenuItem(title: "Start Test Dictation", action: #selector(startTestDictation(_:)), keyEquivalent: "")
        menu.addItem(dictationItem)
        menu.addItem(NSMenuItem(title: "Settings...", action: #selector(openSettings(_:)), keyEquivalent: ","))
        menu.addItem(overlayPositionMenuItem())
        menu.addItem(.separator())
        let aiCleanupItem = NSMenuItem(title: "AI Cleanup", action: #selector(toggleAiCleanup(_:)), keyEquivalent: "")
        aiCleanupItem.state = isAiCleanupEnabled ? .on : .off
        menu.addItem(aiCleanupItem)
        let pauseItem = NSMenuItem(title: "Pause Dictation", action: #selector(togglePaused(_:)), keyEquivalent: "")
        pauseItem.state = hotkeyManager.isPaused ? .on : .off
        menu.addItem(pauseItem)
        menu.addItem(.separator())
        let recentItem = NSMenuItem(title: "Recent Dictations", action: nil, keyEquivalent: "")
        recentItem.submenu = NSMenu()
        menu.addItem(recentItem)
        menu.addItem(NSMenuItem(title: "Quick Add to Dictionary...", action: #selector(openQuickAdd(_:)), keyEquivalent: ""))
        menu.addItem(NSMenuItem(title: "Welcome...", action: #selector(showWelcome(_:)), keyEquivalent: ""))
        menu.addItem(.separator())
        menu.addItem(NSMenuItem(title: "Quit", action: #selector(quit(_:)), keyEquivalent: "q"))

        for item in menu.items {
            applyTargetRecursively(to: item)
        }

        item.menu = menu
        statusItem = item
        dictationMenuItem = dictationItem
        recentDictationsMenuItem = recentItem
        aiCleanupMenuItem = aiCleanupItem
        pauseMenuItem = pauseItem
    }

    /// Builds the "Overlay Position" submenu: a 9-anchor picker mirroring Windows' overlay
    /// position picker, checked against the currently persisted anchor.
    private func overlayPositionMenuItem() -> NSMenuItem {
        let submenuItem = NSMenuItem(title: "Overlay Position", action: nil, keyEquivalent: "")
        let submenu = NSMenu()
        for anchor in OverlayAnchor.allCases {
            let item = NSMenuItem(
                title: anchor.displayName,
                action: #selector(selectOverlayAnchor(_:)),
                keyEquivalent: "")
            item.target = self
            item.representedObject = anchor.rawValue
            item.state = anchor == overlayPanelController.anchor ? .on : .off
            submenu.addItem(item)
        }
        submenuItem.submenu = submenu
        return submenuItem
    }

    @objc private func selectOverlayAnchor(_ sender: NSMenuItem) {
        guard
            let raw = sender.representedObject as? String,
            let anchor = OverlayAnchor(rawValue: raw)
        else {
            return
        }
        setOverlayAnchor(anchor)
        for item in sender.menu?.items ?? [] {
            item.state = item === sender ? .on : .off
        }
    }

    /// `NSMenu.items where item.action != nil` above only targets top-level items; submenu items
    /// (like the overlay anchor picker) need their own target set individually, which happens in
    /// `overlayPositionMenuItem()`. This walks the tree defensively in case future submenus forget.
    private func applyTargetRecursively(to item: NSMenuItem) {
        if item.action != nil {
            item.target = self
        }
        guard let submenu = item.submenu else { return }
        for child in submenu.items {
            applyTargetRecursively(to: child)
        }
    }

    // MARK: - Recovery: recent dictations menu

    /// Fills the "Recent Dictations" submenu just before it opens, mirroring Windows'
    /// `PopulateRecentDictations`. Also refreshes the "AI Cleanup" checkmark against
    /// `CleanupSettingsStore` when the top-level tray menu itself opens, so a change made from the
    /// Settings window's "AI Cleanup" tab is reflected even though this menu item's state isn't
    /// otherwise bound to that store. Runs on the UI thread via `NSMenuDelegate.menuWillOpen`.
    func menuWillOpen(_ menu: NSMenu) {
        if menu === statusItem?.menu {
            aiCleanupMenuItem?.state = isAiCleanupEnabled ? .on : .off
            return
        }
        guard menu === recentDictationsMenuItem?.submenu else {
            return
        }
        populateRecentDictationsMenu(menu)
    }

    private func populateRecentDictationsMenu(_ menu: NSMenu) {
        menu.removeAllItems()

        let recent = lastTranscriptStore.recent()
        // Restart-safe: `seedLastTranscriptStoreFromHistory()` runs at launch, so `recent` reflects
        // durable `dictation_history.transcript_text` rows even before any dictation happens this
        // run, matching Windows' history-backed `CopyLastDictation` fallback.

        if recent.isEmpty {
            let placeholder = NSMenuItem(title: "No recent dictations", action: nil, keyEquivalent: "")
            placeholder.isEnabled = false
            menu.addItem(placeholder)
            return
        }

        for transcript in recent {
            let item = NSMenuItem(
                title: LastTranscriptStore.formatPreview(transcript),
                action: #selector(copyRecentDictation(_:)),
                keyEquivalent: "")
            item.target = self
            item.representedObject = transcript
            menu.addItem(item)
        }
    }

    @objc private func copyRecentDictation(_ sender: NSMenuItem) {
        guard let transcript = sender.representedObject as? String else {
            return
        }
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(transcript, forType: .string)
        Self.writeLogLine("Copied a recent dictation to the clipboard (\(transcript.count) characters).")
    }

    // MARK: - Onboarding: first-run welcome

    /// Shows the one-time welcome window (non-modally, so the tray and dictation loop stay live
    /// behind it), then persists the flag so it never reappears on its own. Mirrors Windows'
    /// first-run onboarding block in `App.xaml.cs`.
    private func showWelcomeIfFirstRun() {
        guard !UserDefaults.standard.bool(forKey: Self.hasCompletedFirstRunDefaultsKey) else {
            return
        }
        showWelcome(nil)
        UserDefaults.standard.set(true, forKey: Self.hasCompletedFirstRunDefaultsKey)
    }

    @objc private func showWelcome(_ sender: Any?) {
        if welcomeWindowController == nil {
            let hostingController = NSHostingController(
                rootView: WelcomeView(
                    onOpenSettings: { [weak self] in
                        self?.openSettings(nil)
                        self?.welcomeWindowController?.close()
                    },
                    onDismiss: { [weak self] in
                        self?.welcomeWindowController?.close()
                    }))
            let window = NSWindow(contentViewController: hostingController)
            window.title = "Welcome to Scribe"
            window.styleMask = [.titled, .closable]
            window.isReleasedWhenClosed = false
            window.center()

            let controller = NSWindowController(window: window)
            controller.shouldCascadeWindows = false
            welcomeWindowController = controller
        }

        welcomeWindowController?.showWindow(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    private func requestMicrophoneAccessIfNeeded() {
        switch AVCaptureDevice.authorizationStatus(for: .audio) {
        case .authorized:
            break
        case .notDetermined:
            AVCaptureDevice.requestAccess(for: .audio) { granted in
                let outcome = granted ? "granted" : "denied"
                fputs("Microphone permission \(outcome).\n", stdout)
            }
        case .denied, .restricted:
            fputs("Microphone permission already denied or restricted.\n", stdout)
        @unknown default:
            fputs("Microphone permission state is unknown.\n", stdout)
        }
    }

    private func promptForAccessibilityAccess() {
        let trusted = textInjector.promptForAccessibilityAccessIfNeeded()
        if trusted {
            Self.writeLogLine("Accessibility permission already granted.")
        }
    }

    /// Events of a recording that no longer holds the microphone are ignored: its owner has moved on.
    private func handleCaptureEvent(_ event: CaptureEvent) {
        guard let recording = activeRecording, recording.id == event.owner else { return }
        switch event.kind {
        case .level(let level):
            overlayPanelController.update(state: .listening(levelDbfs: level.rmsDbfs))
        case .stopRequested:
            // Silence, the duration ceiling or a device fault ended it; what it captured is still processed.
            stopActiveCapture(source: recording.source)
        }
    }

    private func configureHotkeyManager() {
        hotkeyManager.onPushToTalkPressed = { [weak self] in
            self?.startCapture(source: .hotkey)
        }

        hotkeyManager.onPushToTalkReleased = { [weak self] in
            self?.stopActiveCapture(source: .hotkey)
        }
    }

    /// Waits for the migration queued at launch, starts maintenance, makes the first attempt to load the rules
    /// and opens `startupGate`, then seeds the recovery ring; none of it holds the main actor. If the migration or
    /// the first rule read fails, the gate still opens: dictation works without stored rules, and the user is told
    /// once (a log line and a notification) rather than dictation waiting forever.
    private func prepareStorage(after preparation: StoragePreparation) async {
        let state = await startupGate.open(
            afterMigrating: { [weak self] in
                do {
                    try await preparation.finish()
                } catch {
                    self?.reportStorageUnavailable(error)
                    throw error
                }
            },
            then: { [weak self] in self?.storageMaintenance.start() },
            loadingRules: { [weak self] in
                await self?.ruleRefresher.refreshUntilSettled() == .applied
            })
        if state == .withoutStoredRules {
            notifyDictationWithoutStoredRules()
        }
        await seedLastTranscriptStoreFromHistory()
    }

    /// Stopgap persistence for the overlay position: a single UserDefaults value rather than a
    /// full settings model, since macos-overlay-ui doesn't yet have a general Settings/SQLite
    /// preferences story beyond dictionary/snippets/profiles. Revisit once Settings UI grows a
    /// general key-value preferences table.
    private static let overlayAnchorDefaultsKey = "ScribeOverlayAnchor"

    private func loadOverlayAnchorPreference() {
        if
            let raw = UserDefaults.standard.string(forKey: Self.overlayAnchorDefaultsKey),
            let anchor = OverlayAnchor(rawValue: raw)
        {
            overlayPanelController.anchor = anchor
        }
    }

    private func setOverlayAnchor(_ anchor: OverlayAnchor) {
        overlayPanelController.anchor = anchor
        UserDefaults.standard.set(anchor.rawValue, forKey: Self.overlayAnchorDefaultsKey)
        Self.writeLogLine("Overlay position set to \(anchor.displayName).")
    }

    // MARK: - Tray quick toggles: AI cleanup, pause

    private func loadQuickTogglePreferences() {
        hotkeyManager.isPaused = UserDefaults.standard.bool(forKey: Self.isPausedDefaultsKey)
    }

    @objc private func toggleAiCleanup(_ sender: NSMenuItem) {
        isAiCleanupEnabled.toggle()
        sender.state = isAiCleanupEnabled ? .on : .off
        Self.writeLogLine("AI cleanup \(isAiCleanupEnabled ? "enabled" : "disabled") from the tray.")
    }

    /// Mirrors Windows' `DictationController.SetPaused`: stops an in-flight capture immediately
    /// if one is running, then blocks new hotkey-triggered captures until resumed. The event tap
    /// itself stays installed the whole time.
    @objc private func togglePaused(_ sender: NSMenuItem) {
        let paused = !hotkeyManager.isPaused
        hotkeyManager.isPaused = paused
        UserDefaults.standard.set(paused, forKey: Self.isPausedDefaultsKey)
        sender.state = paused ? .on : .off
        updateStatusIcon(paused: paused)

        if paused, activeRecording != nil {
            stopActiveCapture(source: .menu)
        }

        Self.writeLogLine("Dictation \(paused ? "paused" : "resumed") from the tray.")
    }

    private func updateStatusIcon(paused: Bool) {
        guard let button = statusItem?.button else { return }
        let symbolName = paused ? "mic.slash.fill" : "mic.fill"
        button.image = NSImage(systemSymbolName: symbolName, accessibilityDescription: "Scribe")
        if button.image == nil {
            button.title = paused ? "Scribe (paused)" : "Scribe"
        }
        button.toolTip = paused ? "Scribe: paused" : "Scribe"
    }

    /// Starts reading the rules every dictation applies. The read runs off the main actor, and only
    /// the newest refresh is applied (`RuleSetRefresher`).
    private func refreshPostProcessorRules() {
        Task { await ruleRefresher.refresh() }
    }

    private func applyRules(_ rules: PersistenceRuleSet) {
        let libraryEntries = dictionaryLibraryService.enabledLibraryEntries()
        textPostProcessor.reload(
            dictionaryEntries: rules.dictionaryEntries, snippets: rules.snippets, libraryEntries: libraryEntries)
        appProfiles = rules.appProfiles
        Self.writeLogLine(
            "Post-processor loaded \(rules.dictionaryEntries.count) dictionary entr(y/ies), \(libraryEntries.count) library entr(y/ies), \(rules.snippets.count) snippet(s), and \(appProfiles.count) app profile(s).")
    }

    /// A failed refresh keeps the rules already in use.
    private func reportRuleLoadFailure(_ error: any Error) {
        ScribeLog.error(.persistence, "Could not load the dictionary rules, snippets and app profiles", .failure(error))
    }

    private func reportStorageUnavailable(_ error: any Error) {
        ScribeLog.error(.persistence, "Could not open or migrate the database", .failure(error))
    }

    /// Startup's decision when the migration or the first rule read fails: dictation keeps working without the
    /// stored rules, and the user is told once, through the notification path injection failures use.
    private func notifyDictationWithoutStoredRules() {
        ScribeLog.warning(.persistence, "Dictation runs without stored rules until they load")
        postNotification(
            title: "Your dictionary rules are not loaded",
            body: "Scribe could not read your dictionary rules, snippets and app profiles, so dictation works "
                + "without them for now. Restarting Scribe tries again.",
            categoryIdentifier: nil)
    }

    /// Opens the microphone for a new recording. The menu's test dictation is a toggle, so it stops on silence;
    /// the hotkey path is treated as held, which never stops on silence. Both stop at the ten-minute ceiling.
    /// Choosing the policy from the binding that fired (Caps Lock is a toggle) belongs to the lifecycle owner.
    private func startCapture(source: CaptureStopSource) {
        guard activeRecording == nil else { return }
        let id = RecordingID.next()
        let policy: CaptureStopPolicy = source == .menu ? .toggle() : .hold()
        activeRecording = (id, source)
        beginCaptureActivity()
        dictationMenuItem?.title = "Stop Test Dictation"
        overlayPanelController.show(state: .listening(levelDbfs: -120))
        if source == .menu {
            Self.writeLogLine("Started live test dictation capture (toggle mode, silence auto-stop armed).")
        }

        let events = captureEvents.sink
        Task { [weak self] in
            guard let self else { return }
            do {
                _ = try await audioCaptureEngine.start(owner: id, policy: policy, events: events)
            } catch {
                if activeRecording?.id == id {
                    // Never stopped, so its lease is still the current one and nothing will process it.
                    activeRecording = nil
                    captureActivityLease?.end()
                    captureActivityLease = nil
                }
                // A newer recording owns the pill and the menu by now.
                guard activeRecording == nil else { return }
                handleCaptureStartError(error)
            }
        }
    }

    /// A stop with no recording (a release after the recording already ended itself) leaves the pill alone,
    /// which may be showing the previous dictation's processing.
    private func stopActiveCapture(source: CaptureStopSource) {
        guard let recording = activeRecording else { return }
        activeRecording = nil
        handleCaptureStopped(audioCaptureEngine.stop(owner: recording.id), source: source)
    }

    /// A capture has started: storage housekeeping holds off until its dictation is fully processed.
    private func beginCaptureActivity() {
        captureActivityLease = foregroundActivity.begin()
    }

    private func handleCaptureStopped(
        _ captured: CapturedAudio?,
        source: CaptureStopSource
    ) {
        // This capture's lease moves to its processing below and ends when that finishes.
        let activityLease = captureActivityLease
        captureActivityLease = nil

        guard let captured else {
            activityLease?.end()
            dictationMenuItem?.title = "Start Test Dictation"
            overlayPanelController.hide()
            return
        }

        let summary = captured.summary
        dictationMenuItem?.title = "Transcribing Test Dictation..."
        overlayPanelController.update(state: .processing)
        Self.writeLogLine(
            String(
                format: "Stopped live test dictation. Duration %.2f s, sample count %d",
                summary.durationSeconds,
                summary.sampleCount))

        let samples = captured.samples
        guard !samples.isEmpty else {
            dictationMenuItem?.title = "Start Test Dictation"
            overlayPanelController.hide()
            Self.writeLogLine("Capture stopped without any resampled audio samples to transcribe.")
            recordDictationHistory(summary: summary, decodeMilliseconds: nil, cleanupMilliseconds: nil)
            activityLease?.end()
            return
        }

        Task { @MainActor [weak self] in
            defer { activityLease?.end() }
            guard let self else { return }
            await self.transcribeAndInject(samples: samples, summary: summary, source: source)
        }
    }

    /// Queues one `dictation_history` row per capture, mirroring Windows' per-dictation history used by
    /// `DictationStats`. Decode/cleanup timings are optional: a capture that never made it to
    /// transcription (e.g. no audio) still counts toward total audio/duration stats, just with nil
    /// timing columns. Returns at once: `HistoryWriter` commits in the background and logs a failed
    /// write by its shape.
    private func recordDictationHistory(
        summary: AudioCaptureSummary,
        decodeMilliseconds: Double?,
        cleanupMilliseconds: Double?,
        transcriptText: String? = nil,
        targetApp: String? = nil
    ) {
        historyWriter.enqueue(
            DictationHistoryRecord(
                startedAt: summary.startedAt,
                durationSeconds: summary.durationSeconds,
                sampleCount: summary.sampleCount,
                decodeMilliseconds: decodeMilliseconds,
                cleanupMilliseconds: cleanupMilliseconds,
                transcriptText: transcriptText,
                targetApp: targetApp))
    }

    private func handleCaptureStartError(_ error: any Error) {
        dictationMenuItem?.title = "Start Test Dictation"
        overlayPanelController.hide()

        if let captureError = error as? AudioCaptureEngineError,
            case .microphoneNotAuthorized(let status) = captureError
        {
            let detail = "Microphone access is required before live capture can start. Current authorization status: \(status.rawValue)."
            Self.writeLogLine("Microphone capture blocked by authorization status \(status.rawValue).")
            presentErrorAlert(title: "Microphone Access Needed", message: detail)
        } else {
            Self.writeLogLine("Audio capture failed to start: \(error.localizedDescription)")
            presentErrorAlert(title: "Audio Capture Failed", message: error.localizedDescription)
        }
    }

    private func presentErrorAlert(title: String, message: String) {
        NSApp.activate(ignoringOtherApps: true)

        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = message
        alert.addButton(withTitle: "OK")
        alert.runModal()
    }

    private func handleInjectionResult(_ result: InjectionResult) {
        dictationMenuItem?.title = "Start Test Dictation"

        switch result.delivery {
        case .accessibility, .pasted, .typed, .nothingToInsert:
            overlayPanelController.hide()
        case .accessibilityDenied:
            let message = "Accessibility permission is required for text injection. Enable it in System Settings > Privacy & Security > Accessibility, then relaunch Scribe."
            Self.writeLogLine(message)
            showFailedThenHideOverlay()
            presentErrorAlert(title: "Accessibility Access Needed", message: message)
            postInjectionFailureNotification()
        case .noFocusedElement:
            let message = "Scribe could not find a focused text field in the frontmost app to inject into."
            Self.writeLogLine(message)
            showFailedThenHideOverlay()
            presentErrorAlert(title: "No Focused Text Field", message: message)
            postInjectionFailureNotification()
        case .targetChanged, .targetUnknown, .targetUnresponsive, .failed:
            // No modal alert: it would activate Scribe over whatever the user switched to. The transcript
            // is already in the recovery store, and the notification points to it.
            showFailedThenHideOverlay()
            postInjectionFailureNotification()
        case .typedPartially:
            showFailedThenHideOverlay()
            postInjectionFailureNotification(
                title: "Dictation was only partly inserted",
                body: "Scribe stopped part way through typing it. Use \u{201C}Copy Transcript\u{201D} below "
                    + "or the Recent Dictations menu to recover the full text.")
        case .accessibilityUnconfirmed:
            // The app may still apply the text when it recovers, so the notice must not claim it failed.
            showFailedThenHideOverlay()
            postInjectionFailureNotification(
                title: "Dictation may not have been inserted",
                body: "The app stopped responding while Scribe was inserting it, so the text may still appear. "
                    + "If it does not, use \u{201C}Copy Transcript\u{201D} below or the Recent Dictations menu.")
        case .cancelled:
            overlayPanelController.hide()
        }
    }

    // MARK: - Injection failure recovery notification

    private static let injectionFailureCategoryIdentifier = "com.scribe.macos.injectionFailure"
    private static let copyTranscriptActionIdentifier = "com.scribe.macos.copyTranscript"

    /// Registers the notification category/action once at launch. Best-effort like Windows'
    /// balloon: authorization is requested but never blocks startup, and every downstream call
    /// tolerates a denial by silently doing nothing (the modal NSAlert already told the user).
    private func configureNotifications() {
        let center = UNUserNotificationCenter.current()
        center.delegate = self

        let copyAction = UNNotificationAction(
            identifier: Self.copyTranscriptActionIdentifier,
            title: "Copy Transcript",
            options: [])
        let category = UNNotificationCategory(
            identifier: Self.injectionFailureCategoryIdentifier,
            actions: [copyAction],
            intentIdentifiers: [],
            options: [])
        center.setNotificationCategories([category])

        center.requestAuthorization(options: [.alert, .sound]) { granted, error in
            if let error {
                AppDelegate.writeLogLine("Notification authorization request failed: \(error.localizedDescription)")
            } else if !granted {
                AppDelegate.writeLogLine("Notification authorization was denied; injection-failure recovery notifications will not be shown.")
            }
        }
    }

    /// Mirrors Windows' `_controller.InjectionFailed` tray balloon: the failed dictation already
    /// survives in `lastTranscriptStore` (set before injection is attempted), so this notification
    /// closes the loop by telling the user it can still be recovered, with a one-tap "Copy
    /// Transcript" action wired to the same store. Best-effort: any failure here must never
    /// propagate back into the dictation pipeline.
    private func postInjectionFailureNotification(
        title: String = "Dictation could not be inserted",
        body: String = "Use \u{201C}Copy Transcript\u{201D} below or the Recent Dictations menu to recover it."
    ) {
        postNotification(title: title, body: body, categoryIdentifier: Self.injectionFailureCategoryIdentifier)
    }

    /// Posts one local notification, with `categoryIdentifier`'s actions when given. Best-effort: a denied
    /// authorization or a failed add is logged and ignored.
    private func postNotification(title: String, body: String, categoryIdentifier: String?) {
        let content = UNMutableNotificationContent()
        content.title = title
        content.body = body
        if let categoryIdentifier {
            content.categoryIdentifier = categoryIdentifier
        }
        content.sound = .default

        let request = UNNotificationRequest(
            identifier: UUID().uuidString,
            content: content,
            trigger: nil)

        UNUserNotificationCenter.current().add(request) { error in
            if let error {
                AppDelegate.writeLogLine("Failed to show a notification: \(error.localizedDescription)")
            }
        }
    }

    func userNotificationCenter(
        _ center: UNUserNotificationCenter,
        didReceive response: UNNotificationResponse,
        withCompletionHandler completionHandler: @escaping () -> Void
    ) {
        if response.actionIdentifier == Self.copyTranscriptActionIdentifier,
           let transcript = lastTranscriptStore.get() {
            let pasteboard = NSPasteboard.general
            pasteboard.clearContents()
            pasteboard.setString(transcript, forType: .string)
            Self.writeLogLine("Copied the failed dictation from the recovery notification.")
        }
        completionHandler()
    }

    /// Briefly flashes the pill's failed state (mirrors Windows' overlay `Failed` state) before
    /// hiding it, so the user gets a visual cue distinct from a silent, successful completion.
    private func showFailedThenHideOverlay() {
        overlayPanelController.update(state: .failed)
        Task { @MainActor [weak self] in
            try? await Task.sleep(nanoseconds: 1_500_000_000)
            self?.overlayPanelController.hide()
        }
    }

    /// Lines that predate `ScribeLog` and are not all shapes. New code logs through `ScribeLog`, which
    /// only accepts shapes; this keeps the remaining callers on the same standard error sink.
    nonisolated private static func writeLogLine(_ message: String) {
        ScribeLog.legacyUnshapedLine(message)
    }

    private func transcribeAndInject(
        samples: [Float],
        summary: AudioCaptureSummary,
        source: CaptureStopSource
    ) async {
        do {
            let decodeStart = DispatchTime.now()
            let transcript = try await transcriptionEngine.transcribe(
                samples: samples,
                sampleRate: AudioCaptureEngine.targetSampleRate
            ).text
            let decodeMilliseconds = Double(DispatchTime.now().uptimeNanoseconds - decodeStart.uptimeNanoseconds) / 1_000_000.0
            Self.writeLogLine("Real transcript (\(source)): \(transcript.count) characters")

            // Waits for startup's first rule load, so a dictation that finishes before the user's rules are in
            // place is never processed against the empty rule set; afterwards it returns at once (`StartupGate`).
            let (postProcessing, postProcessMilliseconds) = await startupGate.whenOpen {
                () -> (TextPostProcessingResult, Double) in
                let postProcessStart = DispatchTime.now()
                let result = textPostProcessor.processDetailed(transcript)
                let milliseconds = Double(DispatchTime.now().uptimeNanoseconds - postProcessStart.uptimeNanoseconds) / 1_000_000.0
                return (result, milliseconds)
            }
            var processedText = postProcessing.text
            if processedText != transcript {
                Self.writeLogLine("Post-processed transcript: \(processedText)")
            }

            let frontmost = NSWorkspace.shared.frontmostApplication
            let bundleIdentifier = frontmost?.bundleIdentifier
            let processName = frontmost?.localizedName
            let matchedProfile = AppProfileMatcher.match(
                profiles: appProfiles,
                bundleIdentifier: bundleIdentifier,
                processName: processName)
            if let matchedProfile {
                Self.writeLogLine("Matched app profile '\(matchedProfile.name)' for \(bundleIdentifier ?? processName ?? "unknown app").")
            }

            let newlineMode = AppProfileMatcher.resolveNewlineMode(
                profile: matchedProfile,
                globalDefault: globalNewlineMode,
                bundleIdentifier: bundleIdentifier)
            processedText = AppProfileMatcher.applyNewlineMode(newlineMode, to: processedText, bundleIdentifier: bundleIdentifier)

            // Optional AI cleanup runs after dictionary/snippet post-processing, mirroring the
            // order the Playground CLI verbs already document (post-process, then cleanup), and
            // after per-app newline flattening so a terminal profile's flattened text is what gets
            // cleaned. A misconfigured or failing provider must never crash or block a dictation
            // (this is an optional online feature; see AGENTS.md's offline-first guarantee), so any
            // failure here just falls back to the already-good post-processed text.
            var cleanupMilliseconds: Double?
            var cleanupApplied = false
            if isAiCleanupEnabled {
                let cleanupStart = DispatchTime.now()
                do {
                    let preCleanupText = processedText
                    let provider = try CleanupProviderCache.shared.provider()
                    let writingStyle = matchedProfile?.writingStylePrompt ?? CleanupPrompt.defaultWritingStyle
                    let systemPrompt = CleanupPrompt.systemPrompt(
                        writingStyle: writingStyle, useLocalPrompt: provider.usesLocalCleanupPrompt)
                    let response = try await provider.clean(
                        CleanupRequest(
                            transcript: CleanupPrompt.wrapTranscript(processedText),
                            writingStylePrompt: systemPrompt))
                    cleanupMilliseconds = Double(DispatchTime.now().uptimeNanoseconds - cleanupStart.uptimeNanoseconds) / 1_000_000.0
                    switch CleanupResponseGuard.sanitize(candidate: response.cleanedText, original: preCleanupText) {
                    case .accepted(let sanitizedText):
                        if sanitizedText != preCleanupText {
                            Self.writeLogLine("AI cleanup refined the transcription.")
                        }

                        processedText = sanitizedText
                        cleanupApplied = true
                    case .rejected(let reason):
                        Self.writeLogLine(reason.logMessage)
                    }
                } catch {
                    cleanupMilliseconds = Double(DispatchTime.now().uptimeNanoseconds - cleanupStart.uptimeNanoseconds) / 1_000_000.0
                    Self.writeLogLine("AI cleanup failed (\(error.localizedDescription)); using post-processed transcription.")
                }
            }

            lastTranscriptStore.set(processedText)

            let injectionStart = DispatchTime.now()
            let injectionResult = await textInjector.inject(text: processedText)
            let injectionMilliseconds = Double(DispatchTime.now().uptimeNanoseconds - injectionStart.uptimeNanoseconds) / 1_000_000.0

            // After delivery, so storage never sits between speaking and typing. From here until the
            // writer commits, history is best-effort: a crash in that window loses the entry (the
            // recovery ring above is memory-only), and the drain at quit is bounded.
            recordDictationHistory(
                summary: summary,
                decodeMilliseconds: decodeMilliseconds,
                cleanupMilliseconds: cleanupMilliseconds,
                transcriptText: processedText,
                targetApp: bundleIdentifier ?? processName)

            let decodeSeconds = decodeMilliseconds / 1_000.0
            let realTimeFactor = summary.durationSeconds > 0 ? decodeSeconds / summary.durationSeconds : nil
            pipelineReportStore.publish(PipelineReport(
                capturedAt: summary.startedAt,
                source: source,
                captureDuration: summary.durationSeconds,
                decodeDuration: decodeSeconds,
                cleanupDuration: cleanupMilliseconds.map { $0 / 1_000.0 },
                postProcessingDuration: postProcessMilliseconds / 1_000.0,
                injectionDuration: injectionMilliseconds / 1_000.0,
                realTimeFactor: realTimeFactor,
                rawText: transcript,
                cleanupApplied: cleanupApplied,
                postProcessing: postProcessing,
                finalText: processedText,
                injectionResult: injectionResult,
                failureStage: nil,
                failureReason: nil))

            handleInjectionResult(injectionResult)
        } catch {
            dictationMenuItem?.title = "Start Test Dictation"
            showFailedThenHideOverlay()
            Self.writeLogLine("ASR transcription failed: \(error.localizedDescription)")
            if case TranscriptionError.backendMissing = error {
                presentErrorAlert(title: "ASR Not Ready", message: error.localizedDescription)
            } else {
                presentErrorAlert(title: "Transcription Failed", message: error.localizedDescription)
            }
            recordDictationHistory(summary: summary, decodeMilliseconds: nil, cleanupMilliseconds: nil)
            pipelineReportStore.publish(.failure(
                capturedAt: summary.startedAt,
                source: source,
                captureDuration: summary.durationSeconds,
                stage: .decode,
                reason: error.localizedDescription))
        }
    }
}

private enum CommandLineTranscriptionTool {
    static func runIfRequested() -> Bool {
        let arguments = Array(CommandLine.arguments.dropFirst())
        guard let command = arguments.first else {
            return false
        }

        switch command {
        case "--transcribe-file", "--transcribe-wav":
            return runTranscribe(arguments: arguments)
        case "--cleanup-text":
            return runCleanup(arguments: arguments)
        case "--post-process-text":
            return runPostProcess(arguments: arguments)
        case "--resolve-profile":
            return runResolveProfile(arguments: arguments)
        case "--diagnostics":
            return runDiagnostics(arguments: arguments)
        case "--set-azure-client-secret":
            return runSetAzureClientSecret(arguments: arguments)
        case "--list-dictionary-libraries":
            return runListDictionaryLibraries()
        case "--set-launch-at-login":
            return runSetLaunchAtLogin(arguments: arguments)
        case "--list-input-devices":
            return runListInputDevices()
        case "--verify-selected-microphone":
            return runVerifySelectedMicrophone()
        default:
            return false
        }
    }

    /// Saves the Microsoft Foundry service-principal client secret to the Keychain, keyed by
    /// client id. Reads the secret from stdin rather than argv, so it never appears in shell
    /// history or `ps` output; per AGENTS.md, it must never be passed as an environment variable.
    /// Usage: echo "<secret>" | Scribe --set-azure-client-secret <client-id>
    private static func runSetAzureClientSecret(arguments: [String]) -> Bool {
        guard arguments.count == 2 else {
            fputs("Usage: echo \"<secret>\" | Scribe --set-azure-client-secret <client-id>\n", stderr)
            exit(EXIT_FAILURE)
        }

        let clientId = arguments[1]
        guard !CleanupSettingsStore.secretAccount(forClientId: clientId).isEmpty else {
            fputs("Usage: echo \"<secret>\" | Scribe --set-azure-client-secret <client-id>\n", stderr)
            exit(EXIT_FAILURE)
        }
        guard let secret = readLine(strippingNewline: true), !secret.isEmpty else {
            fputs("No secret provided on stdin.\n", stderr)
            exit(EXIT_FAILURE)
        }

        do {
            // Through the store, so the running app's provider cache sees the new secret on its next request.
            try CleanupSettingsStore.live.setAzureClientSecret(secret, clientId: clientId)
            fputs("Saved client secret for client id \(clientId) to the Keychain.\n", stdout)
            return true
        } catch {
            fputs("Failed to save secret: \(error.localizedDescription)\n", stderr)
            exit(EXIT_FAILURE)
        }
    }

    private static func runTranscribe(arguments: [String]) -> Bool {
        guard arguments.count == 2 else {
            fputs("Usage: Scribe --transcribe-wav <wav-path>\n", stderr)
            exit(EXIT_FAILURE)
        }

        let inputURL = URL(fileURLWithPath: arguments[1])

        do {
            let result = try runBlocking {
                try await TranscriptionEngine().transcribe(wavFileAt: inputURL)
            }
            fputs("\(result.text)\n", stdout)
            return true
        } catch {
            fputs("Transcription failed: \(error.localizedDescription)\n", stderr)
            exit(EXIT_FAILURE)
        }
    }

    /// Runs `operation` to completion for a verb, which has no run loop to return to. The main thread only
    /// waits: the operation runs on the global executor, and nothing it awaits needs the main thread.
    private static func runBlocking<T: Sendable>(_ operation: @escaping @Sendable () async throws -> T) throws -> T {
        let outcome = OSAllocatedUnfairLock<Result<T, any Error>?>(initialState: nil)
        let finished = DispatchSemaphore(value: 0)
        Task.detached {
            let result: Result<T, any Error>
            do {
                result = .success(try await operation())
            } catch {
                result = .failure(error)
            }
            outcome.withLock { $0 = result }
            finished.signal()
        }
        finished.wait()
        guard let result = outcome.withLock({ $0 }) else {
            preconditionFailure("The operation signalled before storing its result")
        }
        return try result.get()
    }

    private static func runCleanup(arguments: [String]) -> Bool {
        guard arguments.count == 2 else {
            fputs("Usage: Scribe --cleanup-text <raw-transcript>\n", stderr)
            exit(EXIT_FAILURE)
        }

        let rawTranscript = arguments[1]
        let provider = CleanupProviderResolver.resolveDefaultProvider()
        fputs("Using cleanup provider: \(provider.displayName) (\(provider.id))\n", stderr)

        final class ExitBox: @unchecked Sendable {
            var code: Int32 = EXIT_SUCCESS
        }
        let exitBox = ExitBox()
        let semaphore = DispatchSemaphore(value: 0)

        Task {
            do {
                let systemPrompt = CleanupPrompt.systemPrompt(
                    writingStyle: CleanupPrompt.defaultWritingStyle, useLocalPrompt: provider.usesLocalCleanupPrompt)
                let response = try await provider.clean(
                    CleanupRequest(
                        transcript: CleanupPrompt.wrapTranscript(rawTranscript),
                        writingStylePrompt: systemPrompt))
                let outputText: String
                switch CleanupResponseGuard.sanitize(candidate: response.cleanedText, original: rawTranscript) {
                case .accepted(let sanitizedText):
                    outputText = sanitizedText
                case .rejected(let reason):
                    outputText = rawTranscript
                    fputs("\(reason.logMessage)\n", stderr)
                }

                fputs("\(outputText)\n", stdout)
                fputs(
                    "(\(response.providerID)/\(response.modelID), \(String(format: "%.2f", response.latency))s)\n",
                    stderr)
            } catch {
                fputs("Cleanup failed: \(error.localizedDescription)\n", stderr)
                exitBox.code = EXIT_FAILURE
            }
            semaphore.signal()
        }
        semaphore.wait()

        if exitBox.code != EXIT_SUCCESS {
            exit(exitBox.code)
        }
        return true
    }

    /// Manual verification for the dictionary + snippet pipeline: seeds the real SQLite store
    /// (respecting SCRIBE_STORE_DB_PATH-less default location, same as the live app) with a couple
    /// of fixed entries if it's empty, then runs the given transcript through TextPostProcessor.
    /// Usage: Scribe --post-process-text "raw text"
    private static func runPostProcess(arguments: [String]) -> Bool {
        guard arguments.count == 2 else {
            fputs("Usage: Scribe --post-process-text <raw-transcript>\n", stderr)
            exit(EXIT_FAILURE)
        }

        let store = PersistenceStore()
        do {
            try store.initialize()

            var dictionaryEntries = try store.fetchEnabledDictionaryEntries()
            var snippets = try store.fetchEnabledSnippets()

            if dictionaryEntries.isEmpty && snippets.isEmpty {
                fputs("No dictionary/snippet rows found; seeding verification fixtures.\n", stderr)
                _ = try store.insertDictionaryEntry(DictionaryEntry(pattern: "sherpa onnx", replacement: "sherpa-onnx"))
                _ = try store.insertDictionaryEntry(DictionaryEntry(pattern: "github", replacement: "GitHub"))
                _ = try store.insertSnippet(Snippet(phrase: "sign off block", template: "Best regards,\nScribe Team"))
                dictionaryEntries = try store.fetchEnabledDictionaryEntries()
                snippets = try store.fetchEnabledSnippets()
            }

            let processor = TextPostProcessor()
            processor.reload(dictionaryEntries: dictionaryEntries, snippets: snippets)
            let result = processor.process(arguments[1])
            fputs("\(result)\n", stdout)
            return true
        } catch {
            fputs("Post-process failed: \(error.localizedDescription)\n", stderr)
            exit(EXIT_FAILURE)
        }
    }

    /// Manual verification that the built-in dictionary library CSVs actually loaded from the app
    /// bundle's resource bundle at run time (not just from `swift test`'s `.build` tree). Prints
    /// each library's id, name, category, and entry count.
    /// Usage: Scribe --list-dictionary-libraries
    private static func runListDictionaryLibraries() -> Bool {
        let libraries = BuiltInDictionaryLibraries.all
        fputs("\(libraries.count) built-in dictionary librar(y/ies):\n", stdout)
        for library in libraries {
            fputs("  \(library.id): \(library.name) [\(library.category)] (\(library.entries.count) terms)\n", stdout)
        }
        return true
    }

    /// Lists every microphone CoreAudio currently exposes (built-in, USB, Bluetooth), the same
    /// devices the "Microphone" picker in Settings > Hotkey offers. Diagnostic helper for
    /// confirming a newly paired device (e.g. a Bluetooth dongle) is actually visible before
    /// selecting it in the UI.
    private static func runListInputDevices() -> Bool {
        let devices = AudioDeviceStore.availableInputDevices()
        fputs("\(devices.count) input device(s):\n", stdout)
        for device in devices {
            let marker = device.isDefault ? " (default)" : ""
            fputs("  \(device.uid): \(device.name)\(marker)\n", stdout)
        }
        if let selectedUID = AudioDeviceStore.selectedDeviceUID {
            fputs("Currently selected: \(selectedUID) (\(AudioDeviceStore.selectedDeviceName ?? "unknown name"))\n", stdout)
        } else {
            fputs("Currently selected: system default\n", stdout)
        }
        return true
    }

    /// Starts the real `AudioCaptureEngine`, reads back which `AudioDeviceID` the AUHAL unit is
    /// actually pulling audio from, and prints its name, confirming the saved microphone selection
    /// took effect rather than trusting log output alone.
    private static func runVerifySelectedMicrophone() -> Bool {
        let engine = AudioCaptureEngine()
        let recording = RecordingID.next()
        do {
            _ = try runBlocking {
                try await engine.start(owner: recording, policy: .hold(maximumDuration: nil), events: { _ in })
            }
        } catch {
            fputs("Failed to start capture: \(error.localizedDescription)\n", stderr)
            exit(EXIT_FAILURE)
        }

        defer { engine.stop(owner: recording) }

        guard let deviceID = engine.currentInputDeviceID() else {
            fputs("Could not read back the active input device.\n", stderr)
            exit(EXIT_FAILURE)
        }

        if let device = AudioDeviceStore.describe(deviceID) {
            fputs("Active microphone (AUHAL property): \(device.name) (\(device.uid))\n", stdout)
        } else {
            fputs("Active microphone (AUHAL property): AudioDeviceID \(deviceID) (name unavailable)\n", stdout)
        }

        // AVAudioEngine can report an internal aggregate wrapper here on modern macOS even when a
        // specific hardware device was requested, so also ask CoreAudio directly which physical
        // input device is actually running right now: this is the ground truth.
        fputs("\nDevices CoreAudio reports as actively running:\n", stdout)
        var sawRunningDevice = false
        for device in AudioDeviceStore.availableInputDevices() {
            if AudioDeviceStore.isDeviceRunning(uid: device.uid) {
                fputs("  RUNNING: \(device.name) (\(device.uid))\n", stdout)
                sawRunningDevice = true
            }
        }
        if !sawRunningDevice {
            fputs("  (none reported running)\n", stdout)
        }
        return true
    }

    /// Toggles "Open Scribe AI at Login" without going through the Settings UI. Must run from
    /// inside the installed .app bundle: `SMAppService.mainApp` registers the identity of the
    /// currently-running process, so this only does something meaningful when invoked as
    /// `/Applications/Scribe.app/Contents/MacOS/Scribe --set-launch-at-login on`.
    /// Usage: Scribe --set-launch-at-login <on|off>
    private static func runSetLaunchAtLogin(arguments: [String]) -> Bool {
        guard arguments.count == 2, let enabled = ["on": true, "off": false][arguments[1]] else {
            fputs("Usage: Scribe --set-launch-at-login <on|off>\n", stderr)
            exit(EXIT_FAILURE)
        }

        guard LoginItemManager.setEnabled(enabled) else {
            fputs("Failed to \(enabled ? "register" : "unregister") the login item.\n", stderr)
            exit(EXIT_FAILURE)
        }

        if enabled && LoginItemManager.requiresApproval {
            fputs("Registered, but approval is still needed in System Settings > General > Login Items.\n", stdout)
        } else {
            fputs("Launch at login is now \(enabled ? "enabled" : "disabled").\n", stdout)
        }
        return true
    }

    /// Manual verification for per-app profile resolution: seeds a couple of fixed profiles into
    /// the real SQLite store if it's empty, then runs the matcher + newline application against a
    /// given bundle identifier and sample text.
    /// Usage: Scribe --resolve-profile <bundle-identifier> "raw text"
    private static func runResolveProfile(arguments: [String]) -> Bool {
        guard arguments.count == 3 else {
            fputs("Usage: Scribe --resolve-profile <bundle-identifier> <raw-text>\n", stderr)
            exit(EXIT_FAILURE)
        }

        let bundleIdentifier = arguments[1]
        let rawText = arguments[2]

        let store = PersistenceStore()
        do {
            try store.initialize()

            var profiles = try store.fetchAppProfiles()
            if profiles.isEmpty {
                fputs("No app profile rows found; seeding verification fixtures.\n", stderr)
                _ = try store.insertAppProfile(AppProfile(
                    name: "Terminal",
                    bundleIdentifiers: ["com.apple.Terminal", "com.googlecode.iterm2"],
                    processNames: ["Terminal", "iTerm2"],
                    writingStylePrompt: "Be extremely terse. No filler words.",
                    newlineHandling: .alwaysFlatten))
                _ = try store.insertAppProfile(AppProfile(
                    name: "Email",
                    bundleIdentifiers: ["com.apple.mail", "com.microsoft.Outlook"],
                    processNames: ["Mail", "Microsoft Outlook"],
                    writingStylePrompt: "Use a formal, professional tone with complete sentences.",
                    newlineHandling: .keepNewlines))
                profiles = try store.fetchAppProfiles()
            }

            let matched = AppProfileMatcher.match(profiles: profiles, bundleIdentifier: bundleIdentifier, processName: nil)
            let mode = AppProfileMatcher.resolveNewlineMode(profile: matched, globalDefault: .smartFlatten, bundleIdentifier: bundleIdentifier)
            let result = AppProfileMatcher.applyNewlineMode(mode, to: rawText, bundleIdentifier: bundleIdentifier)

            fputs("Matched profile: \(matched?.name ?? "none")\n", stderr)
            fputs("Writing style override: \(matched?.writingStylePrompt ?? "(none, using global)")\n", stderr)
            fputs("Newline mode: \(mode)\n", stderr)
            fputs("\(result)\n", stdout)
            return true
        } catch {
            fputs("Profile resolution failed: \(error.localizedDescription)\n", stderr)
            exit(EXIT_FAILURE)
        }
    }

    /// Prints the Diagnostics panel numbers (P50/P95 decode latency, RTF) computed from real
    /// `dictation_history` rows, mirroring Windows' Diagnostics tab. `--diagnostics [days]`
    /// defaults to a 7-day window, matching `DictationStats`' typical panel window.
    private static func runDiagnostics(arguments: [String]) -> Bool {
        let windowDays: Double
        if arguments.count >= 2, let parsed = Double(arguments[1]) {
            windowDays = parsed
        } else {
            windowDays = 7
        }

        let store = PersistenceStore()
        do {
            try store.initialize()
            let history = try store.fetchDictationHistory()
            let since = Date().addingTimeInterval(-windowDays * 86400)
            guard let snapshot = DictationStats.compute(entries: history, since: since) else {
                fputs("No dictations in the last \(windowDays) day(s).\n", stdout)
                return true
            }

            fputs("Dictations: \(snapshot.count)\n", stdout)
            fputs(String(format: "Total audio: %.1f s (longest %.1f s)\n", snapshot.totalAudioSeconds, snapshot.longestAudioSeconds), stdout)
            if let decodeMs = snapshot.decodeMs {
                fputs(
                    String(
                        format: "Decode ms: avg %.0f, p50 %.0f, p95 %.0f, min %.0f, max %.0f (n=%d)\n",
                        decodeMs.average, decodeMs.p50, decodeMs.p95, decodeMs.min, decodeMs.max, snapshot.decodeCount),
                    stdout)
                fputs(
                    String(format: "RTF: fastest %.3f, p50 %.3f, p95 %.3f\n", snapshot.fastestRtf, snapshot.rtfP50, snapshot.rtfP95),
                    stdout)
            } else {
                fputs("Decode ms: no timed dictations yet.\n", stdout)
            }
            if let cleanupMs = snapshot.cleanupMs {
                fputs(
                    String(
                        format: "Cleanup ms: avg %.0f, min %.0f, max %.0f (n=%d)\n",
                        cleanupMs.average, cleanupMs.min, cleanupMs.max, snapshot.cleanupCount),
                    stdout)
            }
            return true
        } catch {
            fputs("Diagnostics failed: \(error.localizedDescription)\n", stderr)
            exit(EXIT_FAILURE)
        }
    }
}
