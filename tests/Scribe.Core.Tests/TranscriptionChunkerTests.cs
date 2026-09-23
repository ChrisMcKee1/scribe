using Scribe.Core.Transcription;

namespace Scribe.Core.Tests;

public class TranscriptionChunkerTests
{
    private const int SampleRate = 16_000;

    // A seam is "quiet" when the 100 ms the planner scores around it stays below this RMS. Synthetic speech here is a
    // 0.3 amplitude tone and a lull is digital silence, so the threshold separates them with a wide margin.
    private const double LullRms = 0.01;

    private static float[] Samples(double seconds) => new float[(int)(seconds * SampleRate)];

    [Theory]
    [InlineData(0.5)]
    [InlineData(10)]
    [InlineData(29.9)]
    [InlineData(TranscriptionChunker.MaxChunkSeconds)]
    public void ShortCapturesStayWhole(double seconds)
    {
        var samples = Samples(seconds);
        var spans = TranscriptionChunker.Plan(samples, SampleRate);

        var span = Assert.Single(spans);
        Assert.Equal(0, span.Start);
        Assert.Equal(samples.Length, span.Length);
    }

    [Theory]
    [InlineData(31, 2)]
    [InlineData(65, 3)]
    // Exactly 6 x 30 s: with six chunks every seam would be pinned to a 30 s multiple, so one more chunk is planned.
    [InlineData(180, 7)]
    public void LongCapturesSplitIntoEvenChunks(double seconds, int expectedChunks)
    {
        var spans = TranscriptionChunker.Plan(Samples(seconds), SampleRate);
        Assert.Equal(expectedChunks, spans.Count);
    }

    [Fact]
    public void SpansAreContiguousCompleteAndCapped()
    {
        var samples = Samples(200);
        var spans = TranscriptionChunker.Plan(samples, SampleRate);

        AssertValidPlan(spans, samples.Length, SampleRate);
    }

    [Fact]
    public void BoundariesPreferQuietAudio()
    {
        // 40 s of loud tone with one 0.5 s silent gap near the midpoint. The single split the
        // planner needs must land inside that gap rather than at the arithmetic midpoint.
        var samples = Samples(40);
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = 0.5f;
        }

        var gapStart = (int)(18.0 * SampleRate);
        var gapEnd = (int)(18.5 * SampleRate);
        Array.Clear(samples, gapStart, gapEnd - gapStart);

        var spans = TranscriptionChunker.Plan(samples, SampleRate);

        Assert.Equal(2, spans.Count);
        var boundary = spans[1].Start;
        Assert.InRange(boundary, gapStart, gapEnd);
    }

    [Fact]
    public void BoundaryWithoutAnyQuietAudioStillSplits()
    {
        // Uniform loudness end to end: no lull to find, but the plan must still be valid.
        var samples = Samples(70);
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = 0.4f;
        }

        var spans = TranscriptionChunker.Plan(samples, SampleRate);

        Assert.Equal(3, spans.Count);
        Assert.Equal(samples.Length, spans[^1].Start + spans[^1].Length);
    }

    [Fact]
    public void Featureless_audio_splits_exactly_at_the_even_cuts()
    {
        // With nothing to prefer, the tie-break keeps every chunk even, as the single-seam planner always did.
        var samples = Samples(100);
        Array.Fill(samples, 0.25f);

        var spans = TranscriptionChunker.Plan(samples, SampleRate);

        Assert.Equal(new[] { 0, 400_000, 800_000, 1_200_000 }, spans.Select(span => span.Start));
    }

    [Theory]
    // 50 s leaves two chunks exactly the 10 s of slack a seam's search window needs; any more audio adds a chunk.
    [InlineData(800_000, 2)]
    [InlineData(800_001, 3)]
    [InlineData(960_000, 3)] // 60 s: two chunks would leave no slack at all
    [InlineData(1_280_000, 3)] // 80 s: three chunks, 10 s of slack
    [InlineData(1_280_001, 4)]
    [InlineData(1_440_000, 4)] // 90 s
    public void An_extra_chunk_is_planned_exactly_when_the_slack_cannot_cover_a_search_window(int sampleCount, int expected)
    {
        Assert.Equal(
            expected,
            TranscriptionChunker.ChunkCount(
                sampleCount,
                TranscriptionChunker.MaxChunkSeconds * SampleRate,
                TranscriptionChunker.BoundarySearchSeconds * SampleRate));
    }

    /// <summary>
    /// The reported defect. Speech runs straight through every 30 s multiple and the pauses sit earlier. The old planner
    /// pinned every seam of a 60, 120 or 300 s capture to an exact 30 s multiple, inside speech, splitting a word each
    /// time; every seam must now land on a lull.
    /// </summary>
    [Theory]
    [InlineData(29.9, 1)]
    [InlineData(30, 1)]
    [InlineData(30.1, 2)]
    [InlineData(59.9, 3)]
    [InlineData(60, 3)]
    [InlineData(89.9, 4)]
    [InlineData(120, 5)]
    [InlineData(300, 11)]
    public void Seams_land_on_lulls_even_when_speech_runs_through_every_thirty_second_multiple(
        double seconds, int expectedChunks)
    {
        // Lulls every 6 s, placed 1, 3 or 4.5 s before each 30 s multiple and never within a second of one.
        foreach (var offset in new[] { 1.0, 2.5, 4.5 })
        {
            var profile = new SpeechProfile(seconds, SampleRate, period: 6, offset);
            for (var multiple = 30; multiple < seconds; multiple += 30)
            {
                Assert.False(profile.IsQuietAround(multiple * SampleRate), "the profile must hold speech at each multiple");
            }

            var spans = TranscriptionChunker.Plan(profile.Samples, SampleRate);

            Assert.Equal(expectedChunks, spans.Count);
            AssertValidPlan(spans, profile.Samples.Length, SampleRate);
            foreach (var (seam, _) in spans.Skip(1))
            {
                Assert.True(
                    profile.IsQuietAround(seam),
                    $"{seconds} s, lull offset {offset}: seam at {seam / (double)SampleRate:F2} s cuts through speech");
            }
        }
    }

    /// <summary>
    /// The general property, swept deterministically over lengths on both sides of every chunk-count change and over
    /// every lull phase, in its strongest achievable form.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Where seams are independent (two chunks, or even chunks of at most the cap less twice the search radius, 20 s),
    /// any seam positions inside their windows form a legal plan, so the literal property holds: no seam lands in speech
    /// while its search window, within the chunk limits, holds a lull.
    /// </para>
    /// <para>
    /// Longer even chunks couple the seams through the cap: a seam may sit only so much later, relative to its even cut,
    /// than the seam before it. Then the property is about plans: whenever any plan within the limits puts every seam on
    /// a lull, the planner must return one. An independent oracle decides that by propagating which lull candidates are
    /// reachable seam to seam, rather than by minimizing anything. A planner that chose each seam on its own and then
    /// clamped it failed this at 300 s with lull offset 5.5, leaving the last seam in speech.
    /// </para>
    /// </remarks>
    [Fact]
    public void Seams_avoid_speech_whenever_the_chunk_limits_allow_it()
    {
        // The planner works in samples and scales every constant with the rate, so a low one keeps this sweep fast.
        const int rate = 1_000;
        var maxChunk = TranscriptionChunker.MaxChunkSeconds * rate;
        var minChunk = TranscriptionChunker.MinChunkSeconds * rate;
        var radius = TranscriptionChunker.BoundarySearchSeconds * rate;
        var hop = rate / 20; // the planner's candidate grid: half its 100 ms scoring window
        double[] lengths =
        [
            30.5, 45, 49.9, 50, 50.1, 55, 59.9, 60, 60.1, 75, 79.9, 80, 80.1, 89.9, 90, 119.9, 120, 150,
            179.9, 180, 240, 299.9, 300, 301, 450, 600,
        ];

        var independentSeams = 0;
        var coupledPlans = 0;
        foreach (var seconds in lengths)
        {
            foreach (var period in new[] { 4.0, 6.0 })
            {
                for (var offset = 0.0; offset < period; offset += 0.5)
                {
                    var profile = new SpeechProfile(seconds, rate, period, offset);
                    var length = profile.Samples.Length;
                    var spans = TranscriptionChunker.Plan(profile.Samples, rate);
                    AssertValidPlan(spans, length, rate);

                    var count = spans.Count;
                    var windows = new (long Even, long Lo, long Hi)[count];
                    for (var i = 1; i < count; i++)
                    {
                        // The window a seam may use: its search radius around the even cut, less whatever the chunks on
                        // either side could never cover.
                        var even = (long)Math.Round(i * (length / (double)count));
                        windows[i] = (
                            even,
                            Math.Max(even - radius, Math.Max((long)i * minChunk, length - (long)(count - i) * maxChunk)),
                            Math.Min(even + radius, Math.Min((long)i * maxChunk, length - (long)(count - i) * minChunk)));
                    }

                    var label = $"{seconds} s, lulls every {period} s from {offset} s";
                    if (count == 2 || length / (double)count <= maxChunk - 2 * radius)
                    {
                        for (var i = 1; i < count; i++)
                        {
                            if (!profile.HasLullWithin(windows[i].Lo, windows[i].Hi))
                            {
                                continue;
                            }

                            independentSeams++;
                            Assert.True(
                                profile.IsQuietAround(spans[i].Start),
                                $"{label}: seam {i} at {spans[i].Start / (double)rate:F2} s cuts through speech " +
                                $"although its window [{windows[i].Lo / (double)rate:F2}, " +
                                $"{windows[i].Hi / (double)rate:F2}] holds a lull");
                        }

                        continue;
                    }

                    if (!AllLullPlanExists(profile, windows, count, hop, minChunk, maxChunk))
                    {
                        continue;
                    }

                    coupledPlans++;
                    for (var i = 1; i < count; i++)
                    {
                        Assert.True(
                            profile.IsQuietAround(spans[i].Start),
                            $"{label}: seam {i} at {spans[i].Start / (double)rate:F2} s cuts through speech although a " +
                            "plan with every seam on a lull exists");
                    }
                }
            }
        }

        Assert.True(independentSeams >= 200, $"the sweep only exercised {independentSeams} independent seams");
        Assert.True(coupledPlans >= 300, $"the sweep only exercised {coupledPlans} coupled plans");
    }

    /// <summary>
    /// Where no plan can put every seam on a lull, the planner still minimizes the damage. At 450 s the 16 chunks average
    /// 28.125 s, so each seam may sit at most 1.875 s later, relative to its even cut, than the one before it. With lulls
    /// every 4 s from 2 s, the first chunk's cap forces seam 1 onto the one lull track that the last chunk's cap later
    /// rules out, and the other track is 4 s away, beyond any single step. One seam in speech is the least any plan
    /// allows.
    /// </summary>
    [Fact]
    public void When_no_plan_can_avoid_speech_everywhere_only_one_seam_is_given_up()
    {
        const int rate = 1_000;
        var profile = new SpeechProfile(450, rate, period: 4, offset: 2);

        var spans = TranscriptionChunker.Plan(profile.Samples, rate);

        AssertValidPlan(spans, profile.Samples.Length, rate);
        Assert.Equal(16, spans.Count);
        Assert.Equal(1, spans.Skip(1).Count(span => !profile.IsQuietAround(span.Start)));
    }

    // Independent of the planner's scoring: which quiet candidates on the planner's grid can each seam reach from a quiet
    // candidate of the seam before it, within the chunk limits? A plan with every seam on a lull exists exactly when the
    // last seam still has one. The first and last chunk limits are already inside each seam's window.
    private static bool AllLullPlanExists(
        SpeechProfile profile, (long Even, long Lo, long Hi)[] windows, int count, int hop, int minChunk, int maxChunk)
    {
        List<long>? reachable = null;
        for (var i = 1; i < count; i++)
        {
            var next = new List<long>();
            var (even, lo, hi) = windows[i];
            for (var k = (long)Math.Ceiling((lo - even) / (double)hop); even + k * hop <= hi; k++)
            {
                var position = even + k * hop;
                if (!profile.IsQuietAround((int)position))
                {
                    continue;
                }

                if (reachable is null || reachable.Any(from => position - from >= minChunk && position - from <= maxChunk))
                {
                    next.Add(position);
                }
            }

            if (next.Count == 0)
            {
                return false;
            }

            reachable = next;
        }

        return true;
    }

    [Fact]
    public void Every_plan_is_contiguous_complete_within_the_chunk_limits_and_near_the_even_cuts()
    {
        // Uniform and silent captures across lengths from just over the cap to 300 s, at a low rate for speed.
        const int rate = 100;
        foreach (var fill in new[] { 0f, 0.3f })
        {
            for (var tenths = TranscriptionChunker.MaxChunkSeconds * 10 + 1; tenths <= 3_000; tenths += 7)
            {
                var samples = new float[tenths * rate / 10];
                Array.Fill(samples, fill);

                var spans = TranscriptionChunker.Plan(samples, rate);

                AssertValidPlan(spans, samples.Length, rate);
                var expectedCount = TranscriptionChunker.ChunkCount(
                    samples.Length,
                    TranscriptionChunker.MaxChunkSeconds * rate,
                    TranscriptionChunker.BoundarySearchSeconds * rate);
                Assert.Equal(expectedCount, spans.Count);
                for (var i = 1; i < spans.Count; i++)
                {
                    var even = i * (samples.Length / (double)spans.Count);
                    Assert.InRange(
                        spans[i].Start,
                        even - TranscriptionChunker.BoundarySearchSeconds * rate - 1,
                        even + TranscriptionChunker.BoundarySearchSeconds * rate + 1);
                }
            }
        }
    }

    // Contiguous, complete, and every chunk between the minimum and the cap: never empty, never oversized.
    private static void AssertValidPlan(IReadOnlyList<(int Start, int Length)> spans, int sampleCount, int rate)
    {
        var position = 0;
        foreach (var (start, length) in spans)
        {
            Assert.Equal(position, start);
            Assert.True(length > 0, "empty chunk");
            Assert.True(
                length <= TranscriptionChunker.MaxChunkSeconds * rate,
                $"chunk of {length / (double)rate:F2} s exceeds the cap");
            if (spans.Count > 1)
            {
                Assert.True(
                    length >= TranscriptionChunker.MinChunkSeconds * rate,
                    $"chunk of {length / (double)rate:F2} s is below the minimum");
            }

            position += length;
        }

        Assert.Equal(sampleCount, position);
    }

    // Speech as a loud tone, with 0.5 s of silence every period seconds starting at offset.
    private sealed class SpeechProfile
    {
        private const double LullSeconds = 0.5;
        private readonly int _rate;
        private readonly List<(int Start, int End)> _lulls = [];

        public SpeechProfile(double seconds, int rate, double period, double offset)
        {
            _rate = rate;
            Samples = new float[(int)Math.Round(seconds * rate)];
            for (var i = 0; i < Samples.Length; i++)
            {
                Samples[i] = (i & 1) == 0 ? 0.3f : -0.3f;
            }

            for (var t = offset; t + LullSeconds <= seconds; t += period)
            {
                var start = (int)Math.Round(t * rate);
                var end = Math.Min(Samples.Length, (int)Math.Round((t + LullSeconds) * rate));
                Array.Clear(Samples, start, end - start);
                _lulls.Add((start, end));
            }
        }

        public float[] Samples { get; }

        // The 100 ms the planner scores around a seam, below the lull threshold.
        public bool IsQuietAround(int position)
        {
            var half = Math.Max(1, _rate / 20);
            var from = Math.Max(0, position - half);
            var to = Math.Min(Samples.Length, position + half);
            double energy = 0;
            for (var i = from; i < to; i++)
            {
                energy += Samples[i] * (double)Samples[i];
            }

            return Math.Sqrt(energy / Math.Max(1, to - from)) < LullRms;
        }

        // Whether some lull could host a quiet seam inside [lo, hi]: the part of it at least half the scoring window
        // from its edges must overlap the range by at least one planner hop.
        public bool HasLullWithin(long lo, long hi)
        {
            var half = Math.Max(1, _rate / 20);
            foreach (var (start, end) in _lulls)
            {
                var usableFrom = Math.Max(lo, start + half);
                var usableTo = Math.Min(hi, end - half);
                if (usableTo - usableFrom >= half)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
