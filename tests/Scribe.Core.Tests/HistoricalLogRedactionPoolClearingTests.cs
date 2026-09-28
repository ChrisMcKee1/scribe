using System.Buffers;
using System.Text;
using Scribe.Core.Diagnostics;

namespace Scribe.Core.Tests;

/// <summary>
/// The historical scrub's chunk holds raw log bytes, so it has to go back to its pool cleared however the copy ends: when it
/// completes, when the source fails partway through a read, and when the destination fails while a line is written, with
/// whole-chunk and short reads and a line across the chunk boundary. Every case hands the copy a pool the test controls and
/// reads what came back to it.
/// </summary>
public sealed class HistoricalLogRedactionPoolClearingTests
{
    // The copy's chunk size; every case checks that it is what the copy asked its pool for.
    private const int ChunkBytes = 64 * 1024;

    private const string Transcript = "please send the quarterly numbers to Dana before Friday";

    private const string OrdinaryLine = "14:05:08.000 [Information] DictationController: dictation #7 start trigger=Standard";

    private const string FirstLine = "the first line";

    private const string LineAcrossTheBoundary = "the line across the chunk boundary";

    private static readonly string LongTranscript = string.Join(' ', Enumerable.Repeat(Transcript, 30));

    private static readonly (byte[] Bytes, int SpanningStart, int SpanningEnd, int Transcripts) Log = BuildLog();

    // A whole chunk at a time, then reads shorter than the line across the boundary, and reads shorter than any line.
    private static readonly int[] ReadLimits = [int.MaxValue, 1_000, 7];

    public static TheoryData<int> Reads
    {
        get
        {
            var data = new TheoryData<int>();
            foreach (var readLimit in ReadLimits)
            {
                data.Add(readLimit);
            }

            return data;
        }
    }

    public static TheoryData<int, int> SourceFailures
    {
        get
        {
            // Neither offset is a multiple of any read limit, so the failing read always copies part of itself into the
            // chunk first: one offset inside the first chunk, one inside the line across the boundary, past the boundary.
            var data = new TheoryData<int, int>();
            foreach (var readLimit in ReadLimits)
            {
                data.Add(readLimit, 1_234);
                data.Add(readLimit, ChunkBytes + 321);
            }

            return data;
        }
    }

    public static TheoryData<int, string> DestinationFailures
    {
        get
        {
            var data = new TheoryData<int, string>();
            foreach (var readLimit in ReadLimits)
            {
                data.Add(readLimit, FirstLine);
                data.Add(readLimit, LineAcrossTheBoundary);
            }

            return data;
        }
    }

    [Fact]
    public void The_log_has_a_line_across_the_chunk_boundary() =>
        Assert.InRange(ChunkBytes, Log.SpanningStart + 1, Log.SpanningEnd - 1);

    [Theory]
    [MemberData(nameof(Reads))]
    public void A_copy_that_completes_gives_its_chunk_back_cleared(int readLimit)
    {
        var pool = new RecordingPool();
        using var source = new ScriptedSource(Log.Bytes, readLimit);
        using var destination = new MemoryStream();

        var counts = HistoricalLogRedaction.CopyRedacted(source, destination, pool);

        using var expected = new MemoryStream();
        using (var whole = new MemoryStream(Log.Bytes))
        {
            Assert.Equal(HistoricalLogRedaction.CopyRedacted(whole, expected), counts);
        }

        Assert.Equal(expected.ToArray(), destination.ToArray());
        Assert.Equal(Log.Transcripts, counts.Transcripts);
        Assert.DoesNotContain("quarterly", Encoding.ASCII.GetString(destination.ToArray()), StringComparison.Ordinal);
        pool.AssertTheChunkCameBackCleared(source);
    }

    [Theory]
    [MemberData(nameof(SourceFailures))]
    public void A_source_that_fails_after_filling_part_of_the_chunk_gets_it_back_cleared(int readLimit, int failAt)
    {
        var pool = new RecordingPool();
        using var source = new ScriptedSource(Log.Bytes, readLimit, failAt);

        var thrown = Assert.Throws<IOException>(() => HistoricalLogRedaction.CopyRedacted(source, Stream.Null, pool));

        Assert.Same(source.Failure, thrown);
        Assert.True(source.FailingReadCopiedBytes, "The failing read copied nothing into the chunk before it threw.");
        pool.AssertTheChunkCameBackCleared(source);
    }

    [Theory]
    [MemberData(nameof(DestinationFailures))]
    public void A_destination_that_fails_while_a_line_is_written_gets_the_chunk_back_cleared(int readLimit, string line)
    {
        var pool = new RecordingPool();
        using var source = new ScriptedSource(Log.Bytes, readLimit);
        using var destination = new FailingDestination(line == FirstLine ? 20 : OffsetInsideTheLineAcrossTheBoundary());

        var thrown = Assert.Throws<IOException>(() => HistoricalLogRedaction.CopyRedacted(source, destination, pool));

        Assert.Same(destination.Failure, thrown);
        pool.AssertTheChunkCameBackCleared(source);
    }

    [Fact]
    public void The_public_copy_takes_its_chunk_from_the_shared_pool()
    {
        // The overload the cases above drive is the one production runs, handed the shared pool, and nothing else in the file
        // rents from that pool or returns to it.
        var code = File.ReadAllText(SourcePath());

        Assert.Contains("CopyRedacted(source, destination, ArrayPool<byte>.Shared);", code, StringComparison.Ordinal);
        Assert.Equal(2, code.Split("ArrayPool<byte>.Shared").Length);
    }

    // Whole lines to just short of the chunk boundary, one long decode line across it, more lines, and a last line with no
    // terminator. ASCII, so offsets in the text are offsets in the bytes.
    private static (byte[] Bytes, int SpanningStart, int SpanningEnd, int Transcripts) BuildLog()
    {
        var text = new StringBuilder();
        var transcripts = 0;
        for (var i = 0; text.Length < ChunkBytes - 600; i++)
        {
            text.Append(i % 2 == 0 ? OrdinaryLine : DecodeLine(Transcript, ref transcripts)).Append("\r\n");
        }

        var spanningStart = text.Length;
        text.Append(DecodeLine(LongTranscript, ref transcripts)).Append("\r\n");
        var spanningEnd = text.Length;
        for (var i = 0; i < 8; i++)
        {
            text.Append(i % 2 == 0 ? DecodeLine(Transcript, ref transcripts) : OrdinaryLine).Append('\n');
        }

        text.Append(DecodeLine(Transcript, ref transcripts));
        return (Encoding.ASCII.GetBytes(text.ToString()), spanningStart, spanningEnd, transcripts);
    }

    private static string DecodeLine(string transcript, ref int count)
    {
        count++;
        return $"14:05:10.456 [Debug] TranscriptionService: Decoded 4520 ms of audio in 210 ms (RTF 0.05): \"{transcript}\"";
    }

    // Inside the line across the boundary as the copy writes it, redacted.
    private static int OffsetInsideTheLineAcrossTheBoundary()
    {
        using var whole = new MemoryStream(Log.Bytes);
        using var output = new MemoryStream();
        HistoricalLogRedaction.CopyRedacted(whole, output);
        var placeholder = Encoding.ASCII.GetBytes(HistoricalLogRedaction.TranscriptPlaceholder(LongTranscript.Length));
        var at = output.ToArray().AsSpan().IndexOf(placeholder);
        Assert.True(at > 0, "The line across the boundary was not redacted.");
        return at + 5;
    }

    private static string SourcePath()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return Path.Combine(root.FullName, "src", "Scribe.Core", "Diagnostics", "HistoricalLogRedaction.cs");
    }

    // Hands out fresh arrays, so a byte that is not zero is one the copy put there, and records what comes back and how. A
    // returned array is cleared only when the caller asks, as the shared pool does it: the whole array.
    private sealed class RecordingPool : ArrayPool<byte>
    {
        private readonly List<(byte[] Array, int MinimumLength)> _rented = [];
        private readonly List<(byte[] Array, bool ClearArray, bool ZeroWhenReturned)> _returned = [];

        public override byte[] Rent(int minimumLength)
        {
            var array = new byte[minimumLength];
            _rented.Add((array, minimumLength));
            return array;
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            _returned.Add((array, clearArray, array.AsSpan().IndexOfAnyExcept((byte)0) < 0));
            if (clearArray)
            {
                Array.Clear(array);
            }
        }

        public void AssertTheChunkCameBackCleared(ScriptedSource source)
        {
            var (chunk, minimumLength) = Assert.Single(_rented);
            Assert.Equal(ChunkBytes, minimumLength);
            var (returned, clearArray, zeroWhenReturned) = Assert.Single(_returned);
            Assert.Same(chunk, returned);
            Assert.Contains(chunk, source.FilledBuffers);
            Assert.True(clearArray || zeroWhenReturned, "The chunk went back to its pool with log bytes in it.");
            Assert.True(chunk.AsSpan().IndexOfAnyExcept((byte)0) < 0, "Part of the chunk went back holding log bytes.");
        }
    }

    // Returns at most readLimit bytes a read. The read that would pass failAt copies the bytes before it into the caller's
    // buffer, then throws.
    private sealed class ScriptedSource(byte[] log, int readLimit, int failAt = int.MaxValue) : Stream
    {
        private int _position;

        public IOException Failure { get; } = new("The source failed partway through a read.");

        public List<byte[]> FilledBuffers { get; } = [];

        public bool FailingReadCopiedBytes { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var length = Math.Min(Math.Min(count, readLimit), log.Length - _position);
            var failing = _position + length > failAt;
            if (failing)
            {
                length = failAt - _position;
            }

            log.AsSpan(_position, length).CopyTo(buffer.AsSpan(offset, length));
            _position += length;
            if (length > 0 && !FilledBuffers.Contains(buffer))
            {
                FilledBuffers.Add(buffer);
            }

            if (failing)
            {
                FailingReadCopiedBytes = length > 0;
                throw Failure;
            }

            return length;
        }

        public override int Read(Span<byte> buffer) => throw new NotSupportedException("The copy reads into its pooled array.");

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // Throws from the write that would take the output past failAt, a line written only in part.
    private sealed class FailingDestination(long failAt) : Stream
    {
        private long _written;

        public IOException Failure { get; } = new("The destination failed while a line was written.");

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (_written + buffer.Length > failAt)
            {
                _written = failAt;
                throw Failure;
            }

            _written += buffer.Length;
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}