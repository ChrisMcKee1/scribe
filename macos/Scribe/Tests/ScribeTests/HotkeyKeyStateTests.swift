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

    /// Caps Lock counts a change of its lock state as a tap: a recording starts when the lock turns on and ends at the
    /// next change, and a flags event that repeats the lock state is not a tap. With nothing recording, a tap that
    /// turns the lock off starts nothing, so the light is on exactly while a recording runs.
    func testCapsLockPressesWhenItsLockTurnsOnAndReleasesAtTheNextChange() {
        var state = HotkeyKeyState(binding: capsLock, lockIsOn: false)

        XCTAssertEqual(state.receive(.lockChanged(isOn: false)), .none, "no change")
        XCTAssertEqual(state.receive(.lockChanged(isOn: true)), .press)
        state.pressAnswered(started: true)
        XCTAssertEqual(state.receive(.lockChanged(isOn: true)), .none, "no change")
        XCTAssertEqual(state.receive(.lockChanged(isOn: false)), .release)
        state.released()
        XCTAssertEqual(state.receive(.lockChanged(isOn: true)), .press)

        // Launched with the light on: the first tap turns it off and starts nothing, and the next one starts.
        var launched = HotkeyKeyState(binding: capsLock, lockIsOn: true)
        XCTAssertEqual(launched.receive(.lockChanged(isOn: false)), .none, "a tap turning the light off pressed")
        XCTAssertEqual(launched.receive(.lockChanged(isOn: true)), .press)
    }

    /// After the owner ended a toggle's recording itself (silence, the ceiling, a fault, a pause), the light is still
    /// on with nothing recording. The next tap turns it off and starts nothing, and the tap after it starts the next
    /// recording with the light on: the light is back in step after one tap.
    func testAfterACancelledToggleTheLightComesBackInStepAfterOneTap() {
        var state = HotkeyKeyState(binding: capsLock, lockIsOn: false)
        XCTAssertEqual(state.receive(.lockChanged(isOn: true)), .press)
        state.pressAnswered(started: true)

        state.cancelToggle()

        XCTAssertFalse(state.isEngaged)
        XCTAssertEqual(state.receive(.lockChanged(isOn: false)), .none, "the tap turning the light off pressed")
        XCTAssertEqual(state.receive(.lockChanged(isOn: true)), .press)
    }

    /// A press the owner turned away with the light coming on leaves the light on with nothing recording; it comes
    /// back in step the same way.
    func testARefusedCapsLockPressLeavesNothingEngagedAndTheLightComesBackInStep() {
        var state = HotkeyKeyState(binding: capsLock, lockIsOn: false)
        XCTAssertEqual(state.receive(.lockChanged(isOn: true)), .press)
        state.pressAnswered(started: false)

        XCTAssertFalse(state.isEngaged)
        XCTAssertEqual(state.receive(.lockChanged(isOn: false)), .none, "the tap turning the light off pressed")
        XCTAssertEqual(state.receive(.lockChanged(isOn: true)), .press)
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
        XCTAssertTrue(free.awaitsRelease)
        // The key's own down, still queued behind the re-enable, arrives after the resync: not a fresh press.
        XCTAssertEqual(free.receive(.modifierChanged(isDown: true)), .none, "a queued down pressed")
        XCTAssertEqual(free.receive(.modifierChanged(isDown: false)), .none)
        XCTAssertFalse(free.awaitsRelease)
        XCTAssertEqual(free.receive(.modifierChanged(isDown: true)), .press, "the fresh press after the release")

        var freeKey = HotkeyKeyState(binding: f13, lockIsOn: false)
        XCTAssertEqual(freeKey.resynchronize(isDown: true, lockIsOn: false), .none)
        XCTAssertEqual(freeKey.receive(.keyDown(isRepeat: false)), .none, "a queued down pressed")
        XCTAssertEqual(freeKey.receive(.keyUp), .none)
        XCTAssertEqual(freeKey.receive(.keyDown(isRepeat: false)), .press)

        // Found up at a later resync, a key waiting for its release is free again.
        var lost = HotkeyKeyState(binding: rightOption, lockIsOn: false)
        XCTAssertEqual(lost.resynchronize(isDown: true, lockIsOn: false), .none)
        XCTAssertEqual(lost.resynchronize(isDown: false, lockIsOn: false), .none)
        XCTAssertEqual(lost.receive(.modifierChanged(isDown: true)), .press)

        var toggle = HotkeyKeyState(binding: capsLock, lockIsOn: false)
        XCTAssertEqual(toggle.resynchronize(isDown: false, lockIsOn: true), .none)
        XCTAssertTrue(toggle.lockIsOn)
        XCTAssertEqual(toggle.receive(.lockChanged(isOn: true)), .none, "the new baseline counted as a tap")
        XCTAssertEqual(toggle.receive(.lockChanged(isOn: false)), .none, "the tap turning the light off pressed")
        XCTAssertEqual(toggle.receive(.lockChanged(isOn: true)), .press)
    }

    /// A Caps Lock recording whose stop tap was lost while the tap was off: the lock changed an odd number of times,
    /// so the resync releases it once. An even number (a stop and a start) leaves the recording as it is, and a lock
    /// change while nothing records only becomes the baseline. Both starting polarities of the light.
    func testResynchronizingACapsLockRecordingReleasesItOnlyWhenTheLockChangedAnOddNumberOfTimes() {
        for initial in [false, true] {
            var state = HotkeyKeyState(binding: capsLock, lockIsOn: initial)
            if initial {
                XCTAssertEqual(state.receive(.lockChanged(isOn: false)), .none, "the light turning off pressed")
            }
            XCTAssertEqual(state.receive(.lockChanged(isOn: true)), .press, "light on at launch: \(initial)")
            state.pressAnswered(started: true)

            XCTAssertEqual(state.resynchronize(isDown: false, lockIsOn: true), .none, "no tap was missed")
            XCTAssertTrue(state.isEngaged)
            XCTAssertEqual(state.resynchronize(isDown: false, lockIsOn: false), .release, "one tap was missed")
            XCTAssertFalse(state.isEngaged)
            XCTAssertEqual(state.resynchronize(isDown: false, lockIsOn: false), .none, "released twice")

            // Idle now: a lock change missed while nothing records is not a made-up press.
            XCTAssertEqual(state.resynchronize(isDown: false, lockIsOn: true), .none, "a press was made up")
            XCTAssertEqual(state.receive(.lockChanged(isOn: false)), .none, "the light turning off pressed")
            XCTAssertEqual(state.receive(.lockChanged(isOn: true)), .press, "the next real tap")
        }
    }

    /// Two taps missed while recording: the lock is back where it was, and the recording goes on.
    func testResynchronizingACapsLockRecordingAfterAnEvenNumberOfMissedTapsKeepsIt() {
        var state = HotkeyKeyState(binding: capsLock, lockIsOn: false)
        XCTAssertEqual(state.receive(.lockChanged(isOn: true)), .press)
        state.pressAnswered(started: true)
        XCTAssertEqual(state.resynchronize(isDown: false, lockIsOn: true), .none)
        XCTAssertTrue(state.isEngaged)
        XCTAssertEqual(state.receive(.lockChanged(isOn: false)), .release)
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

    /// Caps Lock through the manager: a recording starts when the light comes on and ends at the next change. After a
    /// cancelled toggle the light is on with nothing recording, so the next tap turns it off and starts nothing, and
    /// the tap after it starts the next recording.
    func testCapsLockThroughTheManager() {
        let recorder = Recorder()
        let manager = makeManager(keyCode: 57, recorder: recorder)

        manager.receive(event(.flagsChanged, keyCode: 57, flags: [.maskAlphaShift]))
        XCTAssertEqual(recorder.presses.count, 1)
        manager.cancelToggle(HotkeyBinding(keyCode: 57))
        manager.receive(event(.flagsChanged, keyCode: 57, flags: []))
        XCTAssertEqual(recorder.presses.count, 1, "the tap turning the light off started a recording")
        XCTAssertTrue(recorder.releases.isEmpty)
        manager.receive(event(.flagsChanged, keyCode: 57, flags: [.maskAlphaShift]))
        XCTAssertEqual(recorder.presses.count, 2)
        manager.receive(event(.flagsChanged, keyCode: 57, flags: []))
        XCTAssertEqual(recorder.causes, [.keyReleased])
    }

    /// Launched with Caps Lock's light on, and after a press Scribe turned away, the light is on with nothing
    /// recording: the next tap turns it off and starts nothing, and the one after it starts a recording.
    func testACapsLockLightOnWithNothingRecordingComesBackInStepAfterOneTap() {
        let recorder = Recorder()
        recorder.flags = [.maskAlphaShift]
        let manager = makeManager(keyCode: 57, recorder: recorder)

        manager.receive(event(.flagsChanged, keyCode: 57, flags: []))
        XCTAssertTrue(recorder.presses.isEmpty, "the tap turning the light off started a recording")
        manager.receive(event(.flagsChanged, keyCode: 57, flags: [.maskAlphaShift]))
        XCTAssertEqual(recorder.presses.count, 1)
        XCTAssertTrue(manager.isEngaged)
        manager.receive(event(.flagsChanged, keyCode: 57, flags: []))
        XCTAssertEqual(recorder.causes, [.keyReleased])

        recorder.startsRecording = false
        manager.receive(event(.flagsChanged, keyCode: 57, flags: [.maskAlphaShift]))
        XCTAssertEqual(recorder.presses.count, 2)
        XCTAssertFalse(manager.isEngaged)
        recorder.startsRecording = true
        manager.receive(event(.flagsChanged, keyCode: 57, flags: []))
        XCTAssertEqual(recorder.presses.count, 2, "the tap turning the light off started a recording")
        manager.receive(event(.flagsChanged, keyCode: 57, flags: [.maskAlphaShift]))
        XCTAssertEqual(recorder.presses.count, 3)
        XCTAssertTrue(manager.isEngaged)
    }

    /// A cancel for a binding the manager no longer has (the key was rebound since) settles nothing.
    func testACancelForAnotherBindingLeavesTheToggleEngaged() {
        let recorder = Recorder()
        let manager = makeManager(keyCode: 57, recorder: recorder)
        manager.receive(event(.flagsChanged, keyCode: 57, flags: [.maskAlphaShift]))
        manager.cancelToggle(HotkeyBinding(keyCode: 61))
        XCTAssertTrue(manager.isEngaged)
    }

    /// The stop tap for a Caps Lock recording was lost while the tap was off: read again, the lock has changed, and
    /// the recording is released once, as `tapResynchronized`.
    func testResynchronizingAfterTheTapCameBackReleasesACapsLockWhoseLockChanged() {
        let recorder = Recorder()
        let manager = makeManager(keyCode: 57, recorder: recorder)
        manager.receive(event(.flagsChanged, keyCode: 57, flags: [.maskAlphaShift]))
        XCTAssertTrue(manager.isEngaged)

        recorder.flags = [.maskAlphaShift]
        manager.resynchronizeHeldState()
        XCTAssertTrue(recorder.releases.isEmpty, "released with the lock unchanged")

        recorder.flags = []
        manager.resynchronizeHeldState()
        XCTAssertEqual(recorder.causes, [.tapResynchronized])
        XCTAssertFalse(manager.isEngaged)
        XCTAssertEqual(recorder.presses.count, 1)
    }

    /// A held key found down at the resync with nothing engaged: its own down, queued behind the re-enable, is not a
    /// fresh press; the key has to come up and go down again.
    func testAKeyFoundDownAtTheResyncNeedsAFreshPress() {
        let recorder = Recorder()
        let manager = makeManager(keyCode: 61, recorder: recorder)
        recorder.flags = rightOptionDown
        manager.resynchronizeHeldState()

        manager.receive(event(.flagsChanged, keyCode: 61, flags: rightOptionDown))
        XCTAssertTrue(recorder.presses.isEmpty, "the queued down pressed")
        manager.receive(event(.flagsChanged, keyCode: 61, flags: []))
        XCTAssertTrue(recorder.releases.isEmpty)
        manager.receive(event(.flagsChanged, keyCode: 61, flags: rightOptionDown))
        XCTAssertEqual(recorder.presses, [HotkeyBinding(keyCode: 61)])
    }
}
