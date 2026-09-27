using System.Buffers;
using System.Runtime.InteropServices;
using NAudio.Wave;
using Scribe.Core.Audio;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// Pins the capture conversion's own scratch pool: exactly the lengths the shared pool hands out (the conversion's output
/// depends on them at 44.1 kHz), each array rented to one conversion at a time, at most one kept per length and none
/// past the bound, everything dropped by a release, and nothing kept that still holds audio.
/// </summary>
public sealed class CaptureScratchPoolTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(1_000)]
    [InlineData(16_000)]
    [InlineData(16_384)]
    [InlineData(16_385)]
    [InlineData(70_000)]
    [InlineData(CaptureScratchPool.MaxRetainedLength)]
    [InlineData(CaptureScratchPool.MaxRetainedLength + 1)]
    public void Hands_out_the_lengths_the_shared_pool_does(int minimumLength)
    {
        var pool = new CaptureScratchPool();
        var shared = ArrayPool<float>.Shared.Rent(minimumLength);
        try
        {
            Assert.Equal(shared.Length, pool.Rent(minimumLength).Length);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(shared);
        }
    }

    [Fact]
    public void A_zero_length_rent_is_empty_and_a_negative_one_is_refused()
    {
        var pool = new CaptureScratchPool();

        Assert.Empty(pool.Rent(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.Rent(-1));
        Assert.Throws<ArgumentNullException>(() => pool.Return(null!));
    }

    [Fact]
    public void A_returned_array_is_what_the_next_rent_of_its_length_gets()
    {
        var pool = new CaptureScratchPool();
        var array = pool.Rent(16_000);

        pool.Return(array);

        Assert.Equal(16_384 * sizeof(float), pool.RetainedBytes);
        Assert.Same(array, pool.Rent(16_384));
        Assert.Equal(0, pool.RetainedBytes);
    }

    [Fact]
    public void Two_rents_of_one_length_never_share_an_array_and_only_one_is_kept()
    {
        var pool = new CaptureScratchPool();
        var first = pool.Rent(32_768);
        var second = pool.Rent(32_768);

        Assert.NotSame(first, second);

        pool.Return(first);
        pool.Return(second);

        Assert.Equal(32_768 * sizeof(float), pool.RetainedBytes);
        Assert.Same(first, pool.Rent(32_768));
        Assert.NotSame(second, pool.Rent(32_768));
    }

    [Fact]
    public void An_array_longer_than_the_bound_is_let_go()
    {
        var pool = new CaptureScratchPool();
        var large = pool.Rent(CaptureScratchPool.MaxRetainedLength + 1);

        pool.Return(large);

        Assert.Equal(CaptureScratchPool.MaxRetainedLength * 2, large.Length);
        Assert.Equal(0, pool.RetainedBytes);
        Assert.NotSame(large, pool.Rent(CaptureScratchPool.MaxRetainedLength + 1));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(100)]
    [InlineData(16_000)]
    public void An_array_of_a_length_it_never_hands_out_is_let_go(int length)
    {
        var pool = new CaptureScratchPool();

        pool.Return(new float[length]);

        Assert.Equal(0, pool.RetainedBytes);
    }

    [Fact]
    public void What_it_keeps_never_exceeds_the_stated_bound()
    {
        var pool = new CaptureScratchPool();
        var arrays = new List<float[]> { pool.Rent(CaptureScratchPool.MinLength) };
        for (var length = CaptureScratchPool.MinLength; length <= CaptureScratchPool.MaxRetainedLength; length *= 2)
        {
            arrays.Add(pool.Rent(length));
        }

        foreach (var array in arrays)
        {
            pool.Return(array);
        }

        // One of each length from 16 to 2^21 floats; the second array of the shortest length was let go.
        Assert.Equal(CaptureScratchPool.MaxRetainedBytes, pool.RetainedBytes);
        Assert.Equal(16_777_152, CaptureScratchPool.MaxRetainedBytes);
    }

    [Fact]
    public void A_release_reports_what_it_dropped_and_leaves_nothing_kept()
    {
        var pool = new CaptureScratchPool();
        var small = pool.Rent(16_384);
        var larger = pool.Rent(32_768);
        pool.Return(small);
        pool.Return(larger);

        Assert.Equal((16_384 + 32_768) * sizeof(float), pool.ReleaseRetained());
        Assert.Equal(0, pool.RetainedBytes);
        Assert.Equal(0, pool.ReleaseRetained());
        Assert.NotSame(small, pool.Rent(16_384));
        Assert.NotSame(larger, pool.Rent(32_768));
    }

    [Fact]
    public void A_release_never_touches_an_array_that_is_rented()
    {
        var pool = new CaptureScratchPool();
        var rented = pool.Rent(16_384);
        rented[0] = 0.5f;

        Assert.Equal(0, pool.ReleaseRetained());

        Assert.Equal(0.5f, rented[0]);
        rented.AsSpan().Clear();
        pool.Return(rented);
        Assert.Same(rented, pool.Rent(16_384));
    }

    [Fact]
    public void An_array_returned_for_clearing_comes_back_zeroed()
    {
        var pool = new CaptureScratchPool();
        var array = pool.Rent(16);
        array.AsSpan().Fill(0.25f);

        pool.Return(array, clearArray: true);

        Assert.All(pool.Rent(16), sample => Assert.Equal(0f, sample));
    }

    [Theory]
    [InlineData(48_000)]
    [InlineData(44_100)]
    public void A_conversion_through_it_matches_the_shared_pool_and_leaves_only_zeros(int rate)
    {
        // 2.3 s of 16 kHz output grows the scratch through 16,384, 32,768 and 65,536 samples. At 44.1 kHz the
        // conversion's requests follow those lengths, so any other length would change the samples.
        var format = WaveFormat.CreateIeeeFloatWaveFormat(rate, 2);
        var raw = Signal(format, frames: (rate * 23 / 10) + 11);
        var pool = new CaptureScratchPool();

        var expected = AudioCaptureService.ResampleToTarget(raw, raw.Length, format);
        var actual = AudioCaptureService.ResampleToTarget(raw, raw.Length, format, pool);

        Assert.Equal(expected.Length, actual.Length);
        Assert.True(MemoryMarshal.AsBytes(expected.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(actual.AsSpan())));
        Assert.Equal((16_384 + 32_768 + 65_536) * sizeof(float), pool.RetainedBytes);
        foreach (var length in new[] { 16_384, 32_768, 65_536 })
        {
            var kept = pool.Rent(length);
            Assert.All(kept, sample => Assert.Equal(0f, sample));
        }

        Assert.Equal(0, pool.RetainedBytes);
    }

    private static byte[] Signal(WaveFormat format, int frames)
    {
        var random = new Random(frames);
        var samples = new float[frames * format.Channels];
        for (var i = 0; i < samples.Length; i++)
        {
            var t = i / format.Channels / (double)format.SampleRate;
            samples[i] = (float)((0.3 * Math.Sin(2 * Math.PI * 220 * t)) + (0.2 * (random.NextDouble() - 0.5)));
        }

        return MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
    }
}
