import AVFoundation
import AppKit
import SwiftUI
import UserNotifications

/// The menu bar app. It owns every service for the app's lifetime and wires them to the dictation lifecycle
/// (`DictationController`), the tray menu, the windows and the notifications; it decides nothing about a dictation
/// itself.
@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private static let hasCompletedFirstRunDefaultsKey = "ScribeHasCompletedFirstRun"
    private static let isPausedDefaultsKey = "ScribeIsPaused"
    /// Shared with `OverlayAnchorSelection.defaultsKey`, which stores the Settings tab's choice under it.
    private static let overlayAnchorDefaultsKey = "ScribeOverlayAnchor"

    private var statusItem: NSStatusItem?
    private var settingsWindowController: NSWindowController?
    /// Outlives the Settings window, which is released on close, so unsaved entries survive a close and reopen.
    private let settingsDrafts = SettingsDrafts()
    private var welcomeWindowController: NSWindowController?
    private var quickAddWindowController: NSWindowController?
    private let persistenceStore = PersistenceStore()
    /// Commits history off the main actor, after the text is delivered, in dictation order.
    private lazy var historyWriter = HistoryWriter(recorder: persistenceStore)
    /// Held by each dictation from its admission until its processing ends, so storage housekeeping never competes
    /// with one (`DictationController`).
    private let foregroundActivity = ForegroundActivity()
    /// Applies the history retention choice and reclaims space while the app is idle.
    private lazy var storageMaintenance = StorageMaintenance(
        store: persistenceStore, historyWriter: historyWriter, activity: foregroundActivity)
    /// Reads the rules every dictation applies off the main actor; the newest refresh wins.
    private lazy var ruleRefresher = RuleSetRefresher(
        load: { [persistenceStore] in try await persistenceStore.loadRuleSet() },
        apply: { [weak self] rules in self?.applyRules(rules) },
        onFailure: { [weak self] error in self?.reportRuleLoadFailure(error) })
    /// Opens once the database is migrated and the first rule load has finished; dictation and Quick Add wait for it.
    private let startupGate = StartupGate()
    private lazy var dictationRules = DictationRules(gate: startupGate)
    private let audioCaptureEngine = AudioCaptureEngine()
    /// Looks the recognizer up again for every dictation, so installing Foundry Local while Scribe runs takes effect
    /// on the next one.
    private let transcriptionEngine = TranscriptionEngine()
    private lazy var textInjector = TextInjector(logSink: { line in ScribeLog.legacyUnshapedLine(line) })
    private lazy var hotkeyManager = HotkeyManager()
    let dictionaryLibraryService = DictionaryLibraryService()
    private let lastTranscriptStore = LastTranscriptStore()
    let pipelineReportStore = PipelineReportStore()
    private let overlayPanelController = OverlayPanelController()
    private lazy var trayPresenter = TrayPresenter(overlay: overlayPanelController)
    private lazy var notifier: any DictationNotifying = Self.makeNotifier(recovery: lastTranscriptStore)
    private lazy var startupNotices = StartupNotices { [weak self] notice in
        self?.notifier.notify(notice)
    }
    private lazy var recentDictationsMenu = RecentDictationsMenu(store: lastTranscriptStore)
    private lazy var dictationController = makeDictationController()
    private lazy var termination = makeTermination()
    private var dictationMenuItem: NSMenuItem?
    private var pauseMenuItem: NSMenuItem?
    private var aiCleanupMenuItem: NSMenuItem?
    private var overlayPositionMenu: NSMenu?
    private var cleanupSettings = CleanupSettingsStore.live.snapshot()
    private var observations: [SettingsNotificationObservation] = []

    private var isAiCleanupEnabled: Bool {
        CleanupSettingsStore.live.isEnabled
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        // First, before anything can reach the store and before the hotkey starts: the storage queue runs operations
        // in order, so every storage call made after this meets the migrated schema.
        let preparation = persistenceStore.beginPreparing()
        Task { await prepareStorage(after: preparation) }
        // A crash or forced quit during a decode can leave a scratch recording behind.
        Task.detached(priority: .utility) {
            _ = ScratchAudioDirectory.live.sweepAbandoned()
        }
        loadOverlayAnchorPreference()
        setUpStatusItem()
        configureNotifications()
        checkAccessibility()
        requestMicrophoneAccessIfNeeded()
        configureHotkey()
        observeSettingsAndActivation()
        showWelcomeIfFirstRun()
    }

    /// Quitting waits for Scribe to shut down in order (`ApplicationTermination`): a paste in progress puts the user's
    /// pasteboard back, and a recognizer, or an `az` or `foundry` a Settings check started, is stopped and reaped,
    /// before Scribe replies and exits. `applicationWillTerminate` alone would be too late for any of it. A second
    /// Quit while that runs changes nothing: the first one replies.
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        termination.request()
    }

    private func makeTermination() -> ApplicationTermination {
        let maintenance = storageMaintenance
        let hotkeyManager = hotkeyManager
        return ApplicationTermination(
            work: ApplicationTermination.Work(
                stopListening: { hotkeyManager.stop() },
                operations: .shared,
                dictation: dictationController,
                // Maintenance stops first: a reclaim in progress rolls back and frees the connection for the last
                // history writes.
                stopMaintenance: { _ = maintenance.stop(timeout: 2) },
                removeScratchAudio: { _ = ScratchAudioDirectory.live.removeFilesOfThisProcess() }),
            reply: { NSApp.reply(toApplicationShouldTerminate: true) })
    }

    private func makeDictationController() -> DictationController {
        let controller = DictationController(
            services: DictationController.Services(
                capture: LiveDictationCapture(engine: audioCaptureEngine),
                transcriber: LiveDictationTranscriber(engine: transcriptionEngine),
                cleanup: LiveDictationCleanup(cache: .shared),
                targeting: LiveDictationTargeting(injector: textInjector),
                injector: textInjector,
                history: historyWriter,
                rules: dictationRules,
                presenter: trayPresenter,
                notifier: notifier,
                clock: SystemDictationClock(),
                activity: foregroundActivity,
                recovery: lastTranscriptStore,
                reports: pipelineReportStore),
            isPaused: UserDefaults.standard.bool(forKey: Self.isPausedDefaultsKey))
        controller.triggers = hotkeyManager
        return controller
    }

    private static func makeNotifier(recovery: LastTranscriptStore) -> any DictationNotifying {
        // The notification center needs an app bundle; a bare `swift run` binary has none.
        guard Bundle.main.bundleIdentifier != nil else { return SilentNotifier() }
        return DictationNotificationCenter(center: .current(), recoveryGeneration: { recovery.generation })
    }

    // MARK: - Startup

    /// Waits for the migration queued at launch, starts maintenance, makes the first attempt to load the rules and
    /// opens `startupGate`, then seeds the recovery ring; none of it holds the main actor. If the migration or the
    /// first rule read fails, the gate still opens: dictation works without stored rules, and the startup notice says
    /// so once, rather than dictation waiting forever.
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
            ScribeLog.warning(.persistence, "Dictation runs without stored rules until they load")
            startupNotices.report(.rulesUnavailable)
        }
        startupNotices.settle(.storage)
        await seedLastTranscriptStoreFromHistory()
    }

    /// Fills `lastTranscriptStore` from durable history on launch, so the Recent Dictations submenu and Quick Add
    /// survive a restart. `LastTranscriptStore.seed(from:)` only ever fills an empty ring and refuses a read that a
    /// successful Clear overtook. The read waits on the storage queue, not the main actor.
    private func seedLastTranscriptStoreFromHistory() async {
        let store = persistenceStore
        await lastTranscriptStore.seed(from: {
            try await store.loadRecentTranscripts(limit: LastTranscriptStore.capacity)
        })
    }

    /// Starts reading the rules every dictation applies. The read runs off the main actor, and only the newest
    /// refresh is applied (`RuleSetRefresher`).
    private func refreshPostProcessorRules() {
        Task { await ruleRefresher.refresh() }
    }

    private func applyRules(_ rules: PersistenceRuleSet) {
        let libraryEntries = dictionaryLibraryService.enabledLibraryEntries()
        dictationRules.apply(rules, libraryEntries: libraryEntries)
        ScribeLog.info(
            .persistence, "Rules loaded", .count("dictionaryEntries", rules.dictionaryEntries.count),
            .count("libraryEntries", libraryEntries.count), .count("snippets", rules.snippets.count),
            .count("appProfiles", rules.appProfiles.count))
    }

    /// A failed refresh keeps the rules already in use.
    private func reportRuleLoadFailure(_ error: any Error) {
        ScribeLog.error(.persistence, "Could not load the dictionary rules, snippets and app profiles", .failure(error))
    }

    private func reportStorageUnavailable(_ error: any Error) {
        ScribeLog.error(.persistence, "Could not open or migrate the database", .failure(error))
    }

    private func configureNotifications() {
        guard let center = notifier as? DictationNotificationCenter else {
            startupNotices.settle(.notifications)
            return
        }
        center.configure { [weak self] in
            self?.startupNotices.settle(.notifications)
        }
    }

    private func checkAccessibility() {
        if textInjector.promptForAccessibilityAccessIfNeeded() {
            ScribeLog.info(.injection, "Accessibility permission is granted")
        } else {
            startupNotices.report(.accessibilityMissing)
        }
    }

    private func requestMicrophoneAccessIfNeeded() {
        let status = AVCaptureDevice.authorizationStatus(for: .audio)
        switch status {
        case .authorized:
            break
        case .notDetermined:
            // `@Sendable`, so the handler is not tied to the main actor: AVFoundation calls it on a queue of its own.
            AVCaptureDevice.requestAccess(for: .audio) { @Sendable granted in
                ScribeLog.info(.audio, "Microphone permission answered", .flag("granted", granted))
            }
        default:
            ScribeLog.warning(
                .audio, "Microphone permission is denied or restricted", .integer("status", status.rawValue))
        }
    }

    private func configureHotkey() {
        hotkeyManager.onPressed = { [weak self] binding in
            self?.dictationController.hotkeyPressed(binding) ?? false
        }
        hotkeyManager.onReleased = { [weak self] binding, cause in
            self?.dictationController.hotkeyReleased(binding, cause: cause)
        }
        if !hotkeyManager.start(requestingAccess: true) {
            startupNotices.report(.inputMonitoringMissing)
        }
    }

    private func observeSettingsAndActivation() {
        // Input Monitoring granted in System Settings takes effect without a relaunch: the tap is tried again when
        // Scribe becomes active and whenever the tray menu opens.
        observations.append(
            SettingsNotificationObservation(NSApplication.didBecomeActiveNotification) { [weak self] in
                self?.retryHotkeyIfPermitted()
            })
        observations.append(
            SettingsNotificationObservation(UserDefaults.didChangeNotification) { [weak self] in
                self?.cleanupSettingsMayHaveChanged()
            })
    }

    private func retryHotkeyIfPermitted() {
        guard !hotkeyManager.isRunning, HotkeyManager.hasInputMonitoringAccess(requesting: false) else { return }
        if hotkeyManager.start(requestingAccess: false) {
            ScribeLog.info(.hotkey, "Input Monitoring was granted; the push-to-talk key works now")
        }
    }

    /// Any preference write in the process, from the tray, Settings or elsewhere. Turning cleanup off, or changing
    /// its provider settings, drops the cached provider and its credential at once (`CleanupProviderCache`).
    private func cleanupSettingsMayHaveChanged() {
        let current = CleanupSettingsStore.live.snapshot()
        let previous = cleanupSettings
        cleanupSettings = current
        if CleanupInvalidation.shouldInvalidate(from: previous, to: current) {
            dictationController.invalidateCleanup()
            ScribeLog.info(.cleanup, "Dropped the cached cleanup provider after a settings change")
        }
        aiCleanupMenuItem?.state = current.isEnabled ? .on : .off
    }

    // MARK: - Tray menu

    private func setUpStatusItem() {
        let paused = UserDefaults.standard.bool(forKey: Self.isPausedDefaultsKey)
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        TrayPresenter.applyStatusIcon(paused: paused, to: item.button)

        let menu = NSMenu()
        menu.delegate = self
        let dictationItem = NSMenuItem(
            title: "Start Test Dictation", action: #selector(toggleTestDictation(_:)), keyEquivalent: "")
        menu.addItem(dictationItem)
        menu.addItem(NSMenuItem(title: "Settings...", action: #selector(openSettings(_:)), keyEquivalent: ","))
        menu.addItem(overlayPositionMenuItem())
        menu.addItem(.separator())
        let aiCleanupItem = NSMenuItem(title: "AI Cleanup", action: #selector(toggleAiCleanup(_:)), keyEquivalent: "")
        aiCleanupItem.state = isAiCleanupEnabled ? .on : .off
        menu.addItem(aiCleanupItem)
        let pauseItem = NSMenuItem(title: "Pause Dictation", action: #selector(togglePaused(_:)), keyEquivalent: "")
        pauseItem.state = paused ? .on : .off
        menu.addItem(pauseItem)
        menu.addItem(.separator())
        menu.addItem(recentDictationsMenu.item)
        menu.addItem(
            NSMenuItem(title: "Quick Add to Dictionary...", action: #selector(openQuickAdd(_:)), keyEquivalent: ""))
        menu.addItem(NSMenuItem(title: "Welcome...", action: #selector(showWelcome(_:)), keyEquivalent: ""))
        menu.addItem(.separator())
        menu.addItem(NSMenuItem(title: "Quit", action: #selector(quit(_:)), keyEquivalent: "q"))
        for entry in menu.items where entry.action != nil {
            entry.target = self
        }

        item.menu = menu
        statusItem = item
        dictationMenuItem = dictationItem
        aiCleanupMenuItem = aiCleanupItem
        pauseMenuItem = pauseItem
        trayPresenter.dictationMenuItem = dictationItem
        trayPresenter.pauseMenuItem = pauseItem
        trayPresenter.statusButton = item.button
    }

    /// Builds the "Overlay Position" submenu: a 9-anchor picker mirroring Windows' overlay position picker.
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
            submenu.addItem(item)
        }
        submenuItem.submenu = submenu
        overlayPositionMenu = submenu
        refreshOverlayPositionChecks()
        return submenuItem
    }

    private func refreshOverlayPositionChecks() {
        for item in overlayPositionMenu?.items ?? [] {
            item.state = (item.representedObject as? String) == overlayPanelController.anchor.rawValue ? .on : .off
        }
    }

    /// The top-level tray menu is about to open: show what the tray's switches and the overlay picker store now (a
    /// change from Settings included), and try the push-to-talk key again if Input Monitoring was granted meanwhile.
    /// The Recent Dictations submenu fills itself (`RecentDictationsMenu`).
    func menuWillOpen(_ menu: NSMenu) {
        guard menu === statusItem?.menu else { return }
        aiCleanupMenuItem?.state = isAiCleanupEnabled ? .on : .off
        refreshOverlayPositionChecks()
        retryHotkeyIfPermitted()
    }

    @objc private func toggleTestDictation(_ sender: Any?) {
        dictationController.toggleMenuDictation()
    }

    @objc private func toggleAiCleanup(_ sender: NSMenuItem) {
        let store = CleanupSettingsStore.live
        store.isEnabled = !store.isEnabled
        sender.state = store.isEnabled ? .on : .off
        if store.isEnabled {
            ScribeLog.info(.cleanup, "AI cleanup turned on from the tray")
        } else {
            ScribeLog.info(.cleanup, "AI cleanup turned off from the tray")
        }
    }

    /// Mirrors Windows' `DictationController.SetPaused`: a live recording stops at once and what it captured is still
    /// processed; every press until resume is let through to other apps and starts nothing.
    @objc private func togglePaused(_ sender: NSMenuItem) {
        let paused = !dictationController.isPaused
        UserDefaults.standard.set(paused, forKey: Self.isPausedDefaultsKey)
        dictationController.setPaused(paused)
    }

    @objc private func quit(_ sender: Any?) {
        NSApp.terminate(nil)
    }

    // MARK: - Overlay position

    private func loadOverlayAnchorPreference() {
        if let raw = UserDefaults.standard.string(forKey: Self.overlayAnchorDefaultsKey),
            let anchor = OverlayAnchor(rawValue: raw)
        {
            overlayPanelController.anchor = anchor
        }
    }

    @objc private func selectOverlayAnchor(_ sender: NSMenuItem) {
        guard let raw = sender.representedObject as? String, let anchor = OverlayAnchor(rawValue: raw) else { return }
        overlayPanelController.anchor = anchor
        UserDefaults.standard.set(anchor.rawValue, forKey: Self.overlayAnchorDefaultsKey)
        refreshOverlayPositionChecks()
        ScribeLog.info(.overlay, "Overlay position changed", .name("anchor", anchor))
    }

    // MARK: - Windows

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
                    onHistoryCleared: { [weak self] in self?.historyWasCleared() },
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

    /// A successful Clear history empties everything that could bring the deleted text back: the Recent Dictations
    /// ring (beyond Windows, whose tray list survives a Clear) and its submenu if it is open, the transcripts earlier
    /// notices would copy, and a pill notice that offers one.
    private func historyWasCleared() {
        lastTranscriptStore.removeAll()
        (notifier as? DictationNotificationCenter)?.forgetRecoveryTexts()
        dictationController.recoveryWasCleared()
        recentDictationsMenu.invalidate()
    }

    /// Opens the quick "Add to Dictionary" popup, mirroring Windows' `ShowQuickAdd()`. Seeds `LastTranscriptStore`
    /// from durable history the first time the ring is empty, so the popup has real transcripts to pick a word from.
    /// It shows and changes rules, so it waits for startup's first rule load (`startupGate`).
    @objc private func openQuickAdd(_ sender: Any?) {
        Task { [weak self] in
            guard let self else { return }
            _ = await startupGate.wait()
            await seedLastTranscriptStoreFromHistory()
            let existing = (try? await persistenceStore.loadAllDictionaryEntries()) ?? []
            // The transcripts are taken only now, after the reads: a Clear that succeeded while they ran has emptied
            // the ring and made the read above stale, so the popup never shows text the user deleted.
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

    /// Writes the entry (insert for a new rule, update in place for an existing one, keyed by a non-zero id) and
    /// returns the row's id, mirroring Windows' `persist` delegate. The write waits on the storage queue, not the
    /// main actor.
    private func persistQuickAddEntry(_ entry: DictionaryEntry) async throws -> Int64 {
        if entry.id != 0 {
            try await persistenceStore.saveDictionaryEntry(entry)
            return entry.id
        }
        return try await persistenceStore.addDictionaryEntry(entry)
    }

    /// After a successful save: refreshes the rules so the new one takes effect on the next dictation, repairs the
    /// retained copy of the transcript the correction came from, and closes the popup. The log says only that a
    /// rule was saved: rules are dictated content.
    private func handleQuickAddSaved(_ result: QuickAddView.SavedResult) {
        refreshPostProcessorRules()
        if let source = result.sourceTranscript, let corrected = result.correctedTranscript {
            lastTranscriptStore.update(original: source, updated: corrected)
        }
        ScribeLog.info(.settings, "Saved a dictionary rule from Quick Add")
        quickAddWindowController?.close()
    }

    /// Shows the one-time welcome window (non-modally, so the tray and dictation stay live behind it), then persists
    /// the flag so it never reappears on its own. Mirrors Windows' first-run onboarding block in `App.xaml.cs`.
    private func showWelcomeIfFirstRun() {
        guard !UserDefaults.standard.bool(forKey: Self.hasCompletedFirstRunDefaultsKey) else { return }
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
}

/// Shows the lifecycle's presentation: the pill, the tray's test dictation item, the pause checkmark and the status
/// icon. It keeps only the newest revision, like the pill itself.
@MainActor
final class TrayPresenter: DictationPresenting {
    private let overlay: OverlayPanelController
    private var revisions = PresentationRevisionGate()
    private var shownPaused: Bool?
    weak var dictationMenuItem: NSMenuItem?
    weak var pauseMenuItem: NSMenuItem?
    weak var statusButton: NSStatusBarButton?

    init(overlay: OverlayPanelController) {
        self.overlay = overlay
    }

    func present(_ presentation: DictationPresentation) {
        guard revisions.admit(presentation.revision) else { return }
        overlay.render(presentation.overlay, revision: presentation.revision)
        dictationMenuItem?.title = presentation.isRecording ? "Stop Test Dictation" : "Start Test Dictation"
        guard shownPaused != presentation.isPaused else { return }
        shownPaused = presentation.isPaused
        pauseMenuItem?.state = presentation.isPaused ? .on : .off
        Self.applyStatusIcon(paused: presentation.isPaused, to: statusButton)
    }

    /// The tray icon: a distinct glyph while dictation is paused.
    static func applyStatusIcon(paused: Bool, to button: NSStatusBarButton?) {
        guard let button else { return }
        let symbolName = paused ? "mic.slash.fill" : "mic.fill"
        button.image = NSImage(systemSymbolName: symbolName, accessibilityDescription: "Scribe")
        if button.image == nil {
            button.title = paused ? "Scribe (paused)" : "Scribe"
        }
        button.toolTip = paused ? "Scribe: paused" : "Scribe"
    }
}
