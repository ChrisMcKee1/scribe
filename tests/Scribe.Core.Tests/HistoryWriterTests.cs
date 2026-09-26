using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Infrastructure;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.Tests.Concurrency;

namespace Scribe.Core.Tests;

/// <summary>
/// The ordered background history writer and the barrier in front of the repository. History used to commit
/// synchronously at the end of each dictation, which kept the controller Processing (and rejecting the next press)
/// until SQLite finished. The contract pinned here: commits keep call order, a third write waits for room instead of
/// growing the queue, reads and maintenance wait for writes accepted before them, shutdown drains with a bound and
/// accounts for anything it abandons, one failed write never stops the next or leaks its text, and the capture handed
/// over is the capture written.
/// </summary>
public sealed class HistoryWriterTests
{
    private const string SecretText = "Zanzibar lighthouse keeper 90210";
    private static readonly DateTimeOffset Epoch = new(2020, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void Writes_commit_exactly_as_queued_and_in_call_order()
    {
        // Six writes pass the writer's two slots, so a producer waits for the consumer, which starts on the pool. With
        // production's 5 s bound that wait raced the pool: in a loaded full run the consumer started late and a write was
        // refused (stream TR, round 2). The bound is not what this test is about; it is pinned with one of its own in
        // A_producer_that_finds_no_room_within_the_bound_drops_only_its_own_entry.
        var history = new FakeHistory();
        using var writer = Unbounded(history, new CapturingLogger<HistoryWriter>());
        var queued = Enumerable.Range(1, 6).Select(Entry).ToList();

        foreach (var entry in queued)
        {
            Assert.True(writer.Enqueue(entry, audio: null, dictationId: entry.AudioMilliseconds));
        }

        Assert.True(writer.WaitForAcceptedWrites(BlockedThreads.SafetyTimeout));
        // The very same records, timestamps included: nothing is re-stamped at commit time.
        Assert.Equal(queued, history.Committed.Select(write => write.Entry));
        Assert.All(queued.Zip(history.Committed), pair => Assert.Same(pair.First, pair.Second.Entry));
    }

    [Fact]
    public void A_third_write_waits_for_room_and_is_committed_after_the_other_two()
    {
        using var inFirst = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var producerWaiting = new ManualResetEventSlim();
        var history = new FakeHistory { DuringAdd = HoldFirstWrite(inFirst, releaseFirst) };
        using var writer = Unbounded(history, new CapturingLogger<HistoryWriter>());
        using var releaseAtExit = new ReleaseAtExit(releaseFirst);
        writer.ProducerWaiting = producerWaiting.Set;

        Assert.True(writer.Enqueue(Entry(1), null));
        Assert.True(inFirst.Wait(BlockedThreads.SafetyTimeout)); // first write committing
        Assert.True(writer.Enqueue(Entry(2), null));             // second waits in the queue; returns at once
        Assert.False(producerWaiting.IsSet);

        var third = false;
        var producer = BlockedThreads.Start(() => third = writer.Enqueue(Entry(3), null));
        Assert.True(producerWaiting.Wait(BlockedThreads.SafetyTimeout));
        BlockedThreads.WaitUntilBlocked(producer);
        Assert.Empty(history.Committed);

        releaseFirst.Set();
        BlockedThreads.Join(producer);
        Assert.True(third);
        Assert.True(writer.WaitForAcceptedWrites(BlockedThreads.SafetyTimeout));

        Assert.Equal(new[] { 1, 2, 3 }, history.Committed.Select(write => write.Entry.AudioMilliseconds));
    }

    [Fact]
    public void Producers_waiting_for_room_are_admitted_in_the_order_they_arrived()
    {
        using var inFirst = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        var history = new FakeHistory { DuringAdd = HoldFirstWrite(inFirst, releaseFirst) };
        using var writer = Unbounded(history, new CapturingLogger<HistoryWriter>());
        using var releaseAtExit = new ReleaseAtExit(releaseFirst);

        writer.Enqueue(Entry(1), null);
        Assert.True(inFirst.Wait(BlockedThreads.SafetyTimeout));
        writer.Enqueue(Entry(2), null);

        var third = BlockedThreads.Start(() => writer.Enqueue(Entry(3), null));
        BlockedThreads.WaitUntilBlocked(third);
        var fourth = BlockedThreads.Start(() => writer.Enqueue(Entry(4), null));
        BlockedThreads.WaitUntilBlocked(fourth);

        releaseFirst.Set();
        BlockedThreads.Join(third);
        BlockedThreads.Join(fourth);
        Assert.True(writer.WaitForAcceptedWrites(BlockedThreads.SafetyTimeout));

        Assert.Equal(new[] { 1, 2, 3, 4 }, history.Committed.Select(write => write.Entry.AudioMilliseconds));
    }

    [Fact]
    public void A_read_waits_for_writes_accepted_before_it_and_then_sees_them()
    {
        using var inFirst = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var barrierWaiting = new ManualResetEventSlim();
        var history = new FakeHistory { DuringAdd = HoldFirstWrite(inFirst, releaseFirst) };
        using var writer = new HistoryWriter(history, new CapturingLogger<HistoryWriter>())
        {
            BarrierWaiting = barrierWaiting.Set,
        };
        using var releaseAtExit = new ReleaseAtExit(releaseFirst);
        var ordered = Ordered(history, writer);

        writer.Enqueue(Entry(1), null);
        Assert.True(inFirst.Wait(BlockedThreads.SafetyTimeout));

        IReadOnlyList<HistoryEntry>? read = null;
        var reader = BlockedThreads.Start(() => read = ordered.GetRecent());
        Assert.True(barrierWaiting.Wait(BlockedThreads.SafetyTimeout));
        BlockedThreads.WaitUntilBlocked(reader);
        Assert.Equal(0, history.Reads);

        releaseFirst.Set();
        BlockedThreads.Join(reader);

        Assert.Equal(1, Assert.Single(read!).AudioMilliseconds);
    }

    [Fact]
    public void A_barrier_waits_only_for_writes_accepted_before_it()
    {
        using var inFirst = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var inSecond = new ManualResetEventSlim();
        using var releaseSecond = new ManualResetEventSlim();
        using var barrierWaiting = new ManualResetEventSlim();
        var history = new FakeHistory
        {
            DuringAdd = entry =>
            {
                if (entry.AudioMilliseconds == 1)
                {
                    inFirst.Set();
                    releaseFirst.Wait();
                }
                else if (entry.AudioMilliseconds == 2)
                {
                    inSecond.Set();
                    releaseSecond.Wait();
                }
            },
        };
        using var writer = new HistoryWriter(history, new CapturingLogger<HistoryWriter>())
        {
            BarrierWaiting = barrierWaiting.Set,
        };
        using var releaseAtExit = new ReleaseAtExit(releaseFirst, releaseSecond);

        writer.Enqueue(Entry(1), null);
        Assert.True(inFirst.Wait(BlockedThreads.SafetyTimeout));

        var drained = false;
        var barrier = BlockedThreads.Start(() => drained = writer.WaitForAcceptedWrites(BlockedThreads.SafetyTimeout));
        Assert.True(barrierWaiting.Wait(BlockedThreads.SafetyTimeout));

        // Accepted after the barrier began, and still committing when the barrier returns: a Clear must never be held
        // up by dictations that finish after the click.
        writer.Enqueue(Entry(2), null);
        releaseFirst.Set();
        Assert.True(inSecond.Wait(BlockedThreads.SafetyTimeout));
        BlockedThreads.Join(barrier);

        Assert.True(drained);
        Assert.Equal(new[] { 1 }, history.Committed.Select(write => write.Entry.AudioMilliseconds));
        releaseSecond.Set();
        Assert.True(writer.WaitForAcceptedWrites(BlockedThreads.SafetyTimeout));
        Assert.Equal(new[] { 1, 2 }, history.Committed.Select(write => write.Entry.AudioMilliseconds));
    }

    [Fact]
    public void Clear_removes_every_write_accepted_before_it()
    {
        using var inFirst = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var barrierWaiting = new ManualResetEventSlim();
        var history = new FakeHistory { DuringAdd = HoldFirstWrite(inFirst, releaseFirst) };
        using var writer = new HistoryWriter(history, new CapturingLogger<HistoryWriter>())
        {
            BarrierWaiting = barrierWaiting.Set,
        };
        using var releaseAtExit = new ReleaseAtExit(releaseFirst);
        var ordered = Ordered(history, writer);

        writer.Enqueue(Entry(1), null);
        Assert.True(inFirst.Wait(BlockedThreads.SafetyTimeout));
        writer.Enqueue(Entry(2), null);

        var clearer = BlockedThreads.Start(ordered.Clear);
        Assert.True(barrierWaiting.Wait(BlockedThreads.SafetyTimeout));
        BlockedThreads.WaitUntilBlocked(clearer);

        releaseFirst.Set();
        BlockedThreads.Join(clearer);

        Assert.Equal(2, history.CommittedWhenCleared);
        Assert.Empty(history.Committed);
    }

    [Fact]
    public void Delete_and_prune_wait_for_accepted_writes_too()
    {
        using var inFirst = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        var history = new FakeHistory { DuringAdd = HoldFirstWrite(inFirst, releaseFirst) };
        using var writer = new HistoryWriter(history, new CapturingLogger<HistoryWriter>());
        using var releaseAtExit = new ReleaseAtExit(releaseFirst);
        var ordered = Ordered(history, writer);

        writer.Enqueue(Entry(1), null);
        Assert.True(inFirst.Wait(BlockedThreads.SafetyTimeout));

        var deleter = BlockedThreads.Start(() => ordered.Delete(1));
        BlockedThreads.WaitUntilBlocked(deleter);
        var pruner = BlockedThreads.Start(() => ordered.PruneOlderThan(Epoch));
        BlockedThreads.WaitUntilBlocked(pruner);

        releaseFirst.Set();
        BlockedThreads.Join(deleter);
        BlockedThreads.Join(pruner);

        Assert.Equal(1, history.CommittedWhenDeleted);
        Assert.Equal(1, history.CommittedWhenPruned);
    }

    [Fact]
    public void A_read_that_outlives_its_bound_goes_ahead_and_says_so_without_any_text()
    {
        using var inFirst = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        var history = new FakeHistory { DuringAdd = HoldFirstWrite(inFirst, releaseFirst) };
        using var writer = new HistoryWriter(history, new CapturingLogger<HistoryWriter>());
        using var releaseAtExit = new ReleaseAtExit(releaseFirst);
        var logger = new CapturingLogger<OrderedHistoryRepository>();
        var ordered = new OrderedHistoryRepository(history, writer, logger, TimeSpan.Zero, TimeSpan.Zero);

        writer.Enqueue(Entry(1, SecretText), null);
        Assert.True(inFirst.Wait(BlockedThreads.SafetyTimeout));

        Assert.Empty(ordered.GetRecent()); // what is committed, not a frozen UI thread
        var warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal(nameof(IHistoryRepository.GetRecent), warning.Value("Operation"));
        Assert.False(warning.Mentions("Zanzibar"));

        releaseFirst.Set();
    }

    [Fact]
    public void Complete_drains_every_accepted_write_and_then_refuses_new_ones()
    {
        using var inFirst = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var releaseAtExit = new ReleaseAtExit(releaseFirst);
        var logger = new CapturingLogger<HistoryWriter>();
        var history = new FakeHistory { DuringAdd = HoldFirstWrite(inFirst, releaseFirst) };
        var writer = new HistoryWriter(history, logger);

        writer.Enqueue(Entry(1), null);
        Assert.True(inFirst.Wait(BlockedThreads.SafetyTimeout));
        writer.Enqueue(Entry(2), null);

        HistoryDrainResult? drain = null;
        var completer = BlockedThreads.Start(() => drain = writer.Complete(BlockedThreads.SafetyTimeout));
        BlockedThreads.WaitUntilBlocked(completer);
        Assert.Null(drain);

        releaseFirst.Set();
        BlockedThreads.Join(completer);

        Assert.Equal(new HistoryDrainResult(Drained: true, StillWriting: 0, Abandoned: 0), drain);
        Assert.Equal(2, history.Committed.Count);
        Assert.False(writer.Enqueue(Entry(3, SecretText), null, dictationId: 3));
        Assert.Equal(2, history.Committed.Count);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning && Equals(entry.Value("Id"), 3L));
        Assert.All(logger.Entries, entry => Assert.False(entry.Mentions("Zanzibar")));
        writer.Dispose(); // idempotent after Complete
    }

    [Fact]
    public void Complete_that_outlives_a_stuck_write_abandons_what_is_queued_behind_it()
    {
        using var inFirst = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var releaseAtExit = new ReleaseAtExit(releaseFirst);
        var logger = new CapturingLogger<HistoryWriter>();
        var history = new FakeHistory { DuringAdd = HoldFirstWrite(inFirst, releaseFirst) };
        var writer = new HistoryWriter(history, logger);

        writer.Enqueue(Entry(1), null, dictationId: 1);
        Assert.True(inFirst.Wait(BlockedThreads.SafetyTimeout));
        writer.Enqueue(Entry(2), null, dictationId: 2);

        var drain = writer.Complete(TimeSpan.Zero);

        Assert.Equal(new HistoryDrainResult(Drained: false, StillWriting: 1, Abandoned: 1), drain);

        // The stuck write finishes on its own; what was queued behind it is dropped, never committed later.
        releaseFirst.Set();
        Assert.True(writer.WaitForAcceptedWrites(BlockedThreads.SafetyTimeout));
        Assert.Equal(new[] { 1 }, history.Committed.Select(write => write.Entry.AudioMilliseconds));
        Assert.Contains(
            logger.Entries,
            entry => entry.Message.Contains("abandoned at shutdown", StringComparison.Ordinal) && Equals(entry.Value("Id"), 2L));
        Assert.Equal(new HistoryDrainResult(Drained: true, StillWriting: 0, Abandoned: 1), writer.Complete(TimeSpan.Zero));
    }

    [Fact]
    public void A_producer_still_waiting_for_room_gives_up_when_the_writer_closes()
    {
        using var inFirst = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var releaseAtExit = new ReleaseAtExit(releaseFirst);
        var history = new FakeHistory { DuringAdd = HoldFirstWrite(inFirst, releaseFirst) };
        var writer = Unbounded(history, new CapturingLogger<HistoryWriter>());

        writer.Enqueue(Entry(1), null);
        Assert.True(inFirst.Wait(BlockedThreads.SafetyTimeout));
        writer.Enqueue(Entry(2), null);
        bool? third = null;
        var producer = BlockedThreads.Start(() => third = writer.Enqueue(Entry(3), null));
        BlockedThreads.WaitUntilBlocked(producer);

        var drain = writer.Complete(TimeSpan.Zero);
        BlockedThreads.Join(producer);

        Assert.False(third);
        Assert.Equal(2, drain.Abandoned); // the queued write plus the producer that never got in
        releaseFirst.Set();
        Assert.True(writer.WaitForAcceptedWrites(BlockedThreads.SafetyTimeout));
        Assert.Equal(new[] { 1 }, history.Committed.Select(write => write.Entry.AudioMilliseconds));
    }

    /// <summary>
    /// History must never wedge dictation. A producer that finds no room within the bound gives up on its own entry and
    /// returns, and it must not strand the producers behind it: admission is strictly by arrival, so a retired ticket
    /// that still blocked the line would leave every later dictation waiting out its own bound too.
    /// </summary>
    [Fact]
    public void A_producer_that_finds_no_room_within_the_bound_drops_only_its_own_entry()
    {
        using var inFirst = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        var logger = new CapturingLogger<HistoryWriter>();
        var history = new FakeHistory { DuringAdd = HoldFirstWrite(inFirst, releaseFirst) };

        // Any positive bound times out here, because the write it waits behind is held until after the call returns.
        using var writer = new HistoryWriter(
            history, logger, producerWaitBound: TimeSpan.FromMilliseconds(20), disposeTimeout: BlockedThreads.SafetyTimeout);
        using var releaseAtExit = new ReleaseAtExit(releaseFirst);

        Assert.True(writer.Enqueue(Entry(1), null, dictationId: 1));
        Assert.True(inFirst.Wait(BlockedThreads.SafetyTimeout));
        Assert.True(writer.Enqueue(Entry(2), null, dictationId: 2));

        Assert.False(writer.Enqueue(Entry(3, SecretText), Audio(0.25f), dictationId: 3));

        var dropped = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Equal(3L, dropped.Value("Id"));
        Assert.Equal(1, Convert.ToInt32(dropped.Value("Dropped")));
        Assert.Equal(HistoryWriter.Capacity, Convert.ToInt32(dropped.Value("Outstanding")));
        Assert.Equal(20L, dropped.Value("BoundMs"));
        foreach (var word in SecretText.Split(' '))
        {
            Assert.All(logger.Entries, entry => Assert.False(entry.Mentions(word)));
        }

        releaseFirst.Set();
        Assert.True(writer.WaitForAcceptedWrites(BlockedThreads.SafetyTimeout)); // the dropped ticket left the watermark
        Assert.Equal(new[] { 1, 2 }, history.Committed.Select(write => write.Entry.AudioMilliseconds));

        // The line behind the retired ticket still moves: the next dictation's history is admitted and committed.
        Assert.True(writer.Enqueue(Entry(4), null, dictationId: 4));
        Assert.True(writer.WaitForAcceptedWrites(BlockedThreads.SafetyTimeout));
        Assert.Equal(new[] { 1, 2, 4 }, history.Committed.Select(write => write.Entry.AudioMilliseconds));
    }

    [Fact]
    public void Dispose_waits_for_a_stuck_write_only_once_however_often_it_is_called()
    {
        using var inFirst = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var releaseAtExit = new ReleaseAtExit(releaseFirst);
        var logger = new CapturingLogger<HistoryWriter>();
        var history = new FakeHistory { DuringAdd = HoldFirstWrite(inFirst, releaseFirst) };
        var completionWaits = 0;
        var writer = new HistoryWriter(
            history, logger, producerWaitBound: Timeout.InfiniteTimeSpan, disposeTimeout: TimeSpan.FromMilliseconds(1))
        {
            CompletionWaiting = () => completionWaits++,
        };

        writer.Enqueue(Entry(1), null);
        Assert.True(inFirst.Wait(BlockedThreads.SafetyTimeout));

        // The container disposes this singleton once per registration that resolved it.
        writer.Dispose();
        writer.Dispose();

        Assert.Equal(1, completionWaits);
        Assert.Single(logger.Entries, entry => entry.Message.StartsWith("History writer disposed with", StringComparison.Ordinal));

        releaseFirst.Set(); // the stuck write still finishes on its own
        Assert.True(writer.WaitForAcceptedWrites(BlockedThreads.SafetyTimeout));
        Assert.Equal(new[] { 1 }, history.Committed.Select(write => write.Entry.AudioMilliseconds));
    }

    [Fact]
    public void A_failed_write_is_logged_as_a_shape_and_never_stops_the_next_one()
    {
        var logger = new CapturingLogger<HistoryWriter>();
        var history = new FakeHistory
        {
            DuringAdd = entry =>
            {
                if (entry.AudioMilliseconds == 1)
                {
                    // An exception whose message quotes the entry: the writer must log neither.
                    throw new InvalidOperationException($"could not store '{entry.Text}'");
                }
            },
        };
        using var writer = new HistoryWriter(history, logger);

        Assert.True(writer.Enqueue(Entry(1, SecretText), Audio(0.25f), dictationId: 7));
        Assert.True(writer.Enqueue(Entry(2), null, dictationId: 8));
        Assert.True(writer.WaitForAcceptedWrites(BlockedThreads.SafetyTimeout));

        Assert.Equal(new[] { 2 }, history.Committed.Select(write => write.Entry.AudioMilliseconds));
        var failure = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Equal(7L, failure.Value("Id"));
        Assert.Equal(nameof(InvalidOperationException), failure.Value("Error"));
        Assert.Equal(true, failure.Value("HasAudio"));
        Assert.Null(failure.Exception);
        foreach (var word in SecretText.Split(' '))
        {
            Assert.All(logger.Entries, entry => Assert.False(entry.Mentions(word)));
        }
    }

    [Fact]
    public void Commit_timing_is_logged_under_the_dictation_stamp()
    {
        var logger = new CapturingLogger<HistoryWriter>();
        using var writer = new HistoryWriter(new FakeHistory(), logger);

        writer.Enqueue(Entry(1, SecretText), Audio(0.5f), dictationId: 42);
        Assert.True(writer.WaitForAcceptedWrites(BlockedThreads.SafetyTimeout));

        var commit = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Debug, commit.Level);
        Assert.StartsWith("#42 history committed", commit.Message, StringComparison.Ordinal);
        Assert.NotNull(commit.Value("TotalMs"));
        Assert.NotNull(commit.Value("WriteMs"));
        Assert.False(commit.Mentions("Zanzibar"));
    }

    [Fact]
    public void The_capture_handed_over_is_the_capture_written_and_each_write_keeps_its_own_buffer()
    {
        var history = new FakeHistory();
        using var writer = new HistoryWriter(history, new CapturingLogger<HistoryWriter>());
        var first = Audio(0.25f);
        var second = Audio(-0.5f);

        writer.Enqueue(Entry(1), first);
        writer.Enqueue(Entry(2), second);
        Assert.True(writer.WaitForAcceptedWrites(BlockedThreads.SafetyTimeout));

        var committed = history.Committed;
        Assert.Same(first, committed[0].Audio);
        Assert.Same(second, committed[1].Audio);
        Assert.NotSame(first.Samples, second.Samples);
        Assert.All(committed[0].Audio!.Samples, sample => Assert.Equal(0.25f, sample));
        Assert.All(committed[1].Audio!.Samples, sample => Assert.Equal(-0.5f, sample));
    }

    [Fact]
    public void A_real_repository_behind_the_barrier_reads_its_own_writes_and_clears_them()
    {
        using var database = ScribeDatabase.CreateInMemory();
        var repository = new HistoryRepository(database);
        using var writer = new HistoryWriter(repository, new CapturingLogger<HistoryWriter>());
        var ordered = Ordered(repository, writer);

        writer.Enqueue(Entry(1), Audio(0.25f));
        writer.Enqueue(Entry(2), null);

        var recent = ordered.GetRecent();
        Assert.Equal(new[] { 2, 1 }, recent.Select(entry => entry.AudioMilliseconds)); // newest first
        var withAudio = recent.Single(entry => entry.AudioMilliseconds == 1);

        // Stored as 16-bit PCM, so it reads back within half a quantization step of what was captured.
        var stored = ordered.GetAudio(withAudio.AudioBlobId!.Value)!.Samples;
        Assert.Equal(Audio(0.25f).Samples.Length, stored.Length);
        Assert.All(stored, sample => Assert.Equal(0.25f, sample, 0.5f / 32767f));

        writer.Enqueue(Entry(3), null);
        ordered.Clear();

        Assert.Empty(ordered.GetRecent());
    }

    [Fact]
    public void The_container_hands_out_one_writer_and_puts_the_barrier_in_front_of_the_concrete_repository()
    {
        var services = new ServiceCollection();
        services.AddScribeCore();
        services.AddSingleton(new AppPaths(Path.Combine(Path.GetTempPath(), $"scribe-di-{Guid.NewGuid():N}")));
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        using var provider = services.BuildServiceProvider();

        Assert.Same(provider.GetRequiredService<HistoryWriter>(), provider.GetRequiredService<IHistoryWriter>());
        Assert.IsType<OrderedHistoryRepository>(provider.GetRequiredService<IHistoryRepository>());
        Assert.Same(provider.GetRequiredService<IHistoryRepository>(), provider.GetRequiredService<IHistoryRepository>());
        Assert.NotNull(provider.GetRequiredService<HistoryRepository>());
    }

    private static HistoryEntry Entry(int number) => Entry(number, $"entry {number}");

    // AudioMilliseconds doubles as the entry's number, so order is easy to read back from a commit log.
    private static HistoryEntry Entry(int number, string text) =>
        new(Id: 0, TimestampUtc: Epoch.AddSeconds(number), Text: text, AudioMilliseconds: number, DecodeMilliseconds: 10);

    private static CapturedAudio Audio(float value) => new(Enumerable.Repeat(value, 1_600).ToArray(), 16_000);

    // For tests about waiting for room: the production wait bound is real time, and these tests must never race it.
    private static HistoryWriter Unbounded(IHistoryRepository history, CapturingLogger<HistoryWriter> logger) =>
        new(history, logger, producerWaitBound: Timeout.InfiniteTimeSpan, disposeTimeout: BlockedThreads.SafetyTimeout);

    private static OrderedHistoryRepository Ordered(IHistoryRepository inner, IHistoryWriter writer) =>
        new(inner, writer, new CapturingLogger<OrderedHistoryRepository>(), BlockedThreads.SafetyTimeout, BlockedThreads.SafetyTimeout);

    // Holds the first write inside the repository call until the test releases it, as a slow commit would.
    private static Action<HistoryEntry> HoldFirstWrite(ManualResetEventSlim inFirst, ManualResetEventSlim releaseFirst) =>
        entry =>
        {
            if (entry.AudioMilliseconds == 1)
            {
                inFirst.Set();
                releaseFirst.Wait();
            }
        };

    private sealed class FakeHistory : IHistoryRepository
    {
        private readonly object _sync = new();
        private readonly List<(HistoryEntry Entry, CapturedAudio? Audio)> _committed = [];
        private int _reads;

        public Action<HistoryEntry>? DuringAdd { get; init; }

        public IReadOnlyList<(HistoryEntry Entry, CapturedAudio? Audio)> Committed
        {
            get { lock (_sync) { return [.. _committed]; } }
        }

        public int Reads => Volatile.Read(ref _reads);

        public int? CommittedWhenCleared { get; private set; }

        public int? CommittedWhenDeleted { get; private set; }

        public int? CommittedWhenPruned { get; private set; }

        public HistoryEntry Add(HistoryEntry entry) => Add(entry, audio: null);

        public HistoryEntry Add(HistoryEntry entry, CapturedAudio? audio)
        {
            DuringAdd?.Invoke(entry);
            lock (_sync)
            {
                _committed.Add((entry, audio));
                return entry with { Id = _committed.Count };
            }
        }

        public long AddAudioBlob(CapturedAudio audio) => 0;

        public IReadOnlyList<HistoryEntry> GetRecent(int limit = 100)
        {
            Interlocked.Increment(ref _reads);
            lock (_sync)
            {
                return [.. _committed.Select(write => write.Entry).Reverse().Take(limit)];
            }
        }

        public CapturedAudio? GetAudio(long blobId) => null;

        public void SetAiRating(long id, AiRating rating)
        {
        }

        public void Delete(long id)
        {
            lock (_sync)
            {
                CommittedWhenDeleted = _committed.Count;
            }
        }

        public void Clear()
        {
            lock (_sync)
            {
                CommittedWhenCleared = _committed.Count;
                _committed.Clear();
            }
        }

        public int PruneOlderThan(DateTimeOffset cutoffUtc)
        {
            lock (_sync)
            {
                CommittedWhenPruned = _committed.Count;
            }

            return 0;
        }
    }
}
