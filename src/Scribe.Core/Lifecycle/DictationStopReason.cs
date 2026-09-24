namespace Scribe.Core.Lifecycle;

/// <summary>Why a recording ended. Written to the log on every stop, without exception.</summary>
public enum DictationStopReason
{
    /// <summary>The user released the hold key, or pressed the toggle key a second time.</summary>
    HotkeyReleased,

    /// <summary>Toggle mode with auto-stop enabled decided the speaker had gone quiet.</summary>
    SilenceAutoStop,

    /// <summary>The capture stream faulted: device removed, format change, driver reset.</summary>
    MicrophoneFault,

    /// <summary>Dictation was paused from the tray while a recording was live.</summary>
    Paused,

    /// <summary>The recording reached the MaxDictationMinutes ceiling and was ended cleanly.</summary>
    DurationLimit,

    /// <summary>
    /// The input desktop switched (the lock screen, a secure desktop) while the key was held or the toggle was on. The
    /// hook cannot see the key's release there, so it ended the recording as the binding would have.
    /// </summary>
    DesktopSwitch,
}

/// <summary>What a stop means for the hotkey hook, decided here so a test can reach it.</summary>
public static class DictationStopPolicy
{
    /// <summary>
    /// Whether this stop must release the hotkey's toggle latch (<c>IHotkeyService.CancelToggle</c>) as it is made. A
    /// stop Scribe makes itself (the silence auto-stop, a microphone fault, a pause, the duration ceiling) leaves the
    /// hook believing its toggle is still on, so the next press would be swallowed as the toggle-off of a dictation that
    /// has already ended. The two stops the hook sends itself release nothing: a release or second press has already
    /// ended its latch, and a desktop switch reset every latch before sending its stop, so releasing again could only
    /// cancel a fresh press the hook took after sending it. A reason added later releases the toggle unless it is one of
    /// those two, which is the safe default for a stop the hook did not send.
    /// </summary>
    public static bool ReleasesHotkeyToggle(DictationStopReason reason) =>
        reason is not (DictationStopReason.HotkeyReleased or DictationStopReason.DesktopSwitch);
}
