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

    /// <summary>Installs the keyboard hook on a dedicated message-pump thread.</summary>
    void Start();

    /// <summary>Removes the keyboard hook and stops dispatching events.</summary>
    void Stop();

    /// <summary>Replaces the active binding; takes effect immediately without restarting the hook.</summary>
    void UpdateBinding(HotkeyBinding binding);

    /// <summary>
    /// Resets toggle mode's internal on/off state without raising events. Called when the app ends
    /// a toggle dictation itself (e.g. silence auto-stop), so the next press starts a new dictation
    /// instead of being swallowed as the missing "toggle off".
    /// </summary>
    void CancelToggle();

    /// <summary>Raised when dictation should begin (hold key down, or toggle on).</summary>
    event EventHandler? Activated;

    /// <summary>Raised when dictation should end and transcription should run (hold key up, or toggle off).</summary>
    event EventHandler? Deactivated;
}
