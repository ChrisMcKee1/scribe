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
}
