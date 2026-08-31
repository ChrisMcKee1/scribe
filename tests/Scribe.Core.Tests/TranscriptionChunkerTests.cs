using Scribe.Core.Transcription;

namespace Scribe.Core.Tests;

public class TranscriptionChunkerTests
{
    private const int SampleRate = 16_000;

    private static float[] Samples(double seconds) => new float[(int)(seconds * SampleRate)];

    [Theory]
    [InlineData(0.5)]
    [InlineData(10)]
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
    [InlineData(180, 6)]
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

        var position = 0;
        foreach (var (start, length) in spans)
        {
            Assert.Equal(position, start);
            Assert.True(length > 0);
            Assert.True(length <= TranscriptionChunker.MaxChunkSeconds * SampleRate,
                $"chunk of {length / (double)SampleRate:F1}s exceeds the cap");
            position += length;
        }

        Assert.Equal(samples.Length, position);
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
}
