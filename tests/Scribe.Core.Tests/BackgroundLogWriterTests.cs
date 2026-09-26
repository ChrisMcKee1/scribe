using Microsoft.Extensions.Logging;
using Scribe.Core.Diagnostics;

namespace Scribe.Core.Tests;

public class BackgroundLogWriterTests
{
    // Generous bound for "this must happen"; the tests never sleep to make something happen.
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private static readonly DateTime Now = new(2026, 9, 21, 14, 5, 9, 123);

    private static LogRecord Record(string text, LogLevel level = LogLevel.Information) => new(Now, level, text);

    [Fact]
    public async Task A_debug_line_returns_while_the_disk_is_still_busy()
    {
        using var release = new ManualResetEventSlim(false);
        var sink = new GatedSink(release);
        using var writer = new BackgroundLogWriter(sink);

        writer.Write(Record("first"));
        Assert.True(sink.WriteStarted.Wait(Patience), "the writer thread never picked up the first line");

        // The writer thread is now stuck inside the sink. The caller must not be; WaitAsync throws if
        // the Debug line waited on the disk.
        await Task.Run(() => writer.Write(Record("second", LogLevel.Debug))).WaitAsync(Patience);
        Assert.Empty(sink.Texts);

        release.Set();
        Assert.True(writer.Flush(Patience));
        Assert.Equal(["first", "second"], sink.Texts);
    }

    [Fact]
    public void A_warning_and_everything_before_it_are_written_before_the_call_returns()
    {
        var sink = new GatedSink();
        using var writer = new BackgroundLogWriter(sink, new BackgroundLogWriterOptions { PromptWriteTimeout = Patience });

        writer.Write(Record("queued debug", LogLevel.Debug));
        writer.Write(Record("warning", LogLevel.Warning), prompt: true);

        // No flush: the prompt write itself is the guarantee.
        Assert.Equal(["queued debug", "warning"], sink.Texts);
    }

    [Fact]
    public async Task A_prompt_line_gives_up_after_its_bound_when_the_disk_is_stuck_and_is_written_later()
    {
        using var release = new ManualResetEventSlim(false);
        var sink = new GatedSink(release);
        using var writer = new BackgroundLogWriter(
            sink, new BackgroundLogWriterOptions { PromptWriteTimeout = TimeSpan.FromMilliseconds(50) });

        writer.Write(Record("first"));
        Assert.True(sink.WriteStarted.Wait(Patience));

        // WaitAsync throws if the prompt write hung with the disk instead of giving up.
        await Task.Run(() => writer.Write(Record("error", LogLevel.Error), prompt: true)).WaitAsync(Patience);
        Assert.DoesNotContain("error", sink.Texts);

        release.Set();
        Assert.True(writer.Flush(Patience));
        Assert.Equal(["first", "error"], sink.Texts);
    }

    [Fact]
    public void Flush_reports_false_while_the_disk_is_stuck_and_true_once_everything_is_written()
    {
        using var release = new ManualResetEventSlim(false);
        var sink = new GatedSink(release);
        using var writer = new BackgroundLogWriter(sink);

        writer.Write(Record("1"));
        Assert.True(sink.WriteStarted.Wait(Patience));
        writer.Write(Record("2"));
        writer.Write(Record("3"));

        Assert.False(writer.Flush(TimeSpan.FromMilliseconds(20)));

        release.Set();
        Assert.True(writer.Flush(Patience));
        Assert.Equal(["1", "2", "3"], sink.Texts);
    }

    [Fact]
    public void Overflow_drops_the_newest_lines_and_one_notice_marks_the_gap_once_space_returns()
    {
        var sink = new GatedSink();
        using var writer = new BackgroundLogWriter(
            sink, new BackgroundLogWriterOptions { StartWriterThread = false, MaxQueuedRecords = 2 });

        writer.Write(Record("1"));
        writer.Write(Record("2"));
        writer.Write(Record("3"));
        writer.Write(Record("4"));
        writer.Write(Record("5"));

        Assert.Equal(3, writer.DroppedCount);
        Assert.True(writer.PumpOnce());
        Assert.Equal(["1", "2"], sink.Texts);

        // Still no notice: nothing has found space since the drops.
        Assert.False(writer.PumpOnce());

        writer.Write(Record("6"));
        Assert.True(writer.PumpOnce());

        var texts = sink.Texts;
        Assert.Equal(4, texts.Count);
        Assert.Contains("[Warning] FileLoggerProvider: 3 log line(s) dropped", texts[2]);
        Assert.Equal("6", texts[3]);
        Assert.Single(texts, t => t.Contains("dropped", StringComparison.Ordinal));
        Assert.DoesNotContain("3", texts);
        Assert.DoesNotContain("4", texts);
        Assert.DoesNotContain("5", texts);
    }

    [Fact]
    public void The_character_bound_limits_the_queue_as_well_as_the_count()
    {
        var sink = new GatedSink();
        using var writer = new BackgroundLogWriter(
            sink, new BackgroundLogWriterOptions { StartWriterThread = false, MaxQueuedChars = 10 });

        writer.Write(Record("aaaa"));
        writer.Write(Record("bbbbbb"));
        writer.Write(Record("c"));

        Assert.Equal(1, writer.DroppedCount);
        Assert.True(writer.PumpOnce());
        Assert.Equal(["aaaa", "bbbbbb"], sink.Texts);
    }

    [Fact]
    public void One_oversized_line_is_still_accepted_by_an_empty_queue()
    {
        // A single huge crash report must not be turned away just because it exceeds the bound alone.
        var sink = new GatedSink();
        using var writer = new BackgroundLogWriter(
            sink, new BackgroundLogWriterOptions { StartWriterThread = false, MaxQueuedChars = 10 });

        var huge = new string('x', 1000);
        writer.Write(Record(huge));

        Assert.Equal(0, writer.DroppedCount);
        Assert.True(writer.PumpOnce());
        Assert.Equal([huge], sink.Texts);
    }

    [Fact]
    public async Task A_warning_that_meets_a_full_queue_waits_briefly_for_room_instead_of_being_dropped()
    {
        using var release = new ManualResetEventSlim(false);
        var sink = new GatedSink(release);
        using var writer = new BackgroundLogWriter(
            sink, new BackgroundLogWriterOptions { MaxQueuedRecords = 1, PromptWriteTimeout = Patience });

        writer.Write(Record("1"));
        Assert.True(sink.WriteStarted.Wait(Patience));
        writer.Write(Record("2"));

        // The queue is full and the disk is stuck: the warning must be waiting for room, not dropped.
        var warning = Task.Run(() => writer.Write(Record("warning", LogLevel.Warning), prompt: true));
        Assert.True(SpinWait.SpinUntil(() => writer.PromptWaiterCount == 1, Patience));
        Assert.Equal(0, writer.DroppedCount);

        release.Set();
        await warning.WaitAsync(Patience);

        Assert.Equal(0, writer.DroppedCount);
        Assert.Equal(["1", "2", "warning"], sink.Texts);
    }

    [Fact]
    public void A_warning_that_finds_no_room_within_its_bound_is_dropped_and_counted()
    {
        using var release = new ManualResetEventSlim(false);
        var sink = new GatedSink(release);
        using var writer = new BackgroundLogWriter(
            sink,
            new BackgroundLogWriterOptions { MaxQueuedRecords = 1, PromptWriteTimeout = TimeSpan.FromMilliseconds(50) });

        writer.Write(Record("1"));
        Assert.True(sink.WriteStarted.Wait(Patience));
        writer.Write(Record("2"));

        writer.Write(Record("warning", LogLevel.Warning), prompt: true);
        Assert.Equal(1, writer.DroppedCount);

        release.Set();
        Assert.True(writer.Flush(Patience));
        writer.Write(Record("3"), prompt: true);
        Assert.True(writer.Flush(Patience));

        var texts = sink.Texts;
        Assert.DoesNotContain("warning", texts);
        Assert.Contains(texts, t => t.Contains("1 log line(s) dropped", StringComparison.Ordinal));
        Assert.Equal("3", texts[^1]);
    }

    [Fact]
    public void A_debug_line_that_meets_a_full_queue_is_dropped_at_once()
    {
        using var release = new ManualResetEventSlim(false);
        var sink = new GatedSink(release);
        using var writer = new BackgroundLogWriter(
            sink, new BackgroundLogWriterOptions { MaxQueuedRecords = 1, PromptWriteTimeout = Patience });

        writer.Write(Record("1"));
        Assert.True(sink.WriteStarted.Wait(Patience));
        writer.Write(Record("2"));

        // With a 30 second prompt bound, a Debug line that waited for room would hang this test.
        writer.Write(Record("debug", LogLevel.Debug));
        Assert.Equal(1, writer.DroppedCount);
        release.Set();
    }

    [Fact]
    public void Dispose_writes_a_pending_dropped_lines_notice()
    {
        // Nothing found space after the drops, so without this the gap would never be explained.
        var sink = new GatedSink();
        var writer = new BackgroundLogWriter(
            sink, new BackgroundLogWriterOptions { StartWriterThread = false, MaxQueuedRecords = 1 });

        writer.Write(Record("1"));
        writer.Write(Record("2"));
        writer.Write(Record("3"));
        writer.Dispose();

        var texts = sink.Texts;
        Assert.Equal(2, texts.Count);
        Assert.Equal("1", texts[0]);
        Assert.Contains("[Warning] FileLoggerProvider: 2 log line(s) dropped", texts[1]);
    }

    [Fact]
    public void A_flush_writes_a_pending_dropped_lines_notice_once()
    {
        var sink = new GatedSink();
        using var writer = new BackgroundLogWriter(
            sink, new BackgroundLogWriterOptions { StartWriterThread = false, MaxQueuedRecords = 1 });

        writer.Write(Record("1"));
        writer.Write(Record("2"));
        Assert.True(writer.Flush(Patience));
        Assert.True(writer.Flush(Patience));

        var texts = sink.Texts;
        Assert.Equal(2, texts.Count);
        Assert.Contains("1 log line(s) dropped", texts[1]);
    }

    [Fact]
    public void Dispose_drains_the_queue_and_later_lines_are_written_on_the_callers_thread()
    {
        var sink = new GatedSink();
        var writer = new BackgroundLogWriter(sink);

        writer.Write(Record("a"));
        writer.Write(Record("b"));
        writer.Dispose();

        Assert.Equal(["a", "b"], sink.Texts);

        // Teardown keeps logging after the host is gone. With no writer thread, the line is written
        // before Write returns rather than stranded in a queue nobody drains.
        writer.Write(Record("after dispose", LogLevel.Debug));
        Assert.Equal(["a", "b", "after dispose"], sink.Texts);
        Assert.Equal(0, writer.QueuedCount);

        writer.Dispose();
        Assert.True(writer.Flush(TimeSpan.Zero));
    }

    [Fact]
    public async Task Lines_logged_while_dispose_waits_for_the_disk_are_all_written_in_order()
    {
        using var release = new ManualResetEventSlim(false);
        var sink = new GatedSink(release);
        var writer = new BackgroundLogWriter(
            sink, new BackgroundLogWriterOptions { DisposeTimeout = Patience, PromptWriteTimeout = Patience });

        writer.Write(Record("before"));
        Assert.True(sink.WriteStarted.Wait(Patience));

        // The writer thread is on the disk, so Dispose has to wait for it. Lines logged once it has
        // started land in the queue the writer drains before it stops; none may be stranded or reordered.
        var disposing = Task.Run(writer.Dispose);
        Assert.True(SpinWait.SpinUntil(() => writer.IsStopping, Patience));
        writer.Write(Record("during 1"));
        writer.Write(Record("during 2", LogLevel.Debug));
        Assert.False(disposing.IsCompleted);

        release.Set();
        await disposing.WaitAsync(Patience);
        writer.Write(Record("after"));

        Assert.Equal(["before", "during 1", "during 2", "after"], sink.Texts);
        Assert.Equal(0, writer.QueuedCount);
    }

    [Fact]
    public async Task A_disk_stuck_at_shutdown_bounds_dispose_and_nothing_queued_is_lost_once_it_recovers()
    {
        using var release = new ManualResetEventSlim(false);
        var sink = new GatedSink(release);
        var writer = new BackgroundLogWriter(
            sink,
            new BackgroundLogWriterOptions
            {
                DisposeTimeout = TimeSpan.FromMilliseconds(50),
                PromptWriteTimeout = TimeSpan.FromMilliseconds(50),
            });

        writer.Write(Record("stuck"));
        Assert.True(sink.WriteStarted.Wait(Patience));
        writer.Write(Record("queued behind it"));

        // WaitAsync throws if Dispose waited on the stuck disk instead of giving up at its bound.
        await Task.Run(writer.Dispose).WaitAsync(Patience);

        // The writer thread still owns the sink, so this cannot be written inline yet; it must stay
        // queued, not be dropped, and Write must still return within its bound.
        await Task.Run(() => writer.Write(Record("after dispose"))).WaitAsync(Patience);

        release.Set();
        Assert.True(writer.Flush(Patience));
        Assert.Equal(["stuck", "queued behind it", "after dispose"], sink.Texts);
        Assert.Equal(0, writer.DroppedCount);
    }

    [Fact]
    public async Task Concurrent_writers_lose_nothing_and_keep_each_callers_order()
    {
        const int Writers = 8;
        const int LinesEach = 2000;
        var sink = new GatedSink();
        using var writer = new BackgroundLogWriter(
            sink, new BackgroundLogWriterOptions { MaxQueuedRecords = Writers * LinesEach, PromptWriteTimeout = Patience });

        using var start = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, Writers).Select(w => Task.Run(() =>
        {
            start.Wait(Patience);
            for (var i = 0; i < LinesEach; i++)
            {
                // Every hundredth line waits for the disk, the rest only queue.
                writer.Write(Record($"{w}:{i}", i % 100 == 0 ? LogLevel.Warning : LogLevel.Debug), prompt: i % 100 == 0);
            }
        })).ToArray();
        start.Set();
        await Task.WhenAll(tasks).WaitAsync(Patience);

        Assert.True(writer.Flush(Patience));
        Assert.Equal(0, writer.DroppedCount);

        var texts = sink.Texts;
        Assert.Equal(Writers * LinesEach, texts.Count);
        for (var w = 0; w < Writers; w++)
        {
            var prefix = $"{w}:";
            var mine = texts.Where(t => t.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            Assert.Equal(Enumerable.Range(0, LinesEach).Select(i => $"{w}:{i}"), mine);
        }
    }

    [Fact]
    public void A_failing_sink_never_reaches_the_caller_and_the_writer_keeps_going()
    {
        var sink = new GatedSink { Failure = new IOException("disk gone") };
        using var writer = new BackgroundLogWriter(sink, new BackgroundLogWriterOptions { PromptWriteTimeout = Patience });

        writer.Write(Record("lost", LogLevel.Error), prompt: true);
        Assert.True(writer.Flush(Patience));

        sink.Failure = null;
        writer.Write(Record("kept", LogLevel.Error), prompt: true);

        Assert.Equal(["kept"], sink.Texts);
    }

    [Fact]
    public void A_prompt_line_logged_from_inside_the_sink_does_not_wait_on_itself()
    {
        using var innerReturned = new ManualResetEventSlim(false);
        BackgroundLogWriter? writer = null;
        var sink = new GatedSink();
        sink.OnWrite = batch =>
        {
            if (batch.Any(r => r.Text == "outer"))
            {
                // This runs on the writer thread. Waiting for completion here would wait for the very
                // batch this thread is in the middle of writing.
                writer!.Write(Record("inner", LogLevel.Error), prompt: true);
                innerReturned.Set();
            }
        };

        // A write that waited on itself would wait out its prompt bound, so that bound is made far longer than the hang
        // guard: a correct write returns at once, and one that waits on itself cannot return within Patience.
        writer = new BackgroundLogWriter(sink, new BackgroundLogWriterOptions { PromptWriteTimeout = TimeSpan.FromMinutes(10) });
        try
        {
            writer.Write(Record("outer"));
            Assert.True(innerReturned.Wait(Patience), "a write from inside the sink waited on itself");
            Assert.True(writer.Flush(Patience));
            Assert.Equal(["outer", "inner"], sink.Texts);
        }
        finally
        {
            writer.Dispose();
        }
    }

    [Theory]
    [InlineData(LogLevel.Trace, false)]
    [InlineData(LogLevel.Debug, false)]
    [InlineData(LogLevel.Information, false)]
    [InlineData(LogLevel.Warning, true)]
    [InlineData(LogLevel.Error, true)]
    [InlineData(LogLevel.Critical, true)]
    [InlineData(LogLevel.None, false)]
    public void Warnings_and_worse_are_written_promptly(LogLevel level, bool prompt)
    {
        Assert.Equal(prompt, BackgroundLogWriter.RequiresPromptWrite(level, "an ordinary message"));
    }

    [Fact]
    public void The_session_start_and_end_markers_are_written_promptly_at_any_level()
    {
        Assert.True(BackgroundLogWriter.RequiresPromptWrite(LogLevel.Information, SessionBanner.StartMarker));
        Assert.True(BackgroundLogWriter.RequiresPromptWrite(
            LogLevel.Information, SessionBanner.EndMarker + " session=abc123 uptime=01:02:03"));
        Assert.False(BackgroundLogWriter.RequiresPromptWrite(LogLevel.Information, "session=abc123 pid=42"));
        Assert.False(BackgroundLogWriter.RequiresPromptWrite(LogLevel.Information, null));
    }

    /// <summary>
    /// Records what it is handed. With a gate it blocks inside Write until released, which is how the
    /// tests hold the writer thread "on the disk" without any timing.
    /// </summary>
    private sealed class GatedSink(ManualResetEventSlim? gate = null) : ILogRecordSink
    {
        private readonly object _lock = new();
        private readonly List<string> _texts = [];

        public ManualResetEventSlim WriteStarted { get; } = new(false);

        public Exception? Failure { get; set; }

        public Action<IReadOnlyList<LogRecord>>? OnWrite { get; set; }

        public List<string> Texts
        {
            get { lock (_lock) { return [.. _texts]; } }
        }

        public void Write(IReadOnlyList<LogRecord> batch)
        {
            WriteStarted.Set();
            gate?.Wait(Patience);
            OnWrite?.Invoke(batch);
            if (Failure is { } failure)
            {
                throw failure;
            }

            lock (_lock)
            {
                _texts.AddRange(batch.Select(r => r.Text));
            }
        }
    }
}
