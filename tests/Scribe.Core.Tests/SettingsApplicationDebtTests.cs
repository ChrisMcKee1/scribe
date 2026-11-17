using System.Collections.Concurrent;
using Scribe.Core.Settings;
using Scribe.Core.Tests.Concurrency;
using Scribe.Core.Vocabulary;
using ManualClock = Scribe.Core.Tests.Concurrency.ManualTimeProvider;

namespace Scribe.Core.Tests;

/// <summary>
/// The process-wide owner of what a committed Settings save still owes dictation (<see cref="SettingsApplicationDebt"/>), on
/// a clock the test owns: one background retry at a time, bounded by the waits it was given, cut short by settling the debt,
/// raising it again or shutting down, never polling and never waiting for anything. What a window and the protocol do with it
/// is in <c>WordPackSaveProtocolTests</c> and <c>SettingsApplicationDebtRealStoreTests</c>.
/// </summary>
public sealed class SettingsApplicationDebtTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Short = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Medium = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(30);
    private static readonly VocabularyRefresh Applied = new(VocabularyRefreshOutcome.Applied, VocabularyGeneration.Empty);

    [Fact]
    public void Without_an_attached_application_a_debt_is_only_kept_and_nothing_is_started()
    {
        using var rig = new Rig(attach: false);

        rig.Debt.Owe();

        Assert.True(rig.Debt.IsOwed);
        Assert.False(rig.Debt.IsRetrying);
        Assert.Empty(rig.Time.Timers);

        // The host attaching its application is what starts the retry for a debt that was already owed.
        rig.Debt.Attach(rig.Apply);
        Assert.True(rig.Debt.IsRetrying);
    }

    [Fact]
    public void With_no_waits_there_is_no_background_retry()
    {
        using var rig = new Rig(waits: []);

        rig.Debt.Owe();
        rig.Debt.Resume();

        Assert.True(rig.Debt.IsOwed);
        Assert.False(rig.Debt.IsRetrying);
        Assert.Empty(rig.Time.Timers);
    }

    [Fact]
    public async Task Each_attempt_waits_one_delay_on_the_clock_and_the_attempts_stop_with_the_delays()
    {
        using var rig = new Rig();
        rig.Fail();

        rig.Debt.Owe();

        var expectedApplications = 0;
        foreach (var delay in new[] { Short, Medium, Long })
        {
            var wait = await rig.NextTimerAsync();

            // Nothing is applied before the wait is up, and the wait is exactly the next delay.
            Assert.Equal(delay, wait.DueTime);
            Assert.Equal(expectedApplications++, rig.Applications);
            wait.Fire();
        }

        Assert.True(SpinWait.SpinUntil(() => !rig.Debt.IsRetrying, Bound), "The retry never ended after its last attempt.");

        // The attempts are used up: still owed, in memory, and nothing is left running or waiting.
        Assert.Equal(3, rig.Applications);
        Assert.True(rig.Debt.IsOwed);
        Assert.Equal(3, rig.Reports.Count);
        Assert.All(rig.Reports, report => Assert.Equal(SettingsApplicationStep.Retry, report.Step));
        Assert.Equal(3, rig.Time.Timers.Count);
        Assert.All(rig.Time.Timers, timer => Assert.True(timer.IsDisposed));

        // Raising the debt again is what buys a new run, from its first wait.
        rig.Debt.Owe();
        Assert.Equal(Short, (await rig.NextTimerAsync()).DueTime);
    }

    [Fact]
    public async Task A_retry_that_goes_through_ends_the_debt_and_the_run()
    {
        using var rig = new Rig();
        rig.Fail();
        rig.Debt.Owe();
        (await rig.NextTimerAsync()).Fire();
        var second = await rig.NextTimerAsync();
        Assert.Equal(Medium, second.DueTime);

        rig.Pass();
        second.Fire();

        Assert.True(SpinWait.SpinUntil(() => !rig.Debt.IsOwed && !rig.Debt.IsRetrying, Bound), "The retry did not settle the debt.");
        Assert.Equal(2, rig.Applications);
        Assert.Single(rig.Reports);
        Assert.Equal(2, rig.Time.Timers.Count);
    }

    [Fact]
    public async Task Raising_the_debt_again_restarts_the_one_retry_from_its_first_wait()
    {
        using var rig = new Rig();
        rig.Fail();
        rig.Debt.Owe();
        (await rig.NextTimerAsync()).Fire();
        var second = await rig.NextTimerAsync();
        Assert.Equal(Medium, second.DueTime);

        // Another Save committed and could not be applied while the retry waited for its second attempt.
        rig.Debt.Owe();
        var restarted = await rig.NextTimerAsync();

        Assert.Equal(Short, restarted.DueTime);
        Assert.True(second.IsDisposed, "The wait it cut short was left running.");

        // One run: however often the debt is raised, one wait is live.
        rig.Debt.Owe();
        var again = await rig.NextTimerAsync();
        Assert.Equal(Short, again.DueTime);
        Assert.Single(rig.Time.Timers, timer => !timer.IsDisposed);
        Assert.Equal(1, rig.Applications);
    }

    [Fact]
    public void An_application_that_began_before_the_debt_was_raised_again_cannot_settle_it()
    {
        using var rig = new Rig(attach: false);
        rig.Debt.Owe();
        var stale = rig.Debt.Begin();

        // It may have read the stored settings before the Save that raised the debt committed them.
        rig.Debt.Owe();

        Assert.False(rig.Debt.Settle(stale));
        Assert.True(rig.Debt.IsOwed);
        Assert.True(rig.Debt.Settle(rig.Debt.Begin()));
        Assert.False(rig.Debt.IsOwed);
    }

    [Fact]
    public async Task An_attempt_in_flight_when_the_debt_is_raised_again_does_not_settle_it()
    {
        using var rig = new Rig();
        var answer = new TaskCompletionSource<VocabularyRefresh>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Hold(answer);
        rig.Debt.Owe();
        (await rig.NextTimerAsync()).Fire();
        Assert.True(await rig.WaitForApplicationAsync(), "The attempt never began.");

        // A Save committed while the attempt read the stored settings, and its own application failed.
        rig.Debt.Owe();
        answer.SetResult(Applied);

        // The attempt began before that Save, so it settles nothing, and the retry waits again from its first wait.
        var again = await rig.NextTimerAsync();
        Assert.Equal(Short, again.DueTime);
        Assert.True(rig.Debt.IsOwed);

        rig.Pass();
        again.Fire();
        Assert.True(SpinWait.SpinUntil(() => !rig.Debt.IsOwed, Bound), "The later attempt did not settle the debt.");
        Assert.Equal(2, rig.Applications);
    }

    [Fact]
    public async Task Settling_the_debt_ends_the_wait_without_an_attempt()
    {
        using var rig = new Rig();
        rig.Debt.Owe();
        var wait = await rig.NextTimerAsync();

        // A window paid it in the foreground.
        Assert.True(rig.Debt.Settle(rig.Debt.Begin()));

        Assert.True(SpinWait.SpinUntil(() => !rig.Debt.IsRetrying, Bound), "The retry kept waiting for a debt that was paid.");
        Assert.True(wait.IsDisposed);
        Assert.False(rig.Debt.IsOwed);
        Assert.Equal(0, rig.Applications);
    }

    [Fact]
    public async Task Resume_starts_an_exhausted_retry_again_and_does_nothing_for_a_debt_that_is_paid()
    {
        using var rig = new Rig(waits: [Short]);
        rig.Fail();
        rig.Debt.Owe();
        (await rig.NextTimerAsync()).Fire();
        Assert.True(SpinWait.SpinUntil(() => !rig.Debt.IsRetrying, Bound), "The only attempt never ended the run.");
        Assert.True(rig.Debt.IsOwed);

        // A Settings window opening is what brings it back.
        rig.Debt.Resume();

        Assert.True(rig.Debt.IsRetrying);
        Assert.Equal(Short, (await rig.NextTimerAsync()).DueTime);

        using var idle = new Rig();
        idle.Debt.Resume();
        Assert.False(idle.Debt.IsRetrying);
        Assert.Empty(idle.Time.Timers);
    }

    [Fact]
    public async Task Resume_leaves_an_active_wait_and_its_attempt_budget_unchanged()
    {
        using var rig = new Rig(waits: [Short, Medium]);
        rig.Fail();
        rig.Debt.Owe();
        var first = await rig.NextTimerAsync();

        rig.Debt.Resume();
        rig.Debt.Resume();
        first.Fire();

        var second = await rig.NextTimerAsync();
        Assert.Equal(Medium, second.DueTime);
        Assert.Equal(1, rig.Applications);
        rig.Debt.Resume();
        second.Fire();
        Assert.True(SpinWait.SpinUntil(() => !rig.Debt.IsRetrying, Bound), "Resume restarted an active retry budget.");
        Assert.Equal(2, rig.Applications);
        Assert.Equal(2, rig.Time.Timers.Count);
        Assert.True(rig.Debt.IsOwed);
    }

    [Fact]
    public async Task Dispose_ends_the_wait_and_nothing_is_applied_afterwards()
    {
        using var rig = new Rig();
        rig.Debt.Owe();
        var wait = await rig.NextTimerAsync();

        rig.Debt.Dispose();

        Assert.True(SpinWait.SpinUntil(() => !rig.Debt.IsRetrying, Bound), "The retry outlived the shutdown.");
        Assert.True(wait.IsDisposed);
        Assert.Equal(0, rig.Applications);

        // The debt is still owed, in memory, and nothing starts again: not by raising it, not by resuming, not by attaching.
        Assert.True(rig.Debt.IsOwed);
        rig.Debt.Owe();
        rig.Debt.Resume();
        rig.Debt.Attach(rig.Apply);
        rig.Debt.Dispose();
        Assert.False(rig.Debt.IsRetrying);

        // A tick of the wait that was cut short arriving late applies nothing either.
        wait.Fire();
        Assert.Single(rig.Time.Timers);
        Assert.Equal(0, rig.Applications);
    }

    [Fact]
    public async Task Dispose_never_waits_for_an_attempt_in_flight_and_its_late_answer_is_ignored()
    {
        using var rig = new Rig();
        var answer = new TaskCompletionSource<VocabularyRefresh>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Hold(answer);
        rig.Debt.Owe();
        (await rig.NextTimerAsync()).Fire();
        Assert.True(await rig.WaitForApplicationAsync(), "The attempt never began.");

        var disposing = Task.Run(rig.Debt.Dispose);

        Assert.True(await HangGuard.Completes(disposing, Bound), "Dispose waited for the attempt in flight.");
        Assert.True(SpinWait.SpinUntil(() => !rig.Debt.IsRetrying, Bound), "The run outlived the shutdown.");
        Assert.True(rig.Debt.IsOwed);

        // Shutting down is not a failure, and an answer that comes after it settles nothing.
        answer.SetResult(Applied);
        Assert.Empty(rig.Reports);
        Assert.True(rig.Debt.IsOwed);
    }

    [Fact]
    public async Task A_failure_sink_that_throws_changes_nothing()
    {
        using var rig = new Rig(sink: (_, _) => throw new InvalidOperationException("injected: the sink threw"));
        rig.Fail();

        // Neither the foreground's report nor the retry's can be made to fail by the sink.
        rig.Debt.Report(SettingsApplicationStep.Apply, new IOException("injected"));
        rig.Debt.Owe();
        (await rig.NextTimerAsync()).Fire();

        var next = await rig.NextTimerAsync();
        Assert.Equal(Medium, next.DueTime);
        Assert.Equal(1, rig.Applications);
        Assert.True(rig.Debt.IsOwed);
    }

    [Fact]
    public async Task An_application_that_throws_before_it_returns_a_task_is_an_attempt_that_did_not_go_through()
    {
        using var rig = new Rig();
        rig.FailBeforeAnyTask();
        rig.Debt.Owe();
        (await rig.NextTimerAsync()).Fire();

        var next = await rig.NextTimerAsync();

        Assert.Equal(Medium, next.DueTime);
        var report = Assert.Single(rig.Reports);
        Assert.Equal(SettingsApplicationStep.Retry, report.Step);
        Assert.IsType<IOException>(report.Failure);
        Assert.True(rig.Debt.IsOwed);
    }

    [Fact]
    public void A_report_with_no_sink_does_nothing()
    {
        using var debt = new SettingsApplicationDebt();

        debt.Report(SettingsApplicationStep.Settlement, new IOException("injected"));
        debt.Owe();

        Assert.True(debt.IsOwed);
        Assert.False(debt.IsRetrying);
    }

    [Fact]
    public void The_default_waits_are_five_attempts_over_a_quarter_of_an_hour_at_most()
    {
        var waits = SettingsApplicationDebt.DefaultRetryDelays;

        Assert.Equal(5, waits.Count);
        Assert.Equal(waits.OrderBy(wait => wait), waits);
        Assert.True(waits.Aggregate(TimeSpan.Zero, (total, wait) => total + wait) <= TimeSpan.FromMinutes(15));
    }

    private sealed class Rig : IDisposable
    {
        private readonly SemaphoreSlim _timerMade = new(0);
        private readonly SemaphoreSlim _applicationBegan = new(0);
        private readonly ConcurrentQueue<ManualTimer> _timers = new();
        private readonly ConcurrentQueue<(SettingsApplicationStep Step, Exception Failure)> _reports = new();
        private Func<Task<VocabularyRefresh>> _application = () => Task.FromResult(Applied);
        private int _applications;

        public Rig(IReadOnlyList<TimeSpan>? waits = null, bool attach = true, Action<SettingsApplicationStep, Exception>? sink = null)
        {
            Time.Created = timer =>
            {
                _timers.Enqueue(timer);
                _timerMade.Release();
            };
            Debt = new SettingsApplicationDebt(
                Time,
                sink ?? ((step, failure) => _reports.Enqueue((step, failure))),
                waits ?? [Short, Medium, Long]);
            if (attach)
            {
                Debt.Attach(Apply);
            }
        }

        public ManualClock Time { get; } = new();

        public SettingsApplicationDebt Debt { get; }

        public int Applications => Volatile.Read(ref _applications);

        public IReadOnlyCollection<(SettingsApplicationStep Step, Exception Failure)> Reports => _reports;

        // What the host's application does: counted, then whatever the test set up for it.
        public Task<VocabularyRefresh> Apply()
        {
            Interlocked.Increment(ref _applications);
            var application = Volatile.Read(ref _application);
            try
            {
                return application();
            }
            finally
            {
                _applicationBegan.Release();
            }
        }

        public void Pass() => Volatile.Write(ref _application, () => Task.FromResult(Applied));

        public void Fail() => Volatile.Write(
            ref _application,
            () => Task.FromException<VocabularyRefresh>(new IOException("injected: the stored settings cannot be read")));

        public void FailBeforeAnyTask() => Volatile.Write(
            ref _application,
            () => throw new IOException("injected: the stored settings cannot be read"));

        public void Hold(TaskCompletionSource<VocabularyRefresh> answer) => Volatile.Write(ref _application, () => answer.Task);

        public Task<bool> WaitForApplicationAsync() => _applicationBegan.WaitAsync(Bound);

        // The next wait the retry made, in the order they were made.
        public async Task<ManualTimer> NextTimerAsync()
        {
            Assert.True(await _timerMade.WaitAsync(Bound), "The retry never made its next wait.");
            Assert.True(_timers.TryDequeue(out var timer));
            return timer!;
        }

        public void Dispose() => Debt.Dispose();
    }
}
