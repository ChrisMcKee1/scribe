namespace Scribe.Core.Transcription;

/// <summary>Tuning knobs for the offline recognizer.</summary>
public sealed class TranscriptionOptions
{
    /// <summary>
    /// Decode threads for sherpa-onnx. 0 means auto: roughly half the logical processors,
    /// capped at 8, which keeps decode fast while leaving headroom for the UI and capture.
    /// </summary>
    public int NumThreads { get; set; }
}
