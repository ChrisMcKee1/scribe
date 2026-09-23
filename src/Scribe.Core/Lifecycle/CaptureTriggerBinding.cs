using Scribe.Core.Hotkeys;
using Scribe.Core.Models;

namespace Scribe.Core.Lifecycle;

/// <summary>
/// The hotkey binding that started a capture, and what follows from its mode. The dictation-only binding has its own hold
/// or toggle mode, while the capture settings keep the standard binding whichever trigger fired, so anything that depends
/// on the mode (silence auto-stop, and the log line that explains how a recording ends) asks here instead of reading the
/// standard binding off the settings.
/// </summary>
public static class CaptureTriggerBinding
{
    /// <summary>
    /// The binding <paramref name="trigger"/> belongs to: the dictation-only binding for its own trigger, the standard
    /// binding otherwise. Null when the dictation-only binding is no longer configured (a settings save raced the press).
    /// </summary>
    public static HotkeyBinding? For(AppSettings settings, HotkeyTrigger trigger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return trigger == HotkeyTrigger.DictationOnly ? settings.DictationOnlyHotkey : settings.Hotkey;
    }

    /// <summary>
    /// Whether a capture this trigger started ends by itself when the speaker goes quiet: auto-stop is on, and the binding
    /// that fired is a toggle. A hold ends when the key is released, so a pause while the key is held must never end it;
    /// everything said after the pause would be lost. A binding that is no longer configured counts as a hold: the duration
    /// ceiling still ends a forgotten toggle, while a wrong auto-stop loses speech.
    /// </summary>
    public static bool StopsOnSilence(AppSettings settings, HotkeyTrigger trigger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.AutoStopOnSilence && For(settings, trigger)?.Mode == HotkeyMode.Toggle;
    }
}
