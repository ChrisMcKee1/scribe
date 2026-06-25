using Scribe.Core.Models;

namespace Scribe.Core.Vad;

/// <summary>
/// Trims leading/trailing silence from a capture and rejects captures that contain no speech,
/// using the Silero VAD model. Degrades to a pass-through when the model is unavailable.
/// </summary>
public interface IVadService : IDisposable
{
    /// <summary>True once the VAD model has been located and loaded.</summary>
    bool IsAvailable { get; }

    /// <summary>Loads the model if present. Idempotent; safe to call repeatedly.</summary>
    void Initialize();

    /// <summary>
    /// Returns the speech span of <paramref name="audio"/> (leading/trailing silence removed),
    /// <see cref="CapturedAudio.Empty"/> when no speech is detected, or the input unchanged when
    /// the model is unavailable or the audio is not 16 kHz.
    /// </summary>
    CapturedAudio Trim(CapturedAudio audio);
}
