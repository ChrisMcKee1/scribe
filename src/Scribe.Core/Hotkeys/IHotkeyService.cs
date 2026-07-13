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
    /// Resets toggle mode's internal on/off state without raising events. Called when the app ends
    /// a toggle dictation itself (e.g. silence auto-stop), so the next press starts a new dictation
    /// instead of being swallowed as the missing "toggle off".
    /// </summary>
    void CancelToggle();

    /// <summary>
    /// While enabled, the hook passes every key event through untouched: nothing is suppressed and
    /// no dictation transition fires. The settings window turns this on while its binding-capture
    /// box is armed so the current push-to-talk key can be typed into a new chord (and cannot
    /// start a recording). Entering capture deactivates any dictation already in flight.
    /// </summary>
    void SetCaptureMode(bool enabled);

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

public sealed class HotkeyTriggerEventArgs(HotkeyTrigger trigger) : EventArgs
{
    public HotkeyTrigger Trigger { get; } = trigger;
}
