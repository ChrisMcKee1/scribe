using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Scribe.Core.Diagnostics;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.Tests.Concurrency;

namespace Scribe.Core.Tests;

/// <summary>
/// Every <see cref="HistoryWriterTests"/> case again with <see cref="PerfFlags.HistoryStageTiming"/> on (DATA-O-08 and
/// DATA-A-01): the same rows, order, barriers and drops. With the concrete repository behind it, each committed write also
/// logs where its time went, after today's committed line and in numbers only; nothing else changes.
/// </summary>
public sealed class HistoryWriterStageTimingTests : HistoryWriterTests
{
    private static readonly Regex StageLine = new(
        @"^#(?<id>\d+) history write stages \(us\): room \d+, queue \d+, gate \d+, open \d+, begin \d+, " +
        @"blob columns \d+, encode \d+, blob insert \d+ \((?<bytes>\d+) bytes\), columns \d+, insert \d+, " +
        @"commit \d+, close \d+; collections \d+/\d+/\d+\.$");

    protected override PerfFlags Flags => PerfFlags.Parse(PerfFlags.HistoryStageTiming);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Each_committed_write_logs_its_stages_after_its_committed_line_in_numbers_only(bool on)
    {
        using var database = ScribeDatabase.CreateInMemory();
        var repository = new HistoryRepository(database);
        var logger = new CapturingLogger<HistoryWriter>();
        using (var writer = new HistoryWriter(repository, logger, on ? Flags : PerfFlags.None))
        {
            Assert.True(writer.Enqueue(Entry(1), Audio(1_600), dictationId: 7));
            Assert.True(writer.Enqueue(Entry(2), audio: null, dictationId: 8));
            Assert.True(writer.WaitForAcceptedWrites(TimeSpan.FromSeconds(30)));
        }

        var messages = logger.Entries.Select(entry => entry.Message).ToList();
        var committed = messages.Where(message => message.Contains("history committed", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, committed.Count);
        var stages = messages.Where(message => message.Contains("history write stages", StringComparison.Ordinal)).ToList();
        if (!on)
        {
            Assert.Empty(stages);
            return;
        }

        Assert.Equal(2, stages.Count);
        var first = StageLine.Match(stages[0]);
        var second = StageLine.Match(stages[1]);
        Assert.True(first.Success, stages[0]);
        Assert.True(second.Success, stages[1]);
        Assert.Equal("7", first.Groups["id"].Value);
        Assert.Equal("8", second.Groups["id"].Value);
        Assert.Equal(AudioBlobCodec.EncodedLength(1_600, AudioBlobEncoding.Pcm16).ToString(System.Globalization.CultureInfo.InvariantCulture), first.Groups["bytes"].Value);
        Assert.Equal("0", second.Groups["bytes"].Value);

        // Each stage line follows its own committed line.
        Assert.True(messages.IndexOf(committed[0]) < messages.IndexOf(stages[0]));
        Assert.True(messages.IndexOf(stages[0]) < messages.IndexOf(committed[1]));
        Assert.All(
            logger.Entries.Where(entry => entry.Message.Contains("history write stages", StringComparison.Ordinal)),
            entry =>
            {
                Assert.Equal(LogLevel.Debug, entry.Level);
                Assert.All(
                    entry.State.Where(pair => pair.Key != "{OriginalFormat}"),
                    pair => Assert.True(pair.Value is long or int, pair.Key));
            });

        // The rows are the ones today's path writes.
        var rows = repository.GetRecent();
        Assert.Equal(new[] { 2, 1 }, rows.Select(row => row.AudioMilliseconds));
        Assert.NotNull(rows.Single(row => row.AudioMilliseconds == 1).AudioBlobId);
    }

    [Fact]
    public void A_write_that_fails_logs_no_stages_and_a_writer_over_another_repository_times_nothing()
    {
        using var database = ScribeDatabase.CreateInMemory();
        var repository = new HistoryRepository(database);
        var logger = new CapturingLogger<HistoryWriter>();
        using (var writer = new HistoryWriter(repository, logger, Flags))
        {
            database.Dispose(); // every write through it now fails
            Assert.True(writer.Enqueue(Entry(1), audio: null, dictationId: 3));
            Assert.True(writer.WaitForAcceptedWrites(TimeSpan.FromSeconds(30)));
        }

        Assert.Contains(logger.Entries, entry => entry.Message.Contains("history write failed", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("history write stages", StringComparison.Ordinal));
    }

    [Fact]
    public void The_timing_reads_no_log_file_sets_no_hook_and_runs_no_checkpoint()
    {
        foreach (var file in new[] { "HistoryWriter.cs", "HistoryWriteStages.cs" })
        {
            var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Scribe.Core", "Persistence", file));
            foreach (var forbidden in new[] { "wal_checkpoint", "wal_hook", "WalHook", "-wal", "CommandText", "FileInfo" })
            {
                Assert.DoesNotContain(forbidden, source, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private static HistoryEntry Entry(int number) =>
        new(Id: 0, TimestampUtc: new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero).AddSeconds(number), Text: $"entry {number}",
            AudioMilliseconds: number, DecodeMilliseconds: 10);

    private static CapturedAudio Audio(int samples) => new(Enumerable.Repeat(0.25f, samples).ToArray(), 16_000);

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
}
