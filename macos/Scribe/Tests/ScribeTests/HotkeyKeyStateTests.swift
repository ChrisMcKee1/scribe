import CoreGraphics
import XCTest

@testable import Scribe

/// The bound key's press and release, decided from its events alone.
final class HotkeyKeyStateTests: XCTestCase {
    private let rightOption = HotkeyBinding(keyCode: 61)
    private let f13 = HotkeyBinding(keyCode: 105)
    private let capsLock = HotkeyBinding(keyCode: 57)

    func testAHeldModifierPressesOnceAndReleasesOnce() {
        var state = HotkeyKeyState(binding: rightOption, lockIsOn: false)

        XCTAssertEqual(state.receive(.modifierChanged(isDown: false)), .none, "a release with nothing held")
        XCTAssertEqual(state.receive(.modifierChanged(isDown: true)), .press)
        state.pressAnswered(started: true)
        XCTAssertEqual(state.receive(.modifierChanged(isDown: true)), .none, "a second down while held")
        XCTAssertEqual(state.receive(.modifierChanged(isDown: false)), .release)
        state.released()
        XCTAssertEqual(state.receive(.modifierChanged(isDown: false)), .none)
    }

    func testAKeyRepeatIsNeverANewPress() {
        var state = HotkeyKeyState(binding: f13, lockIsOn: false)

        XCTAssertEqual(state.receive(.keyDown(isRepeat: false)), .press)
        state.pressAnswered(started: true)
        XCTAssertEqual(state.receive(.keyDown(isRepeat: true)), .none)
        XCTAssertEqual(state.receive(.keyDown(isRepeat: false)), .none)
        XCTAssertEqual(state.receive(.keyUp), .release)
        state.released()
        XCTAssertEqual(state.receive(.keyUp), .none)
        XCTAssertEqual(state.receive(.keyDown(isRepeat: true)), .none, "a repeat after the release")
    }

    /// A press the owner turned away (paused, still processing) leaves the key free: its release does nothing and the
    /// next press is a press.
    func testARefusedPressLeavesTheKeyFree() {
        var state = HotkeyKeyState(binding: rightOption, lockIsOn: false)

        XCTAssertEqual(state.receive(.modifierChanged(isDown: true)), .press)
        state.pressAnswered(started: false)
        XCTAssertFalse(state.isEngaged)
        XCTAssertEqual(state.receive(.modifierChanged(isDown: false)), .none)
        XCTAssertEqual(state.receive(.modifierChanged(isDown: true)), .press)
    }

    /// Caps Lock counts a change of its lock state as a tap, and taps alternate press and release; a flags event
    /// that repeats the lock state is not a tap.
    func testCapsLockTogglesOnEachChangeOfItsLockOnly() {
        var state = HotkeyKeyState(binding: capsLock, lockIsOn: false)

        XCTAssertEqual(state.receive(.lockChanged(isOn: false)), .none, "no change")
        XCTAssertEqual(state.receive(.lockChanged(isOn: true)), .press)
        state.pressAnswered(started: true)
        XCTAssertEqual(state.receive(.lockChanged(isOn: true)), .none, "no change")
        XCTAssertEqual(state.receive(.lockChanged(isOn: false)), .release)
        state.released()
        XCTAssertEqual(state.receive(.lockChanged(isOn: true)), .press)
    }

    /// After the owner ended a toggle's recording itself (silence, the ceiling), the light is still on and the next
    /// tap turns it off: that tap starts the next recording rather than ending one that is already over.
    func testAfterACancelledToggleTheNextTapIsAPress() {
        var state = HotkeyKeyState(binding: capsLock, lockIsOn: false)
        XCTAssertEqual(state.receive(.lockChanged(isOn: true)), .press)
        state.pressAnswered(started: true)

        state.cancelToggle()

        XCTAssertFalse(state.isEngaged)
        XCTAssertEqual(state.receive(.lockChanged(isOn: false)), .press)
    }

    /// A held key stays engaged when its recording ended some other way, so neither its release nor its repeats
    /// are taken for a new press.
    func testCancelToggleLeavesAHeldKeyEngagedUntilItComesUp() {
        var state = HotkeyKeyState(binding: f13, lockIsOn: false)
        XCTAssertEqual(state.receive(.keyDown(isRepeat: false)), .press)
        state.pressAnswered(started: true)

        state.cancelToggle()

        XCTAssertTrue(state.isEngaged)
        XCTAssertEqual(state.receive(.keyDown(isRepeat: false)), .none)
        XCTAssertEqual(state.receive(.keyUp), .release)
    }

    /// After the tap came back: a held key found up releases, a key found down that was not engaged needs a fresh
    /// press, and Caps Lock only takes the lock state it finds as its baseline.
    func testResynchronizingReleasesALostReleaseAndNeverPresses() {
        var held = HotkeyKeyState(binding: rightOption, lockIsOn: false)
        XCTAssertEqual(held.receive(.modifierChanged(isDown: true)), .press)
        held.pressAnswered(started: true)
        XCTAssertEqual(held.resynchronize(isDown: true, lockIsOn: false), .none)
        XCTAssertTrue(held.isEngaged)
        XCTAssertEqual(held.resynchronize(isDown: false, lockIsOn: false), .release)
        XCTAssertFalse(held.isEngaged)

        var free = HotkeyKeyState(binding: rightOption, lockIsOn: false)
        XCTAssertEqual(free.resynchronize(isDown: true, lockIsOn: false), .none, "a press was made up")
        XCTAssertEqual(free.receive(.modifierChanged(isDown: false)), .none)

        var toggle = HotkeyKeyState(binding: capsLock, lockIsOn: false)
        XCTAssertEqual(toggle.resynchronize(isDown: false, lockIsOn: true), .none)
        XCTAssertTrue(toggle.lockIsOn)
        XCTAssertEqual(toggle.receive(.lockChanged(isOn: true)), .none, "the new baseline counted as a tap")
        XCTAssertEqual(toggle.receive(.lockChanged(isOn: false)), .press)
    }
}

/// The manager around the state: which events it reads, the owner's answers, rebinding and resynchronizing, without
/// an event tap.
@MainActor
final class HotkeyManagerEventTests: XCTestCase {
    private final class Recorder {
        var presses: [HotkeyBinding] = []
        var releases: [(binding: HotkeyBinding, cause: HotkeyReleaseCause)] = []
        var startsRecording = true
        /// What the manager reads as the modifier state now.
        var flags: CGEventFlags = []

        var causes: [HotkeyReleaseCause] {
            releases.map { $0.cause }
        }
    }

    private func makeManager(keyCode: CGKeyCode, recorder: Recorder) -> HotkeyManager {
        let manager = HotkeyManager(
            keyCode: keyCode, readModifierFlags: { recorder.flags }, readKeyDown: { _ in false })
        manager.onPressed = { binding in
            recorder.presses.append(binding)
            return recorder.startsRecording
        }
        manager.onReleased = { binding, cause in
            recorder.releases.append((binding, cause))
        }
        return manager
    }

    private func event(
        _ type: CGEventType, keyCode: CGKeyCode, flags: CGEventFlags = [], repeat isAutorepeat: Bool = false,
        synthetic: Bool = false
    ) -> HotkeyObservedEvent {
        HotkeyObservedEvent(
            typeRawValue: type.rawValue, keyCode: keyCode, flagsRawValue: flags.rawValue,
            isAutorepeat: isAutorepeat, isSynthetic: synthetic)
    }

    /// Right Option's own device bit, as `CGEventFlags` carries it.
    private let rightOptionDown = CGEventFlags(rawValue: CGEventFlags.maskAlternate.rawValue | 0x0040)

    func testAHeldModifierPressesAndReleasesThroughItsOwnDeviceBit() {
        let recorder = Recorder()
        let manager = makeManager(keyCode: 61, recorder: recorder)

        manager.receive(event(.flagsChanged, keyCode: 61, flags: rightOptionDown))
        XCTAssertTrue(manager.isEngaged)
        // Left Option going down meanwhile is another key's event.
        manager.receive(event(.flagsChanged, keyCode: 58, flags: [.maskAlternate]))
        manager.receive(event(.flagsChanged, keyCode: 61, flags: []))

        XCTAssertEqual(recorder.presses, [HotkeyBinding(keyCode: 61)])
        XCTAssertEqual(recorder.causes, [.keyReleased])
        XCTAssertFalse(manager.isEngaged)
    }

    /// Scribe's own keystrokes (the Command-V of a paste) carry its marker and are never taken for the bound key.
    func testScribesOwnKeystrokesAreIgnored() {
        let recorder = Recorder()
        let manager = makeManager(keyCode: 55, recorder: recorder)

        manager.receive(event(.flagsChanged, keyCode: 55, flags: [.maskCommand], synthetic: true))
        manager.receive(event(.keyDown, keyCode: 55, synthetic: true))

        XCTAssertTrue(recorder.presses.isEmpty)
        XCTAssertFalse(manager.isEngaged)
    }

    /// A press the owner turned away leaves the key free, so the release that follows does nothing.
    func testAPressTheOwnerRefusedIsNotHeld() {
        let recorder = Recorder()
        recorder.startsRecording = false
        let manager = makeManager(keyCode: 105, recorder: recorder)

        manager.receive(event(.keyDown, keyCode: 105))
        manager.receive(event(.keyDown, keyCode: 105, repeat: true))
        manager.receive(event(.keyUp, keyCode: 105))

        XCTAssertEqual(recorder.presses.count, 1)
        XCTAssertTrue(recorder.releases.isEmpty)
    }

    /// Rebinding while the old key holds a recording releases it once, as `bindingChanged`, and the new key needs a
    /// fresh press; rebinding while nothing is held releases nothing.
    func testRebindingWhileHeldReleasesTheOldKeyOnce() {
        let recorder = Recorder()
        let manager = makeManager(keyCode: 61, recorder: recorder)
        manager.receive(event(.flagsChanged, keyCode: 61, flags: rightOptionDown))

        manager.keyCode = 105

        XCTAssertEqual(recorder.releases.count, 1)
        XCTAssertEqual(recorder.releases.first?.binding, HotkeyBinding(keyCode: 61))
        XCTAssertEqual(recorder.releases.first?.cause, .bindingChanged)
        XCTAssertFalse(manager.isEngaged)
        manager.receive(event(.flagsChanged, keyCode: 61, flags: []))
        XCTAssertEqual(recorder.releases.count, 1, "the old key's own release reached the owner")

        manager.keyCode = 107
        XCTAssertEqual(recorder.releases.count, 1)
        manager.receive(event(.keyDown, keyCode: 107))
        XCTAssertEqual(recorder.presses.last, HotkeyBinding(keyCode: 107))
    }

    /// After the tap came back, a held key that is up now is released, as `tapResynchronized`.
    func testResynchronizingAfterTheTapCameBackReleasesAKeyThatIsUp() {
        let recorder = Recorder()
        let manager = makeManager(keyCode: 61, recorder: recorder)
        manager.receive(event(.flagsChanged, keyCode: 61, flags: rightOptionDown))

        recorder.flags = rightOptionDown
        manager.resynchronizeHeldState()
        XCTAssertTrue(recorder.releases.isEmpty)

        recorder.flags = []
        manager.resynchronizeHeldState()
        XCTAssertEqual(recorder.causes, [.tapResynchronized])
        XCTAssertFalse(manager.isEngaged)
    }

    /// Caps Lock through the manager: each change of its lock is a tap, and after a cancelled toggle the tap that turns
    /// the light off starts the next recording.
    func testCapsLockThroughTheManager() {
        let recorder = Recorder()
        let manager = makeManager(keyCode: 57, recorder: recorder)

        manager.receive(event(.flagsChanged, keyCode: 57, flags: [.maskAlphaShift]))
        XCTAssertEqual(recorder.presses.count, 1)
        manager.cancelToggle()
        manager.receive(event(.flagsChanged, keyCode: 57, flags: []))
        XCTAssertEqual(recorder.presses.count, 2)
        XCTAssertTrue(recorder.releases.isEmpty)
        manager.receive(event(.flagsChanged, keyCode: 57, flags: [.maskAlphaShift]))
        XCTAssertEqual(recorder.causes, [.keyReleased])
    }
}
