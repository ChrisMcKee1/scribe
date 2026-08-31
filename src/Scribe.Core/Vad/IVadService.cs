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

    /// <summary>
    /// Total voiced audio the last <see cref="Trim"/> call detected, summed across every speech
    /// segment, or null when the last call could not measure it (model unavailable, wrong sample
    /// rate, or no speech found).
    ///
    /// This is deliberately not the duration of what <see cref="Trim"/> returns. Trim returns the
    /// whole span from the first speech to the last, so a short utterance followed by ten seconds
    /// of thinking is a ten second result containing one second of voice. Anything reasoning about
    /// how much someone actually said has to use this instead.
    /// </summary>
    double? LastSpeechSeconds { get; }

    /// <summary>Loads the model if present. Idempotent; safe to call repeatedly.</summary>
    void Initialize();

    /// <summary>
    /// Returns the speech span of <paramref name="audio"/> (leading/trailing silence removed),
    /// <see cref="CapturedAudio.Empty"/> when no speech is detected, or the input unchanged when
    /// the model is unavailable or the audio is not 16 kHz.
    /// </summary>
    CapturedAudio Trim(CapturedAudio audio);

    /// <summary>
    /// Releases the loaded VAD model. The service stays usable: the next <see cref="Trim"/> or
    /// <see cref="Initialize"/> reloads it on demand. No-op when nothing is loaded.
    /// </summary>
    void Unload();
}
