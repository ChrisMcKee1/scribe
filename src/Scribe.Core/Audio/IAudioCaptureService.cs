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

    /// <summary>Enumerates active input devices, flagging the system default.</summary>
    IReadOnlyList<AudioDevice> GetInputDevices();

    /// <summary>Begins capturing from the given device id, or the default device when null.</summary>
    void Start(string? deviceId = null);

    /// <summary>Stops capturing and returns the resampled 16 kHz mono capture.</summary>
    CapturedAudio Stop();

    /// <summary>Normalized input level (0..1 peak) for the current buffer while recording.</summary>
    event EventHandler<float>? LevelChanged;
}
