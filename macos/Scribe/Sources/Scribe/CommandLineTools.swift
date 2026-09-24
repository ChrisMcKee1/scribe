import AVFoundation
import AppKit
import Foundation
import os

/// The command-line verbs (`Scribe --transcribe-wav <file>` and the rest), which run headless before
/// `NSApplication` starts and then exit. The ones asked to print a transcript or cleaned text print it: that output is
/// what the user asked for, on their own terminal, not a log.
enum CommandLineTranscriptionTool {
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

        let response: CleanupResponse
        do {
            response = try runBlocking {
                let systemPrompt = CleanupPrompt.systemPrompt(
                    writingStyle: CleanupPrompt.defaultWritingStyle, useLocalPrompt: provider.usesLocalCleanupPrompt)
                return try await provider.clean(
                    CleanupRequest(
                        transcript: CleanupPrompt.wrapTranscript(rawTranscript),
                        writingStylePrompt: systemPrompt))
            }
        } catch {
            fputs("Cleanup failed: \(error.localizedDescription)\n", stderr)
            exit(EXIT_FAILURE)
        }

        switch CleanupResponseGuard.sanitize(candidate: response.cleanedText, original: rawTranscript) {
        case .accepted(let sanitizedText):
            fputs("\(sanitizedText)\n", stdout)
        case .rejected(let reason):
            fputs("\(rawTranscript)\n", stdout)
            fputs("The reply was rejected (\(reason.rawValue)), so the input is printed unchanged.\n", stderr)
        }
        fputs(
            "(\(response.providerID)/\(response.modelID), \(String(format: "%.2f", response.latency))s)\n",
            stderr)
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
            fputs(
                "Currently selected: \(selectedUID) (\(AudioDeviceStore.selectedDeviceName ?? "unknown name"))\n",
                stdout)
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
                _ = try store.insertAppProfile(
                    AppProfile(
                        name: "Terminal",
                        bundleIdentifiers: ["com.apple.Terminal", "com.googlecode.iterm2"],
                        processNames: ["Terminal", "iTerm2"],
                        writingStylePrompt: "Be extremely terse. No filler words.",
                        newlineHandling: .alwaysFlatten))
                _ = try store.insertAppProfile(
                    AppProfile(
                        name: "Email",
                        bundleIdentifiers: ["com.apple.mail", "com.microsoft.Outlook"],
                        processNames: ["Mail", "Microsoft Outlook"],
                        writingStylePrompt: "Use a formal, professional tone with complete sentences.",
                        newlineHandling: .keepNewlines))
                profiles = try store.fetchAppProfiles()
            }

            let matched = AppProfileMatcher.match(
                profiles: profiles, bundleIdentifier: bundleIdentifier, processName: nil)
            let mode = AppProfileMatcher.resolveNewlineMode(
                profile: matched, globalDefault: .smartFlatten, bundleIdentifier: bundleIdentifier)
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
            fputs(
                String(
                    format: "Total audio: %.1f s (longest %.1f s)\n", snapshot.totalAudioSeconds,
                    snapshot.longestAudioSeconds), stdout)
            if let decodeMs = snapshot.decodeMs {
                fputs(
                    String(
                        format: "Decode ms: avg %.0f, p50 %.0f, p95 %.0f, min %.0f, max %.0f (n=%d)\n",
                        decodeMs.average, decodeMs.p50, decodeMs.p95, decodeMs.min, decodeMs.max, snapshot.decodeCount),
                    stdout)
                fputs(
                    String(
                        format: "RTF: fastest %.3f, p50 %.3f, p95 %.3f\n", snapshot.fastestRtf, snapshot.rtfP50,
                        snapshot.rtfP95),
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
