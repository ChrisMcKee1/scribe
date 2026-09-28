using System.Text;
using System.Text.RegularExpressions;

namespace Scribe.Core.Tests;

/// <summary>
/// The overlay's logger (Scribe.Overlay's <c>OverlayLog</c>, not referenced from here) writes a line that fits a
/// StreamWriter's 1,024-character buffer with one strict UTF-8 encode into an unbuffered FileStream, instead of through a
/// StreamWriter and the FileStream's own buffer; a longer line keeps the writer. It is under the logging mandate, so these
/// tests pin from its source that it keeps the shared file, the retries and the swallow, and the exact statements of that
/// path; and show, on this runtime, that those statements write the bytes the StreamWriter wrote and fail as it failed.
/// </summary>
public sealed class OverlayLogSingleEncodeTests : IDisposable
{
    private static readonly UTF8Encoding LineEncoding = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly byte[] NewLineBytes = LineEncoding.GetBytes(Environment.NewLine);
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("scribe-overlaylog-");

    public void Dispose() => _folder.Delete(recursive: true);

    // Each line with the failure both writes must end in. The theory's rows are indexes, not the lines: xUnit hands string
    // rows through UTF-8, which turns a lone surrogate into U+FFFD, and the failures would never be reached.
    private static readonly (string Line, string? Failure)[] Lines =
    [
        ("12:34:56.789 [Information] Overlay: OverlayWindow.ShowState exit shown=Listening (was Hidden)", null),
        ("12:34:56.789 [Information] Overlay: caf\u00e9 \u4e2d\u6587 \u00fcber", null),
        ("12:34:56.789 [Information] Overlay: pair \uD83D\uDE00 end", null),
        ("12:34:56.789 [Error] Overlay: failed: System.IO.IOException: first\r\nsecond\nthird", null),
        (string.Empty, null),
        (new string('s', 340), null),
        (new string('t', 341), null),
        (new string('u', 1024 - Environment.NewLine.Length), null),
        ("lone \uD800 high", "System.Text.EncoderFallbackException"),
        ("trailing high \uD800", "System.Text.EncoderFallbackException"),
        ("lone \uDC00 low", "System.Text.EncoderFallbackException"),
    ];

    // A seeded corpus on top of those, which the StreamWriter decides line by line (its bytes and its failure): lines that fit
    // its buffer, from empty to the whole 1,024 characters with the new line, of ASCII, two-, three- and four-byte characters,
    // controls and, in a quarter of them, lone surrogates.
    private static readonly string[] GeneratedLines = GenerateLines(seed: 20260928, count: 256);

    public static TheoryData<int> LinesThatFit() => [.. Enumerable.Range(0, Lines.Length + GeneratedLines.Length)];

    [Fact]
    public void OverlayLog_keeps_the_shared_file_the_retries_and_the_swallow()
    {
        var source = Source();

        Assert.Contains("var line = $\"{DateTime.Now:HH:mm:ss.fff} [{level}] Overlay: {message}\";", source, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(source, Regex.Escape("path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite")).Count);
        Assert.Contains("for (var attempt = 0; attempt < 12; attempt++)", source, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"catch \(IOException\)\s*\{\s*Thread\.Sleep\(15\);\s*\}"), source);
        Assert.Matches(new Regex(@"catch \(UnauthorizedAccessException\)\s*\{\s*Thread\.Sleep\(15\);\s*\}"), source);
        Assert.Matches(new Regex(@"catch\s*\{\s*// Never let logging disrupt the overlay\.\s*\}"), source);
        // The retry loop's lock, in AppendLine (16 spaces); the Path getter's lock sits at 12 and is not this one.
        Assert.Single(Regex.Matches(source, Regex.Escape("lock (Gate)\n                {")));
    }

    [Fact]
    public void OverlayLog_opens_the_file_before_it_encodes_and_keeps_the_writer_for_a_longer_line()
    {
        var source = Source();

        Assert.Contains(
            "LineEncoding = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);", source, StringComparison.Ordinal);
        Assert.Contains("NewLineBytes = LineEncoding.GetBytes(Environment.NewLine);", source, StringComparison.Ordinal);
        Assert.Contains("private const int WriterBufferChars = 1024;", source, StringComparison.Ordinal);
        Assert.Contains("if (line.Length + Environment.NewLine.Length <= WriterBufferChars)", source, StringComparison.Ordinal);

        string[] onePiece =
        [
            "FileShare.ReadWrite, bufferSize: 0);",
            "var bytes = new byte[LineEncoding.GetByteCount(line) + NewLineBytes.Length];",
            "NewLineBytes.CopyTo(bytes, LineEncoding.GetBytes(line, 0, line.Length, bytes, 0));",
            "stream.Write(bytes, 0, bytes.Length);",
            "using var writer = new StreamWriter(stream);",
            "writer.WriteLine(line);",
        ];
        var positions = onePiece.Select(statement => source.IndexOf(statement, StringComparison.Ordinal)).ToArray();
        Assert.DoesNotContain(-1, positions);
        Assert.Equal(positions.Order(), positions);
    }

    [Theory]
    [MemberData(nameof(LinesThatFit))]
    public void A_line_that_fits_is_written_as_the_stream_writer_wrote_it(int index)
    {
        var fixedLine = index < Lines.Length;
        var (line, failure) = fixedLine ? Lines[index] : (GeneratedLines[index - Lines.Length], null);
        Assert.True(line.Length + Environment.NewLine.Length <= 1024);
        var before = Path.Combine(_folder.FullName, "writer.log");
        var after = Path.Combine(_folder.FullName, "one-piece.log");
        File.WriteAllText(before, "existing\r\n");
        File.WriteAllText(after, "existing\r\n");

        var writerFailure = Attempt(() => WriteWithStreamWriter(before, line));
        var onePieceFailure = Attempt(() => WriteInOnePiece(after, line));

        if (fixedLine)
        {
            Assert.Equal(failure, writerFailure);
        }

        Assert.Equal(writerFailure, onePieceFailure);
        Assert.Equal(File.ReadAllBytes(before), File.ReadAllBytes(after));
    }

    [Fact]
    public void The_generated_lines_reach_the_buffer_s_edge_and_both_outcomes()
    {
        var most = 1024 - Environment.NewLine.Length;
        var failing = GeneratedLines.Count(line => Attempt(() => LineEncoding.GetByteCount(line)) is not null);

        Assert.Contains(GeneratedLines, line => line.Length == 0);
        Assert.Contains(GeneratedLines, line => line.Length == most);
        Assert.Contains(GeneratedLines, line => line.Length == most - 1);
        Assert.All(GeneratedLines, line => Assert.InRange(line.Length, 0, most));
        Assert.Contains(GeneratedLines, line => line.Any(char.IsHighSurrogate) && Attempt(() => LineEncoding.GetByteCount(line)) is null);
        Assert.Contains(GeneratedLines, line => line.Any(c => c > 0x7FF && !char.IsSurrogate(c)));
        Assert.Contains(GeneratedLines, line => line.Contains('\r') || line.Contains('\n'));
        Assert.InRange(failing, 16, GeneratedLines.Length - 64);
    }

    [Theory]
    [InlineData(1024, new[] { 1024 })]
    [InlineData(1025, new[] { 1024, 1 })]
    public void A_stream_writer_writes_what_fits_its_buffer_in_one_piece(int charsWithNewLine, int[] writes)
    {
        using var stream = new WriteRecordingStream();
        using (var writer = new StreamWriter(stream, LineEncoding, bufferSize: -1, leaveOpen: true))
        {
            writer.WriteLine(new string('w', charsWithNewLine - Environment.NewLine.Length));
        }

        Assert.Equal(writes, stream.Writes);
    }

    // OverlayLog.Write's statements for a line that fits, as pinned above.
    private static void WriteInOnePiece(string path, string line)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, bufferSize: 0);
        var bytes = new byte[LineEncoding.GetByteCount(line) + NewLineBytes.Length];
        NewLineBytes.CopyTo(bytes, LineEncoding.GetBytes(line, 0, line.Length, bytes, 0));
        stream.Write(bytes, 0, bytes.Length);
    }

    // What OverlayLog.Write did with every line before, and still does with a longer one.
    private static void WriteWithStreamWriter(string path, string line)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        using var writer = new StreamWriter(stream);
        writer.WriteLine(line);
    }

    private static string? Attempt(Action write)
    {
        try
        {
            write();
            return null;
        }
        catch (Exception failure)
        {
            return failure.GetType().FullName;
        }
    }

    // Lines that fit the StreamWriter's 1,024-character buffer with the new line, seeded so every run judges the same ones:
    // the edges first (empty, one character, the whole buffer and one short of it), then lengths spread over the range.
    // Characters of every UTF-8 length, controls, and, in every fourth line, lone surrogates, high or low.
    private static string[] GenerateLines(int seed, int count)
    {
        var random = new Random(seed);
        var most = 1024 - Environment.NewLine.Length;
        var lines = new string[count];
        for (var i = 0; i < count; i++)
        {
            var length = i switch { 0 => 0, 1 => 1, 2 => most, 3 => most - 1, _ => random.Next(0, most + 1) };
            var loneSurrogates = i % 4 == 0;
            var builder = new StringBuilder(length);
            while (builder.Length < length)
            {
                switch (random.Next(10))
                {
                    case 4:
                        builder.Append((char)random.Next(0x80, 0x800));
                        break;
                    case 5:
                        builder.Append((char)random.Next(0x800, 0xD800));
                        break;
                    case 6:
                        builder.Append((char)random.Next(0xE000, 0x10000));
                        break;
                    case 7 when length - builder.Length >= 2:
                        builder.Append(char.ConvertFromUtf32(random.Next(0x10000, 0x110000)));
                        break;
                    case 8:
                        builder.Append(random.Next(4) switch { 0 => '\r', 1 => '\n', 2 => '\t', _ => '\0' });
                        break;
                    case 9 when loneSurrogates:
                        builder.Append((char)random.Next(0xD800, 0xE000));
                        break;
                    default:
                        builder.Append((char)random.Next(0x20, 0x7F));
                        break;
                }
            }

            lines[i] = builder.ToString();
        }

        return lines;
    }

    private static string Source() =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Scribe.Overlay", "Logging", "OverlayLog.cs")).ReplaceLineEndings("\n");

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return root.FullName;
    }

    // Records the size of each write it is handed and keeps nothing. Not a MemoryStream: a type derived from one sends a
    // span write back through Write(byte[], int, int), which would record it twice.
    private sealed class WriteRecordingStream : Stream
    {
        public List<int> Writes { get; } = [];

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Writes.Add(count);

        public override void Write(ReadOnlySpan<byte> buffer) => Writes.Add(buffer.Length);
    }
}
