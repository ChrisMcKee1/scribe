using System.Numerics;
using System.Runtime.InteropServices;
using NAudio.Wave;
using Scribe.Core.Audio;

namespace Scribe.Core.Tests;

/// <summary>
/// The capture peak over 32-bit float buffers, vectorized in 0.5.1 (perf-051's ComputeFloatPeak), against release 0.5.0's
/// scalar loop, copied below. The peak feeds the recording level bars and the muted-microphone check, so it may not move:
/// over seeded buffers of every length up to four vectors past a vector boundary, and longer ones, holding NaN of both
/// signs, both zeros, both infinities, subnormals, the float extremes, values either side of 1 and of the silence
/// threshold, and ordinary speech levels, the new peak is the old one bit for bit.
/// </summary>
public sealed class AudioCapturePeakDifferentialTests
{
    private static readonly float[] Specials =
    [
        float.NaN,
        BitConverter.Int32BitsToSingle(unchecked((int)0xFFC00000)), // a negative quiet NaN
        BitConverter.Int32BitsToSingle(0x7F800001), // a signaling NaN
        0f,
        -0f,
        float.PositiveInfinity,
        float.NegativeInfinity,
        float.Epsilon,
        -float.Epsilon,
        BitConverter.Int32BitsToSingle(0x007FFFFF), // the largest subnormal
        BitConverter.Int32BitsToSingle(0x00800000), // the smallest normal
        float.MaxValue,
        float.MinValue,
        1f,
        -1f,
        BitConverter.Int32BitsToSingle(0x3F7FFFFF), // just below 1
        BitConverter.Int32BitsToSingle(0x3F800001), // just above 1
        AudioCaptureService.SilentCapturePeak,
        -AudioCaptureService.SilentCapturePeak,
        BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(AudioCaptureService.SilentCapturePeak) - 1),
    ];

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(17)]
    [InlineData(510)]
    public void Float_peaks_equal_release_0_5_0_for_seeded_buffers(int seed)
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 1);
        var random = new Random(seed);
        var lanes = Vector<float>.Count;
        var lengths = Enumerable.Range(0, (4 * lanes) + 3)
            .Concat([63, 64, 65, 127, 128, 129, 480, 481, 960, 4_095, 4_096, 4_097])
            .Distinct();

        foreach (var length in lengths)
        {
            for (var trial = 0; trial < 25; trial++)
            {
                var samples = Generate(random, length, trial % 5);

                var expected = ReleaseZeroFiveFloatPeak(samples);
                var actual = AudioCaptureService.ComputePeak(MemoryMarshal.AsBytes(samples.AsSpan()), format);

                Assert.True(
                    BitConverter.SingleToInt32Bits(expected) == BitConverter.SingleToInt32Bits(actual),
                    $"Seed {seed}, length {length}, trial {trial}: 0.5.0 measured {expected:R}, this build {actual:R}.");
            }
        }
    }

    [Fact]
    public void The_peak_is_found_in_every_lane_and_in_the_tail()
    {
        // The largest magnitude alone, at each position of a buffer one vector and a partial vector long, so the vector
        // part, its lane reduction and the scalar tail each have to find it.
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 1);
        var length = (2 * Vector<float>.Count) + 3;
        for (var position = 0; position < length; position++)
        {
            var samples = Enumerable.Repeat(0.01f, length).ToArray();
            samples[position] = -0.75f;

            Assert.Equal(0.75f, AudioCaptureService.ComputePeak(MemoryMarshal.AsBytes(samples.AsSpan()), format));
        }
    }

    private static float[] Generate(Random random, int length, int mode)
    {
        var samples = new float[length];
        for (var i = 0; i < length; i++)
        {
            samples[i] = mode switch
            {
                // Speech levels with an occasional special value.
                0 => random.Next(16) == 0 ? Specials[random.Next(Specials.Length)] : (random.NextSingle() * 2f) - 1f,
                // Nothing but special values.
                1 => Specials[random.Next(Specials.Length)],
                // A quiet room.
                2 => ((random.NextSingle() * 2f) - 1f) * 0.01f,
                // Mostly NaN.
                3 => random.Next(8) == 0 ? (random.NextSingle() * 2f) - 1f : float.NaN,
                // Zeros and subnormals only.
                _ => random.Next(3) switch
                {
                    0 => 0f,
                    1 => -0f,
                    _ => BitConverter.Int32BitsToSingle(random.Next(1, 0x00800000) | (random.Next(2) << 31)),
                },
            };
        }

        if (mode == 2 && length > 0)
        {
            samples[random.Next(length)] = (random.NextSingle() * 2f) - 1f;
        }

        return samples;
    }

    // Release 0.5.0's float branch of AudioCaptureService.ComputePeak (10c9a0b), verbatim, and the method's clamp.
    private static float ReleaseZeroFiveFloatPeak(ReadOnlySpan<float> samples)
    {
        float peak = 0f;

        foreach (float sample in samples)
        {
            float abs = Math.Abs(sample);
            if (abs > peak)
            {
                peak = abs;
            }
        }

        return Math.Clamp(peak, 0f, 1f);
    }
}
