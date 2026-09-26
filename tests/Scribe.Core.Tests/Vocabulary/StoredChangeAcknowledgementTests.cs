using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.PostProcessing;
using Scribe.Core.Vocabulary;
using static Scribe.Core.Tests.Vocabulary.TestVocabularies;
using ManualClock = Scribe.Core.Tests.Concurrency.ManualTimeProvider;
using ScriptedDictionary = Scribe.Core.Tests.Vocabulary.VocabularyPublisherTests.ScriptedDictionary;

namespace Scribe.Core.Tests.Vocabulary;

/// <summary>
/// A Settings save's wait for its vocabulary generation never closes over an edit made during it (round 3, A6). The Save
/// stores its document, then awaits the answer of the generation its application asked for, and the window stays editable
/// meanwhile: an edit made then is not in what was stored, so the Save may neither report success nor close. The window
/// hands its draft to <see cref="StoredChangeAcknowledgement"/> when the wait starts and the draft is read again once the
/// answer is in; the window's side is pinned by source (<see cref="VocabularyApplicationSourceTests"/>).
/// </summary>
public sealed class StoredChangeAcknowledgementTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task An_edit_made_while_the_saves_acknowledgement_is_held_is_reported_when_it_completes_and_never_closed_over()
    {
        // Astra's sequence, through the real publisher with its build held: a Save and close stores a dictionary
        // correction and asks for the generation; while that build waits, another row is edited in the window, which is
        // still interactive; then the build publishes and the answer is in.
        var dictionary = new ScriptedDictionary([Entry("harbour", "Harbour")]);
        var queued = new List<Action>();
        using var publisher = new VocabularyPublisher(
            new TestVocabularySource(Of(3)),
            dictionary,
            new TextPostProcessor(dictionary, NullLogger<TextPostProcessor>.Instance),
            NullLogger<VocabularyPublisher>.Instance,
            queued.Add);
        var starting = publisher.StartAsync();
        Assert.Single(queued)();
        queued.Clear();
        await starting.WaitAsync(Bound);

        var window = new Draft("harbour -> Harbour | quill more -> Quillmoor");
        dictionary.Entries = [Entry("harbour", "Harbour"), Entry("quill more", "Quillmoor")];
        var acknowledgement = StoredChangeAcknowledgement.Watch(publisher.RefreshAsync(), window.Read);
        var completing = acknowledgement.CompleteAsync();
        Assert.False(completing.IsCompleted, "The acknowledgement did not wait for the generation.");

        window.Value = "harbour -> Harbour | quill more -> Quillmoor | lan tern -> Lantern";
        Assert.Single(queued)();

        // The stored correction is in use, but the draft moved on after what was stored: not complete, so no closing.
        Assert.Equal(StoredChangeOutcome.ChangedWhileSaving, await completing.WaitAsync(Bound));
        Assert.Contains(publisher.Current.Dictionary, entry => entry.Replacement == "Quillmoor");
        Assert.DoesNotContain(publisher.Current.Dictionary, entry => entry.Replacement == "Lantern");
        Assert.Equal(2, window.Reads);
    }

    [Fact]
    public async Task With_nothing_edited_during_the_wait_the_save_is_in_effect_and_an_edit_undone_meanwhile_counts_as_none()
    {
        var generation = Generation();

        var untouched = new Draft("stored");
        var held = new TaskCompletionSource<VocabularyRefresh>(TaskCreationOptions.RunContinuationsAsynchronously);
        var quiet = StoredChangeAcknowledgement.Watch(held.Task, untouched.Read).CompleteAsync();
        held.SetResult(new VocabularyRefresh(VocabularyRefreshOutcome.Applied, generation));
        Assert.Equal(StoredChangeOutcome.InEffect, await quiet.WaitAsync(Bound));

        // Changed and changed back before the answer: what the window would store is what it stored, so nothing is lost.
        var undone = new Draft("stored");
        var again = new TaskCompletionSource<VocabularyRefresh>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reverted = StoredChangeAcknowledgement.Watch(again.Task, undone.Read).CompleteAsync();
        undone.Value = "stored, then edited";
        undone.Value = "stored";
        again.SetResult(new VocabularyRefresh(VocabularyRefreshOutcome.Applied, generation));
        Assert.Equal(StoredChangeOutcome.InEffect, await reverted.WaitAsync(Bound));
    }

    [Theory]
    [InlineData(VocabularyRefreshOutcome.NotApplied, false)]
    [InlineData(VocabularyRefreshOutcome.NotApplied, true)]
    [InlineData(VocabularyRefreshOutcome.TimedOut, false)]
    [InlineData(VocabularyRefreshOutcome.TimedOut, true)]
    [InlineData(VocabularyRefreshOutcome.Stopped, false)]
    [InlineData(VocabularyRefreshOutcome.Stopped, true)]
    public async Task A_change_not_in_use_yet_keeps_the_window_open_whatever_the_draft_did(VocabularyRefreshOutcome answered, bool edited)
    {
        var draft = new Draft("stored");
        var held = new TaskCompletionSource<VocabularyRefresh>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completing = StoredChangeAcknowledgement.Watch(held.Task, draft.Read).CompleteAsync();
        if (edited)
        {
            draft.Value = "edited";
        }

        held.SetResult(new VocabularyRefresh(answered, Generation()));
        Assert.Equal(StoredChangeOutcome.NotInUseYet, await completing.WaitAsync(Bound));
    }

    [Fact]
    public async Task A_save_released_by_its_deadline_is_not_in_use_yet_even_if_the_build_later_publishes()
    {
        // A5 and A6 together: the Save's build does not return, its deadline answers it, and the window stays open with
        // the saved-but-not-applied notice; a later publication changes nothing for that Save.
        var clock = new ManualClock();
        var dictionary = new ScriptedDictionary([]);
        var queued = new List<Action>();
        using var publisher = new VocabularyPublisher(
            new TestVocabularySource(Of(3)),
            dictionary,
            new TextPostProcessor(dictionary, NullLogger<TextPostProcessor>.Instance),
            NullLogger<VocabularyPublisher>.Instance,
            queued.Add,
            clock);
        var starting = publisher.StartAsync();
        Assert.Single(queued)();
        queued.Clear();
        await starting.WaitAsync(Bound);

        var completing = StoredChangeAcknowledgement.Watch(publisher.RefreshAsync(), new Draft("stored").Read).CompleteAsync();
        Assert.Single(clock.Timers, timer => !timer.IsDisposed).Fire();
        Assert.Equal(StoredChangeOutcome.NotInUseYet, await completing.WaitAsync(Bound));

        Assert.Single(queued)();
        Assert.Equal(StoredChangeOutcome.NotInUseYet, await completing);
    }

    [Fact]
    public async Task The_draft_is_read_when_the_wait_starts_and_again_on_the_callers_context_once_the_answer_is_in()
    {
        // The window's controls may be read only on its dispatcher, so both reads run there: the first as the wait starts,
        // the second in the continuation after the answer, which is completed here, on another thread.
        var held = new TaskCompletionSource<VocabularyRefresh>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = new ConcurrentQueue<(int Thread, SynchronizationContext? Context)>();
        var watching = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new DedicatedContext();

        var outcome = dispatcher.Run(async () =>
        {
            var acknowledgement = StoredChangeAcknowledgement.Watch(held.Task, () =>
            {
                reads.Enqueue((Environment.CurrentManagedThreadId, SynchronizationContext.Current));
                return "stored";
            });
            watching.SetResult();
            return await acknowledgement.CompleteAsync();
        });

        await watching.Task.WaitAsync(Bound);
        Assert.Single(reads);
        held.SetResult(new VocabularyRefresh(VocabularyRefreshOutcome.Applied, Generation()));

        Assert.Equal(StoredChangeOutcome.InEffect, await outcome.WaitAsync(Bound));
        Assert.Equal(2, reads.Count);
        Assert.All(reads, read =>
        {
            Assert.Equal(dispatcher.ThreadId, read.Thread);
            Assert.Same(dispatcher, read.Context);
        });
    }

    [Fact]
    public async Task A_draft_that_cannot_be_read_counts_as_changed_so_the_window_stays_open()
    {
        var generation = Generation();

        // Unreadable once the answer is in.
        var failsLater = new Draft("stored");
        var held = new TaskCompletionSource<VocabularyRefresh>(TaskCreationOptions.RunContinuationsAsynchronously);
        var later = StoredChangeAcknowledgement.Watch(held.Task, failsLater.Read).CompleteAsync();
        failsLater.Failure = new InvalidOperationException("The calling thread cannot access this object.");
        held.SetResult(new VocabularyRefresh(VocabularyRefreshOutcome.Applied, generation));
        Assert.Equal(StoredChangeOutcome.ChangedWhileSaving, await later.WaitAsync(Bound));

        // Unreadable as the wait starts: nothing to compare with, so it never counts as unchanged.
        var failsFirst = new Draft("stored") { Failure = new InvalidOperationException("not ready") };
        var first = StoredChangeAcknowledgement.Watch(Task.FromResult(new VocabularyRefresh(VocabularyRefreshOutcome.Applied, generation)), failsFirst.Read);
        failsFirst.Failure = null;
        Assert.Equal(StoredChangeOutcome.ChangedWhileSaving, await first.CompleteAsync().WaitAsync(Bound));
    }

    [Fact]
    public void The_changed_while_saving_notice_says_the_settings_were_saved_and_what_to_do_about_the_change()
    {
        Assert.Equal(
            "Settings saved, but something in this window changed while saving. Save again to keep that change.",
            VocabularyNotice.SettingsChangedWhileSaving);
        Assert.DoesNotContain('\u2014', VocabularyNotice.SettingsChangedWhileSaving);
        Assert.DoesNotContain('\u2013', VocabularyNotice.SettingsChangedWhileSaving);
        Assert.Throws<ArgumentNullException>(() => StoredChangeAcknowledgement.Watch(null!, () => string.Empty));
        Assert.Throws<ArgumentNullException>(() => StoredChangeAcknowledgement.Watch(Task.FromResult(new VocabularyRefresh(VocabularyRefreshOutcome.Applied, Generation())), null!));
    }

    private static VocabularyGeneration Generation()
    {
        var dictionary = new ScriptedDictionary([]);
        var processor = new TextPostProcessor(dictionary, NullLogger<TextPostProcessor>.Instance);
        return new VocabularyGeneration(1, [], Of(3), processor.Compile([], []));
    }

    // What the window would store, as the test sets it; counts the reads, and throws when told to.
    private sealed class Draft(string value)
    {
        private int _reads;

        public string Value { get; set; } = value;

        public Exception? Failure { get; set; }

        public int Reads => Volatile.Read(ref _reads);

        public string Read()
        {
            Interlocked.Increment(ref _reads);
            return Failure is { } failure ? throw failure : Value;
        }
    }

    // A single-threaded synchronization context on a thread of its own, as a dispatcher is: posted continuations run there,
    // in order, until the work it was given has finished.
    private sealed class DedicatedContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _posted = new();

        public int ThreadId { get; private set; }

        public override void Post(SendOrPostCallback d, object? state) => _posted.Add((d, state));

        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

        public Task<T> Run<T>(Func<Task<T>> work)
        {
            var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                ThreadId = Environment.CurrentManagedThreadId;
                SetSynchronizationContext(this);
                var running = work();
                running.ContinueWith(_ => _posted.CompleteAdding(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                foreach (var (callback, state) in _posted.GetConsumingEnumerable())
                {
                    callback(state);
                }

                Complete(running, result);
            })
            { IsBackground = true };
            thread.Start();
            return result.Task;
        }

        private static void Complete<T>(Task<T> finished, TaskCompletionSource<T> result)
        {
            if (finished.IsFaulted)
            {
                result.TrySetException(finished.Exception!.InnerExceptions);
            }
            else if (finished.IsCanceled)
            {
                result.TrySetCanceled();
            }
            else
            {
                result.TrySetResult(finished.GetAwaiter().GetResult());
            }
        }
    }
}
