using NAudio.Wave;

namespace Scribe.Benchmarks;

/// <summary>
/// Deterministic device-format audio for exercising the capture path without a microphone: a
/// speech-like mix of amplitude-modulated tones plus a low noise floor, in the 10 ms packets that
/// shared-mode WASAPI typically delivers. Nothing here is recorded audio.
/// </summary>
internal static class SyntheticCapture
{
    public const int PacketsPerSecond = 100;

    /// <summary>The mix format a typical Windows microphone endpoint reports.</summary>
    public static WaveFormat DefaultDeviceFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);

    /// <summary>One second of audio as <see cref="PacketsPerSecond"/> packets of 32-bit float frames.</summary>
    public static byte[][] OneSecondOfPackets(WaveFormat format)
    {
        if (format.Encoding != WaveFormatEncoding.IeeeFloat || format.BitsPerSample != 32)
        {
            throw new ArgumentException("Synthetic capture generates 32-bit float audio only.", nameof(format));
        }

        var framesPerPacket = format.SampleRate / PacketsPerSecond;
        var random = new Random(20260922);
        var packets = new byte[PacketsPerSecond][];
        var frame = 0;
        for (var packet = 0; packet < PacketsPerSecond; packet++)
        {
            var samples = new float[framesPerPacket * format.Channels];
            for (var index = 0; index < framesPerPacket; index++, frame++)
            {
                var t = frame / (float)format.SampleRate;
                var envelope = 0.5f + (0.5f * MathF.Sin(2 * MathF.PI * 3 * t));
                var voiced = (0.20f * MathF.Sin(2 * MathF.PI * 180 * t)) +
                    (0.08f * MathF.Sin(2 * MathF.PI * 720 * t)) +
                    (0.04f * MathF.Sin(2 * MathF.PI * 2_400 * t));
                var noise = ((float)random.NextDouble() - 0.5f) * 0.004f;
                for (var channel = 0; channel < format.Channels; channel++)
                {
                    samples[(index * format.Channels) + channel] = (envelope * voiced) + noise;
                }
            }

            packets[packet] = new byte[samples.Length * sizeof(float)];
            Buffer.BlockCopy(samples, 0, packets[packet], 0, packets[packet].Length);
        }

        return packets;
    }
}
