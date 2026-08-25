import ApplicationServices
import Foundation
import OSLog

final class HotkeyManager {
    private let audioCaptureEngine: AudioCaptureEngine
    private let logger = Logger(subsystem: "com.scribe.macos", category: "Hotkey")
    private let logSink: (String) -> Void

    private var eventTap: CFMachPort?
    private var runLoopSource: CFRunLoopSource?
    private var isPushToTalkHeld = false

    /// The push-to-talk virtual key code, configurable from Settings (`HotkeySettingsStore`,
    /// `HotkeyKeyCodeCatalog`). Defaults to Right Option, matching the previously-hardcoded
    /// behavior. Changing this while a capture is mid-press deliberately does not affect that
    /// in-flight press: `handleFlagsChanged`/`isPushToTalkEvent` only compare the *next* event's
    /// key code, so an already-held key finishes normally via its own release event... except that
    /// release event will no longer match the new code either. Settings therefore only allows
    /// recording a new key while no capture is active (see `HotkeySettingsTab`).
    var keyCode: CGKeyCode = HotkeySettingsStore.keyCode

    /// Mirrors Windows' `DictationController.IsPaused`: the hook stays installed, but a hotkey
    /// press is ignored while paused rather than removing the event tap outright, so resuming
    /// never requires re-granting Input Monitoring.
    var isPaused = false

    var onCaptureStarted: (() -> Void)?
    var onCaptureStopped: ((AudioCaptureSummary?) -> Void)?
    var onCaptureStartError: ((AudioCaptureEngineError) -> Void)?

    init(audioCaptureEngine: AudioCaptureEngine, logSink: @escaping (String) -> Void) {
        self.audioCaptureEngine = audioCaptureEngine
        self.logSink = logSink
    }

    func start() {
        stop()

        guard preflightInputMonitoringAccess() else {
            return
        }

        let eventMask =
            (CGEventMask(1) << CGEventType.flagsChanged.rawValue) |
            (CGEventMask(1) << CGEventType.keyDown.rawValue) |
            (CGEventMask(1) << CGEventType.keyUp.rawValue)

        let callback: CGEventTapCallBack = { _, type, event, userInfo in
            guard let userInfo else {
                return Unmanaged.passUnretained(event)
            }

            let manager = Unmanaged<HotkeyManager>.fromOpaque(userInfo).takeUnretainedValue()
            return manager.handleEvent(type: type, event: event)
        }

        let tap = CGEvent.tapCreate(
            tap: .cgSessionEventTap,
            place: .headInsertEventTap,
            options: .defaultTap,
            eventsOfInterest: eventMask,
            callback: callback,
            userInfo: UnsafeMutableRawPointer(Unmanaged.passUnretained(self).toOpaque()))

        guard let tap else {
            logInputMonitoringDenied("Input Monitoring permission is not granted, so the push-to-talk event tap could not be created.")
            return
        }

        let source = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, tap, 0)
        guard let source else {
            logger.error("Failed to create the hotkey run loop source.")
            logSink("Failed to create the hotkey run loop source.")
            CFMachPortInvalidate(tap)
            return
        }

        eventTap = tap
        runLoopSource = source
        CFRunLoopAddSource(CFRunLoopGetMain(), source, .commonModes)
        CGEvent.tapEnable(tap: tap, enable: true)

        logger.info("Global push-to-talk hotkey ready, key is \(HotkeyKeyCodeCatalog.displayName(for: self.keyCode), privacy: .public).")
        logSink("Global push-to-talk hotkey ready. Hold \(HotkeyKeyCodeCatalog.displayName(for: keyCode)) to drive the live microphone capture path.")
    }

    func stop() {
        isPushToTalkHeld = false

        if let source = runLoopSource {
            CFRunLoopRemoveSource(CFRunLoopGetMain(), source, .commonModes)
        }

        if let tap = eventTap {
            CFMachPortInvalidate(tap)
        }

        runLoopSource = nil
        eventTap = nil
    }

    private func preflightInputMonitoringAccess() -> Bool {
        if ProcessInfo.processInfo.environment["SCRIBE_FORCE_INPUT_MONITORING_DENIED"] == "1" {
            logInputMonitoringDenied("Input Monitoring permission is not granted. Enable it in System Settings > Privacy & Security > Input Monitoring, then relaunch Scribe.")
            return false
        }

        if #available(macOS 10.15, *) {
            if CGPreflightListenEventAccess() {
                return true
            }

            _ = CGRequestListenEventAccess()
        }

        if #available(macOS 10.15, *), CGPreflightListenEventAccess() {
            return true
        }

        logInputMonitoringDenied("Input Monitoring permission is not granted. Enable it in System Settings > Privacy & Security > Input Monitoring, then relaunch Scribe.")
        return false
    }

    private func handleEvent(type: CGEventType, event: CGEvent) -> Unmanaged<CGEvent>? {
        switch type {
        case .tapDisabledByTimeout, .tapDisabledByUserInput:
            if let eventTap {
                logger.info("The event tap was disabled by the system, re-enabling it.")
                logSink("The global hotkey event tap was disabled by the system. Re-enabling it now.")
                CGEvent.tapEnable(tap: eventTap, enable: true)
            }
            return Unmanaged.passUnretained(event)
        case .flagsChanged:
            return handleFlagsChanged(event)
        case .keyDown:
            return handleKeyDown(event)
        case .keyUp:
            return handleKeyUp(event)
        default:
            return Unmanaged.passUnretained(event)
        }
    }

    private func handleFlagsChanged(_ event: CGEvent) -> Unmanaged<CGEvent>? {
        guard isPushToTalkEvent(event) else {
            return Unmanaged.passUnretained(event)
        }

        let isPressed = CGEventSource.keyState(.combinedSessionState, key: keyCode)
        if isPressed {
            beginPushToTalk()
        } else {
            endPushToTalk()
        }

        return nil
    }

    private func handleKeyDown(_ event: CGEvent) -> Unmanaged<CGEvent>? {
        guard isPushToTalkEvent(event) else {
            return Unmanaged.passUnretained(event)
        }

        beginPushToTalk()
        return nil
    }

    private func handleKeyUp(_ event: CGEvent) -> Unmanaged<CGEvent>? {
        guard isPushToTalkEvent(event) else {
            return Unmanaged.passUnretained(event)
        }

        endPushToTalk()
        return nil
    }

    private func isPushToTalkEvent(_ event: CGEvent) -> Bool {
        let eventKeyCode = CGKeyCode(event.getIntegerValueField(.keyboardEventKeycode))
        return eventKeyCode == keyCode
    }

    private func beginPushToTalk() {
        guard !isPushToTalkHeld else {
            return
        }
        guard !isPaused else {
            logSink("Push-to-talk pressed while paused; ignoring.")
            return
        }

        isPushToTalkHeld = true
        OperationQueue.main.addOperation { [weak self] in
            self?.startCaptureOnMainThread()
        }
    }

    private func endPushToTalk() {
        guard isPushToTalkHeld else {
            return
        }

        isPushToTalkHeld = false
        OperationQueue.main.addOperation { [weak self] in
            self?.stopCaptureOnMainThread()
        }
    }

    private func startCaptureOnMainThread() {
        do {
            try audioCaptureEngine.start()
            onCaptureStarted?()
            logSink("Push-to-talk pressed. Started live microphone capture from the global \(HotkeyKeyCodeCatalog.displayName(for: keyCode)) hotkey.")
        } catch let error as AudioCaptureEngineError {
            onCaptureStartError?(error)
        } catch {
            let wrappedError = AudioCaptureEngineError.engineStartFailed(error.localizedDescription)
            onCaptureStartError?(wrappedError)
        }
    }

    private func stopCaptureOnMainThread() {
        let summary = audioCaptureEngine.stop()
        onCaptureStopped?(summary)
        if summary == nil {
            logSink("Push-to-talk released, but there was no active microphone capture to stop.")
            return
        }

        logSink("Push-to-talk released. Stopped live microphone capture from the global \(HotkeyKeyCodeCatalog.displayName(for: keyCode)) hotkey.")
    }

    private func logInputMonitoringDenied(_ message: String) {
        logger.warning("\(message, privacy: .public)")
        logSink(message)
    }
}

extension HotkeyManager: @unchecked Sendable {}
