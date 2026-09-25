using Scribe.Core.Models;

namespace Scribe.Core.Hotkeys;

/// <summary>
/// Installs a global low-level keyboard hook and raises high-level dictation events from the
/// configured <see cref="HotkeyBinding"/>. In hold mode, <see cref="Activated"/> fires on key
/// down and <see cref="Deactivated"/> on key up. In toggle mode each press alternates between
/// the two. Events are dispatched on a dedicated consumer thread, so handlers must marshal to
/// the UI thread themselves and should avoid long blocking work.
/// </summary>
public interface IHotkeyService : IDisposable
{
    /// <summary>True while the keyboard hook is installed.</summary>
    bool IsRunning { get; }

    /// <summary>The binding currently driving the hook.</summary>
    HotkeyBinding Binding { get; }

    /// <summary>The optional binding that records without AI cleanup.</summary>
    HotkeyBinding? DictationOnlyBinding { get; }

    /// <summary>Installs the keyboard hook on a dedicated message-pump thread.</summary>
    void Start();

    /// <summary>Removes the keyboard hook and stops dispatching events.</summary>
    void Stop();

    /// <summary>Replaces the active binding; takes effect immediately without restarting the hook.</summary>
    void UpdateBinding(HotkeyBinding binding);

    /// <summary>Replaces both active bindings; takes effect without restarting the hook.</summary>
    void UpdateBindings(HotkeyBinding binding, HotkeyBinding? dictationOnlyBinding);

    /// <summary>
    /// Releases the latch of one press, without raising events: the press that started a dictation the app has just
    /// ended itself (the silence auto-stop, a microphone fault, a pause, the duration ceiling), or a press the app turned
    /// away because the previous dictation was still processing. Either way the next press starts a new dictation
    /// instead of being swallowed as the missing toggle-off. <paramref name="activation"/> is that press's
    /// <see cref="HotkeyTriggerEventArgs.Activation"/>. Only that press is released, and only while it still owns the
    /// dictation: a newer press keeps its latch, so its own release or second press still ends the dictation it starts.
    /// A latch that owns no dictation (a press refused while another owned one) is forgotten too. Call it only for a stop
    /// the app actually admitted, never for one it turned away.
    /// </summary>
    void CancelToggle(long activation);

    /// <summary>
    /// While enabled, the hook passes every key event through untouched: nothing is suppressed and
    /// no dictation transition fires. The settings window turns this on while its binding-capture
    /// box is armed so the current push-to-talk key can be typed into a new chord (and cannot
    /// start a recording). Entering capture deactivates any dictation already in flight.
    /// </summary>
    void SetCaptureMode(bool enabled);

    /// <summary>
    /// While paused, the hook stands down: every new press of the bindings reaches other apps
    /// untouched and nothing activates dictation. A key whose press was already swallowed before
    /// the pause stays swallowed through its autorepeat and release, so no app is left with an
    /// orphaned key-up; a key pressed during the pause keeps passing through even if dictation
    /// resumes before it is released, and a chord held across the resume activates only when it
    /// is pressed again. Pausing cancels any hold or toggle latch WITHOUT raising
    /// <see cref="Deactivated"/>: stopping a dictation already in progress is the caller's job.
    /// Binding capture keeps working while paused.
    /// </summary>
    void SetPaused(bool paused);

    /// <summary>
    /// <see cref="SetPaused(bool)"/> for a caller whose calls can arrive out of order, such as one
    /// that changes its own pause state under a lock and calls this after releasing it: two such
    /// calls on different threads can reach the hook in the opposite order, and a bare flag cannot
    /// tell which is newer. Number each request while still holding that lock, with a positive
    /// value that increases on every change for the lifetime of this service. A request that is not
    /// newer than one already applied changes nothing, so the latest request wins in any order.
    /// </summary>
    void SetPaused(bool paused, long requestSequence);

    /// <summary>Raised when dictation should begin (hold key down, or toggle on).</summary>
    event EventHandler<HotkeyTriggerEventArgs>? Activated;

    /// <summary>Raised when dictation should end and transcription should run (hold key up, or toggle off).</summary>
    event EventHandler<HotkeyTriggerEventArgs>? Deactivated;
}

public enum HotkeyTrigger
{
    Standard,
    DictationOnly,
}

/// <summary>Why the hook ended a dictation it had started.</summary>
public enum HotkeyDeactivation
{
    /// <summary>
    /// The hold key was released or the toggle key pressed again, or a change to the hook itself (capture mode, a new
    /// binding, a reinstall) ended it.
    /// </summary>
    Released,

    /// <summary>
    /// The input desktop switched (the lock screen, a secure desktop) while the key was held or the toggle was on. The
    /// hook is not called for input there, so it cannot see the key's release, and it ends the recording the way the
    /// binding would have ended it.
    /// </summary>
    DesktopSwitch,

    /// <summary>
    /// Windows had removed the mouse hook (it removes a low-level hook whose callback misses the deadline) while a mouse
    /// button binding was held or toggled on, and the hook's renewal found it gone. The button's release, or the toggle's
    /// second click, may have happened while no hook could see it, so the recording is ended rather than left running.
    /// </summary>
    MouseHookLost,
}

public sealed class HotkeyTriggerEventArgs(
    HotkeyTrigger trigger,
    HotkeyDeactivation deactivation = HotkeyDeactivation.Released,
    long activation = 0)
    : EventArgs
{
    public HotkeyTrigger Trigger { get; } = trigger;

    /// <summary>For <see cref="IHotkeyService.Deactivated"/>, why the dictation ended.</summary>
    public HotkeyDeactivation Deactivation { get; } = deactivation;

    /// <summary>
    /// For <see cref="IHotkeyService.Activated"/>, which press this is: positive, and different for every press. Keep it
    /// with the dictation the press starts, and pass it to <see cref="IHotkeyService.CancelToggle"/> when the app ends
    /// that dictation itself. Zero for a Deactivated.
    /// </summary>
    public long Activation { get; } = activation;
}
