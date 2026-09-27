using System.Buffers;
using System.Text;
using Scribe.Core.Diagnostics;

namespace Scribe.Core.Tests;

public sealed class HistoricalLogRedactionPoolClearingTests
{
    private const int ChunkBytes = 64 * 1024;
    private const string ClearingReturn = "ArrayPool<byte>.Shared.Return(chunk, clearArray: true);";

    [Fact]
    public void The_log_text_a_scrub_read_does_not_stay_in_the_shared_pool()
    {
        var log = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(
            "09:00:00.000 [Debug] TranscriptionService: Decoded 900 ms of audio in 40 ms (RTF 0.04): \"a private sentence\"\r\n",
            400)));

        // The shared pool keeps one array of each size for each thread and gives it to the next rent of that size there, so
        // an array put back right before the scrub is the one the scrub reads the log into. A collection can trim it in
        // between, so the observation is tried three times before the source is checked instead.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var drained = ArrayPool<byte>.Shared.Rent(ChunkBytes);
            var prepared = ArrayPool<byte>.Shared.Rent(ChunkBytes);
            try
            {
                Array.Fill(prepared, (byte)0xAB);
                ArrayPool<byte>.Shared.Return(prepared);
                using (var source = new MemoryStream(log))
                {
                    HistoricalLogRedaction.CopyRedacted(source, Stream.Null);
                }

                if (prepared.AsSpan().IndexOfAnyExcept((byte)0xAB) >= 0)
                {
                    Assert.True(prepared.AsSpan().IndexOfAnyExcept((byte)0) < 0, "The scrub gave its buffer back with log text in it.");
                    return;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(drained);
            }
        }

        Assert.Contains(ClearingReturn, File.ReadAllText(SourcePath()), StringComparison.Ordinal);
    }

    [Fact]
    public void The_scrub_asks_the_pool_to_wipe_its_buffer()
    {
        var source = File.ReadAllText(SourcePath());

        Assert.Contains(ClearingReturn, source, StringComparison.Ordinal);
        Assert.DoesNotContain("ArrayPool<byte>.Shared.Return(chunk);", source, StringComparison.Ordinal);
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
}
