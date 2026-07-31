using Scribe.Core.Diagnostics;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// The degenerate-decode heuristic. The numbers in these cases are taken from real production
/// dictations: the "Yeah." transcripts the recogniser produced for multi-second speech, and the
/// healthy population it must never flag.
/// </summary>
public sealed class TerseDecodeDetectorTests
{
    /// <summary>Real production rows: a filler token returned for seconds of actual speech.</summary>
    [Theory]
    [InlineData("Yeah.", 6.722)]   // id 1444, 0.74 chars/s
    [InlineData("Yeah.", 4.546)]   // id 1899, 1.10
    [InlineData("Yeah.", 4.450)]   // id 1623, 1.12
    [InlineData("Yeah.", 3.458)]   // id 1060, 1.45
    [InlineData("Yeah.", 2.562)]   // id 1500, 1.95
    [InlineData("Yeah.", 1.986)]   // id 1633, 2.52
    [InlineData("Yeah.", 1.631)]   // id 1426, 3.07
    [InlineData("No.", 3.490)]     // id 1475, 0.86
    public void Flags_the_real_collapsed_decodes(string text, double seconds)
    {
        Assert.True(TerseDecodeDetector.IsSuspiciouslyTerse(text, seconds));
    }

    /// <summary>
    /// The long-capture collapse fixed in 0.3.1 is the same failure at a different scale, so the
    /// same signal catches it: 194 characters for 74.9 s of speech.
    /// </summary>
    [Fact]
    public void Flags_the_long_capture_collapse()
    {
        var truncated = new string('x', 194);

        Assert.True(TerseDecodeDetector.IsSuspiciouslyTerse(truncated, 74.93));
    }

    /// <summary>
    /// Healthy dictation measured 11 to 25 characters per second of speech across 85 real
    /// captures. None of it may be flagged.
    /// </summary>
    [Theory]
    [InlineData(332, 18.2)]    // 18.3 chars/s
    [InlineData(53, 3.4)]      // 15.6
    [InlineData(1032, 81.37)]  // 12.7
    [InlineData(80, 6.8)]      // 11.8, the lowest healthy value observed
    [InlineData(129, 5.2)]     // 25.0, the highest
    public void Never_flags_healthy_dictation(int chars, double seconds)
    {
        Assert.False(TerseDecodeDetector.IsSuspiciouslyTerse(new string('x', chars), seconds));
    }

    /// <summary>
    /// A genuine one-word answer is legitimate and common. Below the duration floor the ratio is
    /// too noisy to act on, so short captures are exempt however terse they look.
    /// </summary>
    [Theory]
    [InlineData("Yeah.", 0.738)]
    [InlineData("Yes.", 0.5)]
    [InlineData("Insights.", 0.896)]
    [InlineData("Unique.", 0.834)]
    public void Never_flags_a_short_genuine_answer(string text, double seconds)
    {
        Assert.False(TerseDecodeDetector.IsSuspiciouslyTerse(text, seconds));
    }

    /// <summary>An empty decode is a separate failure with its own error path; do not double-report.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Ignores_empty_text(string? text)
    {
        Assert.False(TerseDecodeDetector.IsSuspiciouslyTerse(text, 30));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Handles_degenerate_durations(double seconds)
    {
        Assert.False(TerseDecodeDetector.IsSuspiciouslyTerse("Yeah.", seconds));
    }

    /// <summary>
    /// Trailing whitespace must not inflate the character count into looking healthy.
    /// </summary>
    [Fact]
    public void Measures_trimmed_length()
    {
        Assert.True(TerseDecodeDetector.IsSuspiciouslyTerse("Yeah.          ", 5));
    }

    /// <summary>
    /// The gap between the healthy floor and the collapse ceiling is what makes the threshold
    /// safe; if either constant drifts into the other, the heuristic starts guessing.
    /// </summary>
    [Fact]
    public void Threshold_sits_well_below_the_healthy_floor()
    {
        Assert.True(TerseDecodeDetector.MinimumCharsPerSpeechSecond < 11.0);
        Assert.True(TerseDecodeDetector.MinimumCharsPerSpeechSecond > 3.1);
    }
}
