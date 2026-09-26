using System.Collections.Concurrent;
using Scribe.Core.Cleanup;
using Scribe.Core.Tests.Concurrency;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Scribe.Core.Tests;

/// <summary>
/// F6: an initialization that a manual Foundry Local load cancels stops without publishing anything,
/// so the Initializing or Downloading status it had reached is left with nothing to finish it. The
/// load restarts it unless something newer owns the status, identical saves coalesce only onto an
/// initialization that is actually live, and a status no run will finish is never treated as one
/// that is in progress. Interleavings are forced with gates, never with sleeps.
/// </summary>
public sealed class CleanupRecoveryTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    private const string MissingAlias = "no-such-model";
    private const string Dictated = "hello there";
    private const string CleanedText = "Hello there.";

    [Fact]
    public async Task A_failed_manual_load_restarts_the_initialization_it_interrupted()
    {
        await using var harness = NewHarness();
        await ParkInitializationInModelLoadAsync(harness);
        harness.Phi.LoadFailure = new InvalidOperationException("The model file is damaged.");

        Assert.False(await harness.Service.LoadFoundryModelAsync(CleanupHarness.OtherAlias).WaitAsync(Bound));

        await AssertServingAsync(harness);
    }

    [Fact]
    public async Task A_manual_load_cancelled_while_it_waits_restarts_the_initialization_it_interrupted()
    {
        await using var harness = NewHarness();
        var svc = harness.Service;

        // The initialization is inside a native call that ignores cancellation, so it keeps the init
        // lock after the load cancels it, and the load is still waiting for that lock when it is
        // cancelled in turn.
        harness.Runtime.EpGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Runtime.IgnoreCancellation = true;
        svc.Configure(CleanupHarness.FoundryOn());
        await harness.Runtime.EpStarted.Task.WaitAsync(Bound);

        using var cancel = new CancellationTokenSource();
        var load = svc.LoadFoundryModelAsync(CleanupHarness.OtherAlias, cancellationToken: cancel.Token);
        Assert.False(load.IsCompleted, "The load waits for the init lock the interrupted run still holds.");
        cancel.Cancel();
        Assert.False(await load.WaitAsync(Bound));

        harness.Runtime.EpGate.SetResult();
        await AssertServingAsync(harness);
    }

    [Fact]
    public async Task A_manual_load_that_times_out_restarts_the_initialization_it_interrupted()
    {
        await using var harness = NewHarness();
        var svc = harness.Service;
        await ParkInitializationInModelLoadAsync(harness);

        // The load never finishes by itself: only the caller's deadline ends it, the way the settings
        // window's ten minute limit does.
        harness.Phi.LoadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new CancellationTokenSource();
        var load = svc.LoadFoundryModelAsync(CleanupHarness.OtherAlias, cancellationToken: deadline.Token);
        await harness.Phi.LoadStarted.Task.WaitAsync(Bound);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(50));

        Assert.False(await load.WaitAsync(Bound));
        await AssertServingAsync(harness);
    }

    [Fact]
    public async Task A_manual_load_naming_a_model_the_catalog_does_not_have_restarts_the_initialization_it_interrupted()
    {
        await using var harness = NewHarness();
        var messages = new ConcurrentQueue<string>();
        await ParkInitializationInModelLoadAsync(harness);

        var loaded = await harness.Service
            .LoadFoundryModelAsync(MissingAlias, new InlineProgress(messages.Enqueue))
            .WaitAsync(Bound);

        Assert.False(loaded);
        Assert.Contains(messages, m => m.Contains("was not found in the Foundry catalog", StringComparison.Ordinal));
        await AssertServingAsync(harness);
    }

    [Fact]
    public async Task A_successful_manual_load_while_a_cloud_provider_initializes_restarts_that_provider()
    {
        // Loading an on-device model by hand never reconciles a cloud configuration, so without the
        // restart even a load that works left the saved provider on "Connecting" for good.
        var probeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        var http = new ScriptedHttpHandler(async (_, ct) =>
        {
            if (Interlocked.Increment(ref requests) == 1)
            {
                probeStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }

            return ScriptedHttpHandler.ChatCompletion(CleanedText);
        });
        await using var harness = NewHarness(http);
        var svc = harness.Service;
        svc.Configure(CleanupHarness.Custom("https://cleanup.example.test/v1"));
        await probeStarted.Task.WaitAsync(Bound);

        Assert.True(await svc.LoadFoundryModelAsync(CleanupHarness.OtherAlias).WaitAsync(Bound));

        await AssertServingAsync(harness);
    }

    [Fact]
    public async Task A_successful_load_of_another_model_during_initialization_reports_the_configured_model_unloaded()
    {
        await using var harness = NewHarness();
        var svc = harness.Service;
        await ParkInitializationInModelLoadAsync(harness);

        Assert.True(await svc.LoadFoundryModelAsync(CleanupHarness.OtherAlias).WaitAsync(Bound));

        Assert.Equal(CleanupStatus.Unavailable, svc.Status);
        Assert.Contains("unloaded", svc.StatusDetail, StringComparison.Ordinal);
        Assert.Equal(1, harness.Qwen.LoadCalls);
    }

    [Fact]
    public async Task A_successful_load_of_the_configured_model_during_initialization_rebuilds_cleanup()
    {
        await using var harness = NewHarness();
        await ParkInitializationInModelLoadAsync(harness);

        Assert.True(await harness.Service.LoadFoundryModelAsync(CleanupHarness.FoundryAlias).WaitAsync(Bound));

        await AssertServingAsync(harness);
    }

    [Fact]
    public async Task A_successful_load_does_not_invalidate_a_configuration_saved_while_it_was_loading()
    {
        await using var harness = NewHarness();
        var svc = harness.Service;
        await ParkInitializationInModelLoadAsync(harness);
        var loadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Phi.LoadGate = loadGate;
        var load = svc.LoadFoundryModelAsync(CleanupHarness.OtherAlias);
        await harness.Phi.LoadStarted.Task.WaitAsync(Bound);

        // A different model is saved while the load holds the init lock; its initialization queues
        // behind the load and loads its own model once the load is done.
        svc.Configure(CleanupHarness.FoundryOn(CleanupHarness.ThirdAlias));
        var statuses = new CleanupStatusRecorder(svc);

        loadGate.SetResult();
        Assert.True(await load.WaitAsync(Bound));
        await AssertServingAsync(harness);

        // The log line too, not only the recorded statuses: the newer initialization publishes as soon
        // as the load releases the init lock, and a status read inside the event can already be its.
        Assert.DoesNotContain(statuses.Snapshot(), s => s.Status == CleanupStatus.Unavailable);
        Assert.DoesNotContain(harness.Log.Entries, e => e.Message.Contains("Cleanup paused", StringComparison.Ordinal));
        Assert.Equal(CleanupHarness.ThirdAlias, await svc.GetLoadedFoundryModelAsync());
    }

    [Fact]
    public async Task A_configuration_that_becomes_ready_before_a_load_reports_its_change_stays_ready()
    {
        await using var harness = NewHarness();
        var svc = harness.Service;
        await ParkInitializationInModelLoadAsync(harness);
        var reachedReady = LandNewerConfigurationWhenLoadHasDecided(harness, CleanupHarness.FoundryOn(CleanupHarness.ThirdAlias));
        var statuses = new CleanupStatusRecorder(svc);

        // Loading another model is decided as an eviction of the configured one. Before that is
        // reported, a newer configuration initializes all the way to Ready.
        Assert.True(await svc.LoadFoundryModelAsync(CleanupHarness.OtherAlias).WaitAsync(Bound));

        Assert.True(reachedReady(), "The newer configuration became Ready before the load reported its change.");
        Assert.DoesNotContain(statuses.Snapshot(), s => s.Status == CleanupStatus.Unavailable);
        await AssertServingAsync(harness);
        Assert.Equal(CleanupHarness.ThirdAlias, await svc.GetLoadedFoundryModelAsync());
    }

    [Fact]
    public async Task A_rebuild_overtaken_by_a_newer_configuration_publishes_nothing()
    {
        await using var harness = NewHarness();
        var svc = harness.Service;
        await ParkInitializationInModelLoadAsync(harness);
        var reachedReady = LandNewerConfigurationWhenLoadHasDecided(harness, CleanupHarness.FoundryOn(CleanupHarness.ThirdAlias));
        var statuses = new CleanupStatusRecorder(svc);

        // Loading the configured model is decided as a rebuild; a newer configuration overtakes it.
        Assert.True(await svc.LoadFoundryModelAsync(CleanupHarness.FoundryAlias).WaitAsync(Bound));

        Assert.True(reachedReady(), "The newer configuration became Ready before the load reported its change.");
        Assert.DoesNotContain(statuses.Snapshot(), s => s.Detail == "Re-enabling cleanup with the reloaded model\u2026");
        Assert.Equal(CleanupStatus.Ready, svc.Status);
        await AssertServingAsync(harness);
        Assert.Equal(CleanupHarness.ThirdAlias, await svc.GetLoadedFoundryModelAsync());
    }

    [Fact]
    public async Task An_unload_overtaken_by_a_new_initialization_of_the_same_model_leaves_it_ready()
    {
        await using var harness = NewHarness();
        var svc = harness.Service;
        svc.Configure(CleanupHarness.FoundryOn());
        await AssertServingAsync(harness);

        // The unload is decided as an eviction and drops the agent. Before it is reported, the user
        // saves again, and that save's initialization loads the model back and becomes Ready.
        var reachedReady = LandNewerConfigurationWhenLoadHasDecided(harness, CleanupHarness.FoundryOn());
        var statuses = new CleanupStatusRecorder(svc);

        Assert.True(await svc.UnloadFoundryModelAsync(CleanupHarness.FoundryAlias).WaitAsync(Bound));

        Assert.True(reachedReady(), "The new initialization became Ready before the unload reported its change.");
        Assert.DoesNotContain(statuses.Snapshot(), s => s.Status == CleanupStatus.Unavailable);
        await AssertServingAsync(harness);
        Assert.Equal(CleanupHarness.FoundryAlias, await svc.GetLoadedFoundryModelAsync());
    }

    [Fact]
    public async Task A_second_load_that_cancels_a_pending_rebuild_owns_its_outcome()
    {
        // The reported sequence: loading the configured model reserves a rebuild, and before that rebuild
        // starts a second load cancels it and loads another model. The second load owns the outcome, so
        // cleanup must end Unavailable, not on a stale "Re-enabling" that nothing will ever finish.
        await using var harness = NewHarness();
        var svc = harness.Service;
        await MakeUnavailableAfterAFailedInitializationAsync(harness);
        var second = RunOnceWhenLoadHasDecided(harness, () => svc.LoadFoundryModelAsync(CleanupHarness.OtherAlias));

        Assert.True(await svc.LoadFoundryModelAsync(CleanupHarness.FoundryAlias).WaitAsync(Bound));
        Assert.True(await second().WaitAsync(Bound));

        Assert.Equal(CleanupStatus.Unavailable, svc.Status);
        Assert.Contains("unloaded", svc.StatusDetail, StringComparison.Ordinal);
        Assert.Equal(CleanupOutcome.Failed, (await svc.CleanAsync(Dictated).WaitAsync(Bound)).Outcome);
        Assert.Equal(CleanupHarness.OtherAlias, await svc.GetLoadedFoundryModelAsync());
    }

    [Fact]
    public async Task A_second_load_that_cancels_a_pending_rebuild_and_then_fails_restarts_it()
    {
        // The second load takes over the rebuild it cancelled; failing, it has to restart it, which it can
        // only do if the rebuild's "in progress" status was already in place when it was cancelled.
        await using var harness = NewHarness();
        var svc = harness.Service;
        await MakeUnavailableAfterAFailedInitializationAsync(harness);
        harness.Phi.LoadFailure = new InvalidOperationException("The model file is damaged.");
        var second = RunOnceWhenLoadHasDecided(harness, () => svc.LoadFoundryModelAsync(CleanupHarness.OtherAlias));

        Assert.True(await svc.LoadFoundryModelAsync(CleanupHarness.FoundryAlias).WaitAsync(Bound));
        Assert.False(await second().WaitAsync(Bound));

        await AssertServingAsync(harness);
        Assert.Equal(CleanupHarness.FoundryAlias, await svc.GetLoadedFoundryModelAsync());
    }

    [Fact]
    public async Task An_unload_before_a_pending_rebuild_starts_leaves_the_rebuild_to_finish()
    {
        // An unload cancels nothing, so the reserved rebuild still owns the status: it runs, loads the
        // configured model back and becomes Ready.
        await using var harness = NewHarness();
        var svc = harness.Service;
        await MakeUnavailableAfterAFailedInitializationAsync(harness);
        var second = RunOnceWhenLoadHasDecided(harness, () => svc.UnloadFoundryModelAsync(null));

        Assert.True(await svc.LoadFoundryModelAsync(CleanupHarness.FoundryAlias).WaitAsync(Bound));
        Assert.True(await second().WaitAsync(Bound));

        await AssertServingAsync(harness);
        Assert.Equal(CleanupHarness.FoundryAlias, await svc.GetLoadedFoundryModelAsync());
    }

    [Fact]
    public async Task A_save_of_another_model_before_a_pending_rebuild_starts_wins()
    {
        await using var harness = NewHarness();
        var svc = harness.Service;
        await MakeUnavailableAfterAFailedInitializationAsync(harness);
        var second = RunOnceWhenLoadHasDecided(harness, () =>
        {
            svc.Configure(CleanupHarness.FoundryOn(CleanupHarness.ThirdAlias));
            return Task.FromResult(true);
        });

        Assert.True(await svc.LoadFoundryModelAsync(CleanupHarness.FoundryAlias).WaitAsync(Bound));
        Assert.True(await second().WaitAsync(Bound));

        await AssertServingAsync(harness);
        Assert.Equal(CleanupHarness.ThirdAlias, await svc.GetLoadedFoundryModelAsync());
    }

    [Fact]
    public async Task An_identical_save_before_a_pending_rebuild_starts_is_coalesced_onto_it()
    {
        // The rebuild's "in progress" status is written when it is reserved, so it already counts as a
        // live initialization and the identical save leaves it alone.
        await using var harness = NewHarness();
        var svc = harness.Service;
        await MakeUnavailableAfterAFailedInitializationAsync(harness);
        var statuses = new CleanupStatusRecorder(svc);
        var second = RunOnceWhenLoadHasDecided(harness, () =>
        {
            svc.Configure(CleanupHarness.FoundryOn());
            return Task.FromResult(true);
        });

        Assert.True(await svc.LoadFoundryModelAsync(CleanupHarness.FoundryAlias).WaitAsync(Bound));
        Assert.True(await second().WaitAsync(Bound));

        await AssertServingAsync(harness);
        Assert.DoesNotContain(statuses.Snapshot(), s => s.Detail == "Applying new settings\u2026");
    }

    [Fact]
    public async Task A_load_that_cancels_a_save_before_its_initialization_starts_owns_its_outcome()
    {
        await using var harness = NewHarness();
        var svc = harness.Service;
        svc.Configure(CleanupHarness.FoundryOn());
        await AssertServingAsync(harness);

        // The save's first notification is raised after its initialization is reserved and before it is
        // started: a load run from there cancels the reserved initialization and loads another model.
        var loading = 0;
        Task<bool>? load = null;
        svc.StatusChanged += () =>
        {
            if (svc.StatusDetail == "Applying new settings\u2026" && Interlocked.Exchange(ref loading, 1) == 0)
            {
                load = svc.LoadFoundryModelAsync(CleanupHarness.OtherAlias);
                load.Wait(Bound);
            }
        };

        svc.Configure(CleanupHarness.FoundryOn(CleanupHarness.ThirdAlias));

        Assert.NotNull(load);
        Assert.True(await load.WaitAsync(Bound));
        Assert.Equal(CleanupStatus.Unavailable, svc.Status);
        Assert.Contains("unloaded", svc.StatusDetail, StringComparison.Ordinal);
        Assert.Equal(CleanupHarness.OtherAlias, await svc.GetLoadedFoundryModelAsync());
    }

    [Fact]
    public async Task A_superseded_initialization_that_fails_late_writes_nothing_over_its_successor()
    {
        // Foundry Local runs a native load to completion once it has started, whatever its token says,
        // and then reports that load's own failure. The run a save superseded must not publish it: the
        // status belongs to the configuration saved since.
        await using var harness = NewHarness();
        var svc = harness.Service;
        var gate = ArmUncooperativeFailingLoad(harness.Qwen);
        svc.Configure(CleanupHarness.FoundryOn());
        await harness.Qwen.LoadStarted.Task.WaitAsync(Bound);

        svc.Configure(CleanupHarness.FoundryOn(CleanupHarness.ThirdAlias));
        var statuses = new CleanupStatusRecorder(svc);
        gate.SetResult();

        await AssertServingAsync(harness);
        Assert.Equal(CleanupHarness.ThirdAlias, await svc.GetLoadedFoundryModelAsync());
        Assert.DoesNotContain(statuses.Snapshot(), s => s.Status == CleanupStatus.Unavailable);
    }

    [Fact]
    public async Task A_superseded_run_that_fails_late_cannot_stop_its_interrupted_successor_restarting()
    {
        // The stale failure used to land on the successor's "in progress" status. A load that then
        // interrupted the successor and failed read it as the successor's own outcome and restarted
        // nothing, so the configuration the user had just saved never initialized at all.
        await using var harness = NewHarness();
        var svc = harness.Service;
        var gate = ArmUncooperativeFailingLoad(harness.Qwen);
        svc.Configure(CleanupHarness.FoundryOn());
        await harness.Qwen.LoadStarted.Task.WaitAsync(Bound);
        svc.Configure(CleanupHarness.FoundryOn(CleanupHarness.ThirdAlias));

        // Interrupts the saved configuration's initialization, still queued behind the superseded run.
        harness.Phi.LoadFailure = new InvalidOperationException("The model file is damaged.");
        var load = svc.LoadFoundryModelAsync(CleanupHarness.OtherAlias);
        gate.SetResult();

        Assert.False(await load.WaitAsync(Bound));
        await AssertServingAsync(harness);
        Assert.Equal(CleanupHarness.ThirdAlias, await svc.GetLoadedFoundryModelAsync());
    }

    [Fact]
    public async Task A_failed_eviction_reload_leaves_a_configuration_changed_meanwhile_alone()
    {
        // A dictation found its model evicted and could not load it back. Cleanup was switched off while
        // it tried, and the failure is about the configuration that was replaced, not this one.
        var evicted = 0;
        var http = new ScriptedHttpHandler((_, _) => Task.FromResult(Volatile.Read(ref evicted) == 1
            ? ScriptedHttpHandler.Json(System.Net.HttpStatusCode.BadRequest,
                "{\"error\":{\"message\":\"Model 'qwen3-1.7b-generic-cpu:2' is not loaded. Please load the model before getting a ChatClient.\"}}")
            : ScriptedHttpHandler.ChatCompletion(CleanedText)));
        await using var harness = NewHarness(http);
        var svc = harness.Service;
        svc.Configure(CleanupHarness.FoundryOn());
        await AssertServingAsync(harness);

        Volatile.Write(ref evicted, 1);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Qwen.LoadGate = gate;
        harness.Qwen.LoadFailure = new InvalidOperationException("The model file is damaged.");
        harness.Qwen.LoadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaning = svc.CleanAsync(Dictated);
        await harness.Qwen.LoadStarted.Task.WaitAsync(Bound);

        svc.Configure(CleanupHarness.FoundryOn() with { Enabled = false });
        Assert.Equal(CleanupStatus.Disabled, svc.Status);
        gate.SetResult();

        Assert.NotEqual(CleanupOutcome.Cleaned, (await cleaning.WaitAsync(Bound)).Outcome);
        Assert.Equal(CleanupStatus.Disabled, svc.Status);
    }

    [Fact]
    public async Task An_identical_save_while_initialization_is_live_is_coalesced()
    {
        // Save, then Save and close: the second must not throw away a run that is still going.
        await using var harness = NewHarness();
        var svc = harness.Service;
        var gate = await ParkInitializationInModelLoadAsync(harness);
        var statuses = new CleanupStatusRecorder(svc);

        svc.Configure(CleanupHarness.FoundryOn());

        Assert.Empty(statuses.Snapshot());
        Assert.Equal(CleanupStatus.Downloading, svc.Status);

        gate.SetResult();
        await AssertServingAsync(harness);
        Assert.Equal(1, harness.Qwen.LoadCalls);
    }

    [Fact]
    public async Task An_identical_save_after_a_manual_load_interrupted_initialization_starts_it_again()
    {
        await using var harness = NewHarness();
        var svc = harness.Service;
        await ParkInitializationInModelLoadAsync(harness);
        var loadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Phi.LoadGate = loadGate;
        harness.Phi.LoadFailure = new InvalidOperationException("The model file is damaged.");
        var load = svc.LoadFoundryModelAsync(CleanupHarness.OtherAlias);
        await harness.Phi.LoadStarted.Task.WaitAsync(Bound);
        var statuses = new CleanupStatusRecorder(svc);

        // The status still reads Downloading, but the run that was downloading no longer exists.
        svc.Configure(CleanupHarness.FoundryOn());

        Assert.Contains(statuses.Snapshot(), s => s is { Status: CleanupStatus.Initializing, Detail: "Applying new settings\u2026" });

        loadGate.SetResult();
        Assert.False(await load.WaitAsync(Bound));
        await AssertServingAsync(harness);
    }

    [Fact]
    public async Task A_newer_configuration_that_arrives_during_recovery_wins()
    {
        await using var harness = NewHarness();
        var svc = harness.Service;
        var statuses = new CleanupStatusRecorder(svc);
        using var verdict = new ManualResetEventSlim();
        svc.StatusChanged += () =>
        {
            if (svc.Status == CleanupStatus.Unavailable)
            {
                verdict.Set();
            }
        };

        harness.Runtime.EpGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Runtime.IgnoreCancellation = true;
        svc.Configure(CleanupHarness.FoundryOn());
        await harness.Runtime.EpStarted.Task.WaitAsync(Bound);

        using var cancel = new CancellationTokenSource();
        var reachedVerdict = false;
        var progress = new InlineProgress(message =>
        {
            if (!message.EndsWith("was cancelled.", StringComparison.Ordinal))
            {
                return;
            }

            // The load has given up and its recovery runs next, on this thread. First let the
            // interrupted run stop, and the newer configuration reach a verdict of its own.
            harness.Runtime.EpGate.SetResult();
            reachedVerdict = verdict.Wait(Bound);
        });
        var load = svc.LoadFoundryModelAsync(CleanupHarness.OtherAlias, progress, cancel.Token);

        // The user saves a different model while the load is still waiting for the init lock.
        svc.Configure(CleanupHarness.FoundryOn(MissingAlias));
        cancel.Cancel();

        Assert.False(await load.WaitAsync(Bound));
        Assert.True(reachedVerdict, "The newer configuration reached its own verdict before recovery ran.");

        var seen = statuses.Snapshot();
        var verdictAt = seen.ToList().FindIndex(s =>
            s.Status == CleanupStatus.Unavailable && s.Detail?.Contains(MissingAlias, StringComparison.Ordinal) == true);
        Assert.True(verdictAt >= 0);
        Assert.DoesNotContain(seen.Skip(verdictAt + 1), s => s.Status is CleanupStatus.Initializing or CleanupStatus.Downloading);
        Assert.Equal(CleanupStatus.Unavailable, svc.Status);
        Assert.Contains(MissingAlias, svc.StatusDetail);
        Assert.Equal(0, harness.Qwen.LoadCalls);
    }

    [Fact]
    public async Task A_newer_configuration_still_initializing_when_recovery_runs_is_left_to_finish()
    {
        await using var harness = NewHarness();
        var svc = harness.Service;
        await ParkInitializationInModelLoadAsync(harness);
        var loadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Phi.LoadGate = loadGate;
        harness.Phi.LoadFailure = new InvalidOperationException("The model file is damaged.");
        var load = svc.LoadFoundryModelAsync(CleanupHarness.OtherAlias);
        await harness.Phi.LoadStarted.Task.WaitAsync(Bound);

        // The user saves a different model; its initialization queues behind the load and then parks
        // in its own model load, so it is still live whatever order the two threads run in.
        var newerGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Mistral.LoadGate = newerGate;
        svc.Configure(CleanupHarness.FoundryOn(CleanupHarness.ThirdAlias));
        var statuses = new CleanupStatusRecorder(svc);

        loadGate.SetResult();
        Assert.False(await load.WaitAsync(Bound));
        await harness.Mistral.LoadStarted.Task.WaitAsync(Bound);

        Assert.DoesNotContain(statuses.Snapshot(), s => s.Detail == "Resuming AI cleanup setup\u2026");
        newerGate.SetResult();
        await AssertServingAsync(harness);
        Assert.Equal(1, harness.Mistral.LoadCalls);
        Assert.Equal(1, harness.Qwen.LoadCalls);
    }

    [Fact]
    public async Task A_load_that_interrupts_an_initialization_which_had_already_finished_restarts_nothing()
    {
        await using var harness = NewHarness();
        var svc = harness.Service;

        // Pause the initialization inside its own Ready notification: it has published its agent and
        // Ready, but has not returned yet, so the load still finds it running and cancels it.
        using var paused = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        using var releaseAtExit = new ReleaseAtExit(resume);
        var pausedOnce = 0;
        svc.StatusChanged += () =>
        {
            if (svc.Status == CleanupStatus.Ready && Interlocked.Exchange(ref pausedOnce, 1) == 0)
            {
                paused.Set();
                resume.Wait();
            }
        };
        svc.Configure(CleanupHarness.FoundryOn());
        Assert.True(paused.Wait(Bound));

        harness.Phi.LoadFailure = new InvalidOperationException("The model file is damaged.");
        var load = svc.LoadFoundryModelAsync(CleanupHarness.OtherAlias);
        var statuses = new CleanupStatusRecorder(svc);
        resume.Set();

        Assert.False(await load.WaitAsync(Bound));
        Assert.Empty(statuses.Snapshot());
        await AssertServingAsync(harness);
        Assert.Equal(1, harness.Qwen.LoadCalls);
    }

    [Fact]
    public async Task Turning_cleanup_off_during_recovery_is_not_undone_by_it()
    {
        await using var harness = NewHarness();
        var svc = harness.Service;
        await ParkInitializationInModelLoadAsync(harness);
        var loadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Phi.LoadGate = loadGate;
        harness.Phi.LoadFailure = new InvalidOperationException("The model file is damaged.");
        var load = svc.LoadFoundryModelAsync(CleanupHarness.OtherAlias);
        await harness.Phi.LoadStarted.Task.WaitAsync(Bound);

        svc.Configure(CleanupOptions.Disabled);
        var statuses = new CleanupStatusRecorder(svc);
        loadGate.SetResult();
        Assert.False(await load.WaitAsync(Bound));

        Assert.Empty(statuses.Snapshot());
        Assert.Equal(CleanupStatus.Disabled, svc.Status);
        Assert.Equal(CleanupOutcome.Skipped, (await svc.CleanAsync(Dictated)).Outcome);
        Assert.Equal(1, harness.Qwen.LoadCalls);
    }

    [Fact]
    public async Task Disposal_during_a_manual_load_does_not_restart_the_initialization_it_interrupted()
    {
        await using var harness = NewHarness();
        var svc = harness.Service;
        harness.DrainOnManualClock();
        TaskCompletionSource? parked = null;
        var manualLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            parked = await ParkInitializationInModelLoadAsync(harness);
            harness.Phi.LoadGate = manualLoad;
            var load = svc.LoadFoundryModelAsync(CleanupHarness.OtherAlias);
            await harness.Phi.LoadStarted.Task.WaitAsync(Bound);
            var published = 0;
            svc.StatusChanged += () => Interlocked.Increment(ref published);

            var dispose = svc.DisposeAsync().AsTask();

            Assert.False(await load.WaitAsync(Bound));
            await dispose.WaitAsync(Bound);
            Assert.Equal(CleanupDisposalOutcome.Released, svc.DisposalOutcome);
            Assert.Equal(0, Volatile.Read(ref published));
            Assert.Equal(1, harness.Qwen.LoadCalls);
            Assert.Equal(1, harness.Runtime.Disposals);
            Assert.False(harness.Runtime.DisposedWhileInUse);
        }
        finally
        {
            // Both waits end with the disposal's cancellation anyway; opened here too, because the drain is on the test's
            // clock and nothing else would time it out. The initialization's gate is still on the fake if parking failed.
            parked?.TrySetResult();
            harness.Qwen.LoadGate?.TrySetResult();
            manualLoad.TrySetResult();
        }
    }

    [Fact]
    public async Task An_interrupted_initialization_whose_cancellation_surfaces_as_another_exception_is_still_restarted()
    {
        await using var harness = NewHarness();
        var svc = harness.Service;

        // A native load that does not stop when asked, then reports the cancellation as a failure of
        // its own, the way an SDK may wrap it.
        var initGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Qwen.LoadGate = initGate;
        harness.Qwen.LoadIgnoresCancellation = true;
        svc.Configure(CleanupHarness.FoundryOn());
        await harness.Qwen.LoadStarted.Task.WaitAsync(Bound);

        var loadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Phi.LoadGate = loadGate;
        harness.Phi.LoadFailure = new InvalidOperationException("The model file is damaged.");
        var load = svc.LoadFoundryModelAsync(CleanupHarness.OtherAlias);

        harness.Qwen.LoadFailure = new InvalidOperationException("The load was cancelled.");
        initGate.SetResult();

        // The load holds the init lock once it reaches its own model, so the interrupted run is over.
        await harness.Phi.LoadStarted.Task.WaitAsync(Bound);
        harness.Qwen.LoadFailure = null;
        harness.Qwen.LoadGate = null;
        loadGate.SetResult();

        Assert.False(await load.WaitAsync(Bound));
        await AssertServingAsync(harness);
    }

    [Fact]
    public async Task An_initialization_ended_by_a_cancellation_nobody_requested_reports_unavailable()
    {
        await using var harness = NewHarness();
        var svc = harness.Service;

        // What HttpClient throws when its own timeout elapses on .NET 5 and later: an
        // OperationCanceledException nesting a TimeoutException (a TaskCanceledException in practice),
        // with no caller token cancelled. Read as "superseded", it left the status on Downloading with
        // nothing to finish it.
        harness.Qwen.LoadFailure = new TaskCanceledException("The request timed out.", new TimeoutException());

        svc.Configure(CleanupHarness.FoundryOn());
        await harness.WaitForStatusAsync(CleanupStatus.Unavailable);

        var result = await svc.CleanAsync(Dictated);
        Assert.Equal(CleanupOutcome.Failed, result.Outcome);
        Assert.Equal(Dictated, result.Text);
        Assert.Contains(harness.Log.Entries, e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains("initialization failed", StringComparison.Ordinal) &&
            e.Message.Contains(nameof(TaskCanceledException), StringComparison.Ordinal));

        // An identical save is the retry.
        harness.Qwen.LoadFailure = null;
        svc.Configure(CleanupHarness.FoundryOn());
        await AssertServingAsync(harness);
    }

    // Every model call answers with a plausible clean of Dictated, so a recovered service is shown to
    // clean a real dictation rather than only to report Ready.
    private static CleanupHarness NewHarness(HttpMessageHandler? http = null) =>
        new(http: http ?? new ScriptedHttpHandler((_, _) => Task.FromResult(ScriptedHttpHandler.ChatCompletion(CleanedText))));

    // Arms the gap between a manual load or unload deciding its change of resident model (and releasing
    // the init lock) and reporting it: the first time it opens, the newer configuration is applied and
    // its initialization is allowed to run all the way to Ready before the decision is carried out.
    // Returns whether that Ready arrived.
    private static Func<bool> LandNewerConfigurationWhenLoadHasDecided(CleanupHarness harness, CleanupOptions newer)
    {
        var svc = harness.Service;
        var ready = new ManualResetEventSlim();
        var landed = 0;
        var restarted = 0;
        var reached = false;
        svc.StatusChanged += () =>
        {
            if (Volatile.Read(ref landed) == 0)
            {
                return;
            }

            if (svc.Status != CleanupStatus.Ready)
            {
                Volatile.Write(ref restarted, 1);
            }
            else if (Volatile.Read(ref restarted) == 1)
            {
                ready.Set();
            }
        };
        svc.ResidentChangeDecidedForTesting = () =>
        {
            if (Interlocked.Exchange(ref landed, 1) != 0)
            {
                return;
            }

            svc.Configure(newer);
            reached = ready.Wait(Bound);
        };
        return () => reached;
    }

    // Arms the same gap to run another operation to completion inside it, the first time it opens.
    // Returns that operation's task.
    private static Func<Task<bool>> RunOnceWhenLoadHasDecided(CleanupHarness harness, Func<Task<bool>> operation)
    {
        Task<bool>? started = null;
        var ran = 0;
        harness.Service.ResidentChangeDecidedForTesting = () =>
        {
            if (Interlocked.Exchange(ref ran, 1) != 0)
            {
                return;
            }

            started = operation();
            try
            {
                started.Wait(Bound);
            }
            catch (AggregateException)
            {
                // Surfaces through the returned task instead.
            }
        };
        return () => started ?? Task.FromException<bool>(new InvalidOperationException("The gap never opened."));
    }

    // Cleanup configured for the default model, whose initialization failed: Unavailable, with no agent.
    // The model loads normally from here on.
    private static async Task MakeUnavailableAfterAFailedInitializationAsync(CleanupHarness harness)
    {
        harness.Qwen.LoadFailure = new InvalidOperationException("The model file is damaged.");
        harness.Service.Configure(CleanupHarness.FoundryOn());
        await harness.WaitForStatusAsync(CleanupStatus.Unavailable);
        harness.Qwen.LoadFailure = null;
    }

    // The next load of this model behaves like a native one that has started: it runs until the returned
    // gate opens whatever its token says, then fails the way a missing execution provider does, which is
    // reported as a status rather than thrown.
    private static TaskCompletionSource ArmUncooperativeFailingLoad(FakeFoundryModel model)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        model.LoadGate = gate;
        model.LoadIgnoresCancellation = true;
        model.LoadFailure = new InvalidOperationException(
            $"Cannot load model '{model.Id}': it requires the 'CUDAExecutionProvider' execution provider, " +
            "which is not available. Available EPs: [CPUExecutionProvider].");
        return gate;
    }

    // Starts the configured model's initialization and parks it inside its model load, where the
    // status reads Downloading. Only that first load waits: whatever loads the model next goes
    // straight through. Returns the gate that releases it.
    private static async Task<TaskCompletionSource> ParkInitializationInModelLoadAsync(CleanupHarness harness)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Qwen.LoadGate = gate;
        harness.Service.Configure(CleanupHarness.FoundryOn());
        await harness.Qwen.LoadStarted.Task.WaitAsync(Bound);
        harness.Qwen.LoadGate = null;

        Assert.Equal(CleanupStatus.Downloading, harness.Service.Status);
        return gate;
    }

    private static async Task AssertServingAsync(CleanupHarness harness)
    {
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        var result = await harness.Service.CleanAsync(Dictated).WaitAsync(Bound);
        Assert.Equal(CleanupOutcome.Cleaned, result.Outcome);
        Assert.Equal(CleanedText, result.Text);
    }
}
