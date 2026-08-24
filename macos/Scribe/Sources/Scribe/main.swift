import AppKit
import ApplicationServices
import AVFoundation
import OSLog
import SwiftUI
@preconcurrency import UserNotifications

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
    private var welcomeWindowController: NSWindowController?
    private var quickAddWindowController: NSWindowController?
    private let logger = Logger(subsystem: "com.scribe.macos", category: "App")
    private let persistenceStore = PersistenceStore()
    private let audioCaptureEngine = AudioCaptureEngine()
    private lazy var transcriptionEngine = try? TranscriptionEngine(
        logSink: { message in AppDelegate.writeLogLine(message) })
    private lazy var hotkeyManager = HotkeyManager(
        audioCaptureEngine: audioCaptureEngine,
        logSink: { message in AppDelegate.writeLogLine(message) })
    private lazy var textInjector = TextInjector(
        logSink: { message in AppDelegate.writeLogLine(message) })
    private let textPostProcessor = TextPostProcessor()
    private let lastTranscriptStore = LastTranscriptStore()
    let pipelineReportStore = PipelineReportStore()
    private var recentDictationsMenuItem: NSMenuItem?
    private var appProfiles: [AppProfile] = []
    /// Global default when no profile overrides it. Not yet Settings-driven (macos-overlay-ui);
    /// SmartFlatten matches Windows' default.
    private var globalNewlineMode: NewlineInjectionMode = .smartFlatten
    private let overlayPanelController = OverlayPanelController()
    private var dictationMenuItem: NSMenuItem?
    private var capturedSamples: [Float] = []
    /// Only armed while capture was started via the menu (toggle mode); push-to-talk capture stops
    /// on hotkey release and must never be preempted by a silence auto-stop.
    private var silenceAutoStopDetector: SilenceAutoStopDetector?
    private static let hasCompletedFirstRunDefaultsKey = "ScribeHasCompletedFirstRun"
    private static let aiCleanupEnabledDefaultsKey = "ScribeAiCleanupEnabled"
    private static let isPausedDefaultsKey = "ScribeIsPaused"
    private var pauseMenuItem: NSMenuItem?
    private var aiCleanupMenuItem: NSMenuItem?
    /// Persisted user intent for AI cleanup. Mirrors Windows' `AppSettings.EnableAiCleanup`, but
    /// macOS has no general settings/preferences table yet (cleanup provider selection is still
    /// env-var driven; see CleanupProviderResolver), so this is a UserDefaults stopgap consistent
    /// with the overlay-anchor and first-run patterns. NOTE: AI cleanup itself is not yet wired
    /// into the live dictation pipeline (only reachable via `--cleanup-text`), so this toggle
    /// currently only records intent for when that wiring lands; it does not yet change live
    /// dictation output.
    private var isAiCleanupEnabled = false

    func applicationDidFinishLaunching(_ notification: Notification) {
        configureAudioCaptureEngine()
        initializePersistenceStore()
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
    }

    @objc private func startTestDictation(_ sender: Any?) {
        if audioCaptureEngine.isCapturing {
            stopActiveCapture(source: .menu)
        } else if hotkeyManager.isPaused {
            Self.writeLogLine("Ignoring Start Test Dictation while paused.")
        } else {
            startCapture()
        }
    }

    @objc private func openSettings(_ sender: Any?) {
        if settingsWindowController == nil {
            let hostingController = NSHostingController(
                rootView: SettingsView(
                    persistenceStore: persistenceStore,
                    overlayPanelController: overlayPanelController,
                    pipelineReportStore: pipelineReportStore,
                    onProfilesOrRulesChanged: { [weak self] in self?.reloadPostProcessorRules() }))
            let window = NSWindow(contentViewController: hostingController)
            window.title = "Scribe Settings"
            window.setContentSize(NSSize(width: 640, height: 480))
            window.styleMask.insert(.titled)
            window.styleMask.insert(.closable)
            window.styleMask.insert(.miniaturizable)
            window.styleMask.insert(.resizable)
            window.isReleasedWhenClosed = false
            window.center()

            let controller = NSWindowController(window: window)
            controller.shouldCascadeWindows = false
            settingsWindowController = controller
        }

        settingsWindowController?.showWindow(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    /// Opens the quick "Add to Dictionary" popup, mirroring Windows' `ShowQuickAdd()`. Seeds
    /// `LastTranscriptStore` from durable history the first time the ring is empty (e.g. right
    /// after launch, before any dictation has happened this run), so the popup has real transcripts
    /// to pick a word from rather than an empty list.
    @objc private func openQuickAdd(_ sender: Any?) {
        if lastTranscriptStore.recent().isEmpty {
            if let history = try? persistenceStore.fetchDictationHistory() {
                let texts = history.reversed().compactMap { $0.transcriptText }
                lastTranscriptStore.seed(texts)
            }
        }

        let recent = lastTranscriptStore.recent()
        let existing = (try? persistenceStore.fetchAllDictionaryEntries()) ?? []

        let hostingController = NSHostingController(
            rootView: QuickAddView(
                recentTranscripts: recent,
                existing: existing,
                onSave: { [weak self] result in self?.handleQuickAddSaved(result) },
                onClose: { [weak self] in self?.quickAddWindowController?.close() },
                persistAction: { [weak self] entry in try self?.persistQuickAddEntry(entry) ?? 0 }))
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
    /// non-zero id) and returns the row's id, mirroring Windows' `persist` delegate.
    private func persistQuickAddEntry(_ entry: DictionaryEntry) throws -> Int64 {
        if entry.id != 0 {
            try persistenceStore.updateDictionaryEntry(entry)
            return entry.id
        }
        return try persistenceStore.insertDictionaryEntry(entry)
    }

    /// After a successful save: reloads the post-processor so the new rule takes effect on the
    /// very next dictation, repairs the retained copy of the transcript the correction came from
    /// (so "copy last dictation" hands back the corrected wording), and closes the popup.
    private func handleQuickAddSaved(_ result: QuickAddView.SavedResult) {
        reloadPostProcessorRules()
        if let source = result.sourceTranscript, let corrected = result.correctedTranscript {
            lastTranscriptStore.update(original: source, updated: corrected)
        }
        Self.writeLogLine("Saved a dictionary rule from Quick Add: '\(result.entry.pattern)' -> '\(result.entry.replacement)'.")
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
    /// `PopulateRecentDictations`. Runs on the UI thread via `NSMenuDelegate.menuWillOpen`.
    func menuWillOpen(_ menu: NSMenu) {
        guard menu === recentDictationsMenuItem?.submenu else {
            return
        }
        populateRecentDictationsMenu(menu)
    }

    private func populateRecentDictationsMenu(_ menu: NSMenu) {
        menu.removeAllItems()

        let recent = lastTranscriptStore.recent()
        // NOTE: dictation_history does not yet store transcript text (only timing/duration), so
        // there is no durable fallback to seed from on macOS today, unlike Windows' history-backed
        // CopyLastDictation fallback. The ring only ever reflects the current run's dictations.
        // Tracked as a follow-up alongside a text-retaining history schema change.

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
            let hotkeyDisplayName = "Right Control"
            let hostingController = NSHostingController(
                rootView: WelcomeView(
                    hotkeyDisplayName: hotkeyDisplayName,
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

    private func configureAudioCaptureEngine() {
        audioCaptureEngine.onChunk = { chunk in
            if
                let channelData = chunk.buffer.floatChannelData?[0]
            {
                let frameCount = Int(chunk.buffer.frameLength)
                self.capturedSamples.append(contentsOf: UnsafeBufferPointer(start: channelData, count: frameCount))
            }

            let message = String(
                format: "Live capture meter: peak %.1f dBFS, rms %.1f dBFS, total chunk samples %u",
                chunk.level.peakDbfs,
                chunk.level.rmsDbfs,
                chunk.buffer.frameLength)
            Self.writeLogLine(message)
            self.overlayPanelController.update(state: .listening(levelDbfs: chunk.level.rmsDbfs))

            if let detector = self.silenceAutoStopDetector, detector.observe(level: chunk.level) {
                let message = String(
                    format: "Silence auto-stop triggered after %.1f s below %.0f dBFS.",
                    detector.requiredSilenceDuration,
                    detector.silenceThresholdDbfs)
                Self.writeLogLine(message)
                self.silenceAutoStopDetector = nil
                Task { @MainActor [weak self] in
                    self?.stopActiveCapture(source: .menu)
                }
            }
        }

        audioCaptureEngine.onCaptureError = { [weak self] error in
            Task { @MainActor in
                self?.silenceAutoStopDetector = nil
                self?.dictationMenuItem?.title = "Start Test Dictation"
                self?.overlayPanelController.hide()
                self?.presentErrorAlert(
                    title: "Audio Capture Stopped",
                    message: error.localizedDescription)
            }
        }
    }

    private func configureHotkeyManager() {
        self.hotkeyManager.onCaptureStarted = { [weak self] in
            self?.dictationMenuItem?.title = "Stop Test Dictation"
            self?.overlayPanelController.show(state: .listening(levelDbfs: -120))
        }

        self.hotkeyManager.onCaptureStopped = { [weak self] summary in
            self?.handleCaptureStopped(summary, source: .hotkey)
        }

        self.hotkeyManager.onCaptureStartError = { [weak self] error in
            self?.handleCaptureStartError(error)
        }
    }

    private func initializePersistenceStore() {
        do {
            try persistenceStore.initialize()
            reloadPostProcessorRules()
        } catch {
            Self.writeLogLine("Failed to initialize persistence store: \(error.localizedDescription)")
            logger.error("Failed to initialize persistence store: \(error.localizedDescription, privacy: .public)")
        }
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
        isAiCleanupEnabled = UserDefaults.standard.bool(forKey: Self.aiCleanupEnabledDefaultsKey)
        hotkeyManager.isPaused = UserDefaults.standard.bool(forKey: Self.isPausedDefaultsKey)
    }

    @objc private func toggleAiCleanup(_ sender: NSMenuItem) {
        isAiCleanupEnabled.toggle()
        UserDefaults.standard.set(isAiCleanupEnabled, forKey: Self.aiCleanupEnabledDefaultsKey)
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

        if paused, audioCaptureEngine.isCapturing {
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

    private func reloadPostProcessorRules() {
        do {
            let dictionaryEntries = try persistenceStore.fetchEnabledDictionaryEntries()
            let snippets = try persistenceStore.fetchEnabledSnippets()
            textPostProcessor.reload(dictionaryEntries: dictionaryEntries, snippets: snippets)
            appProfiles = try persistenceStore.fetchAppProfiles()
            Self.writeLogLine(
                "Post-processor loaded \(dictionaryEntries.count) dictionary entr(y/ies), \(snippets.count) snippet(s), and \(appProfiles.count) app profile(s).")
        } catch {
            Self.writeLogLine("Failed to load dictionary/snippets/profiles: \(error.localizedDescription)")
            logger.error("Failed to load dictionary/snippets/profiles: \(error.localizedDescription, privacy: .public)")
        }
    }

    private func startCapture() {
        do {
            capturedSamples.removeAll(keepingCapacity: true)
            try audioCaptureEngine.start()
            // Menu-triggered capture is toggle mode: there is no release gesture, so silence
            // auto-stop is armed. Push-to-talk (hotkeyManager) never arms it; see
            // configureHotkeyManager and HotkeyManager.startCaptureOnMainThread.
            silenceAutoStopDetector = SilenceAutoStopDetector()
            dictationMenuItem?.title = "Stop Test Dictation"
            overlayPanelController.show(state: .listening(levelDbfs: -120))
            Self.writeLogLine("Started live test dictation capture (toggle mode, silence auto-stop armed).")
        } catch let error as AudioCaptureEngineError {
            handleCaptureStartError(error)
        } catch {
            handleCaptureStartError(.engineStartFailed(error.localizedDescription))
        }
    }

    private func stopActiveCapture(source: CaptureStopSource) {
        silenceAutoStopDetector = nil
        let summary = audioCaptureEngine.stop()
        handleCaptureStopped(summary, source: source)
    }

    private func handleCaptureStopped(
        _ summary: AudioCaptureSummary?,
        source: CaptureStopSource
    ) {
        guard let summary else {
            dictationMenuItem?.title = "Start Test Dictation"
            overlayPanelController.hide()
            return
        }

        dictationMenuItem?.title = "Transcribing Test Dictation..."
        overlayPanelController.update(state: .processing)
        Self.writeLogLine(
            String(
                format: "Stopped live test dictation. Duration %.2f s, sample count %d",
                summary.durationSeconds,
                summary.sampleCount))

        let samples = capturedSamples
        capturedSamples.removeAll(keepingCapacity: true)

        guard !samples.isEmpty else {
            dictationMenuItem?.title = "Start Test Dictation"
            overlayPanelController.hide()
            Self.writeLogLine("Capture stopped without any resampled audio samples to transcribe.")
            recordDictationHistory(summary: summary, decodeMilliseconds: nil, cleanupMilliseconds: nil)
            return
        }

        Task { @MainActor [weak self] in
            guard let self else { return }
            await self.transcribeAndInject(samples: samples, summary: summary, source: source)
        }
    }

    /// Writes one `dictation_history` row per capture, mirroring Windows' per-dictation history
    /// used by `DictationStats`. Decode/cleanup timings are optional: a capture that never made it
    /// to transcription (e.g. no audio) still counts toward total audio/duration stats, just with
    /// nil timing columns.
    private func recordDictationHistory(
        summary: AudioCaptureSummary,
        decodeMilliseconds: Double?,
        cleanupMilliseconds: Double?,
        transcriptText: String? = nil,
        targetApp: String? = nil
    ) {
        do {
            try persistenceStore.recordDictation(
                startedAt: summary.startedAt,
                durationSeconds: summary.durationSeconds,
                sampleCount: summary.sampleCount,
                decodeMilliseconds: decodeMilliseconds,
                cleanupMilliseconds: cleanupMilliseconds,
                transcriptText: transcriptText,
                targetApp: targetApp)
        } catch {
            Self.writeLogLine("Failed to write dictation history: \(error.localizedDescription)")
            logger.error("Failed to write dictation history: \(error.localizedDescription, privacy: .public)")
        }
    }

    private func handleCaptureStartError(_ error: AudioCaptureEngineError) {
        dictationMenuItem?.title = "Start Test Dictation"
        overlayPanelController.hide()

        switch error {
        case .microphoneNotAuthorized(let status):
            let detail = "Microphone access is required before live capture can start. Current authorization status: \(status.rawValue)."
            Self.writeLogLine("Microphone capture blocked by authorization status \(status.rawValue).")
            presentErrorAlert(title: "Microphone Access Needed", message: detail)
        default:
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

        switch result {
        case .success:
            Self.writeLogLine("Text injection succeeded via the Accessibility value path.")
            overlayPanelController.hide()
        case .fallbackUsed:
            Self.writeLogLine("Text injection succeeded via the pasteboard fallback path.")
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
    private func postInjectionFailureNotification() {
        let content = UNMutableNotificationContent()
        content.title = "Dictation could not be inserted"
        content.body = "Use \u{201C}Copy Transcript\u{201D} below or the Recent Dictations menu to recover it."
        content.categoryIdentifier = Self.injectionFailureCategoryIdentifier
        content.sound = .default

        let request = UNNotificationRequest(
            identifier: UUID().uuidString,
            content: content,
            trigger: nil)

        UNUserNotificationCenter.current().add(request) { error in
            if let error {
                AppDelegate.writeLogLine("Failed to show the injection recovery notification: \(error.localizedDescription)")
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

    nonisolated private static func writeLogLine(_ message: String) {
        let line = "\(message)\n"
        fputs(line, stderr)
    }

    private func transcribeAndInject(
        samples: [Float],
        summary: AudioCaptureSummary,
        source: CaptureStopSource
    ) async {
        guard let transcriptionEngine else {
            dictationMenuItem?.title = "Start Test Dictation"
            showFailedThenHideOverlay()
            let message = "ASR backend is not configured. Install Foundry Local (brew install microsoft/foundrylocal/foundrylocal) or whisper-cpp as a fallback."
            Self.writeLogLine(message)
            presentErrorAlert(title: "ASR Not Ready", message: message)
            recordDictationHistory(summary: summary, decodeMilliseconds: nil, cleanupMilliseconds: nil)
            pipelineReportStore.publish(.failure(
                capturedAt: summary.startedAt,
                source: source,
                captureDuration: summary.durationSeconds,
                stage: .decode,
                reason: message))
            return
        }

        do {
            let decodeStart = DispatchTime.now()
            let transcript = try transcriptionEngine.transcribe(
                samples: samples,
                sampleRate: AudioCaptureEngine.targetSampleRate)
            let decodeMilliseconds = Double(DispatchTime.now().uptimeNanoseconds - decodeStart.uptimeNanoseconds) / 1_000_000.0
            Self.writeLogLine("Real transcript (\(source)): \(transcript)")

            let postProcessStart = DispatchTime.now()
            let postProcessing = textPostProcessor.processDetailed(transcript)
            let postProcessMilliseconds = Double(DispatchTime.now().uptimeNanoseconds - postProcessStart.uptimeNanoseconds) / 1_000_000.0
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
                    let provider = try CleanupProviderResolver.tryResolveDefaultProvider()
                    let writingStyle = matchedProfile?.writingStylePrompt ?? CleanupPrompt.defaultWritingStyle
                    let response = try await provider.clean(
                        CleanupRequest(transcript: processedText, writingStylePrompt: writingStyle))
                    cleanupMilliseconds = Double(DispatchTime.now().uptimeNanoseconds - cleanupStart.uptimeNanoseconds) / 1_000_000.0
                    if response.cleanedText != processedText {
                        Self.writeLogLine("AI cleanup refined the transcription.")
                    }
                    processedText = response.cleanedText
                    cleanupApplied = true
                } catch {
                    cleanupMilliseconds = Double(DispatchTime.now().uptimeNanoseconds - cleanupStart.uptimeNanoseconds) / 1_000_000.0
                    Self.writeLogLine("AI cleanup failed (\(error.localizedDescription)); using post-processed transcription.")
                }
            }

            recordDictationHistory(
                summary: summary,
                decodeMilliseconds: decodeMilliseconds,
                cleanupMilliseconds: cleanupMilliseconds,
                transcriptText: processedText,
                targetApp: bundleIdentifier ?? processName)
            lastTranscriptStore.set(processedText)

            let injectionStart = DispatchTime.now()
            let injectionResult = textInjector.inject(text: processedText)
            let injectionMilliseconds = Double(DispatchTime.now().uptimeNanoseconds - injectionStart.uptimeNanoseconds) / 1_000_000.0

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
            presentErrorAlert(title: "Transcription Failed", message: error.localizedDescription)
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
        guard let secret = readLine(strippingNewline: true), !secret.isEmpty else {
            fputs("No secret provided on stdin.\n", stderr)
            exit(EXIT_FAILURE)
        }

        do {
            try KeychainStore.set(
                secret, service: CleanupProviderResolver.azureClientSecretKeychainService, account: clientId)
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
            let engine = try TranscriptionEngine(logSink: { message in fputs("\(message)\n", stderr) })
            let transcript = try engine.transcribeAudioFile(at: inputURL)
            fputs("\(transcript)\n", stdout)
            return true
        } catch {
            fputs("Transcription failed: \(error.localizedDescription)\n", stderr)
            exit(EXIT_FAILURE)
        }
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
                let response = try await provider.clean(CleanupRequest(transcript: rawTranscript))
                fputs("\(response.cleanedText)\n", stdout)
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
