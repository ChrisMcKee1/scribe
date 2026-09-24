using System.Diagnostics;
using Scribe.Core.Audio;

namespace Scribe.Core.Lifecycle;

/// <summary>How <see cref="RecordingCapture.Open{TCapture}"/> ended.</summary>
public enum RecordingOpenOutcome
{
    /// <summary>The recording was still live: it now shows as recording and owns the capture.</summary>
    Live,

    /// <summary>
    /// A stop (a pause, a fault) admitted the recording to processing after its microphone opened, or while it was opening.
    /// The capture is left running for that processing, whose own stop receives every sample recorded until the stop.
    /// </summary>
    LeftToProcessing,

    /// <summary>
    /// The recording's stop had already reached the capture service before the open, so nothing was opened: the
    /// recording's processing has taken its (empty) capture already, and a microphone opened now would belong to nobody.
    /// </summary>
    NotOpened,

    /// <summary>Shutdown began before any stop admitted the recording, so nobody owned the capture; it was stopped again.</summary>
    Reclaimed,
}

/// <summary>The result of opening a recording's microphone.</summary>
/// <param name="Outcome">Who ended up with the capture.</param>
/// <param name="Presentation">With <see cref="RecordingOpenOutcome.Live"/>, the Recording change to raise; null otherwise.</param>
/// <param name="OpenDuration">How long the device took to open, for the slow-open warning.</param>
/// <param name="Discarded">With <see cref="RecordingOpenOutcome.Reclaimed"/>, how much audio the stop threw away.</param>
/// <param name="ReclaimFailure">With <see cref="RecordingOpenOutcome.Reclaimed"/>, what the stop threw, if it threw.</param>
/// <param name="RequestedDeviceUnavailable">
/// True when this open was asked for a chosen microphone that could not be opened, so it recorded from the Windows
/// default instead. Read from this open's own start, so never true for <see cref="RecordingOpenOutcome.NotOpened"/>.
/// </param>
/// <param name="DeviceName">The microphone this open recorded from; null for <see cref="RecordingOpenOutcome.NotOpened"/>.</param>
public readonly record struct RecordingOpen(
    RecordingOpenOutcome Outcome,
    DictationPresentation? Presentation,
    TimeSpan OpenDuration,
    TimeSpan Discarded,
    Exception? ReclaimFailure,
    bool RequestedDeviceUnavailable = false,
    string? DeviceName = null);

/// <summary>
/// Opens the microphone for a recording the lifecycle has started, so that every capture has exactly one owner and every
/// sample reaches exactly one place. The activation path is easily preempted between starting a recording and opening its
/// device, and opening can take seconds, so a stop (a pause, a microphone fault) can land before, during or just after the
/// open. Each case has one right owner: an open that comes after the recording's stop reached the capture service opens
/// nothing; a capture a stop admitted to processing belongs to that processing, which receives all of its samples; and
/// only a capture nobody can own, because shutdown began first, is stopped here.
/// </summary>
public static class RecordingCapture
{
    /// <summary>
    /// Opens the capture for recording <paramref name="dictationId"/>, as its owner, then hands it over (see
    /// <see cref="HandOff{TCapture}"/>). Throws whatever the capture service's Start throws, in which case nothing was
    /// opened.
    /// </summary>
    public static RecordingOpen Open<TCapture>(
        DictationLifecycle<TCapture> lifecycle,
        IAudioCaptureService audio,
        long dictationId,
        string? deviceId)
        where TCapture : class
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(audio);

        var started = Stopwatch.GetTimestamp();
        var opened = audio.Start(deviceId, dictationId);
        var openDuration = Stopwatch.GetElapsedTime(started);
        if (!opened)
        {
            // Nothing opened, so the capture service's last-start values belong to an earlier recording.
            return new RecordingOpen(RecordingOpenOutcome.NotOpened, null, openDuration, TimeSpan.Zero, null);
        }

        // Taken from the start that just opened this recording's microphone, before the hand-off can stop it. Only the
        // activation thread starts captures, so no later start can have replaced them yet.
        var requestedDeviceUnavailable = audio.LastRequestedDeviceUnavailable;
        var deviceName = audio.LastDeviceName;
        return HandOff(lifecycle, audio, dictationId, openDuration) with
        {
            RequestedDeviceUnavailable = requestedDeviceUnavailable,
            DeviceName = deviceName,
        };
    }

    /// <summary>
    /// The second half of <see cref="Open{TCapture}"/>: hands a capture the capture service has just opened for recording
    /// <paramref name="dictationId"/> to its owner, as <see cref="DictationLifecycle{TCapture}.HandOffOpenedCapture"/>
    /// decides. Only a capture that nobody owns is stopped, on the calling thread, before this returns, so no later recording
    /// can start while it is still running.
    /// </summary>
    public static RecordingOpen HandOff<TCapture>(
        DictationLifecycle<TCapture> lifecycle,
        IAudioCaptureService audio,
        long dictationId,
        TimeSpan openDuration)
        where TCapture : class
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(audio);

        var handOff = lifecycle.HandOffOpenedCapture(dictationId);
        switch (handOff.Owner)
        {
            case OpenedCaptureOwner.Recording:
                return new RecordingOpen(RecordingOpenOutcome.Live, handOff.Presentation, openDuration, TimeSpan.Zero, null);

            case OpenedCaptureOwner.Processing:
                return new RecordingOpen(RecordingOpenOutcome.LeftToProcessing, null, openDuration, TimeSpan.Zero, null);
        }

        try
        {
            var discarded = audio.Stop(dictationId);
            return new RecordingOpen(RecordingOpenOutcome.Reclaimed, null, openDuration, discarded.Duration, null);
        }
        catch (Exception ex)
        {
            return new RecordingOpen(RecordingOpenOutcome.Reclaimed, null, openDuration, TimeSpan.Zero, ex);
        }
    }
}
