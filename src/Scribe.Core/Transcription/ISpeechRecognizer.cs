namespace Scribe.Core.Transcription;

/// <summary>
/// A loaded offline recognizer as <see cref="TranscriptionService"/> drives it. The production implementation wraps
/// sherpa-onnx; the seam exists so the service's locking, lifetime and cancellation can be tested deterministically
/// without loading the native engine.
/// </summary>
/// <remarks>
/// Implementations are not thread-safe. The service touches one only while holding its gate, so a decode, an unload
/// and a dispose can never overlap.
/// </remarks>
internal interface ISpeechRecognizer : IDisposable
{
    /// <summary>Catalog id of the model the recognizer was loaded from.</summary>
    string ModelId { get; }

    /// <summary>
    /// Decodes one buffer to trimmed text. Blocks until the native decode returns; it cannot be interrupted.
    /// </summary>
    string Decode(float[] samples, int sampleRate);
}
