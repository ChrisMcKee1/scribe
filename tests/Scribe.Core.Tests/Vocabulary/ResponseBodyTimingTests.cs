using System.Diagnostics;
using System.Text;
using Scribe.Core.Cleanup;

namespace Scribe.Core.Tests.Vocabulary;

/// <summary>
/// PLAT-R-03: <see cref="CleanupPhaseTimings.TimedContent"/> times a response body to the last read that returned bytes,
/// published once the end is confirmed by a read with room in its buffer that returns nothing. An empty read request is no
/// evidence of the end, a consumer that waits before asking for the end adds nothing, an empty body is counted as empty and
/// a body released before its end as unfinished, and neither is given a time. The bytes, the headers and disposal pass
/// through unchanged. Every time here comes from a clock the test sets; nothing sleeps.
/// </summary>
public sealed class ResponseBodyTimingTests
{
    private static readonly long Ms = Stopwatch.Frequency / 1000;

    [Fact]
    public async Task An_empty_read_request_is_not_the_end_of_the_body()
    {
        var clock = new Clock();
        var timings = Timings();
        using var content = Timed(new StringContent("abcdef", Encoding.UTF8, "application/json"), timings, clock);
        await using var stream = await content.ReadAsStreamAsync();

        clock.Now = 5 * Ms;
        Assert.Equal(0, await stream.ReadAsync(Memory<byte>.Empty));
        Assert.Equal(0, stream.Read(Span<byte>.Empty));
        Assert.Equal(default, Body(timings)); // nothing ended, nothing measured

        clock.Now = 40 * Ms;
        using var body = new MemoryStream();
        await stream.CopyToAsync(body);

        Assert.Equal("abcdef", Encoding.UTF8.GetString(body.ToArray()));
        Assert.Equal((Ticks(40), 1L, 0L, 0L), Body(timings));
    }

    [Fact]
    public async Task The_time_is_that_of_the_last_read_that_returned_bytes_not_of_the_end()
    {
        var clock = new Clock();
        var timings = Timings();
        using var content = Timed(new ByteArrayContent([1, 2, 3]), timings, clock);
        await using var stream = await content.ReadAsStreamAsync();
        var buffer = new byte[3];

        clock.Now = 100 * Ms;
        Assert.Equal(3, await stream.ReadAsync(buffer));
        Assert.Equal(default, Body(timings)); // the end is not confirmed yet

        clock.Now = 350 * Ms; // the consumer waits before asking for the end
        Assert.Equal(0, await stream.ReadAsync(buffer));

        Assert.Equal((Ticks(100), 1L, 0L, 0L), Body(timings));
        Assert.Equal(0, await stream.ReadAsync(buffer)); // a second end is not counted again
        Assert.Equal((Ticks(100), 1L, 0L, 0L), Body(timings));
    }

    [Fact]
    public async Task Several_fragments_are_timed_to_the_last_one()
    {
        var clock = new Clock();
        var timings = Timings();
        var fragments = new FragmentingStream(Encoding.UTF8.GetBytes("abcdefgh"), fragment: 3, clock, stepMs: 10);
        using var content = Timed(new StreamContent(fragments), timings, clock);
        await using var stream = await content.ReadAsStreamAsync();

        var text = new StringBuilder();
        var buffer = new byte[16];
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            text.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }

        // Reads at 10, 20 and 30 ms return bytes; the end, read at 40 ms, reports 30.
        Assert.Equal("abcdefgh", text.ToString());
        Assert.Equal((Ticks(30), 1L, 0L, 0L), Body(timings));
    }

    [Fact]
    public async Task An_empty_body_is_counted_as_empty_and_given_no_time()
    {
        var clock = new Clock { Now = 70 * Ms };
        var timings = Timings();
        using var content = Timed(new ByteArrayContent([]), timings, clock);
        await using var stream = await content.ReadAsStreamAsync();

        Assert.Equal(0, await stream.ReadAsync(new byte[8]));

        Assert.Equal((0L, 0L, 1L, 0L), Body(timings));
    }

    [Fact]
    public async Task A_body_released_before_its_end_is_unfinished_and_given_no_time()
    {
        var clock = new Clock();
        var timings = Timings();
        using (var content = Timed(new ByteArrayContent([1, 2, 3, 4]), timings, clock))
        {
            var stream = await content.ReadAsStreamAsync();
            clock.Now = 20 * Ms;
            Assert.Equal(2, await stream.ReadAsync(new byte[2]));
            await stream.DisposeAsync();
        }

        Assert.Equal((0L, 0L, 0L, 1L), Body(timings));
    }

    [Fact]
    public async Task A_read_cancelled_part_way_leaves_the_body_unfinished_and_rethrows_the_cancellation()
    {
        var clock = new Clock();
        var timings = Timings();
        var stalled = new StallingStream([1, 2, 3]);
        using var content = Timed(new StreamContent(stalled), timings, clock);
        var stream = await content.ReadAsStreamAsync();
        using var cancel = new CancellationTokenSource();

        clock.Now = 10 * Ms;
        Assert.Equal(3, await stream.ReadAsync(new byte[3]));
        var reading = stream.ReadAsync(new byte[3], cancel.Token).AsTask();
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        stream.Dispose();

        Assert.Equal((0L, 0L, 0L, 1L), Body(timings));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Synchronous_reads_are_timed_the_same_way(bool synchronousContent)
    {
        var clock = new Clock();
        var timings = Timings();
        using var content = Timed(new ByteArrayContent([1, 2, 3, 4]), timings, clock);
        using var stream = synchronousContent ? content.ReadAsStream() : await content.ReadAsStreamAsync();

        clock.Now = 15 * Ms;
        Assert.Equal(2, stream.Read(new byte[2], 0, 2));
        clock.Now = 25 * Ms;
        Assert.Equal(2, stream.Read(new byte[4].AsSpan()));
        clock.Now = 90 * Ms;
        Assert.Equal(0, stream.Read(new byte[4], 0, 4));

        Assert.Equal((Ticks(25), 1L, 0L, 0L), Body(timings));
    }

    private static CleanupPhaseTimings Timings() => new(capturePhases: true, TimeSpan.Zero);

    private static CleanupPhaseTimings.TimedContent Timed(HttpContent inner, CleanupPhaseTimings timings, Clock clock) =>
        new(inner, timings, started: 0, () => clock.Now);

    private static long Ticks(int milliseconds) => Stopwatch.GetElapsedTime(0, milliseconds * Ms).Ticks;

    private static (long BodyTicks, long Bodies, long EmptyBodies, long UnfinishedBodies) Body(CleanupPhaseTimings timings)
    {
        var read = timings.Read();
        return (read.BodyTicks, read.Bodies, read.EmptyBodies, read.UnfinishedBodies);
    }

    private sealed class Clock
    {
        public long Now { get; set; }
    }

    // Returns at most `fragment` bytes per read, and moves the clock on by `stepMs` before each read, the end's included.
    // Every read overload lands in one place, so a read moves the clock once.
    private sealed class FragmentingStream(byte[] bytes, int fragment, Clock clock, int stepMs) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => bytes.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            clock.Now += stepMs * Ms;
            var read = Math.Min(Math.Min(buffer.Length, fragment), bytes.Length - _position);
            bytes.AsSpan(_position, read).CopyTo(buffer);
            _position += read;
            return read;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer.AsSpan(offset, count)));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // Hands over its bytes, then never ends: the next read waits until it is cancelled.
    private sealed class StallingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position < Length)
            {
                return Read(buffer.Span);
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}
