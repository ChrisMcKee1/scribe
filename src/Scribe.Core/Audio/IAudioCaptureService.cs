using Scribe.Core.Models;

namespace Scribe.Core.Audio;

/// <summary>
/// Captures microphone audio via WASAPI and returns it normalized to 16 kHz mono float —
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

    /// <summary>Enumerates active input devices, flagging the system default.</summary>
    IReadOnlyList<AudioDevice> GetInputDevices();

    /// <summary>Begins capturing from the given device id, or the default device when null.</summary>
    void Start(string? deviceId = null);

    /// <summary>Requests that the capture endpoint stop immediately without waiting or resampling.</summary>
    void RequestStop();

    /// <summary>Stops capturing and returns the resampled 16 kHz mono capture.</summary>
    CapturedAudio Stop();

    /// <summary>Normalized input level (0..1 peak) for the current buffer while recording.</summary>
    event EventHandler<float>? LevelChanged;

    /// <summary>Raised when the active audio endpoint stops because of a device or driver failure.</summary>
    event EventHandler<Exception>? CaptureFaulted;
}
