import AppKit
import ApplicationServices
import AVFoundation
import OSLog
import SwiftUI

if CommandLineTranscriptionTool.runIfRequested() {
    exit(EXIT_SUCCESS)
}

let application = NSApplication.shared
let delegate = AppDelegate()
application.setActivationPolicy(.accessory)
application.delegate = delegate
application.run()

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private enum CaptureStopSource {
        case menu
        case hotkey
    }

    private var statusItem: NSStatusItem?
    private var settingsWindowController: NSWindowController?
    private var welcomeWindowController: NSWindowController?
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

    func applicationDidFinishLaunching(_ notification: Notification) {
        configureAudioCaptureEngine()
        initializePersistenceStore()
        loadOverlayAnchorPreference()
        setUpStatusItem()
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

    @objc private func quit(_ sender: Any?) {
        NSApp.terminate(nil)
    }

    private func setUpStatusItem() {
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        if let button = item.button {
            button.toolTip = "Scribe"
            button.image = NSImage(systemSymbolName: "mic.fill", accessibilityDescription: "Scribe")
            if button.image == nil {
                button.title = "Scribe"
            }
        }

        let menu = NSMenu()
        menu.delegate = self
        let dictationItem = NSMenuItem(title: "Start Test Dictation", action: #selector(startTestDictation(_:)), keyEquivalent: "")
        menu.addItem(dictationItem)
        menu.addItem(NSMenuItem(title: "Settings...", action: #selector(openSettings(_:)), keyEquivalent: ","))
        menu.addItem(overlayPositionMenuItem())
        menu.addItem(.separator())
        let recentItem = NSMenuItem(title: "Recent Dictations", action: nil, keyEquivalent: "")
        recentItem.submenu = NSMenu()
        menu.addItem(recentItem)
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
        cleanupMilliseconds: Double?
    ) {
        do {
            try persistenceStore.recordDictation(
                startedAt: summary.startedAt,
                durationSeconds: summary.durationSeconds,
                sampleCount: summary.sampleCount,
                decodeMilliseconds: decodeMilliseconds,
                cleanupMilliseconds: cleanupMilliseconds)
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
        case .noFocusedElement:
            let message = "Scribe could not find a focused text field in the frontmost app to inject into."
            Self.writeLogLine(message)
            showFailedThenHideOverlay()
            presentErrorAlert(title: "No Focused Text Field", message: message)
        }
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
            return
        }

        do {
            let decodeStart = DispatchTime.now()
            let transcript = try transcriptionEngine.transcribe(
                samples: samples,
                sampleRate: AudioCaptureEngine.targetSampleRate)
            let decodeMilliseconds = Double(DispatchTime.now().uptimeNanoseconds - decodeStart.uptimeNanoseconds) / 1_000_000.0
            Self.writeLogLine("Real transcript (\(source)): \(transcript)")

            var processedText = textPostProcessor.process(transcript)
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

            // NOTE: matchedProfile.writingStylePrompt would override the cleanup provider's system
            // prompt here once AI cleanup is wired into this live pipeline (currently only
            // reachable via the `--cleanup-text` CLI verb; see PORTING-PLAN.md cleanup provider
            // rows). Tracked as a follow-up alongside live cleanup wiring, not a per-app-profiles
            // gap specifically.

            recordDictationHistory(summary: summary, decodeMilliseconds: decodeMilliseconds, cleanupMilliseconds: nil)
            lastTranscriptStore.set(processedText)

            let injectionResult = textInjector.inject(text: processedText)
            handleInjectionResult(injectionResult)
        } catch {
            dictationMenuItem?.title = "Start Test Dictation"
            showFailedThenHideOverlay()
            Self.writeLogLine("ASR transcription failed: \(error.localizedDescription)")
            presentErrorAlert(title: "Transcription Failed", message: error.localizedDescription)
            recordDictationHistory(summary: summary, decodeMilliseconds: nil, cleanupMilliseconds: nil)
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
        default:
            return false
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
