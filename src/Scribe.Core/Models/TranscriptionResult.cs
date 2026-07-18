namespace Scribe.Core.Models;

/// <summary>The outcome of decoding a single capture.</summary>
public sealed record TranscriptionResult(
    string Text,
    TimeSpan AudioDuration,
    TimeSpan DecodeDuration,
    string? ModelId = null)
{
    public static TranscriptionResult Empty { get; } =
        new(string.Empty, TimeSpan.Zero, TimeSpan.Zero);

    public bool IsEmpty => string.IsNullOrWhiteSpace(Text);

    /// <summary>Decode time relative to audio length; &lt; 1 means faster than real time.</summary>
    public double RealTimeFactor =>
        AudioDuration > TimeSpan.Zero
            ? DecodeDuration.TotalSeconds / AudioDuration.TotalSeconds
            : 0;
}
