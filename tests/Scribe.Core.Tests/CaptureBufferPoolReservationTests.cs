using Scribe.Core.Audio;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// Pins what the capture pool keeps after a capture outgrew its reservation: the reservation it started with, zeroed when
/// it was outgrown, and never an array the capture grew into, so a long dictation no longer makes the next press reserve
/// a fresh working buffer (11,520,000 bytes at 48 kHz stereo float) and what stays resident is still one reservation.
/// </summary>
public sealed class CaptureBufferPoolReservationTests
{
    [Fact]
    public void A_reservation_holding_audio_when_it_is_outgrown_is_zeroed_before_it_is_kept()
    {
        var pool = new CaptureBufferPool();
        var recording = pool.Rent(1_024);
        var reservation = recording.Buffer;
        recording.Write(Filled(900, 0x5A));

        recording.Write(Filled(300, 0x6B));

        // Outgrown with 900 bytes of audio in it: zeroed at once, while the capture goes on in the grown array.
        Assert.True(recording.Grew);
        Assert.True(IsAllZero(reservation));
        Assert.Equal(1_200, recording.Length);
        Assert.True(recording.Written[..900].IndexOfAnyExcept((byte)0x5A) < 0);
        Assert.True(recording.Written[900..].IndexOfAnyExcept((byte)0x6B) < 0);

        pool.Return(recording, retain: true);

        var next = pool.Rent(1_024);
        Assert.Same(reservation, next.Buffer);
        Assert.True(IsAllZero(next.Buffer));
    }

    [Fact]
    public void Only_the_first_array_is_kept_when_a_capture_grows_twice()
    {
        var pool = new CaptureBufferPool();
        var recording = pool.Rent(256);
        var reservation = recording.Buffer;
        recording.Write(Filled(200, 0x11));
        recording.Write(Filled(100, 0x11));
        var firstGrowth = recording.Buffer;
        recording.Write(Filled(300, 0x22));
        var secondGrowth = recording.Buffer;

        Assert.Equal(512, firstGrowth.Length);
        Assert.Equal(1_024, secondGrowth.Length);
        Assert.True(IsAllZero(reservation));
        Assert.True(IsAllZero(firstGrowth));

        pool.Return(recording, retain: true);

        Assert.Equal(256, pool.RetainedBytes);
        Assert.True(IsAllZero(secondGrowth));
        var next = pool.Rent(256);
        Assert.Same(reservation, next.Buffer);
        Assert.True(IsAllZero(next.Buffer));
    }

    [Fact]
    public void A_grown_recording_returned_without_retain_keeps_nothing()
    {
        var pool = new CaptureBufferPool();
        var recording = pool.Rent(512);
        var reservation = recording.Buffer;
        recording.Write(Filled(500, 0x33));
        recording.Write(Filled(100, 0x33));
        var grown = recording.Buffer;

        pool.Return(recording, retain: false);

        Assert.Equal(0, pool.RetainedBytes);
        Assert.True(IsAllZero(reservation));
        Assert.True(IsAllZero(grown));
        Assert.NotSame(reservation, pool.Rent(512).Buffer);
    }

    [Fact]
    public void An_idle_release_during_a_long_capture_frees_nothing_and_the_reservation_comes_back_on_return()
    {
        var pool = new CaptureBufferPool();
        pool.Return(pool.Rent(1_024), retain: true);
        var recording = pool.Rent(1_024);
        var reservation = recording.Buffer;
        Assert.True(recording.Reused);
        recording.Write(Filled(1_500, 0x44));

        // The recording holds its reservation while it records, so the pool has nothing to release.
        Assert.Equal(0, pool.ReleaseRetained());

        pool.Return(recording, retain: true);

        Assert.Equal(1_024, pool.RetainedBytes);
        Assert.Same(reservation, pool.Rent(1_024).Buffer);
    }

    [Fact]
    public void A_write_after_a_grown_recording_returned_never_reaches_the_kept_reservation()
    {
        var pool = new CaptureBufferPool();
        var recording = pool.Rent(1_024);
        var reservation = recording.Buffer;
        recording.Write(Filled(1_000, 0x55));
        recording.Write(Filled(1_000, 0x55));
        pool.Return(recording, retain: true);

        // A capture callback that somehow ran after the join: it grows into a fresh array of its own.
        recording.Write(Filled(3_000, 0x7F));

        Assert.NotSame(reservation, recording.Buffer);
        Assert.True(IsAllZero(reservation));
        Assert.Equal(1_024, pool.RetainedBytes);

        // Returned again, the late recording has nothing that may be kept: its reservation was already taken.
        pool.Return(recording, retain: true);
        Assert.Equal(1_024, pool.RetainedBytes);
        Assert.Same(reservation, pool.Rent(1_024).Buffer);
    }

    [Fact]
    public void A_larger_kept_buffer_that_a_capture_outgrew_is_kept_again()
    {
        var pool = new CaptureBufferPool();
        pool.Return(pool.Rent(4_096), retain: true);
        var recording = pool.Rent(1_024);
        var kept = recording.Buffer;
        Assert.Equal(4_096, kept.Length);
        recording.Write(Filled(4_000, 0x66));
        recording.Write(Filled(1_000, 0x66));

        pool.Return(recording, retain: true);

        Assert.Equal(4_096, pool.RetainedBytes);
        Assert.Same(kept, pool.Rent(1_024).Buffer);
        Assert.True(IsAllZero(kept));
    }

    private static bool IsAllZero(byte[] array) => array.AsSpan().IndexOfAnyExcept((byte)0) < 0;

    private static byte[] Filled(int length, byte value)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, value);
        return bytes;
    }
}
