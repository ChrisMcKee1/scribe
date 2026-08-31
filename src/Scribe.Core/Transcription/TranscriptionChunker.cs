namespace Scribe.Core.Transcription;

/// <summary>
/// Plans how a long capture is split into bounded chunks before decoding.
///
/// Why chunk at all: the recognizer's encoder scratch memory grows superlinearly with sequence
/// length, and ONNX Runtime's arena allocator never returns that memory to the OS (its docs:
/// "the memory allocated by the arena is never returned to the system"). Measured on the bundled
/// Parakeet model, one 180 s decode permanently pins ~4 GB of private bytes; capped 30 s decodes
/// hold the process near 1 GB total regardless of capture length. Splitting long audio into
/// speech-bounded pieces is also the pattern sherpa-onnx's own long-audio examples use.
///
/// Captures at or under <see cref="MaxChunkSeconds"/> are returned as a single span, so ordinary
/// dictations keep the exact whole-buffer decode the model leaderboard was validated against.
/// </summary>
internal static class TranscriptionChunker
{
    /// <summary>
    /// Upper bound per decoded chunk. 30 s holds the arena's high-water mark near 350 MB above
    /// the loaded model (measured), while staying far above the length of a typical utterance so
    /// the split path stays rare.
    /// </summary>
    internal const int MaxChunkSeconds = 30;

    /// <summary>
    /// How far each side of an ideal boundary the planner may move a split to land on quiet
    /// audio. Wide enough to find a breath or pause in normal speech; narrow enough that chunk
    /// sizes stay near the target.
    /// </summary>
    internal const int BoundarySearchSeconds = 5;

    /// <summary>
    /// Window over which boundary loudness is scored. 100 ms spans a few phonemes, so a minimum
    /// here is a genuine lull rather than the closure of a single plosive.
    /// </summary>
    private const double EnergyWindowSeconds = 0.1;

    /// <summary>
    /// Splits <paramref name="sampleCount"/> samples into contiguous, non-overlapping spans of at
    /// most <see cref="MaxChunkSeconds"/>, choosing each boundary at the quietest point of
    /// <paramref name="samples"/> near the ideal cut. Returns one span covering everything when no
    /// split is needed.
    /// </summary>
    internal static IReadOnlyList<(int Start, int Length)> Plan(
        float[] samples, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        var maxChunk = MaxChunkSeconds * sampleRate;
        if (samples.Length <= maxChunk)
        {
            return [(0, samples.Length)];
        }

        // Even-sized ideal chunks rather than "max, max, …, remainder": a 65 s capture becomes
        // 3 × ~21.7 s instead of 30 + 30 + 5, so no chunk is ever a fragment too short to carry
        // its own sentence context.
        var chunkCount = (int)Math.Ceiling(samples.Length / (double)maxChunk);
        var idealLength = samples.Length / (double)chunkCount;
        var searchRadius = BoundarySearchSeconds * sampleRate;
        var energyWindow = Math.Max(1, (int)(EnergyWindowSeconds * sampleRate));

        var spans = new List<(int Start, int Length)>(chunkCount);
        var start = 0;
        for (var i = 1; i < chunkCount; i++)
        {
            var ideal = (int)Math.Round(i * idealLength);
            var boundary = FindQuietestPoint(samples, ideal, searchRadius, energyWindow);

            // Two invariants the lull search must never break: this chunk stays at or under the
            // cap (upper clamp), and enough audio remains behind the boundary for every later
            // chunk to also fit under the cap (lower clamp). Without the lower clamp, boundaries
            // that drift early leave the final chunk oversized.
            var minBoundary = Math.Max(start + 1, samples.Length - (chunkCount - i) * maxChunk);
            var maxBoundary = Math.Min(start + maxChunk, samples.Length - 1);
            boundary = Math.Clamp(boundary, minBoundary, maxBoundary);

            spans.Add((start, boundary - start));
            start = boundary;
        }

        spans.Add((start, samples.Length - start));
        return spans;
    }

    /// <summary>
    /// Returns the start of the quietest <paramref name="window"/>-sample stretch within
    /// <paramref name="radius"/> samples of <paramref name="ideal"/>, scored by mean squared
    /// amplitude. Scanning with a half-window hop keeps this O(radius) with good-enough
    /// resolution; an exact per-sample scan buys nothing a pause detector cares about.
    /// </summary>
    private static int FindQuietestPoint(float[] samples, int ideal, int radius, int window)
    {
        var lo = Math.Max(0, ideal - radius);
        var hi = Math.Min(samples.Length - window, ideal + radius);
        if (hi <= lo)
        {
            return ideal;
        }

        var best = ideal;
        var bestEnergy = double.MaxValue;
        var hop = Math.Max(1, window / 2);

        for (var pos = lo; pos <= hi; pos += hop)
        {
            double energy = 0;
            for (var j = 0; j < window; j++)
            {
                double s = samples[pos + j];
                energy += s * s;
            }

            var candidate = pos + window / 2; // cut mid-lull, not at its onset
            var improves = energy < bestEnergy
                // On equal energy (uniform silence, uniform tone) stay closest to the ideal cut,
                // so boundaries don't all drift toward the scan's starting edge.
                || (energy == bestEnergy && Math.Abs(candidate - ideal) < Math.Abs(best - ideal));
            if (improves)
            {
                bestEnergy = energy;
                best = candidate;
            }
        }

        return best;
    }
}
