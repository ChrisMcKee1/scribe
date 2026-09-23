using System.Buffers;
using System.Numerics;
using NAudio.Wave;
using Scribe.Core.Audio;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// Pins the capture working-buffer contract: one buffer reused across captures instead of a fresh
/// 30-second large-object-heap reservation per press, zeroed before it is kept (a retained buffer must
/// never hold the last dictation's audio), dropped instead of kept when a capture outgrew it, and
/// releasable on demand from any thread without ever touching a buffer a capture is using. The
/// converted capture must be an owned copy, because history persistence holds
/// <see cref="Scribe.Core.Models.CapturedAudio"/> asynchronously while the next capture writes
/// into the same buffer.
/// </summary>
public sealed class CaptureBufferPoolTests
{
    private static readonly WaveFormat StereoFloat48k = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);

    [Fact]
    public void The_default_reservation_is_thirty_seconds_of_the_device_format_clamped()
    {
        Assert.Equal(11_520_000, AudioCaptureService.ReservationBytes(StereoFloat48k));
        Assert.Equal(64 * 1024, AudioCaptureService.ReservationBytes(new WaveFormat(1_000, 8, 1)));
        Assert.Equal(
            32 * 1024 * 1024,
            AudioCaptureService.ReservationBytes(WaveFormat.CreateIeeeFloatWaveFormat(192_000, 8)));
    }

    [Fact]
    public void A_returned_buffer_is_reused_by_the_next_capture_and_arrives_zeroed()
    {
        var pool = new CaptureBufferPool();
        var first = pool.Rent(4096);
        first.Write(Filled(1000, 0x5A));
        var array = first.Buffer;

        pool.Return(first, retain: true);
        var second = pool.Rent(4096);

        Assert.False(first.Reused);
        Assert.True(second.Reused);
        Assert.Same(array, second.Buffer);
        Assert.Equal(0, second.Length);
        Assert.All(second.Buffer, value => Assert.Equal(0, value));
    }

    [Fact]
    public void Returning_zeroes_the_audio_before_the_buffer_is_kept()
    {
        var pool = new CaptureBufferPool();
        var recording = pool.Rent(2048);
        recording.Write(Filled(2048, 0x7F));
        var array = recording.Buffer;

        pool.Return(recording, retain: true);

        Assert.Equal(2048, pool.RetainedBytes);
        Assert.All(array, value => Assert.Equal(0, value));
    }

    [Fact]
    public void A_recording_that_outgrew_its_reservation_is_dropped_and_its_old_array_zeroed()
    {
        var pool = new CaptureBufferPool();
        var recording = pool.Rent(1024);
        var original = recording.Buffer;
        recording.Write(Filled(1000, 0x11));
        recording.Write(Filled(1000, 0x22));

        Assert.True(recording.Grew);
        Assert.NotSame(original, recording.Buffer);
        Assert.All(original, value => Assert.Equal(0, value));
        Assert.Equal(2000, recording.Length);
        Assert.Equal(0x11, recording.Written[999]);
        Assert.Equal(0x22, recording.Written[1000]);

        var grown = recording.Buffer;
        pool.Return(recording, retain: true);

        Assert.Equal(0, pool.RetainedBytes);
        Assert.All(grown, value => Assert.Equal(0, value));
        Assert.NotSame(grown, pool.Rent(1024).Buffer);
    }

    [Fact]
    public void A_recording_returned_without_retain_is_zeroed_but_not_kept()
    {
        var pool = new CaptureBufferPool();
        var recording = pool.Rent(512);
        recording.Write(Filled(512, 0x33));
        var array = recording.Buffer;

        pool.Return(recording, retain: false);

        Assert.Equal(0, pool.RetainedBytes);
        Assert.All(array, value => Assert.Equal(0, value));
    }

    [Fact]
    public void Release_drops_the_retained_buffer_and_reports_its_size()
    {
        var pool = new CaptureBufferPool();
        var recording = pool.Rent(8192);
        var array = recording.Buffer;
        pool.Return(recording, retain: true);

        Assert.Equal(8192, pool.ReleaseRetained());
        Assert.Equal(0, pool.RetainedBytes);
        Assert.Equal(0, pool.ReleaseRetained());
        Assert.NotSame(array, pool.Rent(8192).Buffer);
    }

    [Fact]
    public void A_retained_buffer_too_small_for_a_higher_rate_device_is_replaced()
    {
        var pool = new CaptureBufferPool();
        pool.Return(pool.Rent(1024), retain: true);

        var larger = pool.Rent(4096);

        Assert.False(larger.Reused);
        Assert.Equal(4096, larger.Buffer.Length);
        Assert.Equal(0, pool.RetainedBytes);
    }

    [Fact]
    public void A_larger_retained_buffer_serves_a_lower_rate_device()
    {
        var pool = new CaptureBufferPool();
        var first = pool.Rent(4096);
        var array = first.Buffer;
        pool.Return(first, retain: true);

        var smaller = pool.Rent(1024);

        Assert.True(smaller.Reused);
        Assert.Same(array, smaller.Buffer);
    }

    [Fact]
    public void Overlapping_rentals_never_share_an_array()
    {
        var pool = new CaptureBufferPool();
        pool.Return(pool.Rent(1024), retain: true);

        var first = pool.Rent(1024);
        var second = pool.Rent(1024);

        Assert.NotSame(first.Buffer, second.Buffer);

        // Only one buffer is ever kept, whichever comes back first.
        var firstArray = first.Buffer;
        pool.Return(first, retain: true);
        pool.Return(second, retain: true);
        Assert.Equal(1024, pool.RetainedBytes);
        Assert.Same(firstArray, pool.Rent(1024).Buffer);
    }

    [Fact]
    public void Converted_audio_is_an_owned_copy_that_survives_the_next_capture_reusing_the_buffer()
    {
        var pool = new CaptureBufferPool();
        var reservation = AudioCaptureService.ReservationBytes(StereoFloat48k);

        var first = pool.Rent(reservation);
        AppendTone(first, seconds: 1, amplitude: 0.5f);
        CaptureSignalReport? firstSignal = null;
        var firstAudio = AudioCaptureService.ConvertCapture(first, StereoFloat48k, report => firstSignal = report);
        var snapshot = (float[])firstAudio.Samples.Clone();
        var firstArray = first.Buffer;
        pool.Return(first, retain: true);

        var second = pool.Rent(reservation);
        Assert.Same(firstArray, second.Buffer);
        AppendTone(second, seconds: 1, amplitude: 0.1f);
        var secondAudio = AudioCaptureService.ConvertCapture(second, StereoFloat48k, _ => { });
        pool.Return(second, retain: true);

        Assert.Equal(16_000, firstAudio.SampleRate);
        Assert.Equal(16_000, firstAudio.Samples.Length);
        Assert.Equal(snapshot, firstAudio.Samples);
        Assert.NotSame(firstAudio.Samples, secondAudio.Samples);
        Assert.NotNull(firstSignal);
        Assert.True(firstSignal.Peak > 0.4f);
        Assert.Equal(2, firstSignal.Channels);
    }

    [Fact]
    public void Appending_a_chunk_stores_it_and_returns_its_peak()
    {
        var pool = new CaptureBufferPool();
        var recording = pool.Rent(1024);
        var chunk = new byte[8 * sizeof(float)];
        Buffer.BlockCopy(new[] { 0.1f, -0.2f, 0.3f, -0.9f, 0f, 0f, 0f, 0f }, 0, chunk, 0, chunk.Length);

        var peak = AudioCaptureService.AppendChunk(recording, chunk, StereoFloat48k);

        Assert.Equal(0.9f, peak, 3);
        Assert.Equal(chunk.Length, recording.Length);
        Assert.Equal(chunk, recording.Written.ToArray());
    }

    [Fact]
    public void An_empty_recording_converts_to_empty_audio()
    {
        var pool = new CaptureBufferPool();
        var recording = pool.Rent(1024);

        var audio = AudioCaptureService.ConvertCapture(recording, StereoFloat48k, _ => { });

        Assert.True(audio.IsEmpty);
    }

    [Fact]
    public void The_signal_is_reported_before_resampling_so_a_failed_conversion_still_describes_it()
    {
        // Stop publishes the signal report before resampling, as it always has; a device format the
        // resampler rejects must still leave the capture's measured shape behind for the log.
        var pool = new CaptureBufferPool();
        var recording = pool.Rent(1024);
        recording.Write(new byte[64]);
        var unsupported = WaveFormat.CreateCustomFormat(WaveFormatEncoding.Adpcm, 8_000, 1, 4_000, 1, 4);
        CaptureSignalReport? reported = null;

        Assert.ThrowsAny<Exception>(() =>
            AudioCaptureService.ConvertCapture(recording, unsupported, report => reported = report));
        Assert.NotNull(reported);
        Assert.Equal(1, reported.Channels);
    }

    [Fact]
    public void The_capture_service_implements_the_buffer_release_instead_of_inheriting_the_no_op()
    {
        // The interface method has a default body returning 0 so other implementations stay
        // source compatible; the real service must override it or an idle release frees nothing.
        // Checked through the interface map because constructing the service opens a COM
        // device enumerator.
        var map = typeof(AudioCaptureService).GetInterfaceMap(typeof(IAudioCaptureService));
        var index = Array.FindIndex(
            map.InterfaceMethods,
            method => method.Name == nameof(IAudioCaptureService.ReleaseRetainedBuffers));

        Assert.True(index >= 0);
        Assert.Equal(typeof(AudioCaptureService), map.TargetMethods[index].DeclaringType);
    }

    [Fact]
    public void ReadAll_returns_an_owned_copy_and_wipes_every_scratch_array_before_it_goes_back()
    {
        // 70,000 samples outgrow the first 16,384-sample scratch array several times.
        var input = Enumerable.Range(0, 70_000).Select(index => 0.25f + (index % 7)).ToArray();
        var pool = new RecordingPool();

        var output = AudioCaptureService.ReadAll(new ArraySource(input), pool);

        Assert.Equal(input, output);
        Assert.True(pool.Returned.Count >= 3);
        Assert.All(pool.Returned, array => Assert.DoesNotContain(array, sample => sample != 0f));
        Assert.All(pool.Returned, array => Assert.NotSame(array, output));
        Assert.Equal(pool.Rented.Count, pool.Returned.Count);
    }

    [Fact]
    public void ReadAll_wipes_its_scratch_even_when_a_read_throws_after_writing()
    {
        var pool = new RecordingPool();
        var source = new ArraySource(Enumerable.Repeat(0.5f, 1_000).ToArray()) { ThrowAfterWriting = true };

        Assert.Throws<InvalidOperationException>(() => AudioCaptureService.ReadAll(source, pool));

        var returned = Assert.Single(pool.Returned);
        Assert.DoesNotContain(returned, sample => sample != 0f);
    }

    // History persistence keeps the CapturedAudio and commits it asynchronously, after the next
    // capture may already be writing into the same working buffer and reading through the same
    // pooled scratch. The tests below pin that nothing handed out can change afterwards.

    [Fact]
    public void ReadAll_results_through_the_shared_pool_are_owned_and_a_later_read_never_changes_them()
    {
        // The production pool. The second read on this thread rents the scratch the first one just
        // returned, so a result that aliased the scratch would be wiped and then overwritten.
        var first = AudioCaptureService.ReadAll(new ArraySource(Enumerable.Repeat(0.25f, 40_000).ToArray()));
        var second = AudioCaptureService.ReadAll(new ArraySource(Enumerable.Repeat(-0.5f, 40_000).ToArray()));

        Assert.NotSame(first, second);
        Assert.Equal(40_000, first.Length);
        Assert.All(first, sample => Assert.Equal(0.25f, sample));
        Assert.All(second, sample => Assert.Equal(-0.5f, sample));
    }

    [Fact]
    public void A_later_capture_into_the_reused_working_buffer_never_changes_audio_already_handed_out()
    {
        // 16 kHz mono float is the passthrough path: no downmix and no resampler, so ReadAll reads
        // straight from the capture bytes and any aliasing would show up sample for sample.
        var pool = new CaptureBufferPool();
        var reservation = AudioCaptureService.ReservationBytes(Mono16k);

        var first = pool.Rent(reservation);
        WriteConstant(first, 0.25f, frames: 16_000);
        var firstAudio = AudioCaptureService.ConvertCapture(first, Mono16k, _ => { });
        var buffer = first.Buffer;
        pool.Return(first, retain: true);

        // Returning zeroed the working buffer; the audio handed out must not have been zeroed with it.
        Assert.All(firstAudio.Samples, sample => Assert.Equal(0.25f, sample));

        var second = pool.Rent(reservation);
        Assert.Same(buffer, second.Buffer);
        WriteConstant(second, -0.75f, frames: 16_000);
        var secondAudio = AudioCaptureService.ConvertCapture(second, Mono16k, _ => { });

        Assert.Equal(16_000, firstAudio.Samples.Length);
        Assert.All(firstAudio.Samples, sample => Assert.Equal(0.25f, sample));
        Assert.All(secondAudio.Samples, sample => Assert.Equal(-0.75f, sample));
        Assert.NotSame(firstAudio.Samples, secondAudio.Samples);
    }

    // The idle release runs on its own thread, outside the controller lock, and can land at any
    // point of a capture. Every state change in the pool is a single locked section except the wipe
    // in Return, so these orderings are exhaustive: before a capture rents, while it records, while
    // it converts, between Return's wipe and its publish, and after it returned.

    [Fact]
    public void An_idle_release_that_lands_before_a_capture_rents_makes_it_reserve_a_fresh_buffer()
    {
        var pool = new CaptureBufferPool();
        var reservation = AudioCaptureService.ReservationBytes(Mono16k);
        var previous = pool.Rent(reservation);
        var dropped = previous.Buffer;
        pool.Return(previous, retain: true);

        Assert.Equal(reservation, OnAnotherThread(pool.ReleaseRetained));
        var recording = pool.Rent(reservation);

        Assert.False(recording.Reused);
        Assert.NotSame(dropped, recording.Buffer);
        Assert.Equal(0, pool.RetainedBytes);
    }

    [Fact]
    public void An_idle_release_that_lands_while_a_capture_records_leaves_that_capture_its_buffer()
    {
        var pool = new CaptureBufferPool();
        var reservation = AudioCaptureService.ReservationBytes(Mono16k);
        pool.Return(pool.Rent(reservation), retain: true);
        var recording = pool.Rent(reservation);
        var buffer = recording.Buffer;
        WriteConstant(recording, 0.5f, frames: 8_000);

        // Nothing is retained while the capture holds the buffer, so there is nothing to release.
        Assert.Equal(0, OnAnotherThread(pool.ReleaseRetained));

        WriteConstant(recording, 0.5f, frames: 8_000);
        Assert.Same(buffer, recording.Buffer);
        Assert.Equal(16_000 * sizeof(float), recording.Length);
        var audio = AudioCaptureService.ConvertCapture(recording, Mono16k, _ => { });
        Assert.All(audio.Samples, sample => Assert.Equal(0.5f, sample));

        // The capture happened after the idle point, so its buffer is kept again for the next one.
        pool.Return(recording, retain: true);
        Assert.Equal(reservation, pool.RetainedBytes);
    }

    [Fact]
    public void An_idle_release_that_lands_while_a_capture_converts_never_touches_the_audio()
    {
        var pool = new CaptureBufferPool();
        var reservation = AudioCaptureService.ReservationBytes(Mono16k);
        pool.Return(pool.Rent(reservation), retain: true);
        var recording = pool.Rent(reservation);
        WriteConstant(recording, 0.5f, frames: 16_000);
        long releasedDuringConversion = -1;

        // The signal callback runs inside the conversion, before ReadAll resamples the capture, so
        // the release on the other thread completes while this capture is mid-conversion.
        var audio = AudioCaptureService.ConvertCapture(
            recording,
            Mono16k,
            _ => releasedDuringConversion = OnAnotherThread(pool.ReleaseRetained));

        Assert.Equal(0, releasedDuringConversion);
        Assert.Equal(16_000, audio.Samples.Length);
        Assert.All(audio.Samples, sample => Assert.Equal(0.5f, sample));
    }

    [Fact]
    public void An_idle_release_from_inside_a_read_over_the_capture_buffer_never_touches_it()
    {
        // The real window: ReadAll pulling samples straight out of a rented recording's buffer, as
        // the conversion does for 16 kHz mono, while a release runs on another thread mid-read.
        var pool = new CaptureBufferPool();
        var reservation = AudioCaptureService.ReservationBytes(Mono16k);
        pool.Return(pool.Rent(reservation), retain: true);
        var recording = pool.Rent(reservation);
        Assert.True(recording.Reused);
        WriteConstant(recording, 0.5f, frames: 48_000);
        long released = -1;
        var source = new ReleasingSource(
            new RawSourceWaveStream(recording.Buffer, 0, recording.Length, Mono16k).ToSampleProvider(),
            releaseOnRead: 2,
            release: () => released = OnAnotherThread(pool.ReleaseRetained));

        var samples = AudioCaptureService.ReadAll(source);

        // Three seconds need several reads, so the release landed with part of the buffer consumed.
        Assert.True(source.Reads > 2);
        Assert.Equal(0, released);
        Assert.Equal(48_000, samples.Length);
        Assert.All(samples, sample => Assert.Equal(0.5f, sample));
    }

    [Fact]
    public void A_release_or_a_rental_between_the_wipe_and_the_publish_never_sees_a_half_returned_buffer()
    {
        var reservation = AudioCaptureService.ReservationBytes(Mono16k);
        CaptureBufferPool? pool = null;
        CaptureRecording? returning = null;
        byte[]? buffer = null;
        long releasedInWindow = -1;
        CaptureRecording? rentedInWindow = null;
        var windowZeroed = false;
        var windowDetached = false;
        pool = new CaptureBufferPool(afterWipe: () =>
        {
            // Return is paused here, outside the gate, after detaching and zeroing, before publishing.
            windowZeroed = IsAllZero(buffer!);
            windowDetached = !ReferenceEquals(buffer, returning!.Buffer);
            releasedInWindow = OnAnotherThread(pool!.ReleaseRetained);
            rentedInWindow = OnAnotherThread(() => pool!.Rent(reservation));
        });

        returning = pool.Rent(reservation);
        WriteConstant(returning, 0.5f, frames: 16_000);
        buffer = returning.Buffer;
        pool.Return(returning, retain: true);

        Assert.True(windowZeroed);
        Assert.True(windowDetached);
        Assert.Equal(0, releasedInWindow);
        Assert.NotNull(rentedInWindow);
        Assert.False(rentedInWindow.Reused);
        Assert.NotSame(buffer, rentedInWindow.Buffer);

        // The paused Return finished afterwards and published its zeroed buffer.
        Assert.Equal(reservation, pool.RetainedBytes);
        Assert.Same(buffer, pool.Rent(reservation).Buffer);
        Assert.True(IsAllZero(buffer));
    }

    [Fact]
    public void A_write_that_arrives_after_return_lands_in_a_fresh_array_never_in_the_kept_buffer()
    {
        var pool = new CaptureBufferPool();
        var recording = pool.Rent(4096);
        recording.Write(Filled(1000, 0x11));
        var buffer = recording.Buffer;
        pool.Return(recording, retain: true);

        // A capture callback that somehow ran after the join: Return already took the array away.
        recording.Write(Filled(512, 0x7F));

        Assert.NotSame(buffer, recording.Buffer);
        Assert.Equal(512, recording.Length);
        Assert.True(IsAllZero(buffer));
        Assert.Equal(4096, pool.RetainedBytes);
        var next = pool.Rent(4096);
        Assert.Same(buffer, next.Buffer);
        Assert.True(IsAllZero(next.Buffer));
    }

    [Fact]
    public void An_idle_release_that_lands_after_a_capture_returned_drops_the_zeroed_buffer()
    {
        var pool = new CaptureBufferPool();
        var reservation = AudioCaptureService.ReservationBytes(Mono16k);
        var recording = pool.Rent(reservation);
        WriteConstant(recording, 0.5f, frames: 16_000);
        var buffer = recording.Buffer;
        pool.Return(recording, retain: true);

        Assert.Equal(reservation, OnAnotherThread(pool.ReleaseRetained));
        Assert.Equal(0, pool.RetainedBytes);
        Assert.True(IsAllZero(buffer));
        Assert.NotSame(buffer, pool.Rent(reservation).Buffer);
    }

    private static readonly WaveFormat Mono16k = WaveFormat.CreateIeeeFloatWaveFormat(16_000, 1);

    private static bool IsAllZero(byte[] array) => array.AsSpan().IndexOfAnyExcept((byte)0) < 0;

    /// <summary>
    /// Runs <paramref name="action"/> on a dedicated thread and waits for it, so it happens at
    /// exactly the point the calling thread has reached. The timeout only turns a hang into a
    /// failure; it never decides an ordering.
    /// </summary>
    private static T OnAnotherThread<T>(Func<T> action)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
        };

        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "the other thread did not finish");
        if (failure is not null)
        {
            throw new InvalidOperationException("The other thread failed.", failure);
        }

        return result;
    }

    private static void WriteConstant(CaptureRecording recording, float value, int frames)
    {
        var chunk = new float[160];
        Array.Fill(chunk, value);
        var bytes = new byte[chunk.Length * sizeof(float)];
        Buffer.BlockCopy(chunk, 0, bytes, 0, bytes.Length);
        for (var written = 0; written < frames; written += chunk.Length)
        {
            AudioCaptureService.AppendChunk(recording, bytes, Mono16k);
        }
    }

    private static byte[] Filled(int length, byte value)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, value);
        return bytes;
    }

    private static void AppendTone(CaptureRecording recording, int seconds, float amplitude)
    {
        // 10 ms chunks, the packet size shared-mode WASAPI typically delivers.
        const int framesPerChunk = 480;
        var chunk = new float[framesPerChunk * 2];
        var bytes = new byte[chunk.Length * sizeof(float)];
        for (var frame = 0; frame < 48_000 * seconds; frame += framesPerChunk)
        {
            for (var index = 0; index < framesPerChunk; index++)
            {
                var sample = amplitude * MathF.Sin(2 * MathF.PI * 440 * (frame + index) / 48_000f);
                chunk[index * 2] = sample;
                chunk[(index * 2) + 1] = sample;
            }

            Buffer.BlockCopy(chunk, 0, bytes, 0, bytes.Length);
            AudioCaptureService.AppendChunk(recording, bytes, StereoFloat48k);
        }
    }

    /// <summary>Hands out fresh arrays and keeps every returned one for inspection.</summary>
    private sealed class RecordingPool : ArrayPool<float>
    {
        public List<float[]> Rented { get; } = [];

        public List<float[]> Returned { get; } = [];

        public override float[] Rent(int minimumLength)
        {
            var array = new float[Math.Max(16_384, (int)BitOperations.RoundUpToPowerOf2((uint)minimumLength))];
            Rented.Add(array);
            return array;
        }

        public override void Return(float[] array, bool clearArray = false) => Returned.Add(array);
    }

    /// <summary>Passes reads through, running the release callback on the given read.</summary>
    private sealed class ReleasingSource(ISampleProvider inner, int releaseOnRead, Action release) : ISampleProvider
    {
        public int Reads { get; private set; }

        public WaveFormat WaveFormat => inner.WaveFormat;

        public int Read(Span<float> buffer)
        {
            if (++Reads == releaseOnRead)
            {
                release();
            }

            return inner.Read(buffer);
        }
    }

    /// <summary>A 16 kHz mono source; optionally writes into the buffer and then throws.</summary>
    private sealed class ArraySource(float[] samples) : ISampleProvider
    {
        private int _position;

        public bool ThrowAfterWriting { get; init; }

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(16_000, 1);

        public int Read(Span<float> buffer)
        {
            var available = Math.Min(buffer.Length, samples.Length - _position);
            samples.AsSpan(_position, available).CopyTo(buffer);
            _position += available;
            if (ThrowAfterWriting)
            {
                throw new InvalidOperationException("The source failed mid-read.");
            }

            return available;
        }
    }
}
