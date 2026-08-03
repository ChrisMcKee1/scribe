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
    /// sherpa-onnx decoding method. Only <c>"greedy_search"</c> is reachable from the app; see
    /// <see cref="TranscriptionDecoding"/> for why <c>"modified_beam_search"</c> is refused.
    /// </summary>
    public string DecodingMethod { get; set; } = TranscriptionDecoding.Greedy;

    /// <summary>Beam width for <c>modified_beam_search</c>; ignored by greedy decoding.</summary>
    public int MaxActivePaths { get; set; } = 4;

    /// <summary>
    /// Lets a diagnostic tool select a decoding method that <see cref="TranscriptionDecoding"/>
    /// otherwise refuses, so a decoder regression can be reproduced against real audio. The app
    /// never sets this.
    /// </summary>
    public bool AllowUnsafeDecodingMethod { get; set; }
}
