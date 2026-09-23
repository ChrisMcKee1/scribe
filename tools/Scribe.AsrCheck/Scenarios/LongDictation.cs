namespace Scribe.AsrCheck.Scenarios;

internal sealed record SeamInfo(
    int Index,
    double AtSeconds,
    string Location,
    double LevelDbfs,
    bool InPause,
    double WindowStartSeconds,
    double WindowEndSeconds,
    int WindowWords,
    int LostAtSeam,
    int DoubledAtSeam,
    CheckStatus Verdict,
    int MergedOrSplitAtSeam,
    string ReferenceText,
    string ChunkText);

internal sealed record InstanceInfo(int Index, string Clip, double StartSeconds, double EndSeconds, int Tokens, int Matched);

/// <summary>What a long decode kept, dropped and repeated, and where its chunk seams fell.</summary>
internal sealed class SeamAnalysis
{
    public int Chunks { get; set; }

    public List<double> ChunkSeconds { get; set; } = [];

    public double DecodedSeconds { get; set; }

    public double TrimOffsetSeconds { get; set; }

    public int Phrases { get; set; }

    public int ReferenceTokens { get; set; }

    public int MatchedTokens { get; set; }

    public int DroppedTokens { get; set; }

    public int InsertedTokens { get; set; }

    public int DuplicatedTokens { get; set; }

    public double CoverageVsReference { get; set; }

    public double OrderedCoverageVsSource { get; set; }

    public double WordOverlapVsSource { get; set; }

    public List<SeamInfo> Seams { get; set; } = [];

    public List<InstanceInfo> LossyPhrases { get; set; } = [];
}

/// <summary>
/// Measures a long, chunked decode against the phrases it was built from, and judges every chunk seam.
/// <para>
/// Coverage compares the transcript with what the recogniser produced for each phrase in isolation
/// (its clean decode), so a word the synthetic voice fumbles everywhere is not charged to the chunking.
/// </para>
/// <para>
/// A seam is judged against the same audio decoded without it: the phrases either side of the cut are
/// decoded again as one piece (always under the chunk limit, so Transcribe does not split it), and the
/// seam is charged only with words the seamless decode has and the chunked transcript lacks, or words
/// the chunked transcript says twice. That keeps ordinary context-to-context variation in the
/// recogniser, which is real and large for invented words, from reading as seam damage. For the same
/// reason a word the two decodes only space differently ("gethubcopilot" against "gethub copilot") is
/// not charged: those words are compared as letters (see <see cref="Words.CreditMergedAndSplit"/>),
/// while a word that is missing, misheard or repeated still is.
/// </para>
/// <para>
/// A seam that lands on a lull is asserted: it must not lose or double words. A seam that has to cut
/// through speech, which the chunker is forced into when a capture sits just under a multiple of its
/// limit, is reported with the same measurements but not asserted, because that is a property of the
/// chunk plan rather than a regression in any one build.
/// </para>
/// </summary>
internal static class LongDictationAnalyzer
{
    // A 100 ms window this far under full scale is a pause: the synthetic voices' own gaps and the
    // scenario's room tone sit near -70 dBFS, while any voiced sound is above -45 dBFS.
    private const double PauseDbfs = -50;

    private const double LevelWindowSeconds = 0.1;

    // The seamless comparison decodes at most this much audio, so it can never itself be chunked.
    private const double MaxWindowSeconds = 20;

    public static SeamAnalysis Analyze(
        IReadOnlyList<Placement> placements,
        float[] captured,
        float[] decodeInput,
        string transcript,
        Func<string, IReadOnlyList<string>> referenceTokens,
        IReadOnlyList<(int Start, int Length)> chunks,
        Func<float[], string> decodeWhole,
        int sampleRate,
        bool assertSeams)
    {
        var offset = decodeInput.Length == captured.Length ? 0 : FindOffset(captured, decodeInput);
        var analysis = new SeamAnalysis
        {
            Chunks = chunks.Count,
            ChunkSeconds = chunks.Select(c => Math.Round(c.Length / (double)sampleRate, 2)).ToList(),
            DecodedSeconds = Math.Round(decodeInput.Length / (double)sampleRate, 2),
            TrimOffsetSeconds = offset < 0 ? -1 : Math.Round(offset / (double)sampleRate, 3),
            Phrases = placements.Count,
        };

        // Placements are in capture coordinates; seams and windows work in decode-input coordinates.
        var shift = Math.Max(0, offset);
        var local = placements.Select(p => p with { Start = p.Start - shift }).ToList();

        var expected = new List<(string Token, int Instance)>();
        for (var i = 0; i < local.Count; i++)
        {
            expected.AddRange(referenceTokens(local[i].Clip).Select(t => (t, i)));
        }

        var actual = Words.Tokenize(transcript);
        var actualSpans = Words.TokenSpans(transcript);
        var match = Words.Align(expected.Select(e => e.Token).ToList(), actual);
        var matchedActual = new bool[actual.Count];
        foreach (var index in match.Where(m => m >= 0))
        {
            matchedActual[index] = true;
        }

        analysis.ReferenceTokens = expected.Count;
        analysis.MatchedTokens = match.Count(m => m >= 0);
        analysis.DroppedTokens = expected.Count - analysis.MatchedTokens;
        analysis.InsertedTokens = matchedActual.Count(m => !m);
        analysis.DuplicatedTokens = Enumerable.Range(0, actual.Count).Count(j => !matchedActual[j] && IsDoubled(actual, j));
        analysis.CoverageVsReference = expected.Count == 0 ? 0 : Math.Round(analysis.MatchedTokens / (double)expected.Count, 4);

        var source = placements.SelectMany(p => Words.Tokenize(p.Text)).ToList();
        var sourceMatch = Words.Align(source, actual);
        analysis.OrderedCoverageVsSource = source.Count == 0 ? 0 : Math.Round(sourceMatch.Count(m => m >= 0) / (double)source.Count, 4);
        analysis.WordOverlapVsSource = Math.Round(Program.WordOverlap(string.Join(' ', placements.Select(p => p.Text)), transcript), 4);

        var tokens = new int[local.Count];
        var matched = new int[local.Count];
        var firstActual = Enumerable.Repeat(int.MaxValue, local.Count).ToArray();
        var lastActual = Enumerable.Repeat(int.MinValue, local.Count).ToArray();
        for (var e = 0; e < expected.Count; e++)
        {
            var instance = expected[e].Instance;
            tokens[instance]++;
            if (match[e] >= 0)
            {
                matched[instance]++;
                firstActual[instance] = Math.Min(firstActual[instance], match[e]);
                lastActual[instance] = Math.Max(lastActual[instance], match[e]);
            }
        }

        for (var s = 1; s < chunks.Count; s++)
        {
            var seam = chunks[s].Start;
            analysis.Seams.Add(JudgeSeam(
                s, seam, decodeInput, local, transcript, actual, actualSpans, firstActual, lastActual, decodeWhole,
                sampleRate, assertSeams));
        }

        for (var i = 0; i < local.Count; i++)
        {
            if (tokens[i] > matched[i])
            {
                analysis.LossyPhrases.Add(new InstanceInfo(
                    i,
                    local[i].Clip,
                    Math.Round(placements[i].Start / (double)sampleRate, 2),
                    Math.Round(placements[i].End / (double)sampleRate, 2),
                    tokens[i],
                    matched[i]));
            }
        }

        return analysis;
    }

    private static SeamInfo JudgeSeam(
        int index,
        int seam,
        float[] input,
        List<Placement> placements,
        string transcript,
        List<string> actual,
        List<(int Start, int End)> actualSpans,
        int[] firstActual,
        int[] lastActual,
        Func<float[], string> decodeWhole,
        int sampleRate,
        bool assertSeams)
    {
        var half = Math.Max(1, (int)(LevelWindowSeconds * sampleRate / 2));
        var level = AudioTransforms.ToDbfs(AudioTransforms.Rms(
            input.AsSpan(Math.Max(0, seam - half), Math.Min(input.Length, seam + half) - Math.Max(0, seam - half))));
        var inPause = level < PauseDbfs;

        var containing = placements.FindIndex(p => seam > p.Start && seam < p.End);
        var before = placements.FindLastIndex(p => p.End <= seam);
        var after = placements.FindIndex(p => p.Start >= seam);
        var location = containing >= 0
            ? string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "inside {0}#{1} at +{2:0.00}s of {3:0.00}s", placements[containing].Clip, containing,
                (seam - placements[containing].Start) / (double)sampleRate, placements[containing].Length / (double)sampleRate)
            : $"between {Describe(placements, before)} and {Describe(placements, after)}";
        location += inPause ? ", on a lull" : ", through speech";

        // The seamless window spans the phrases either side of the cut, whole where they fit.
        var first = containing >= 0 ? Math.Max(0, containing - 1) : Math.Max(0, before);
        var last = containing >= 0 ? Math.Min(placements.Count - 1, containing + 1) : after >= 0 ? after : placements.Count - 1;
        var start = Math.Clamp(placements.Count == 0 ? seam : placements[first].Start, 0, input.Length);
        var end = Math.Clamp(placements.Count == 0 ? seam : placements[last].End, 0, input.Length);
        var maxWindow = (int)(MaxWindowSeconds * sampleRate);
        if (end - start > maxWindow)
        {
            start = QuietestNear(input, Math.Max(start, seam - (maxWindow / 2)), sampleRate);
            end = QuietestNear(input, Math.Min(end, seam + (maxWindow / 2)), sampleRate);
        }

        start = Math.Clamp(start, 0, Math.Max(0, seam - 1));
        end = Math.Clamp(end, Math.Min(input.Length, seam + 1), input.Length);
        var referenceText = decodeWhole(input[start..end]);
        var window = Words.Tokenize(referenceText);

        // Only the stretch of the chunked transcript that belongs to these phrases, so a repeated
        // phrase elsewhere in the capture can never stand in for the one at the seam.
        var low = int.MaxValue;
        var high = int.MinValue;
        for (var i = first; i <= last && i < placements.Count; i++)
        {
            low = Math.Min(low, firstActual[i]);
            high = Math.Max(high, lastActual[i]);
        }

        if (low == int.MaxValue)
        {
            low = 0;
            high = actual.Count - 1;
        }

        low = Math.Max(0, low - 3);
        high = Math.Min(actual.Count - 1, high + 3);
        var region = high >= low ? actual.GetRange(low, high - low + 1) : [];
        var chunkText = high >= low ? transcript[actualSpans[low].Start..actualSpans[high].End] : string.Empty;
        var alignment = Words.Align(window, region);

        // A word the chunked decode merged with its neighbour, or split in two, is the same audio spelled with
        // different spacing, so it is compared as letters rather than charged as lost (see Words.CreditMergedAndSplit).
        var (present, covered) = Words.CreditMergedAndSplit(window, region, alignment);

        // The window's first and last two words can be clipped by its own edges when it had to be
        // shortened, so they are never charged to the seam.
        var lost = 0;
        var mergedOrSplit = 0;
        for (var w = 2; w < window.Count - 2; w++)
        {
            if (!present[w])
            {
                lost++;
            }
            else if (alignment[w] < 0)
            {
                mergedOrSplit++;
            }
        }

        var doubled = Enumerable.Range(0, region.Count).Count(j => !covered[j] && IsDoubled(region, j));
        var verdict = !assertSeams || !inPause
            ? CheckStatus.Report
            : lost >= 2 || doubled > 0 ? CheckStatus.Fail : CheckStatus.Pass;

        return new SeamInfo(
            index,
            Math.Round(seam / (double)sampleRate, 3),
            location,
            Math.Round(level, 1),
            inPause,
            Math.Round(start / (double)sampleRate, 2),
            Math.Round(end / (double)sampleRate, 2),
            window.Count,
            lost,
            doubled,
            verdict,
            mergedOrSplit,
            referenceText,
            chunkText);
    }

    /// <summary>A word that repeats the one before it, or the pair before it ("the the", "and so and so").</summary>
    private static bool IsDoubled(List<string> words, int j) =>
        (j > 0 && words[j] == words[j - 1])
        || (j >= 3 && words[j] == words[j - 2] && words[j - 1] == words[j - 3]);

    /// <summary>The middle of the quietest 100 ms within a second of <paramref name="around"/>.</summary>
    private static int QuietestNear(float[] samples, int around, int sampleRate)
    {
        var window = Math.Max(1, (int)(LevelWindowSeconds * sampleRate));
        var best = around;
        var bestLevel = double.MaxValue;
        for (var start = Math.Max(0, around - sampleRate); start + window <= Math.Min(samples.Length, around + sampleRate); start += window / 2)
        {
            var level = AudioTransforms.Rms(samples.AsSpan(start, window));
            if (level < bestLevel)
            {
                bestLevel = level;
                best = start + (window / 2);
            }
        }

        return best;
    }

    /// <summary>
    /// Where the VAD-trimmed audio starts inside the capture. VAD copies a contiguous span out of
    /// the capture without altering a sample, so an exact match on a short probe finds it; the probe
    /// starts at the first non-zero sample because synthetic speech can begin with exact zeros.
    /// </summary>
    internal static int FindOffset(float[] captured, float[] trimmed)
    {
        if (trimmed.Length == 0 || trimmed.Length > captured.Length)
        {
            return -1;
        }

        var probeStart = Math.Max(0, Array.FindIndex(trimmed, s => s != 0f));
        var probeLength = Math.Min(64, trimmed.Length - probeStart);
        for (var candidate = 0; candidate + trimmed.Length <= captured.Length; candidate++)
        {
            var found = true;
            for (var k = 0; k < probeLength; k++)
            {
                if (captured[candidate + probeStart + k] != trimmed[probeStart + k])
                {
                    found = false;
                    break;
                }
            }

            if (found)
            {
                return candidate;
            }
        }

        return -1;
    }

    private static string Describe(IReadOnlyList<Placement> placements, int index) =>
        index < 0 ? "(edge)" : $"{placements[index].Clip}#{index}";
}

/// <summary>Tokenization and alignment for the long-dictation checks.</summary>
internal static class Words
{
    /// <summary>
    /// Lower-cased runs of letters and digits: the same tokens <see cref="Program.WordOverlap"/>
    /// scores on, so the overlap and the alignment never disagree about what a word is.
    /// </summary>
    public static List<string> Tokenize(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : new string(text.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ').ToArray())
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .ToList();

    /// <summary>Where each token <see cref="Tokenize"/> returns sits in <paramref name="text"/>, as start and end offsets.</summary>
    public static List<(int Start, int End)> TokenSpans(string? text)
    {
        var spans = new List<(int Start, int End)>();
        if (string.IsNullOrEmpty(text))
        {
            return spans;
        }

        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var inToken = i < text.Length && char.IsLetterOrDigit(text[i]);
            if (inToken && start < 0)
            {
                start = i;
            }
            else if (!inToken && start >= 0)
            {
                spans.Add((start, i));
                start = -1;
            }
        }

        return spans;
    }

    /// <summary>
    /// Which <paramref name="expected"/> words the <paramref name="actual"/> words carry, and which actual words are
    /// accounted for, once words the recogniser merged or split are compared as letters.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The token alignment reads "gethubcopilot" and "gethup copilot" as three different words, so a chunk that merged
    /// two words, or split one, was charged with losing words it had not lost: the audio was all decoded, only its
    /// spacing differed. So between each pair of words the token alignment matched, the unmatched words on both sides
    /// are compared again as one run of letters (the tokens are already lower case, and joining them drops the
    /// whitespace), aligned by edit distance. That is the only tolerance added.
    /// </para>
    /// <para>
    /// An expected word is credited only when every one of its letters matches exactly, as one contiguous stretch of
    /// the actual letters, and any other letters of the actual words it lands in are accounted for by neighbouring
    /// expected words. So a word that is really missing (its letters are absent) is still lost, a misheard word (one
    /// letter differs, as "gethup" against "gethub") is still lost, and a short word that merely occurs inside a longer
    /// actual word ("a" in "cat") is still lost, because that word's other letters account for nothing in the reference.
    /// An actual word is accounted for when none of its letters is left over, so a repeated word, whose letters are all
    /// left over, is still found as doubled.
    /// </para>
    /// </remarks>
    public static (bool[] Present, bool[] Covered) CreditMergedAndSplit(
        IReadOnlyList<string> expected, IReadOnlyList<string> actual, int[] alignment)
    {
        var present = alignment.Select(a => a >= 0).ToArray();
        var covered = new bool[actual.Count];
        foreach (var a in alignment.Where(a => a >= 0))
        {
            covered[a] = true;
        }

        var previousActual = -1;
        var e = 0;
        while (e < expected.Count)
        {
            if (alignment[e] >= 0)
            {
                previousActual = alignment[e++];
                continue;
            }

            var gapStart = e;
            while (e < expected.Count && alignment[e] < 0)
            {
                e++;
            }

            var nextActual = e < expected.Count ? alignment[e] : actual.Count;
            if (nextActual > previousActual + 1)
            {
                CreditGap(expected, gapStart, e, actual, previousActual + 1, nextActual, present, covered);
            }
        }

        return (present, covered);
    }

    // Aligns the letters of expected[expectedStart..expectedEnd) with those of actual[actualStart..actualEnd) by edit
    // distance, preferring a match or substitution over a gap on ties so a merged or split word stays one diagonal run.
    private static void CreditGap(
        IReadOnlyList<string> expected, int expectedStart, int expectedEnd,
        IReadOnlyList<string> actual, int actualStart, int actualEnd,
        bool[] present, bool[] covered)
    {
        var (eLetters, eOwner) = Letters(expected, expectedStart, expectedEnd);
        var (aLetters, aOwner) = Letters(actual, actualStart, actualEnd);
        var n = eLetters.Length;
        var m = aLetters.Length;
        var width = m + 1;
        var cost = new int[(n + 1) * width];
        for (var j = 0; j <= m; j++)
        {
            cost[(n * width) + j] = m - j;
        }

        for (var i = n - 1; i >= 0; i--)
        {
            cost[(i * width) + m] = n - i;
            for (var j = m - 1; j >= 0; j--)
            {
                var diagonal = cost[((i + 1) * width) + j + 1] + (eLetters[i] == aLetters[j] ? 0 : 1);
                var dropExpected = cost[((i + 1) * width) + j] + 1;
                var dropActual = cost[(i * width) + j + 1] + 1;
                cost[(i * width) + j] = Math.Min(diagonal, Math.Min(dropExpected, dropActual));
            }
        }

        // For each letter, the letter it is aligned with on the other side, or -1 when it is left over.
        var eAligned = Enumerable.Repeat(-1, n).ToArray();
        var aAligned = Enumerable.Repeat(-1, m).ToArray();
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            var here = cost[(x * width) + y];
            if (here == cost[((x + 1) * width) + y + 1] + (eLetters[x] == aLetters[y] ? 0 : 1))
            {
                eAligned[x] = y;
                aAligned[y] = x;
                x++;
                y++;
            }
            else if (here == cost[((x + 1) * width) + y] + 1)
            {
                x++;
            }
            else
            {
                y++;
            }
        }

        for (var word = expectedStart; word < expectedEnd; word++)
        {
            var first = Array.IndexOf(eOwner, word);
            var last = Array.LastIndexOf(eOwner, word);
            var exact = true;
            for (var k = first; k <= last && exact; k++)
            {
                exact = eAligned[k] >= 0
                    && eLetters[k] == aLetters[eAligned[k]]
                    && (k == first || eAligned[k] == eAligned[k - 1] + 1);
            }

            if (!exact)
            {
                continue;
            }

            // The rest of every actual word this one lands in must be aligned with other expected letters.
            var from = eAligned[first];
            var to = eAligned[last];
            var accounted = true;
            for (var k = from - 1; k >= 0 && aOwner[k] == aOwner[from] && accounted; k--)
            {
                accounted = aAligned[k] >= 0;
            }

            for (var k = to + 1; k < m && aOwner[k] == aOwner[to] && accounted; k++)
            {
                accounted = aAligned[k] >= 0;
            }

            present[word] |= accounted;
        }

        for (var word = actualStart; word < actualEnd; word++)
        {
            var first = Array.IndexOf(aOwner, word);
            var last = Array.LastIndexOf(aOwner, word);
            var allAligned = true;
            for (var k = first; k <= last && allAligned; k++)
            {
                allAligned = aAligned[k] >= 0;
            }

            covered[word] |= allAligned;
        }
    }

    // The letters of words[start..end) run together, with the word each letter came from.
    private static (char[] Letters, int[] Owner) Letters(IReadOnlyList<string> words, int start, int end)
    {
        var letters = new List<char>();
        var owner = new List<int>();
        for (var word = start; word < end; word++)
        {
            foreach (var c in words[word])
            {
                letters.Add(c);
                owner.Add(word);
            }
        }

        return ([.. letters], [.. owner]);
    }

    /// <summary>
    /// Longest common subsequence alignment. Returns, for each expected token, the index of the
    /// actual token it aligned to, or -1 when it was dropped.
    /// </summary>
    public static int[] Align(IReadOnlyList<string> expected, IReadOnlyList<string> actual)
    {
        var n = expected.Count;
        var m = actual.Count;
        var width = m + 1;
        var suffix = new int[(n + 1) * width];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                suffix[(i * width) + j] = string.Equals(expected[i], actual[j], StringComparison.Ordinal)
                    ? suffix[((i + 1) * width) + j + 1] + 1
                    : Math.Max(suffix[((i + 1) * width) + j], suffix[(i * width) + j + 1]);
            }
        }

        var alignment = Enumerable.Repeat(-1, n).ToArray();
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (string.Equals(expected[x], actual[y], StringComparison.Ordinal)
                && suffix[(x * width) + y] == suffix[((x + 1) * width) + y + 1] + 1)
            {
                alignment[x++] = y++;
            }
            else if (suffix[((x + 1) * width) + y] >= suffix[(x * width) + y + 1])
            {
                x++;
            }
            else
            {
                y++;
            }
        }

        return alignment;
    }
}
