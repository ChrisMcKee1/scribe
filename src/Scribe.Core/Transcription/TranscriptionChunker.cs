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
///
/// Where the seams go matters as much as how many there are: a seam inside speech splits a word,
/// and the recognizer drops or garbles it on both sides. Seams are therefore placed on the
/// quietest audio near the even cuts, and all of them are placed together rather than one at a
/// time, because chunk lengths couple neighboring seams: moving one seam earlier lengthens the
/// next chunk, so a seam chosen on its own can push a later seam off the only lull it could reach.
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
    /// Shortest chunk the planner may produce, so no chunk is ever a fragment too short to carry
    /// its own sentence context. The even split and the search radius already keep every chunk
    /// above about 6.7 s, so this is an invariant rather than a limit that shapes real plans.
    /// </summary>
    internal const int MinChunkSeconds = 5;

    /// <summary>
    /// Window over which boundary loudness is scored. 100 ms spans a few phonemes, so a minimum
    /// here is a genuine lull rather than the closure of a single plosive.
    /// </summary>
    private const double EnergyWindowSeconds = 0.1;

    /// <summary>
    /// Splits <paramref name="samples"/> into contiguous, non-overlapping spans of between
    /// <see cref="MinChunkSeconds"/> and <see cref="MaxChunkSeconds"/>, each seam within
    /// <see cref="BoundarySearchSeconds"/> of its even cut and on the quietest audio the other
    /// seams allow. Returns one span covering everything when no split is needed.
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

        var searchRadius = BoundarySearchSeconds * sampleRate;
        var chunkCount = ChunkCount(samples.Length, maxChunk, searchRadius);
        var seams = PlaceSeams(
            samples,
            chunkCount,
            maxChunk,
            MinChunkSeconds * sampleRate,
            searchRadius,
            Math.Max(1, (int)(EnergyWindowSeconds * sampleRate)));

        var spans = new List<(int Start, int Length)>(chunkCount);
        var start = 0;
        foreach (var seam in seams)
        {
            spans.Add((start, seam - start));
            start = seam;
        }

        spans.Add((start, samples.Length - start));
        return spans;
    }

    /// <summary>How many chunks a capture of <paramref name="sampleCount"/> samples is split into.</summary>
    /// <remarks>
    /// <para>
    /// Even-sized chunks rather than "max, max, ..., remainder": a 65 s capture becomes 3 x ~21.7 s
    /// instead of 30 + 30 + 5, so no chunk is a fragment too short to carry its own context. The
    /// starting point is the fewest chunks that fit under the cap.
    /// </para>
    /// <para>
    /// That count, though, leaves the seams only its slack to move in. With N chunks of at most
    /// <c>max</c> each, seam i can sit only in [i x max - slack, i x max], where slack is
    /// N x max minus the capture length. A capture of exactly N x 30 s therefore pins every seam to
    /// a 30 s multiple whatever is being said there, which is how long dictations lost words at
    /// every seam. When the slack cannot cover a seam's whole search window (twice the radius), one
    /// more chunk is planned, which hands the seams a full chunk of extra slack.
    /// </para>
    /// </remarks>
    internal static int ChunkCount(int sampleCount, int maxChunk, int searchRadius)
    {
        var count = (int)Math.Ceiling(sampleCount / (double)maxChunk);
        var slack = (long)count * maxChunk - sampleCount;
        return slack < 2L * searchRadius ? count + 1 : count;
    }

    /// <summary>
    /// Chooses every seam at once: each within <paramref name="searchRadius"/> of its even cut,
    /// every chunk between <paramref name="minChunk"/> and <paramref name="maxChunk"/>, and among
    /// all such plans the one whose seams are quietest in total. Ties (uniform silence, uniform
    /// tone) go to the plan closest to the even cuts, so featureless audio still splits evenly.
    /// </summary>
    /// <remarks>
    /// A dynamic program over a half-window candidate grid centered on each even cut: about 200
    /// candidates per seam, each scored once, each linked to the best compatible candidate for the
    /// seam before it. The even cuts are themselves candidates and always form a valid plan (every
    /// even chunk is at least a sample under the cap, because the chunk count keeps some slack), so
    /// a plan is always found.
    /// </remarks>
    private static int[] PlaceSeams(
        float[] samples, int chunkCount, int maxChunk, int minChunk, int searchRadius, int window)
    {
        var length = samples.Length;
        var seamCount = chunkCount - 1;
        var hop = Math.Max(1, window / 2);
        var evenLength = length / (double)chunkCount;

        var candidates = new int[seamCount][];
        var energies = new double[seamCount][];
        var evenCuts = new int[seamCount];
        for (var s = 0; s < seamCount; s++)
        {
            var seam = s + 1;
            var even = (int)Math.Round(seam * evenLength);
            evenCuts[s] = even;

            // Positions no complete plan could use are dropped up front: the chunks before this seam and the chunks
            // after it must each fit between the minimum and the cap.
            var lo = Math.Max(
                (long)even - searchRadius,
                Math.Max((long)seam * minChunk, length - (long)(chunkCount - seam) * maxChunk));
            var hi = Math.Min(
                (long)even + searchRadius,
                Math.Min((long)seam * maxChunk, length - (long)(chunkCount - seam) * minChunk));

            var first = (long)Math.Ceiling((lo - even) / (double)hop);
            var last = (long)Math.Floor((hi - even) / (double)hop);
            var grid = new List<int>();
            for (var k = first; k <= last; k++)
            {
                grid.Add((int)(even + k * hop));
            }

            if (grid.Count == 0)
            {
                // Unreachable while the even cut lies inside [lo, hi], which the chunk count guarantees. Kept so a future
                // change to the constants degrades to an even split instead of an exception mid-dictation.
                grid.Add((int)Math.Clamp(even, lo, Math.Max(lo, hi)));
            }

            candidates[s] = [.. grid];
            energies[s] = [.. grid.Select(position => WindowEnergy(samples, position, window))];
        }

        // Best plan for seams 0..s ending at candidate a of seam s, scored as (total energy, total distance from the
        // even cuts) and compared in that order.
        var totalEnergy = new double[seamCount][];
        var totalDistance = new long[seamCount][];
        var previous = new int[seamCount][];
        for (var s = 0; s < seamCount; s++)
        {
            var count = candidates[s].Length;
            totalEnergy[s] = new double[count];
            totalDistance[s] = new long[count];
            previous[s] = new int[count];

            for (var a = 0; a < count; a++)
            {
                var position = candidates[s][a];
                var distance = Math.Abs((long)position - evenCuts[s]);
                if (s == 0)
                {
                    // The first chunk's bounds are already enforced by the seam's own range.
                    totalEnergy[s][a] = energies[s][a];
                    totalDistance[s][a] = distance;
                    previous[s][a] = -1;
                    continue;
                }

                var bestEnergy = double.PositiveInfinity;
                var bestDistance = long.MaxValue;
                var bestFrom = -1;
                for (var b = 0; b < candidates[s - 1].Length; b++)
                {
                    var chunk = position - candidates[s - 1][b];
                    if (chunk < minChunk || chunk > maxChunk || double.IsPositiveInfinity(totalEnergy[s - 1][b]))
                    {
                        continue;
                    }

                    var energy = totalEnergy[s - 1][b];
                    var total = totalDistance[s - 1][b];
                    if (energy < bestEnergy || (energy == bestEnergy && total < bestDistance))
                    {
                        bestEnergy = energy;
                        bestDistance = total;
                        bestFrom = b;
                    }
                }

                totalEnergy[s][a] = bestFrom < 0 ? double.PositiveInfinity : bestEnergy + energies[s][a];
                totalDistance[s][a] = bestFrom < 0 ? long.MaxValue : bestDistance + distance;
                previous[s][a] = bestFrom;
            }
        }

        // The last chunk's bounds are already enforced by the last seam's own range.
        var end = seamCount - 1;
        var winner = -1;
        for (var a = 0; a < candidates[end].Length; a++)
        {
            if (double.IsPositiveInfinity(totalEnergy[end][a]))
            {
                continue;
            }

            if (winner < 0
                || totalEnergy[end][a] < totalEnergy[end][winner]
                || (totalEnergy[end][a] == totalEnergy[end][winner]
                    && totalDistance[end][a] < totalDistance[end][winner]))
            {
                winner = a;
            }
        }

        if (winner < 0)
        {
            // Unreachable for the same reason as the empty grid above: the even cuts form a valid plan.
            return evenCuts;
        }

        var seams = new int[seamCount];
        for (var s = end; s >= 0; s--)
        {
            seams[s] = candidates[s][winner];
            winner = previous[s][winner];
        }

        return seams;
    }

    /// <summary>
    /// Sum of squared amplitudes over the <paramref name="window"/> samples centered on
    /// <paramref name="position"/>, so a seam cuts mid-lull rather than at its onset. Every window
    /// is the same size, so sums compare like means. Summed directly rather than from running
    /// totals, so identical audio scores exactly equal and the even-cut tie-break still applies to
    /// uniform input.
    /// </summary>
    private static double WindowEnergy(float[] samples, int position, int window)
    {
        var start = Math.Clamp(position - window / 2, 0, Math.Max(0, samples.Length - window));
        var end = Math.Min(samples.Length, start + window);
        double energy = 0;
        for (var i = start; i < end; i++)
        {
            double sample = samples[i];
            energy += sample * sample;
        }

        return energy;
    }
}
