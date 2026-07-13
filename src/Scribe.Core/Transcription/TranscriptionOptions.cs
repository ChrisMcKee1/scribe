namespace Scribe.Core.Transcription;

/// <summary>Tuning knobs for the offline recognizer.</summary>
public sealed class TranscriptionOptions
{
    public string ModelId { get; set; } = TranscriptionModelCatalog.DefaultId;

    /// <summary>
    /// Decode threads for sherpa-onnx. 0 means auto: roughly half the logical processors,
    /// capped at 8, which keeps decode fast while leaving headroom for the UI and capture.
    /// </summary>
    public int NumThreads { get; set; }

    /// <summary>
    /// sherpa-onnx decoding method: <c>"greedy_search"</c> (default, fastest) or
    /// <c>"modified_beam_search"</c> (explores <see cref="MaxActivePaths"/> hypotheses for slightly
    /// higher accuracy at a latency cost). Anything else falls back to greedy inside the engine.
    /// </summary>
    public string DecodingMethod { get; set; } = "greedy_search";

    /// <summary>Beam width for <c>modified_beam_search</c>; ignored by greedy decoding.</summary>
    public int MaxActivePaths { get; set; } = 4;
}
