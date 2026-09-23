using Scribe.Core.Models;

namespace Scribe.Core.Audio;

/// <summary>
/// Captures microphone audio via WASAPI and returns it normalized to 16 kHz mono float,
/// the format the VAD and recognizer consume. Raises <see cref="LevelChanged"/> while
/// recording so the overlay can render an input meter.
/// </summary>
public interface IAudioCaptureService : IDisposable
{
    /// <summary>True between <see cref="Start"/> and <see cref="Stop"/>.</summary>
    bool IsCapturing { get; }

    /// <summary>
    /// Friendly name of the device the most recent capture used (survives <see cref="Stop"/>), or
    /// null before the first capture. Lets error messages name the microphone that produced nothing.
    /// </summary>
    string? LastDeviceName { get; }

    /// <summary>
    /// True when the most recent capture started on an endpoint that was muted (or had its volume
    /// at zero) at the system level, e.g. a headset hardware mute or the Windows 11 taskbar mic
    /// mute while in a meeting. WASAPI still records in that state, it just records silence, so
    /// callers should warn the user instead of waiting for an empty transcription.
    /// </summary>
    bool LastDeviceMuted { get; }

    /// <summary>
    /// True when the most recent capture never rose above the digital-silence threshold, meaning
    /// the endpoint delivered audio buffers but they contained no signal (muted mic, disconnected
    /// boom, driver-level mute). Valid after <see cref="Stop"/>; survives until the next Start.
    /// </summary>
    bool LastCaptureWasSilent { get; }

    /// <summary>
    /// Measured shape of the most recent completed capture: levels in dBFS, clipping, DC offset,
    /// and per-channel contributions taken before the downmix. Null until a capture completes.
    /// <para>
    /// <see cref="LastCaptureWasSilent"/> only answers "were these literally zeros", which is a
    /// -60 dBFS bar and tells you almost nothing about why a decode failed. This is what separates
    /// a working microphone from one running 40 dB too quiet, clipping, or averaging a live channel
    /// against a dead one. Statistics only; no audio and no content.
    /// </para>
    /// </summary>
    CaptureSignalReport? LastSignalReport { get; }

    /// <summary>Enumerates active input devices, flagging the system default.</summary>
    IReadOnlyList<AudioDevice> GetInputDevices();

    /// <summary>
    /// Begins capturing from the given device id, or the default device when null, and returns true when a capture was
    /// opened. <paramref name="owner"/> names the recording the capture is for: a positive number, higher for every later
    /// recording (the dictation id). When the stop for that recording, or for a later one, already reached this service,
    /// nothing is opened and false is returned: a recording stopped before its microphone opened must never be handed a
    /// microphone that nothing would stop. Also false when a capture is already running. Zero means no owner.
    /// </summary>
    bool Start(string? deviceId = null, long owner = 0);

    /// <summary>
    /// Requests that the capture endpoint stop immediately without waiting or resampling. With an
    /// <paramref name="owner"/>, records that this recording's stop has arrived (see <see cref="Start"/>) and touches only
    /// that recording's capture; zero stops whatever is capturing.
    /// </summary>
    void RequestStop(long owner = 0);

    /// <summary>
    /// Stops capturing and returns the resampled 16 kHz mono capture. With an <paramref name="owner"/>, records that this
    /// recording's stop has arrived (see <see cref="Start"/>) and returns only that recording's capture, empty when it has
    /// none; zero stops whatever is capturing. However many callers stop the same capture, exactly one receives it.
    /// </summary>
    CapturedAudio Stop(long owner = 0);

    /// <summary>
    /// Drops any capture working buffer kept between captures so an idle-time release can hand the
    /// memory back to the garbage collector, and returns the bytes released. A retained buffer is
    /// always zeroed; the next capture simply reserves a fresh one. Safe to call from any thread at
    /// any time, including while a capture is starting, recording or being converted: a buffer a
    /// capture is using is never retained, so it is never released or cleared by this call.
    /// Implementations that retain nothing return 0.
    /// </summary>
    long ReleaseRetainedBuffers() => 0;

    /// <summary>Normalized input level (0..1 peak) for the current buffer while recording.</summary>
    event EventHandler<float>? LevelChanged;

    /// <summary>Raised when the active audio endpoint stops because of a device or driver failure.</summary>
    event EventHandler<Exception>? CaptureFaulted;
}
