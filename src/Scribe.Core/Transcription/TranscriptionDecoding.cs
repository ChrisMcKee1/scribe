namespace Scribe.Core.Transcription;

/// <summary>
/// Decides which sherpa-onnx decoding method a model may actually use.
///
/// Scribe used to expose a "higher-accuracy decoding" switch that selected
/// <c>modified_beam_search</c>. Measured against 80 real production captures on Parakeet TDT it was
/// not more accurate, it was lossy: 19 transcripts changed, and the changes included a whole
/// closing sentence disappearing, "MAU" vanishing from a list of three things, "Scribe" becoming
/// "Scrib", and a near-silent capture that greedy decoding correctly returned empty coming back as
/// the invented word "Yeah." That last mode is the one users report, because it is the only failure
/// in the pipeline that looks like success: a paragraph they just spoke is typed out as one word,
/// so they have to say the whole thing again.
///
/// The wins were cosmetic by comparison (an acronym spaced differently, a capital letter), so the
/// trade is a handful of formatting nits against silently dropped words. Synthetic fixtures never
/// caught it because clean text-to-speech decodes identically either way; only real, disfluent
/// microphone audio diverges.
///
/// Beam search stays reachable for diagnostics through
/// <see cref="TranscriptionOptions.AllowUnsafeDecodingMethod"/> so the regression can be
/// reproduced on demand, and it is never reachable from the app.
/// </summary>
public static class TranscriptionDecoding
{
    public const string Greedy = "greedy_search";
    public const string ModifiedBeamSearch = "modified_beam_search";

    /// <summary>
    /// True when beam search is known to be safe for <paramref name="architecture"/>. No shipped
    /// architecture qualifies today. Moonshine does not implement it at all, and the measurement
    /// above rules it out for NeMo TDT. Flipping an entry here needs a fresh comparison over real
    /// captures, not synthetic fixtures.
    /// </summary>
    public static bool IsBeamSearchSafe(TranscriptionModelArchitecture architecture) => false;

    /// <summary>
    /// Resolves the decoding method to hand sherpa-onnx. Anything unrecognized, and anything unsafe
    /// for the model being loaded, decodes greedily so a stale or hand-edited setting can never
    /// quietly cost the user words.
    /// </summary>
    public static DecodingSelection Resolve(
        string? configured,
        TranscriptionModelArchitecture architecture,
        bool allowUnsafe = false)
    {
        var wantsBeamSearch = string.Equals(configured, ModifiedBeamSearch, StringComparison.OrdinalIgnoreCase);
        if (!wantsBeamSearch) return new DecodingSelection(Greedy, false);

        if (allowUnsafe || IsBeamSearchSafe(architecture))
        {
            return new DecodingSelection(ModifiedBeamSearch, false);
        }

        return new DecodingSelection(Greedy, true);
    }
}

/// <param name="Method">The method to pass to sherpa-onnx.</param>
/// <param name="Overridden">
/// True when the caller asked for something the model cannot safely use, so the caller can say so
/// rather than silently ignoring the request.
/// </param>
public readonly record struct DecodingSelection(string Method, bool Overridden);
