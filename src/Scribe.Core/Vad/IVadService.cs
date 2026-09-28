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

    /// <summary>Loads the model if present. Idempotent; safe to call repeatedly. Throws once disposed.</summary>
    void Initialize();

    /// <summary>
    /// Returns the speech span of <paramref name="audio"/> (leading/trailing silence removed),
    /// <see cref="CapturedAudio.Empty"/> when no speech is detected, or the input unchanged when
    /// the model is unavailable or the audio is not 16 kHz. Loads the model first if needed, in the same
    /// step as the trim, so a concurrent <see cref="Unload"/> lands before (and this call reloads) or after,
    /// never in between. Throws <see cref="ObjectDisposedException"/> once disposed.
    /// </summary>
    CapturedAudio Trim(CapturedAudio audio);

    /// <summary>
    /// <see cref="Trim(CapturedAudio)"/> for a dictation that can be abandoned. With
    /// <c>PerfFlags.VadWindowCancellation</c> on, the trim checks <paramref name="cancellationToken"/> before it waits for
    /// the model, again before it loads it, and between windows, and a canceled trim throws
    /// <see cref="OperationCanceledException"/>: never a partial trim, and the detector is reset for the next one. With the
    /// flag off, and in an implementation that does not honour it, this is <see cref="Trim(CapturedAudio)"/>.
    /// </summary>
    CapturedAudio Trim(CapturedAudio audio, CancellationToken cancellationToken) => Trim(audio);

    /// <summary>
    /// Releases the loaded VAD model. The service stays usable: the next <see cref="Trim"/> or
    /// <see cref="Initialize"/> reloads it on demand. No-op when nothing is loaded.
    /// </summary>
    void Unload();
}
