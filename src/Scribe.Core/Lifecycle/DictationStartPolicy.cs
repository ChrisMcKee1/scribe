namespace Scribe.Core.Lifecycle;

/// <summary>What starting a recording means for the hotkey hook, decided here so a test can reach it.</summary>
public static class DictationStartPolicy
{
    /// <summary>
    /// Starts a recording for a hotkey press (<see cref="DictationLifecycle{TCapture}.TryBeginRecording"/>), and when the
    /// lifecycle turns the press away because the previous dictation is still processing, releases that press's hotkey
    /// latch (<paramref name="releaseHotkey"/>: the controller's <c>IHotkeyService.CancelToggle</c> with the press's
    /// activation, which releases that press and never another). The press started nothing, and a toggle whose latch
    /// stayed on took its next tap as the toggle-off of a dictation that never began, so after a stop Scribe made itself
    /// (whose release had just turned the tap into a new start) only a third tap started a dictation.
    /// </summary>
    /// <remarks>
    /// The other refusals keep the latch. Paused: the hook's own pause clears every latch. Closing: the hook is about to
    /// stop. Already recording: no press but this one owns the hook's dictation then, so its release or second tap is what
    /// can still end the live recording. An exception from <paramref name="releaseHotkey"/> propagates to the caller:
    /// nothing was started or admitted, so nothing is left waiting on it.
    /// </remarks>
    /// <param name="lifecycle">The dictation loop.</param>
    /// <param name="captureFactory">Passed to <see cref="DictationLifecycle{TCapture}.TryBeginRecording"/>.</param>
    /// <param name="releaseHotkey">Releases the press, after the lifecycle's gate is released.</param>
    public static DictationActivation<TCapture> BeginRecording<TCapture>(
        DictationLifecycle<TCapture> lifecycle,
        Func<TCapture> captureFactory,
        Action releaseHotkey)
        where TCapture : class
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(releaseHotkey);

        var activation = lifecycle.TryBeginRecording(captureFactory);
        if (activation.Decision == ActivationDecision.StillProcessing)
        {
            releaseHotkey();
        }

        return activation;
    }
}
