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

    /// <summary>
    /// Windows had removed the mouse hook while a mouse button binding was held or toggled on, and the hook's renewal
    /// found it gone. The button's release may have happened while no hook saw the mouse, so the hook ended the recording.
    /// </summary>
    MouseHookLost,
}

/// <summary>What a stop means for the hotkey hook, decided here so a test can reach it.</summary>
public static class DictationStopPolicy
{
    /// <summary>
    /// Whether this stop, once admitted, must release the hotkey latch of the press that started the recording
    /// (<c>IHotkeyService.CancelToggle</c>, through <see cref="BeginStop"/>). A stop Scribe makes itself (the silence
    /// auto-stop, a microphone fault, a pause, the duration ceiling) leaves the hook believing that press is still held
    /// or its toggle still on, so the next press would be swallowed as the toggle-off of a dictation that has already
    /// ended. The stops the hook sends itself release nothing: a release or second press ended that press's latch, and
    /// a desktop switch or a mouse hook found removed cleared the latches it concerned, before the stop was sent, so
    /// nothing of it is left to release. A reason added later releases unless it is one of those, which is the safe
    /// default for a stop the hook did not send.
    /// </summary>
    public static bool ReleasesHotkeyToggle(DictationStopReason reason) =>
        reason is not (DictationStopReason.HotkeyReleased or DictationStopReason.DesktopSwitch
            or DictationStopReason.MouseHookLost);

    /// <summary>
    /// The first step of every stop, in the only safe order: the lifecycle admits the stop (only the one that ends the
    /// live recording is admitted), and only then, for a stop Scribe makes itself (<see cref="ReleasesHotkeyToggle"/>),
    /// the press that started the admitted recording is released. The controller's <paramref name="releaseHotkey"/> calls
    /// <c>IHotkeyService.CancelToggle</c> with the activation that recording kept, which releases that press and never a
    /// newer one. Released before the admission, or released whatever the hook held, the stop could clear the latch of a
    /// press the user made meanwhile, still queued for the consumer, and the recording that press then started would
    /// never hear its key's release. A stop that is not admitted releases nothing.
    /// </summary>
    /// <param name="lifecycle">The dictation loop.</param>
    /// <param name="reason">Why the recording is stopping.</param>
    /// <param name="expectedDictationId">Passed to <see cref="DictationLifecycle{TCapture}.TryBeginProcessing"/>.</param>
    /// <param name="releaseHotkey">Releases the press that started the admitted recording, given that recording's context.</param>
    /// <param name="releaseFailure">
    /// What <paramref name="releaseHotkey"/> threw, if anything. It is caught rather than thrown on: the admission is
    /// ended only by its processing, which has not started yet, so a throw here would leave the dictation processing for
    /// good.
    /// </param>
    public static StopDecision<TCapture> BeginStop<TCapture>(
        DictationLifecycle<TCapture> lifecycle,
        DictationStopReason reason,
        long expectedDictationId,
        Action<TCapture> releaseHotkey,
        out Exception? releaseFailure)
        where TCapture : class
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(releaseHotkey);

        releaseFailure = null;
        var stop = lifecycle.TryBeginProcessing(expectedDictationId);
        if (stop.Admission is { } admission && ReleasesHotkeyToggle(reason))
        {
            try
            {
                releaseHotkey(admission.Capture);
            }
            catch (Exception ex)
            {
                releaseFailure = ex;
            }
        }

        return stop;
    }
}
