using Scribe.Core.Cleanup;
using Scribe.Core.Tests.CleanupLogging;
using Scribe.Core.Infrastructure;

namespace Scribe.Core.Tests;

/// <summary>
/// N2a: the maintainer's Foundry Local reclaim policy, end to end through
/// <see cref="TextCleanupService.Configure"/>, against a fake runtime and an isolated profile whose
/// Foundry directory stands in for <c>%USERPROFILE%\.Scribe</c>.
/// </summary>
public sealed class FoundryStorageReclaimTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    private const int CudaBytes = 64 * 1024;
    private const int WebGpuBytes = 4 * 1024;
    private const int LeftoverModelBytes = 32 * 1024;

    private static void SeedLeftovers(CleanupHarness harness)
    {
        WriteFile(harness.InFoundryDir(@"ep\cuda-ep\onnxruntime_providers_cuda.dll"), CudaBytes);
        WriteFile(harness.InFoundryDir(@"ep\webgpu-ep\webgpu.dll"), WebGpuBytes);
        WriteFile(harness.InFoundryDir(@"cache\models\Microsoft\qwen3-1.7b-generic-cpu-2\model.onnx"), LeftoverModelBytes);
        WriteFile(harness.InFoundryDir(@"logs\foundry.log"), 100);
    }

    private static void WriteFile(string path, int bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    // Gives the fake SDK's cached model real files inside the Foundry directory, so the SDK removal
    // path has something to measure.
    private static void GiveModelFiles(CleanupHarness harness, string variantId, int bytes)
    {
        harness.State.ModelRoot = harness.InFoundryDir(@"cache\models");
        WriteFile(Path.Combine(harness.State.ModelRoot, FakeFoundryModel.SafeName(variantId), "model.onnx"), bytes);
    }

    private static List<FoundryStorageReclaim> RecordNotices(CleanupHarness harness)
    {
        var notices = new List<FoundryStorageReclaim>();
        harness.Service.FoundryStorageReclaimed += notice =>
        {
            lock (notices)
            {
                notices.Add(notice);
            }
        };

        return notices;
    }

    [Fact]
    public async Task At_startup_with_another_provider_leftover_downloads_are_deleted_without_starting_foundry()
    {
        await using var harness = new CleanupHarness(armStorage: true);
        SeedLeftovers(harness);
        var notices = RecordNotices(harness);

        harness.Service.Configure(CleanupHarness.OtherProviderOff);
        await harness.Service.LastStorageWork.WaitAsync(Bound);

        Assert.False(Directory.Exists(harness.InFoundryDir("ep")));
        Assert.False(Directory.Exists(harness.InFoundryDir(@"cache\models")));
        Assert.True(File.Exists(harness.InFoundryDir(@"logs\foundry.log")));
        Assert.Equal(0, harness.Host.Creations);

        var reclaimed = Assert.Single(harness.Log.Entries, e => e.Message.StartsWith("Reclaimed Foundry Local storage"));
        Assert.Contains("3 file(s) deleted", reclaimed.Message);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.DoesNotContain(profile, harness.Log.AllText, StringComparison.OrdinalIgnoreCase);

        // The one-time "Scribe freed ..." notice gets the exact size and counts.
        var notice = Assert.Single(notices);
        Assert.Equal(FoundryStorageReclaimReason.ProviderIsNotFoundryLocal, notice.Reason);
        Assert.Equal(CudaBytes + WebGpuBytes + LeftoverModelBytes, notice.BytesFreed);
        Assert.Equal(3, notice.FilesDeleted);
        Assert.Equal(0, notice.ModelsRemoved);
        Assert.False(notice.RuntimeDeletedAtNextStart);
    }

    [Fact]
    public async Task Nothing_to_give_back_raises_no_notice()
    {
        await using var harness = new CleanupHarness(armStorage: true);
        var notices = RecordNotices(harness);

        harness.Service.Configure(CleanupHarness.OtherProviderOff);
        await harness.Service.LastStorageWork.WaitAsync(Bound);

        Assert.Empty(notices);
        Assert.DoesNotContain(harness.Log.Entries, e => e.Message.StartsWith("Reclaimed Foundry Local storage"));
    }

    [Fact]
    public async Task A_throwing_notice_subscriber_does_not_stop_the_others()
    {
        await using var harness = new CleanupHarness(armStorage: true);
        SeedLeftovers(harness);
        harness.Service.FoundryStorageReclaimed += _ => throw new InvalidOperationException("closing window");
        var notices = RecordNotices(harness);

        harness.Service.Configure(CleanupHarness.OtherProviderOff);
        await harness.Service.LastStorageWork.WaitAsync(Bound);

        Assert.Single(notices);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task At_startup_with_foundry_local_and_a_cached_model_the_model_and_runtime_stay(bool enabled)
    {
        await using var harness = new CleanupHarness(armStorage: true);
        SeedLeftovers(harness);
        var notices = RecordNotices(harness);

        harness.Service.Configure(CleanupHarness.FoundryOn() with { Enabled = enabled });
        if (enabled)
        {
            await harness.WaitForStatusAsync(CleanupStatus.Ready);
        }

        await harness.Service.LastStorageWork.WaitAsync(Bound);

        Assert.True(File.Exists(harness.InFoundryDir(@"ep\cuda-ep\onnxruntime_providers_cuda.dll")));
        Assert.True(File.Exists(harness.InFoundryDir(@"cache\models\Microsoft\qwen3-1.7b-generic-cpu-2\model.onnx")));
        Assert.DoesNotContain(harness.State.Events, e => e.StartsWith("remove:"));
        Assert.Empty(notices);
    }

    [Fact]
    public async Task At_startup_with_foundry_local_off_and_no_model_the_browsed_runtime_is_given_back()
    {
        // The real layout after browsing the list without loading a model: the runtime downloads,
        // and only the SDK's catalog index in the model cache. "Only if they swap to it and choose to
        // load the model should it be kept."
        await using var harness = new CleanupHarness(armStorage: true);
        WriteFile(harness.InFoundryDir(@"ep\cuda-ep\onnxruntime_providers_cuda.dll"), CudaBytes);
        WriteFile(harness.InFoundryDir(@"ep\webgpu-ep\webgpu.dll"), WebGpuBytes);
        var index = harness.InFoundryDir(@"cache\models\foundry.modelinfo.json");
        WriteFile(index, 2048);
        var log = harness.InFoundryDir(@"logs\foundry.log");
        WriteFile(log, 100);
        harness.Storage!.WriteKeepOnlySelected(true);
        var notices = RecordNotices(harness);

        harness.Service.Configure(CleanupHarness.FoundryOn() with { Enabled = false });
        await harness.Service.LastStorageWork.WaitAsync(Bound);

        Assert.False(Directory.Exists(harness.InFoundryDir("ep")));
        Assert.True(File.Exists(index), "The review leaves the model cache, and the SDK's index in it, alone.");
        Assert.True(File.Exists(log));
        Assert.Equal(0, harness.Host.Creations);
        Assert.False(harness.Storage.ReadKeepOnlySelected(), "With no model cached there is nothing left to narrow down.");

        var notice = Assert.Single(notices);
        Assert.Equal(FoundryStorageReclaimReason.RuntimeWithoutModel, notice.Reason);
        Assert.Equal(CudaBytes + WebGpuBytes, notice.BytesFreed);
        Assert.Equal(2, notice.FilesDeleted);
        Assert.Equal(0, notice.ModelsRemoved);
        Assert.False(notice.RuntimeDeletedAtNextStart);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.DoesNotContain(profile, harness.Log.AllText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task At_startup_with_foundry_local_on_the_runtime_stays_for_the_model_it_loads()
    {
        // Cleanup on is the user having chosen to load a model: deleting the runtime here would only
        // download it again moments later.
        await using var harness = new CleanupHarness(armStorage: true);
        WriteFile(harness.InFoundryDir(@"ep\cuda-ep\onnxruntime_providers_cuda.dll"), CudaBytes);
        WriteFile(harness.InFoundryDir(@"cache\models\foundry.modelinfo.json"), 2048);
        var notices = RecordNotices(harness);

        harness.Service.Configure(CleanupHarness.FoundryOn());
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        await harness.Service.LastStorageWork.WaitAsync(Bound);

        Assert.True(File.Exists(harness.InFoundryDir(@"ep\cuda-ep\onnxruntime_providers_cuda.dll")));
        Assert.Empty(notices);
    }

    [Fact]
    public async Task At_startup_with_foundry_local_other_aliases_left_behind_go_once_the_selected_one_is_in_use()
    {
        // Leftovers from model switches made before this rule existed: no marker records them.
        await using var harness = new CleanupHarness(armStorage: true);
        GiveModelFiles(harness, CleanupHarness.OtherVariant, 4 * 1024);
        GiveModelFiles(harness, CleanupHarness.ThirdVariant, 2 * 1024);
        harness.State.SetCached(CleanupHarness.OtherVariant, true);
        harness.State.SetCached(CleanupHarness.ThirdVariant, true);
        WriteFile(harness.InFoundryDir(@"ep\cuda-ep\onnxruntime_providers_cuda.dll"), CudaBytes);
        var notices = RecordNotices(harness);
        Assert.False(harness.Storage!.ReadKeepOnlySelected());

        harness.Service.Configure(CleanupHarness.FoundryOn());
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        await harness.Service.LastStorageWork.WaitAsync(Bound);

        var events = harness.State.Events.ToList();
        Assert.Contains("remove:" + CleanupHarness.OtherVariant, events);
        Assert.Contains("remove:" + CleanupHarness.ThirdVariant, events);
        Assert.DoesNotContain("remove:" + CleanupHarness.FoundryVariant, events);
        Assert.True(harness.State.IsLoaded(CleanupHarness.FoundryVariant));
        Assert.True(File.Exists(harness.InFoundryDir(@"ep\cuda-ep\onnxruntime_providers_cuda.dll")), "The selected model's runtime stays.");
        Assert.True(
            events.IndexOf("load:" + CleanupHarness.FoundryVariant) < events.IndexOf("remove:" + CleanupHarness.OtherVariant),
            "Nothing goes until the selected model is loaded and serving.");

        var notice = Assert.Single(notices);
        Assert.Equal(FoundryStorageReclaimReason.ModelSwitched, notice.Reason);
        Assert.Equal(2, notice.ModelsRemoved);
        Assert.Equal(6 * 1024, notice.BytesFreed);
    }

    [Fact]
    public async Task Saving_settings_while_another_provider_stays_saved_never_touches_a_hand_loaded_model()
    {
        await using var harness = new CleanupHarness(armStorage: true);
        var svc = harness.Service;
        svc.Configure(CleanupHarness.OtherProviderOff);
        await svc.LastStorageWork.WaitAsync(Bound);

        // The user loads a model by hand from the Foundry Local section, then the tray AI toggle and
        // the usage page's add-term button each re-apply the saved snapshot.
        Assert.True(await svc.LoadFoundryModelAsync(CleanupHarness.FoundryAlias).WaitAsync(Bound));
        svc.Configure(CleanupHarness.OtherProviderOff with { Enabled = true });
        svc.Configure(CleanupHarness.OtherProviderOff);
        await svc.LastStorageWork.WaitAsync(Bound);

        Assert.DoesNotContain(harness.State.Events, e => e.StartsWith("unload:") || e.StartsWith("remove:") || e == "stop-web");
        Assert.True(harness.State.IsLoaded(CleanupHarness.FoundryVariant));
        Assert.True(harness.State.IsCached(CleanupHarness.FoundryVariant));

        // The next start, with nothing loaded, is where a hand download goes.
        WriteFile(harness.InFoundryDir(@"cache\models\Microsoft\qwen3-1.7b-generic-cpu-2\model.onnx"), LeftoverModelBytes);
        await using var restarted = new TextCleanupService(
            harness.Log,
            harness.Paths,
            new FakeFoundryHost(() => throw new InvalidOperationException("must not start")),
            harness.Storage);
        restarted.Configure(CleanupHarness.OtherProviderOff);
        await restarted.LastStorageWork.WaitAsync(Bound);

        Assert.False(Directory.Exists(harness.InFoundryDir(@"cache\models")));
    }

    [Fact]
    public async Task A_queued_reclaim_never_undoes_a_load_the_user_started_after_it()
    {
        // Without the explicit-use epoch this deletes the model: by the time the queued reclaim runs,
        // the catalog the load created is live, so re-deriving from presence alone would unload,
        // remove and stop everything.
        await using var harness = new CleanupHarness(armStorage: true);
        var svc = harness.Service;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        svc.StorageWorkGateForTesting = gate.Task;

        svc.Configure(CleanupHarness.OtherProviderOff);
        var reclaim = svc.LastStorageWork;
        Assert.True(await svc.LoadFoundryModelAsync(CleanupHarness.FoundryAlias).WaitAsync(Bound));

        gate.SetResult();
        await reclaim.WaitAsync(Bound);

        Assert.DoesNotContain(harness.State.Events, e => e.StartsWith("unload:") || e.StartsWith("remove:") || e == "stop-web");
        Assert.True(harness.State.IsLoaded(CleanupHarness.FoundryVariant));
        Assert.True(harness.State.IsCached(CleanupHarness.FoundryVariant));
    }

    [Fact]
    public async Task A_queued_unload_never_undoes_a_load_the_user_started_after_it()
    {
        await using var harness = new CleanupHarness(armStorage: true);
        var svc = harness.Service;
        svc.Configure(CleanupHarness.FoundryOn());
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        await svc.LastStorageWork.WaitAsync(Bound);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        svc.StorageWorkGateForTesting = gate.Task;

        // Cleanup off queues the unload; before it runs, the user loads a model by hand.
        svc.Configure(CleanupHarness.FoundryOn() with { Enabled = false });
        var unload = svc.LastStorageWork;
        Assert.True(await svc.LoadFoundryModelAsync(CleanupHarness.OtherAlias).WaitAsync(Bound));

        gate.SetResult();
        await unload.WaitAsync(Bound);

        Assert.True(harness.State.IsLoaded(CleanupHarness.OtherVariant));
        Assert.DoesNotContain("unload:" + CleanupHarness.OtherVariant, harness.State.Events);
    }

    [Fact]
    public async Task A_queued_startup_review_never_deletes_the_runtime_the_user_just_set_up()
    {
        await using var harness = new CleanupHarness(armStorage: true);
        WriteFile(harness.InFoundryDir(@"ep\cuda-ep\onnxruntime_providers_cuda.dll"), CudaBytes);
        WriteFile(harness.InFoundryDir(@"cache\models\foundry.modelinfo.json"), 2048);
        var svc = harness.Service;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        svc.StorageWorkGateForTesting = gate.Task;

        svc.Configure(CleanupHarness.FoundryOn() with { Enabled = false });
        var review = svc.LastStorageWork;

        // "Set up Foundry Local" lists the models, which is the user choosing to use it.
        Assert.NotEmpty(await svc.ListFoundryModelsAsync().WaitAsync(Bound));
        gate.SetResult();
        await review.WaitAsync(Bound);

        Assert.True(File.Exists(harness.InFoundryDir(@"ep\cuda-ep\onnxruntime_providers_cuda.dll")));
    }

    [Fact]
    public async Task Turning_cleanup_off_while_foundry_stays_selected_only_unloads()
    {
        await using var harness = new CleanupHarness(armStorage: true);
        SeedLeftovers(harness);
        var svc = harness.Service;
        var notices = RecordNotices(harness);
        svc.Configure(CleanupHarness.FoundryOn());
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        Assert.True(harness.State.IsLoaded(CleanupHarness.FoundryVariant));

        svc.Configure(CleanupHarness.FoundryOn() with { Enabled = false });
        await svc.LastStorageWork.WaitAsync(Bound);

        Assert.False(harness.State.IsLoaded(CleanupHarness.FoundryVariant));
        Assert.True(harness.State.IsCached(CleanupHarness.FoundryVariant));
        Assert.DoesNotContain(harness.State.Events, e => e.StartsWith("remove:") || e == "stop-web");
        Assert.True(File.Exists(harness.InFoundryDir(@"ep\cuda-ep\onnxruntime_providers_cuda.dll")));
        Assert.Empty(notices);
    }

    [Fact]
    public async Task Switching_away_gives_everything_back_and_the_loaded_runtime_goes_at_the_next_start()
    {
        var state = new FakeFoundryState();
        await using var harness = new CleanupHarness(armStorage: true, state: state);
        SeedLeftovers(harness);
        GiveModelFiles(harness, CleanupHarness.FoundryVariant, 8 * 1024);
        var notices = RecordNotices(harness);
        var svc = harness.Service;
        svc.Configure(CleanupHarness.FoundryOn());
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        harness.Storage!.WriteKeepOnlySelected(true);

        svc.Configure(CleanupHarness.OtherProviderOff);
        await svc.LastStorageWork.WaitAsync(Bound);

        Assert.Contains("unload:" + CleanupHarness.FoundryVariant, state.Events);
        Assert.Contains("remove:" + CleanupHarness.FoundryVariant, state.Events);
        Assert.Contains("stop-web", state.Events);
        Assert.False(state.IsCached(CleanupHarness.FoundryVariant));
        Assert.False(harness.Storage.ReadKeepOnlySelected());

        // Registered execution providers are native libraries loaded in this process.
        Assert.True(File.Exists(harness.InFoundryDir(@"ep\cuda-ep\onnxruntime_providers_cuda.dll")));
        Assert.Contains(harness.Log.Entries, e => e.Message.Contains("deleted at the next start"));
        var switchedAway = Assert.Single(notices);
        Assert.Equal(1, switchedAway.ModelsRemoved);
        Assert.Equal(8 * 1024, switchedAway.BytesFreed);
        Assert.True(switchedAway.RuntimeDeletedAtNextStart);

        // The next start, still on another provider and with no manager yet, finishes the job.
        await using var restarted = new TextCleanupService(
            harness.Log,
            harness.Paths,
            new FakeFoundryHost(() => throw new InvalidOperationException("must not start")),
            harness.Storage);
        var restartNotices = new List<FoundryStorageReclaim>();
        restarted.FoundryStorageReclaimed += restartNotices.Add;
        restarted.Configure(CleanupHarness.OtherProviderOff);
        await restarted.LastStorageWork.WaitAsync(Bound);

        Assert.False(Directory.Exists(harness.InFoundryDir("ep")));
        var atNextStart = Assert.Single(restartNotices);
        Assert.True(atNextStart.BytesFreed >= CudaBytes + WebGpuBytes);
        Assert.False(atNextStart.RuntimeDeletedAtNextStart);
    }

    [Fact]
    public async Task Switching_models_keeps_only_the_selected_one_once_it_is_in_use()
    {
        await using var harness = new CleanupHarness(armStorage: true);
        GiveModelFiles(harness, CleanupHarness.FoundryVariant, 16 * 1024);
        var notices = RecordNotices(harness);
        var svc = harness.Service;
        svc.Configure(CleanupHarness.FoundryOn(CleanupHarness.FoundryAlias));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        svc.Configure(CleanupHarness.FoundryOn(CleanupHarness.OtherAlias));
        Assert.True(harness.Storage!.ReadKeepOnlySelected(), "Recorded at once, so a restart still finishes it.");
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        await svc.LastStorageWork.WaitAsync(Bound);

        Assert.Contains("remove:" + CleanupHarness.FoundryVariant, harness.State.Events);
        Assert.DoesNotContain("remove:" + CleanupHarness.OtherVariant, harness.State.Events);
        Assert.True(harness.State.IsCached(CleanupHarness.OtherVariant));
        Assert.True(harness.State.IsLoaded(CleanupHarness.OtherVariant));
        Assert.False(harness.Storage.ReadKeepOnlySelected());

        // Ordering: the old model goes only after the new one was loaded and is serving.
        var events = harness.State.Events.ToList();
        Assert.True(
            events.IndexOf("load:" + CleanupHarness.OtherVariant) < events.IndexOf("remove:" + CleanupHarness.FoundryVariant));

        var notice = Assert.Single(notices);
        Assert.Equal(FoundryStorageReclaimReason.ModelSwitched, notice.Reason);
        Assert.Equal(1, notice.ModelsRemoved);
        Assert.Equal(16 * 1024, notice.BytesFreed);
    }

    [Fact]
    public async Task A_model_switch_interrupted_by_a_restart_is_finished_after_the_next_start()
    {
        await using var harness = new CleanupHarness(armStorage: true);
        harness.State.SetCached(CleanupHarness.FoundryVariant, true);
        harness.Storage!.WriteKeepOnlySelected(true);

        harness.Service.Configure(CleanupHarness.FoundryOn(CleanupHarness.OtherAlias));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        await harness.Service.LastStorageWork.WaitAsync(Bound);

        Assert.False(harness.State.IsCached(CleanupHarness.FoundryVariant));
        Assert.True(harness.State.IsCached(CleanupHarness.OtherVariant));
        Assert.False(harness.Storage.ReadKeepOnlySelected());
    }

    [Fact]
    public async Task A_newer_switch_made_while_the_pass_runs_stops_it_before_it_deletes_the_new_choice()
    {
        await using var harness = new CleanupHarness(armStorage: true);
        var svc = harness.Service;
        svc.Configure(CleanupHarness.FoundryOn(CleanupHarness.FoundryAlias));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        await svc.LastStorageWork.WaitAsync(Bound);

        // Downloaded after the startup pass, so only the switches below decide its fate.
        harness.State.SetCached(CleanupHarness.ThirdVariant, true);
        var oldVariant = harness.Qwen.VariantModels[0];
        oldVariant.RemoveGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The pass for this switch would remove the old model and then the cached third one.
        svc.Configure(CleanupHarness.FoundryOn(CleanupHarness.OtherAlias));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var pass = svc.LastStorageWork;
        await oldVariant.RemoveStarted.Task.WaitAsync(Bound);

        // Mid-pass, the user picks the third model.
        svc.Configure(CleanupHarness.FoundryOn(CleanupHarness.ThirdAlias));
        oldVariant.RemoveGate.SetResult();
        await pass.WaitAsync(Bound);

        Assert.DoesNotContain("remove:" + CleanupHarness.ThirdVariant, harness.State.Events);
        Assert.True(harness.Storage!.ReadKeepOnlySelected(), "The newer switch's marker survives.");

        // The newer switch then finishes the job once its own model is in use.
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        await svc.LastStorageWork.WaitAsync(Bound);

        Assert.Contains("remove:" + CleanupHarness.OtherVariant, harness.State.Events);
        Assert.DoesNotContain("remove:" + CleanupHarness.ThirdVariant, harness.State.Events);
        Assert.True(harness.State.IsCached(CleanupHarness.ThirdVariant));
        Assert.False(harness.Storage.ReadKeepOnlySelected());
    }

    [Fact]
    public async Task A_reclaim_queued_behind_a_load_does_nothing_once_the_user_switched_back()
    {
        await using var harness = new CleanupHarness(armStorage: true);
        var svc = harness.Service;
        svc.Configure(CleanupHarness.FoundryOn());
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        // An explicit load holds the init lock, so the reclaim below has to queue behind it.
        harness.Qwen.LoadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var load = svc.LoadFoundryModelAsync(CleanupHarness.FoundryAlias);
        await harness.Qwen.LoadStarted.Task.WaitAsync(Bound);

        svc.Configure(CleanupHarness.OtherProviderOff);
        var reclaim = svc.LastStorageWork;
        svc.Configure(CleanupHarness.FoundryOn());

        harness.Qwen.LoadGate.SetResult();
        Assert.True(await load.WaitAsync(Bound));
        await reclaim.WaitAsync(Bound);

        Assert.DoesNotContain(harness.State.Events, e => e.StartsWith("remove:") || e == "stop-web");
        Assert.True(harness.State.IsCached(CleanupHarness.FoundryVariant));
    }
}

/// <summary>
/// Isolated profiles (tests, <c>SCRIBE_DATA_DIR</c>) keep Foundry Local inside their own root, and the
/// normal profile keeps the SDK default every existing install already uses. One resolved value
/// feeds both the SDK configuration and the only root reclaim may delete under.
/// </summary>
public sealed class FoundryLocalDataDirTests
{
    [Fact]
    public void The_normal_profile_keeps_the_sdk_default_directory()
    {
        Assert.Equal(
            @"C:\Users\someone\.Scribe",
            FoundryLocalStorage.ResolveAppDataDir(isolatedRoot: false, @"C:\ignored", @"C:\Users\someone"));
    }

    [Fact]
    public void An_isolated_profile_keeps_foundry_inside_its_own_root()
    {
        Assert.Equal(
            @"D:\portable\ScribeData\foundry",
            FoundryLocalStorage.ResolveAppDataDir(isolatedRoot: true, @"D:\portable\ScribeData", @"C:\Users\someone"));

        // A relative SCRIBE_DATA_DIR is made absolute, and is never the user's own directory.
        var relative = FoundryLocalStorage.ResolveAppDataDir(isolatedRoot: true, @"relative\data", @"C:\Users\someone");
        Assert.NotNull(relative);
        Assert.True(Path.IsPathFullyQualified(relative));
        Assert.EndsWith(@"relative\data\foundry", relative);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"relative\profile")]
    public void An_unresolvable_profile_folder_disarms_reclaim_rather_than_guessing(string? profile)
    {
        Assert.Null(FoundryLocalStorage.ResolveAppDataDir(isolatedRoot: false, @"C:\data", profile));
    }

    [Fact]
    public void An_explicit_root_is_isolated_and_its_foundry_files_stay_inside_it()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);

        Assert.True(paths.IsIsolatedRoot);
        var storage = FoundryLocalStorage.For(paths);
        Assert.NotNull(storage);
        Assert.Equal(Path.Combine(temp.Path, "foundry"), storage.AppDataDir);
        Assert.Equal(Path.Combine(temp.Path, "foundry", "cache", "models"), storage.ModelCacheDir);
        Assert.Equal(Path.Combine(temp.Path, "foundry", "ep"), storage.ExecutionProviderDir);
        Assert.Equal(Path.Combine(temp.Path, FoundryLocalStorage.MarkerFileName), storage.MarkerPath);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.NotEqual(Path.Combine(home, ".Scribe"), storage.AppDataDir, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_fallback_for_an_isolated_root_stays_isolated()
    {
        using var temp = new TempDirectory();
        var blocked = temp.Combine("blocked");
        File.WriteAllText(blocked, "a file where the root directory should be");

        var paths = AppPaths.CreateForStartup(blocked, temp.Combine("fallback"));

        Assert.True(paths.IsFallbackRoot);
        Assert.True(paths.IsIsolatedRoot);
        Assert.Equal(Path.Combine(temp.Combine("fallback"), "foundry"), FoundryLocalStorage.ResolveAppDataDir(paths));
    }

    [Fact]
    public void The_default_profile_resolves_to_the_unchanged_sdk_default()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SCRIBE_DATA_DIR")))
        {
            return; // This machine is itself running an isolated profile.
        }

        var paths = new AppPaths();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.False(paths.IsIsolatedRoot);
        Assert.Equal(Path.Combine(home, ".Scribe"), FoundryLocalStorage.ResolveAppDataDir(paths));
    }

    [Fact]
    public async Task The_sdk_is_configured_with_the_same_directory_reclaim_is_confined_to()
    {
        await using var harness = new CleanupHarness(armStorage: true);

        Assert.True(await harness.Service.ProbeAsync());

        // The first (and only) configuration this process gives the SDK carries the directory.
        var first = Assert.Single(harness.Host.Configurations);
        Assert.Equal(harness.Storage!.AppDataDir, first.AppDataDir);
        Assert.Equal(harness.FoundryDir, first.AppDataDir);
        Assert.Equal(FoundryLocalStorage.AppName, first.AppName);
    }

    [Fact]
    public async Task An_unarmed_service_is_still_configured_with_the_resolved_directory()
    {
        // Harnesses and evals construct the service without reclaim; they must still never download
        // into the real user's directory when they run on an isolated root.
        await using var harness = new CleanupHarness(armStorage: false);

        Assert.Null(harness.Storage);
        Assert.Equal(harness.FoundryDir, harness.Service.CreateFoundryConfiguration().AppDataDir);
    }

    [Fact]
    public async Task The_default_profile_configures_the_sdk_default_it_would_have_used_anyway()
    {
        await using var svc = new TextCleanupService(
            new CapturingLogger<TextCleanupService>(),
            paths: null,
            new FakeFoundryHost(() => throw new InvalidOperationException("must not start")),
            foundryStorage: null);

        // Whatever mode this machine runs in, the service and the resolver agree.
        Assert.Equal(FoundryLocalStorage.ResolveAppDataDir(new AppPaths()), svc.CreateFoundryConfiguration().AppDataDir);
    }
}