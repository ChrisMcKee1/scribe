using Microsoft.Extensions.Logging;
using Scribe.Core.Cleanup;

namespace Scribe.Core.Tests;

/// <summary>
/// R04: every operation that may use the cleanup service's shared resources is owned through
/// disposal. Admission closes atomically, admitted work is cancelled cooperatively and awaited,
/// and only then are the runtime, clients and semaphores released; one that will not stop in time
/// keeps them rather than having them disposed underneath it. Interleavings are forced with gates,
/// never with sleeps, and a test that expects disposal to release once the work in flight stops runs the drain on a
/// clock only it moves (<see cref="CleanupHarness.DrainOnManualClock"/>), so the real 5 s never decides it. Such a test
/// opens every gate it shut in a finally, so a wait or an assertion that fails first cannot leave work parked behind one:
/// the harness's disposal would wait on that drain for ever.
/// </summary>
public sealed class CleanupLifecycleTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Showing_the_settings_page_never_starts_the_foundry_runtime()
    {
        await using var harness = new CleanupHarness();
        var svc = harness.Service;

        Assert.Empty(await svc.ListFoundryModelsIfInitializedAsync());
        Assert.Null(await svc.GetLoadedFoundryModelAsync());
        Assert.False(await svc.UnloadFoundryModelAsync(null));

        Assert.Equal(0, harness.Host.Creations);
        Assert.Equal(0, harness.Runtime.EpRegistrations);
    }

    [Fact]
    public async Task Checking_availability_creates_the_manager_but_downloads_nothing()
    {
        await using var harness = new CleanupHarness();

        Assert.True(await harness.Service.ProbeAsync());

        Assert.Equal(1, harness.Host.Creations);
        Assert.Equal(0, harness.Runtime.EpRegistrations);
        Assert.Equal(0, harness.Runtime.CatalogReads);
    }

    [Fact]
    public async Task An_explicit_listing_registers_execution_providers_before_the_first_catalog_read()
    {
        // Otherwise the SDK caches a CPU-only catalog for the life of the process.
        await using var harness = new CleanupHarness();
        var svc = harness.Service;

        var models = await svc.ListFoundryModelsAsync();

        Assert.Equal(
            [CleanupHarness.ThirdAlias, CleanupHarness.OtherAlias, CleanupHarness.FoundryAlias],
            models.Select(m => m.Alias).Order(StringComparer.Ordinal));
        Assert.Equal(["register-eps", "catalog"], harness.State.Events.Where(e => e is "register-eps" or "catalog"));

        // Once something explicitly started it, merely showing the page reads it.
        Assert.Equal(3, (await svc.ListFoundryModelsIfInitializedAsync()).Count);
        Assert.Equal(1, harness.Runtime.CatalogReads);
    }

    [Fact]
    public async Task Concurrent_first_listings_create_register_and_read_exactly_once()
    {
        await using var harness = new CleanupHarness();
        var svc = harness.Service;
        harness.Runtime.EpGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = svc.ListFoundryModelsAsync();
        await harness.Runtime.EpStarted.Task.WaitAsync(Bound);
        var second = svc.ListFoundryModelsAsync();
        Assert.False(second.IsCompleted, "The second caller waits for the first one's runtime.");

        harness.Runtime.EpGate.SetResult();
        var results = await Task.WhenAll(first, second).WaitAsync(Bound);

        Assert.All(results, list => Assert.Equal(3, list.Count));
        Assert.Equal(1, harness.Host.Creations);
        Assert.Equal(1, harness.Runtime.EpRegistrations);
        Assert.Equal(1, harness.Runtime.CatalogReads);
    }

    [Fact]
    public async Task Catalog_reads_do_not_queue_behind_a_model_load()
    {
        await using var harness = new CleanupHarness();
        var svc = harness.Service;
        await svc.ListFoundryModelsAsync();
        harness.Phi.LoadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var load = svc.LoadFoundryModelAsync(CleanupHarness.OtherAlias);
        await harness.Phi.LoadStarted.Task.WaitAsync(Bound);

        // The load holds the init lock for as long as a multi-gigabyte model takes; the picker must not.
        Assert.Equal(3, (await svc.ListFoundryModelsAsync().WaitAsync(Bound)).Count);
        Assert.Equal(3, (await svc.ListFoundryModelsIfInitializedAsync().WaitAsync(Bound)).Count);
        Assert.False(load.IsCompleted);

        harness.Phi.LoadGate.SetResult();
        Assert.True(await load.WaitAsync(Bound));
        Assert.Equal(CleanupHarness.OtherAlias, await svc.GetLoadedFoundryModelAsync());
    }

    [Fact]
    public async Task Disposal_cancels_an_admitted_operation_and_releases_nothing_until_it_stops()
    {
        await using var harness = new CleanupHarness();
        var svc = harness.Service;
        harness.DrainOnManualClock();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Runtime.EpGate = gate;
        try
        {
            var listing = svc.ListFoundryModelsAsync();
            await harness.Runtime.EpStarted.Task.WaitAsync(Bound);
            var dispose = svc.DisposeAsync().AsTask();

            Assert.Empty(await listing.WaitAsync(Bound));
            await dispose.WaitAsync(Bound);

            Assert.Equal(CleanupDisposalOutcome.Released, svc.DisposalOutcome);
            Assert.Equal(1, harness.Runtime.Disposals);
            Assert.False(harness.Runtime.DisposedWhileInUse);
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    [Fact]
    public async Task An_operation_that_ignores_cancellation_keeps_the_runtime_rather_than_having_it_disposed()
    {
        await using var harness = new CleanupHarness();
        var svc = harness.Service;
        svc.DisposalDrainTimeout = TimeSpan.FromMilliseconds(100);
        harness.Runtime.EpGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Runtime.IgnoreCancellation = true;

        var listing = svc.ListFoundryModelsAsync();
        await harness.Runtime.EpStarted.Task.WaitAsync(Bound);
        await svc.DisposeAsync().AsTask().WaitAsync(Bound);

        Assert.Equal(CleanupDisposalOutcome.LeftToProcessExit, svc.DisposalOutcome);
        Assert.Equal(0, harness.Runtime.Disposals);
        Assert.Contains(harness.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("still running"));

        // When it does finish, it publishes nothing and still does not have its runtime taken away.
        harness.Runtime.EpGate.SetResult();
        Assert.Empty(await listing.WaitAsync(Bound));
        Assert.Equal(0, harness.Runtime.Disposals);
        Assert.Empty(await svc.ListFoundryModelsIfInitializedAsync());
    }

    [Fact]
    public async Task After_disposal_every_entry_point_is_refused_without_touching_anything()
    {
        await using var harness = new CleanupHarness();
        var svc = harness.Service;
        var raised = 0;
        svc.StatusChanged += () => Interlocked.Increment(ref raised);

        await svc.DisposeAsync();
        svc.Configure(CleanupHarness.FoundryOn());

        Assert.Empty(await svc.ListFoundryModelsAsync());
        Assert.Empty(await svc.ListFoundryModelsIfInitializedAsync());
        Assert.False(await svc.ProbeAsync());
        Assert.False(await svc.LoadFoundryModelAsync(CleanupHarness.FoundryAlias));
        Assert.False(await svc.UnloadFoundryModelAsync(null));
        Assert.Null(await svc.GetLoadedFoundryModelAsync());
        Assert.Null(svc.Recipient);
        Assert.Equal(
            CompletionOutcome.NotReady,
            (await svc.CompleteAsync("system", "user", new CleanupRecipient(CleanupHarness.FoundryOn()))).Outcome);
        var clean = await svc.CleanAsync("keep my words");
        Assert.Equal(CleanupOutcome.Skipped, clean.Outcome);
        Assert.Equal("keep my words", clean.Text);

        Assert.Equal(0, harness.Host.Creations);
        Assert.Equal(0, Volatile.Read(ref raised));
        Assert.Equal(CleanupStatus.Disabled, svc.Status);
    }

    [Fact]
    public async Task A_runtime_created_after_disposal_began_is_disposed_and_never_published()
    {
        await using var harness = new CleanupHarness();
        var svc = harness.Service;
        harness.DrainOnManualClock();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Host.CreateGate = gate;
        harness.Host.IgnoreCancellation = true;
        try
        {
            var probe = svc.ProbeAsync();
            await harness.Host.CreateStarted.Task.WaitAsync(Bound);
            var dispose = svc.DisposeAsync().AsTask();
            Assert.False(dispose.IsCompleted, "Disposal waits for the admitted probe.");

            gate.SetResult();

            Assert.False(await probe.WaitAsync(Bound));
            await dispose.WaitAsync(Bound);
            Assert.Equal(CleanupDisposalOutcome.Released, svc.DisposalOutcome);
            Assert.Equal(1, harness.Runtime.Disposals);
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    [Fact]
    public async Task A_superseded_initialization_is_still_awaited_by_disposal()
    {
        await using var harness = new CleanupHarness();
        var svc = harness.Service;
        harness.DrainOnManualClock();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Runtime.EpGate = gate;
        harness.Runtime.IgnoreCancellation = true;
        try
        {
            svc.Configure(CleanupHarness.FoundryOn());
            await harness.Runtime.EpStarted.Task.WaitAsync(Bound);

            // A newer save cancels it, but it is still inside the runtime and still owns what it holds.
            svc.Configure(CleanupOptions.Disabled);
            var dispose = svc.DisposeAsync().AsTask();
            Assert.False(dispose.IsCompleted);

            gate.SetResult();
            await dispose.WaitAsync(Bound);

            Assert.Equal(CleanupDisposalOutcome.Released, svc.DisposalOutcome);
            Assert.False(harness.Runtime.DisposedWhileInUse);
            Assert.Equal(1, harness.Runtime.Disposals);
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    [Fact]
    public async Task An_initialization_that_finishes_after_disposal_publishes_nothing()
    {
        await using var harness = new CleanupHarness();
        var svc = harness.Service;
        harness.DrainOnManualClock();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Qwen.LoadGate = gate;
        harness.Qwen.LoadIgnoresCancellation = true;
        try
        {
            svc.Configure(CleanupHarness.FoundryOn());
            await harness.Qwen.LoadStarted.Task.WaitAsync(Bound);
            var dispose = svc.DisposeAsync().AsTask();
            var lateStatuses = new List<CleanupStatus>();
            svc.StatusChanged += () =>
            {
                lock (lateStatuses)
                {
                    lateStatuses.Add(svc.Status);
                }
            };

            gate.SetResult();
            await dispose.WaitAsync(Bound);

            Assert.Empty(lateStatuses);
            Assert.NotEqual(CleanupStatus.Ready, svc.Status);
            Assert.Equal(CleanupDisposalOutcome.Released, svc.DisposalOutcome);
            Assert.Equal(0, ((ScriptedHttpHandler)harness.Http).Requests);
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    [Fact]
    public async Task Disposal_during_a_dictation_keeps_the_raw_text_and_waits_for_the_call()
    {
        // The call is held after it sees the disposal's cancellation, for as long as the test likes, and the drain runs on a
        // clock only the test moves: the drain can then end only because the call stopped, never because a loaded machine
        // took longer than the real 5 s to unwind it (which is how this test used to fail with LeftToProcessExit).
        var requestArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unwind = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probed = 0;
        var http = new ScriptedHttpHandler(async (_, ct) =>
        {
            if (Interlocked.Increment(ref probed) == 1)
            {
                return ScriptedHttpHandler.ChatCompletion("ok");
            }

            requestArrived.TrySetResult();
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (ct.Register(() => cancelled.TrySetResult()))
            {
                await cancelled.Task;
            }

            cancelSeen.TrySetResult();
            await unwind.Task;
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });
        await using var harness = new CleanupHarness(http: http);
        var svc = harness.Service;
        var clock = harness.DrainOnManualClock();
        try
        {
            svc.Configure(CleanupHarness.Custom("https://cleanup.example.test/v1"));
            await harness.WaitForStatusAsync(CleanupStatus.Ready);

            var dictation = svc.CleanAsync("please keep these exact words");
            await requestArrived.Task.WaitAsync(Bound);
            var dispose = svc.DisposeAsync().AsTask();

            // DisposeAsync returned at its first wait, the drain, which the held call keeps open: production's 5 s timer is
            // armed on the test's clock.
            var drainTimer = Assert.Single(clock.Timers);
            Assert.Equal(TimeSpan.FromSeconds(5), drainTimer.DueTime);
            await cancelSeen.Task.WaitAsync(Bound);
            Assert.False(dictation.IsCompleted, "The call is still unwinding.");
            Assert.False(dispose.IsCompleted, "Disposal waits for the call.");

            unwind.SetResult();
            var result = await dictation.WaitAsync(Bound);
            await dispose.WaitAsync(Bound);

            Assert.Equal(CleanupOutcome.Skipped, result.Outcome);
            Assert.Equal("please keep these exact words", result.Text);
            Assert.Contains("shutting down", result.SkipReason);
            Assert.Equal(CleanupDisposalOutcome.Released, svc.DisposalOutcome);
        }
        finally
        {
            unwind.TrySetResult();
        }
    }

    [Fact]
    public async Task Production_drains_for_five_seconds_on_the_system_clock()
    {
        await using var harness = new CleanupHarness();

        Assert.Equal(TimeSpan.FromSeconds(5), harness.Service.DisposalDrainTimeout);
        Assert.Same(TimeProvider.System, harness.Service.DisposalDrainClock);
    }

    [Fact]
    public async Task Disposal_from_a_blocked_dispatcher_still_releases_the_copilot_session()
    {
        // The app disposes from the WPF dispatcher through Host.Dispose, which blocks that thread on
        // DisposeAsync. With nothing in flight the drain completes synchronously, and the Copilot
        // SDK's own cleanup awaits without ConfigureAwait(false), so a release that ran on that
        // context would post its continuation to a thread that can never run it: the app would never
        // exit, and would keep holding the single-instance mutex.
        await using var harness = new CleanupHarness();
        var session = new ContextCapturingSession();
        harness.Service.CopilotSessionForTesting = session;
        var dispatcher = new BlockedDispatcherContext();
        Exception? failure = null;
        using var finished = new ManualResetEventSlim();

        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(dispatcher);
            try
            {
                harness.Service.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                finished.Set();
            }
        })
        {
            IsBackground = true,
            Name = "blocked-dispatcher",
        };
        thread.Start();

        Assert.True(
            finished.Wait(Bound),
            $"Disposal deadlocked: {dispatcher.Posted} continuation(s) were posted to the blocked dispatcher.");
        Assert.Null(failure);
        Assert.True(session.Disposed);
        Assert.Equal(CleanupDisposalOutcome.Released, harness.Service.DisposalOutcome);
    }

    // A dispatcher whose only thread is blocked: work posted to it is queued and never runs.
    private sealed class BlockedDispatcherContext : SynchronizationContext
    {
        private int _posted;

        public int Posted => Volatile.Read(ref _posted);

        public override void Post(SendOrPostCallback d, object? state) => Interlocked.Increment(ref _posted);

        public override void Send(SendOrPostCallback d, object? state) =>
            throw new InvalidOperationException("Send to a blocked dispatcher deadlocks too.");
    }

    // Stands in for the Copilot client: its disposal awaits without ConfigureAwait(false), exactly as
    // GitHub.Copilot.SDK 1.0.5's CLI shutdown does, so it resumes on whatever context it began on.
    private sealed class ContextCapturingSession : IAsyncDisposable
    {
        private int _disposed;

        public bool Disposed => Volatile.Read(ref _disposed) == 1;

        public async ValueTask DisposeAsync()
        {
            await Task.Delay(20);
            Interlocked.Exchange(ref _disposed, 1);
        }
    }
}
