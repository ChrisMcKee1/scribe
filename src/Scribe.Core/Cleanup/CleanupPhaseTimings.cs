using System.Diagnostics;

namespace Scribe.Core.Cleanup;

/// <summary>
/// Where one cleanup operation's time went, in numbers only: carried by the operation's <see cref="CleanupAdmission"/> so
/// the credentials and the hand-off can add to it from inside the SDK's pipeline, and read back per attempt for the log.
/// Nothing here holds text, a host, a token or an exception; every update is an interlocked add, so the permission gate
/// and the credential path stay lock-free and allocation-free.
/// </summary>
/// <remarks>
/// Every phase is counted as well as timed, and a phase that ended without finishing is counted apart, so the log can tell
/// a phase that never ran (count 0) from one that finished in no time (count 1, 0 ms) and from one that was cut short (the
/// unfinished count): a dictation that waited at the Azure CLI gate and was cancelled there shows that wait, not 0 ms.
/// </remarks>
/// <param name="capturePhases">
/// <see cref="Scribe.Core.Diagnostics.PerfFlags.CleanupPhaseTelemetry"/>: time the send path too (response headers and the
/// last body byte read), which wraps the response content. Off, the transport path is exactly today's.
/// </param>
/// <param name="selection">From the operation's entry to its admission: the service gate, prompt and agent selection.</param>
internal sealed class CleanupPhaseTimings(bool capturePhases, TimeSpan selection)
{
    private long _gateTicks;
    private long _gateWaits;
    private long _gateUnadmitted;
    private long _tokenTicks;
    private long _tokenCalls;
    private long _tokenUnfinished;
    private long _sharedTicks;
    private long _sharedWaits;
    private long _sharedUnreceived;
    private long _headersTicks;
    private long _responses;
    private long _bodyTicks;
    private long _bodies;
    private long _emptyBodies;
    private long _unfinishedBodies;

    public bool CapturePhases { get; } = capturePhases;

    public TimeSpan Selection { get; } = selection;

    /// <summary>One wait for the Azure CLI gate, recorded when it ended: by admission, or before it (cancellation).</summary>
    public void AddGateWait(TimeSpan wait, bool admitted)
    {
        Interlocked.Add(ref _gateTicks, wait.Ticks);
        Interlocked.Increment(ref _gateWaits);
        if (!admitted)
        {
            Interlocked.Increment(ref _gateUnadmitted);
        }
    }

    /// <summary>One Azure CLI token call, recorded when it ended: with a token, or by a failure or cancellation.</summary>
    public void AddTokenCall(TimeSpan call, bool finished)
    {
        Interlocked.Add(ref _tokenTicks, call.Ticks);
        Interlocked.Increment(ref _tokenCalls);
        if (!finished)
        {
            Interlocked.Increment(ref _tokenUnfinished);
        }
    }

    /// <summary>
    /// One wait for a token another request was already acquiring (<see cref="CachingCliTokenCredential"/>), recorded when
    /// it ended: with the token, or before it (this request's cancellation, or the acquisition's failure).
    /// </summary>
    public void AddSharedTokenWait(TimeSpan wait, bool received)
    {
        Interlocked.Add(ref _sharedTicks, wait.Ticks);
        Interlocked.Increment(ref _sharedWaits);
        if (!received)
        {
            Interlocked.Increment(ref _sharedUnreceived);
        }
    }

    /// <summary>One HTTP attempt's hand-off to response headers.</summary>
    public void AddHeaders(TimeSpan elapsed)
    {
        Interlocked.Add(ref _headersTicks, elapsed.Ticks);
        Interlocked.Increment(ref _responses);
    }

    /// <summary>One response body read to its end, timed from the hand-off to the read that returned its last bytes.</summary>
    public void AddBody(TimeSpan lastBytesRead)
    {
        Interlocked.Add(ref _bodyTicks, lastBytesRead.Ticks);
        Interlocked.Increment(ref _bodies);
    }

    /// <summary>One response body that ended with no bytes at all: it has no last byte to time.</summary>
    public void AddEmptyBody() => Interlocked.Increment(ref _emptyBodies);

    /// <summary>One response body released before its end was read: its last byte was never observed.</summary>
    public void AddUnfinishedBody() => Interlocked.Increment(ref _unfinishedBodies);

    public Snapshot Read() => new(
        Interlocked.Read(ref _gateTicks),
        Interlocked.Read(ref _gateWaits),
        Interlocked.Read(ref _gateUnadmitted),
        Interlocked.Read(ref _tokenTicks),
        Interlocked.Read(ref _tokenCalls),
        Interlocked.Read(ref _tokenUnfinished),
        Interlocked.Read(ref _sharedTicks),
        Interlocked.Read(ref _sharedWaits),
        Interlocked.Read(ref _sharedUnreceived),
        Interlocked.Read(ref _headersTicks),
        Interlocked.Read(ref _responses),
        Interlocked.Read(ref _bodyTicks),
        Interlocked.Read(ref _bodies),
        Interlocked.Read(ref _emptyBodies),
        Interlocked.Read(ref _unfinishedBodies));

    /// <summary>The counters at one moment; subtract two to get one attempt's share.</summary>
    public readonly record struct Snapshot(
        long GateTicks,
        long GateWaits,
        long GateUnadmitted,
        long TokenTicks,
        long TokenCalls,
        long TokenUnfinished,
        long SharedTicks,
        long SharedWaits,
        long SharedUnreceived,
        long HeadersTicks,
        long Responses,
        long BodyTicks,
        long Bodies,
        long EmptyBodies,
        long UnfinishedBodies)
    {
        public Snapshot Since(Snapshot earlier) => new(
            GateTicks - earlier.GateTicks,
            GateWaits - earlier.GateWaits,
            GateUnadmitted - earlier.GateUnadmitted,
            TokenTicks - earlier.TokenTicks,
            TokenCalls - earlier.TokenCalls,
            TokenUnfinished - earlier.TokenUnfinished,
            SharedTicks - earlier.SharedTicks,
            SharedWaits - earlier.SharedWaits,
            SharedUnreceived - earlier.SharedUnreceived,
            HeadersTicks - earlier.HeadersTicks,
            Responses - earlier.Responses,
            BodyTicks - earlier.BodyTicks,
            Bodies - earlier.Bodies,
            EmptyBodies - earlier.EmptyBodies,
            UnfinishedBodies - earlier.UnfinishedBodies);
    }

    /// <summary>
    /// The response content as the transport handed it over, read through unchanged, noting when its last bytes were read.
    /// Buffers nothing and keeps no byte: every read, copy and disposal goes straight to the original content.
    /// </summary>
    /// <param name="timestamp">The clock, <see cref="Stopwatch.GetTimestamp"/> when null; a test's own otherwise.</param>
    internal sealed class TimedContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly CleanupPhaseTimings _timings;
        private readonly long _started;
        private readonly Func<long> _timestamp;

        public TimedContent(HttpContent inner, CleanupPhaseTimings timings, long started, Func<long>? timestamp = null)
        {
            _inner = inner;
            _timings = timings;
            _started = started;
            _timestamp = timestamp ?? Stopwatch.GetTimestamp;
            foreach (var header in inner.Headers)
            {
                Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            new TimedStream(
                await _inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), _timings, _started, _timestamp);

        protected override Task<Stream> CreateContentReadStreamAsync() => CreateContentReadStreamAsync(CancellationToken.None);

        protected override Stream CreateContentReadStream(CancellationToken cancellationToken) =>
            new TimedStream(_inner.ReadAsStream(cancellationToken), _timings, _started, _timestamp);

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override void SerializeToStream(
            Stream stream, System.Net.TransportContext? context, CancellationToken cancellationToken)
        {
            using var source = CreateContentReadStream(cancellationToken);
            source.CopyTo(stream);
        }

        protected override async Task SerializeToStreamAsync(
            Stream stream, System.Net.TransportContext? context, CancellationToken cancellationToken)
        {
            await using var source = await CreateContentReadStreamAsync(cancellationToken).ConfigureAwait(false);
            await source.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _inner.Headers.ContentLength ?? 0;
            return _inner.Headers.ContentLength is not null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    // The body's end is a read that returns nothing for a buffer with room in it: an empty request says nothing about the
    // end (a stream may answer it with zero at any time), and the time reported is that of the last read that returned
    // bytes, so a consumer that waits before asking for the end does not add its wait. A body released before its end was
    // read is counted as unfinished, and one that ended with no bytes as empty; neither is given a time.
    private sealed class TimedStream(Stream inner, CleanupPhaseTimings timings, long started, Func<long> timestamp) : Stream
    {
        private long _lastBytesRead = -1;
        private int _reported;

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Note(inner.Read(buffer, offset, count), count);

        public override int Read(Span<byte> buffer) => Note(inner.Read(buffer), buffer.Length);

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Note(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false), count);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Note(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false), buffer.Length);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Report(ended: false);
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            Report(ended: false);
            await inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }

        private int Note(int read, int requested)
        {
            if (read > 0)
            {
                _lastBytesRead = timestamp();
            }
            else if (requested > 0)
            {
                Report(ended: true);
            }

            return read;
        }

        private void Report(bool ended)
        {
            if (Interlocked.Exchange(ref _reported, 1) != 0)
            {
                return;
            }

            if (!ended)
            {
                timings.AddUnfinishedBody();
            }
            else if (_lastBytesRead < 0)
            {
                timings.AddEmptyBody();
            }
            else
            {
                timings.AddBody(Stopwatch.GetElapsedTime(started, _lastBytesRead));
            }
        }
    }
}