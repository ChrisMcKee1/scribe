import ApplicationServices
import Foundation

/// Why the bound key's release reached the dictation.
enum HotkeyReleaseCause: Equatable, Sendable {
    /// The key came up (Caps Lock: turned off).
    case keyReleased
    /// The key was rebound while it held a recording; its own release would never match the new binding.
    case bindingChanged
    /// The event tap was disabled and re-enabled, and the key, read again, had been released meanwhile: a held key
    /// was no longer down, or Caps Lock's lock had changed an odd number of times.
    case tapResynchronized
}

/// One keyboard event about the bound key, reduced to what decides press and release.
enum HotkeyKeyEvent: Equatable, Sendable {
    /// A modifier's flags changed; whether the bound modifier is down after the change.
    case modifierChanged(isDown: Bool)
    /// Caps Lock's flags changed; whether its lock is on after the change.
    case lockChanged(isOn: Bool)
    case keyDown(isRepeat: Bool)
    case keyUp
}

/// What the bound key did.
enum HotkeyKeyAction: Equatable, Sendable {
    case none
    case press
    case release
}

/// Whether the bound key holds a recording, decided from its events alone, so every rule is tested without an event
/// tap. A held key (every key but Caps Lock) presses on its way down and releases on its way up. Caps Lock toggles: it
/// counts only a change of its lock state, so a flags event that repeats the state (a second event for one tap, or
/// one after the tap was re-enabled) is never taken for a tap, and each change alternates press and release.
/// After the tap was disabled, `resynchronize` settles what the lost events would have done without ever making up
/// a press: a held key found up releases, Caps Lock releases when its lock changed an odd number of times, and a held
/// key found down with nothing engaged must come up and go down again before it presses.
struct HotkeyKeyState: Equatable, Sendable {
    let binding: HotkeyBinding
    /// A press the owner accepted and nothing has released: the key is held, or the toggle is on.
    private(set) var isEngaged = false
    /// Caps Lock's lock state as last seen.
    private(set) var lockIsOn: Bool
    /// A held key that `resynchronize` found down with nothing engaged. Its down may still be queued behind the tap's
    /// re-enable, so nothing presses until the key has come up.
    private(set) var awaitsRelease = false

    init(binding: HotkeyBinding, lockIsOn: Bool) {
        self.binding = binding
        self.lockIsOn = lockIsOn
    }

    mutating func receive(_ event: HotkeyKeyEvent) -> HotkeyKeyAction {
        switch (binding.gesture, event) {
        case (.toggle, .lockChanged(let isOn)):
            guard isOn != lockIsOn else { return .none }
            lockIsOn = isOn
            return isEngaged ? .release : .press
        case (.hold, .modifierChanged(let isDown)):
            if awaitsRelease {
                awaitsRelease = isDown
                return .none
            }
            if isDown {
                return isEngaged ? .none : .press
            }
            return isEngaged ? .release : .none
        case (.hold, .keyDown(let isRepeat)):
            return isRepeat || isEngaged || awaitsRelease ? .none : .press
        case (.hold, .keyUp):
            if awaitsRelease {
                awaitsRelease = false
                return .none
            }
            return isEngaged ? .release : .none
        default:
            return .none
        }
    }

    /// The owner's answer to a press: engaged only when it started a recording. A press refused (paused, still
    /// processing) leaves the key free, so its release does nothing and the next press is a new press.
    mutating func pressAnswered(started: Bool) {
        isEngaged = started
    }

    mutating func released() {
        isEngaged = false
    }

    /// The owner ended a toggle's recording some other way (silence, the ceiling, a fault, a pause, the tray), so the
    /// lock's next change is a new press. A held key stays engaged until it comes up, so neither its release nor its
    /// key repeats are taken for a new press.
    mutating func cancelToggle() {
        guard binding.gesture == .toggle else { return }
        isEngaged = false
    }

    /// After the event tap was disabled and re-enabled, when events may have been lost, with the key as read now.
    /// Nothing ever presses. A held key that is no longer down releases; one found down with nothing engaged waits
    /// for its release (`awaitsRelease`), so a down still queued behind the re-enable is not taken for a fresh
    /// press. Caps Lock releases once when it is engaged and its lock changed, which means an odd number of taps
    /// was missed; an even number (none, or a stop and a start) leaves the recording as it is. Either way the lock
    /// state found becomes the baseline.
    mutating func resynchronize(isDown: Bool, lockIsOn: Bool) -> HotkeyKeyAction {
        switch binding.gesture {
        case .toggle:
            let changed = lockIsOn != self.lockIsOn
            self.lockIsOn = lockIsOn
            guard isEngaged, changed else { return .none }
            isEngaged = false
            return .release
        case .hold:
            guard isEngaged else {
                awaitsRelease = isDown
                return .none
            }
            guard !isDown else { return .none }
            isEngaged = false
            return .release
        }
    }
}

/// What the event tap's callback reads from a `CGEvent`, as plain values, so it can be handed to the main actor.
struct HotkeyObservedEvent: Equatable, Sendable {
    let typeRawValue: UInt32
    let keyCode: CGKeyCode
    let flagsRawValue: UInt64
    let isAutorepeat: Bool
    /// Posted by Scribe itself (`KeystrokeEvents.syntheticMarker`), such as the Command-V of a paste.
    let isSynthetic: Bool

    init(typeRawValue: UInt32, keyCode: CGKeyCode, flagsRawValue: UInt64, isAutorepeat: Bool, isSynthetic: Bool) {
        self.typeRawValue = typeRawValue
        self.keyCode = keyCode
        self.flagsRawValue = flagsRawValue
        self.isAutorepeat = isAutorepeat
        self.isSynthetic = isSynthetic
    }

    init(type: CGEventType, event: CGEvent) {
        self.init(
            typeRawValue: type.rawValue,
            keyCode: CGKeyCode(truncatingIfNeeded: event.getIntegerValueField(.keyboardEventKeycode)),
            flagsRawValue: event.flags.rawValue,
            isAutorepeat: event.getIntegerValueField(.keyboardEventAutorepeat) != 0,
            isSynthetic: event.getIntegerValueField(.eventSourceUserData) == KeystrokeEvents.syntheticMarker)
    }
}

/// The push-to-talk key: a listen-only event tap on the main run loop that reports the bound key's presses and
/// releases to its owner. It never starts or stops a recording itself and never waits on anything: the owner answers
/// a press at once (`onPressed` only admits the recording and returns), so the callback returns straight away. A
/// listen-only tap cannot hold back or change an event, so the key reaches other apps whatever Scribe decides,
/// including while dictation is paused.
@MainActor
final class HotkeyManager: DictationTriggerSource {
    /// A press of the bound key (Caps Lock: turned on). Returns whether a recording started.
    var onPressed: ((HotkeyBinding) -> Bool)?
    /// The bound key's recording should end.
    var onReleased: ((HotkeyBinding, HotkeyReleaseCause) -> Void)?

    private var state: HotkeyKeyState
    private var eventTap: CFMachPort?
    private var runLoopSource: CFRunLoopSource?
    private let readModifierFlags: () -> CGEventFlags
    private let readKeyDown: (CGKeyCode) -> Bool

    /// - Parameters:
    ///   - readModifierFlags: the modifier state now, for Caps Lock's baseline and for re-reading a modifier after
    ///     the tap is re-enabled; `CGEventSource.flagsState` in the app.
    ///   - readKeyDown: whether a key without a modifier flag (F13 and the like) is down now.
    init(
        keyCode: CGKeyCode = HotkeySettingsStore.keyCode,
        readModifierFlags: @escaping () -> CGEventFlags = { CGEventSource.flagsState(.combinedSessionState) },
        readKeyDown: @escaping (CGKeyCode) -> Bool = { CGEventSource.keyState(.combinedSessionState, key: $0) }
    ) {
        self.readModifierFlags = readModifierFlags
        self.readKeyDown = readKeyDown
        state = HotkeyKeyState(
            binding: HotkeyBinding(keyCode: keyCode),
            lockIsOn: readModifierFlags().contains(.maskAlphaShift))
    }

    /// The bound key. Setting it rebinds at once: a recording the old key holds ends (`bindingChanged`), because the
    /// old key's release would never match the new binding, and the new key needs a fresh press.
    var keyCode: CGKeyCode {
        get { state.binding.keyCode }
        set { rebind(to: newValue) }
    }

    var binding: HotkeyBinding {
        state.binding
    }

    /// Whether the bound key holds a recording.
    var isEngaged: Bool {
        state.isEngaged
    }

    /// Whether the event tap is installed.
    var isRunning: Bool {
        eventTap != nil
    }

    /// Installs the event tap and returns whether it runs. `requestingAccess` lets macOS show its Input Monitoring
    /// prompt when access has not been decided; a retry passes false, so the prompt is never shown again.
    @discardableResult
    func start(requestingAccess: Bool = true) -> Bool {
        guard eventTap == nil else { return true }
        guard Self.hasInputMonitoringAccess(requesting: requestingAccess) else {
            ScribeLog.warning(.hotkey, "Input Monitoring is not granted, so the push-to-talk key cannot be heard")
            return false
        }

        let eventMask =
            (CGEventMask(1) << CGEventType.flagsChanged.rawValue)
            | (CGEventMask(1) << CGEventType.keyDown.rawValue)
            | (CGEventMask(1) << CGEventType.keyUp.rawValue)

        let callback: CGEventTapCallBack = { _, type, event, userInfo in
            guard let userInfo else {
                return Unmanaged.passUnretained(event)
            }
            let manager = Unmanaged<HotkeyManager>.fromOpaque(userInfo).takeUnretainedValue()
            let observed = HotkeyObservedEvent(type: type, event: event)
            // The tap's run loop source is on the main run loop (`start`), so this callback runs on the main thread.
            MainActor.assumeIsolated {
                manager.receive(observed)
            }
            return Unmanaged.passUnretained(event)
        }

        // Deliberately `.listenOnly`, not `.defaultTap`. An active tap can be silently starved of *all* events (not
        // just the hotkey's) on machines where an MDM-managed endpoint security agent restricts event-modifying taps
        // from unnotarized third-party apps: verified on a Microsoft Intune/Defender-managed Mac, where an active tap
        // received zero events while an otherwise-identical listen-only tap worked perfectly. None of the supported
        // push-to-talk keys (modifiers, Caps Lock, F13-F19) has a default system action worth suppressing.
        guard
            let tap = CGEvent.tapCreate(
                tap: .cgSessionEventTap,
                place: .headInsertEventTap,
                options: .listenOnly,
                eventsOfInterest: eventMask,
                callback: callback,
                userInfo: UnsafeMutableRawPointer(Unmanaged.passUnretained(self).toOpaque()))
        else {
            ScribeLog.warning(.hotkey, "The push-to-talk event tap could not be created; Input Monitoring may be off")
            return false
        }

        guard let source = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, tap, 0) else {
            ScribeLog.error(.hotkey, "The push-to-talk event tap's run loop source could not be created")
            CFMachPortInvalidate(tap)
            return false
        }

        eventTap = tap
        runLoopSource = source
        state = HotkeyKeyState(binding: state.binding, lockIsOn: readModifierFlags().contains(.maskAlphaShift))
        CFRunLoopAddSource(CFRunLoopGetMain(), source, .commonModes)
        CGEvent.tapEnable(tap: tap, enable: true)
        ScribeLog.info(
            .hotkey, "The push-to-talk key is ready", .integer("keyCode", state.binding.keyCode),
            .name("gesture", state.binding.gesture))
        return true
    }

    func stop() {
        if let runLoopSource {
            CFRunLoopRemoveSource(CFRunLoopGetMain(), runLoopSource, .commonModes)
        }
        if let eventTap {
            CGEvent.tapEnable(tap: eventTap, enable: false)
            CFMachPortInvalidate(eventTap)
        }
        runLoopSource = nil
        eventTap = nil
        state.released()
    }

    func cancelToggle(_ binding: HotkeyBinding) {
        guard binding == state.binding else { return }
        state.cancelToggle()
    }

    /// Whether macOS lets Scribe listen to the keyboard, asking it once when `requesting` and the answer is not known.
    static func hasInputMonitoringAccess(requesting: Bool) -> Bool {
        if ProcessInfo.processInfo.environment["SCRIBE_FORCE_INPUT_MONITORING_DENIED"] == "1" {
            return false
        }
        if CGPreflightListenEventAccess() {
            return true
        }
        guard requesting else { return false }
        _ = CGRequestListenEventAccess()
        return CGPreflightListenEventAccess()
    }

    /// One event from the tap, on the main actor.
    func receive(_ observed: HotkeyObservedEvent) {
        guard let type = CGEventType(rawValue: observed.typeRawValue) else { return }
        let flags = CGEventFlags(rawValue: observed.flagsRawValue)
        let event: HotkeyKeyEvent
        switch type {
        case .tapDisabledByTimeout, .tapDisabledByUserInput:
            reenableAndResynchronize()
            return
        case .flagsChanged:
            guard !observed.isSynthetic, observed.keyCode == state.binding.keyCode else { return }
            if state.binding.gesture == .toggle {
                event = .lockChanged(isOn: flags.contains(.maskAlphaShift))
            } else {
                event = .modifierChanged(isDown: isDown(observed.keyCode, flags: flags))
            }
        case .keyDown:
            guard !observed.isSynthetic, observed.keyCode == state.binding.keyCode else { return }
            event = .keyDown(isRepeat: observed.isAutorepeat)
        case .keyUp:
            guard !observed.isSynthetic, observed.keyCode == state.binding.keyCode else { return }
            event = .keyUp
        default:
            return
        }
        perform(state.receive(event))
    }

    private func perform(_ action: HotkeyKeyAction) {
        let binding = state.binding
        switch action {
        case .none:
            return
        case .press:
            let started = onPressed?(binding) ?? false
            state.pressAnswered(started: started)
            ScribeLog.debug(.hotkey, "Push-to-talk pressed", .flag("started", started))
        case .release:
            state.released()
            ScribeLog.debug(.hotkey, "Push-to-talk released")
            onReleased?(binding, .keyReleased)
        }
    }

    private func rebind(to keyCode: CGKeyCode) {
        guard keyCode != state.binding.keyCode else { return }
        let previous = state
        state = HotkeyKeyState(
            binding: HotkeyBinding(keyCode: keyCode), lockIsOn: readModifierFlags().contains(.maskAlphaShift))
        ScribeLog.info(
            .hotkey, "The push-to-talk key changed", .integer("keyCode", keyCode),
            .name("gesture", state.binding.gesture), .flag("heldRecording", previous.isEngaged))
        if previous.isEngaged {
            onReleased?(previous.binding, .bindingChanged)
        }
    }

    /// macOS disables a tap it judged too slow (or on some user input); events may have been lost meanwhile, so the
    /// key is read again rather than trusted.
    private func reenableAndResynchronize() {
        guard let eventTap else { return }
        CGEvent.tapEnable(tap: eventTap, enable: true)
        ScribeLog.warning(.hotkey, "macOS disabled the push-to-talk event tap; it is enabled again and the key re-read")
        resynchronizeHeldState()
    }

    /// Reads the bound key's state now and settles what events lost meanwhile would have
    /// (`HotkeyKeyState.resynchronize`): a held key found up, or a Caps Lock whose lock changed an odd number of times,
    /// releases its recording. Nothing presses; a key found down needs a fresh press.
    func resynchronizeHeldState() {
        let binding = state.binding
        let flags = readModifierFlags()
        let action = state.resynchronize(
            isDown: isDown(binding.keyCode, flags: flags), lockIsOn: flags.contains(.maskAlphaShift))
        if action == .release {
            ScribeLog.info(
                .hotkey, "The push-to-talk key was released while the event tap was off",
                .name("gesture", binding.gesture))
            onReleased?(binding, .tapResynchronized)
        } else if state.awaitsRelease {
            ScribeLog.info(.hotkey, "The push-to-talk key was found down; it has to come up before it starts anything")
        }
    }

    /// Per-side modifier bits carried in `CGEventFlags` (the well-known, historically stable `NX_DEVICE*KEYMASK`
    /// constants from `IOLLEvent.h`). A modifier's state is read from these rather than from
    /// `CGEventSource.keyState`, which was found to report `false` right after a matching flags event on at least one
    /// MDM-managed Mac. Caps Lock is not here: it has no sides, and its lock state is `.maskAlphaShift`.
    private static let deviceKeyMasks: [CGKeyCode: UInt64] = [
        59: 0x0001,  // Left Control
        56: 0x0002,  // Left Shift
        60: 0x0004,  // Right Shift
        55: 0x0008,  // Left Command
        54: 0x0010,  // Right Command
        58: 0x0020,  // Left Option
        61: 0x0040,  // Right Option
        62: 0x2000,  // Right Control
    ]

    private func isDown(_ keyCode: CGKeyCode, flags: CGEventFlags) -> Bool {
        if let mask = Self.deviceKeyMasks[keyCode] {
            return flags.rawValue & mask != 0
        }
        return readKeyDown(keyCode)
    }
}
