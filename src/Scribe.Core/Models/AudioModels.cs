namespace Scribe.Core.Models;

/// <summary>An enumerable input device that can be selected for capture.</summary>
public sealed record AudioDevice(string Id, string Name, bool IsDefault);

/// <summary>
/// A finished mono PCM capture, normalized to <see cref="SampleRate"/> Hz as 32-bit floats
/// in the range [-1, 1] — the format sherpa-onnx and Silero VAD expect.
/// </summary>
public sealed record CapturedAudio(float[] Samples, int SampleRate = 16000)
{
    public static CapturedAudio Empty { get; } = new(Array.Empty<float>());

    public bool IsEmpty => Samples.Length == 0;

    public TimeSpan Duration =>
        SampleRate <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((double)Samples.Length / SampleRate);
}
