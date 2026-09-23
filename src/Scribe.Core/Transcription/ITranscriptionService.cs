using Scribe.Core.Models;

namespace Scribe.Core.Transcription;

/// <summary>
/// Owns the warm-loaded sherpa-onnx offline recognizer (NVIDIA Parakeet TDT 0.6b v3, int8)
/// and decodes captured audio to text entirely on-device.
/// </summary>
public interface ITranscriptionService : IDisposable
{
    /// <summary>True once the recognizer has been loaded into memory.</summary>
    bool IsReady { get; }

    /// <summary>
    /// Loads the model into memory. Safe to call more than once; subsequent calls are no-ops.
    /// Throws <see cref="FileNotFoundException"/> if the model files cannot be located.
    /// </summary>
    void Initialize();

    /// <summary>Decodes a capture to text. Empty input yields <see cref="TranscriptionResult.Empty"/>.</summary>
    TranscriptionResult Transcribe(CapturedAudio audio);

    /// <summary>
    /// Decodes a capture to text, loading the model first if it is not resident. Loading and decoding happen as one
    /// step, so a concurrent <see cref="Unload"/> lands before (and this call reloads) or after, never in between.
    /// <see cref="TranscriptionResult.DecodeDuration"/> covers decoding only; a cold load is logged separately.
    /// </summary>
    /// <remarks>
    /// Cancellation is cooperative. It is observed before the model loads, once the engine has been acquired, and
    /// between the chunks of a long capture, and it throws <see cref="OperationCanceledException"/> rather than returning
    /// the chunks decoded so far, so a partial transcript is never reported as complete. A native decode already in
    /// progress runs to completion, because the engine offers no way to interrupt it.
    /// </remarks>
    TranscriptionResult Transcribe(CapturedAudio audio, CancellationToken cancellationToken);

    /// <summary>
    /// Releases the loaded recognizer and every byte ONNX Runtime's arena is holding, which is the
    /// only way that memory returns to the OS, since the arena never shrinks on its own. The service
    /// stays usable: the next <see cref="Initialize"/> or <see cref="Transcribe(CapturedAudio)"/> reloads the
    /// model on demand. No-op when nothing is loaded.
    /// </summary>
    void Unload();
}
