using System.Net;
using System.Text.Json;
using Scribe.Core.Cleanup;

namespace Scribe.Core.Tests;

/// <summary>
/// How Foundry Local 2.x falls back from a graphics card build, and what it remembers across starts. A build ONNX Runtime
/// cannot run falls back for the session only, since ONNX Runtime reports a busy graphics card in the same words. A WebGPU
/// shader failure is remembered in the 2.x file. A fallback a 1.x build remembered is set aside, and the model starts on the
/// build Foundry Local would pick on a new install, which on an RTX 5080 took Qwen3 1.7B from 12.6 s on the CPU to 0.70 s
/// on CUDA (0.5.2, Foundry Local 2.1.0).
/// </summary>
public sealed class FoundryLocalFallbackTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);

    private const string Alias = "qwen3-4b";
    private const string Cuda = "qwen3-4b-cuda-gpu:2";
    private const string WebGpu = "qwen3-4b-generic-gpu:2";
    private const string Cpu = "qwen3-4b-generic-cpu:3";

    private const string CudaProvider = "CUDAExecutionProvider";
    private const string WebGpuProvider = "WebGpuExecutionProvider";
    private const string CpuProvider = "CPUExecutionProvider";

    // Foundry Local 2.1.0's answer to Qwen3 4B's CUDA build on an RTX 5080 while cuDNN attention was on.
    private const string BuildFailure =
        "{\"error\":{\"code\":null,\"message\":\"Inference failed: onnx_chat_generator.cc:431 " +
        "fl::OnnxChatGenerator::CreatePrepared failed to create generator: Non-zero status code returned while " +
        "running GroupQueryAttention node. Name:'/model/layers.0/attn/GroupQueryAttention_qknorm'\"}}";

    // Foundry Local 1.2.4's answer to every Qwen3 WebGPU build on the same card.
    private const string ShaderFailure =
        "{\"error\":{\"message\":\"Failed to handle OpenAI completion: Non-zero status code returned " +
        "while running QuickGelu node. Name:'/model/layers.0/mlp/act_fn/Mul/QuickGeluFusion/' " +
        "Status Message: Failed to create a WebGPU compute pipeline: [Invalid ShaderModule " +
        "\\\"QuickGelu\\\"] is invalid due to a previous error.\"}}";

    [Fact]
    public async Task A_build_onnx_runtime_cannot_run_falls_back_for_this_session_only()
    {
        var requests = new RequestLog();
        FakeFoundryModel family = null!;
        await using var harness = new CleanupHarness(
            http: requests.Answering(model => model == Cuda ? BuildFailure : null),
            extraFamilies: state => [family = FakeFoundryModel.FamilyOn(state, Alias, (Cuda, CudaProvider), (Cpu, CpuProvider))]);
        harness.Runtime.ExtraEps = [CudaProvider];
        var svc = harness.Service;

        svc.Configure(CleanupHarness.FoundryOn(Alias));
        await harness.WaitForStatusAsync(status => status is CleanupStatus.Ready or CleanupStatus.Unavailable);

        Assert.Equal(CleanupStatus.Ready, svc.Status);
        Assert.Contains(Cpu, harness.State.LoadedIds());
        Assert.DoesNotContain(Cuda, harness.State.LoadedIds());
        Assert.False(File.Exists(DemotionsPath(harness)), "A build failure is not remembered past this session.");
        Assert.False(File.Exists(LegacyDemotionsPath(harness)));
        Assert.Contains(harness.Log.Entries, entry => entry.Message.Contains("until Scribe restarts", StringComparison.Ordinal));

        // Saved again in the same session, the model starts on its CPU build without trying the graphics card again.
        svc.Configure(CleanupOptions.Disabled);
        svc.Configure(CleanupHarness.FoundryOn(Alias));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        Assert.Equal(1, requests.To(Cuda));

        // The next start tries the graphics card build again, which Foundry Local ranks first among those downloaded.
        family.SelectVariant(family.VariantModels[0]);
        await using var next = NextStart(harness);
        next.Configure(CleanupHarness.FoundryOn(Alias));
        await WaitForAsync(next, status => status is CleanupStatus.Ready or CleanupStatus.Unavailable);

        Assert.Equal(CleanupStatus.Ready, next.Status);
        Assert.Equal(2, requests.To(Cuda));
        Assert.Equal(CleanupOutcome.Cleaned, (await next.CleanAsync("um so we ship on friday").WaitAsync(Bound)).Outcome);
        Assert.False(File.Exists(DemotionsPath(harness)));
    }

    [Fact]
    public async Task A_tensorrt_rtx_build_falls_back_to_its_family_s_cpu_build()
    {
        const string Qwen25 = "qwen2.5-1.5b";
        const string TensorRtRtx = "qwen2.5-1.5b-instruct-trtrtx-gpu:2";
        const string FamilyCpu = "qwen2.5-1.5b-instruct-generic-cpu:4";
        const string TensorRtRtxProvider = "NvTensorRTRTXExecutionProvider";

        var requests = new RequestLog();
        await using var harness = new CleanupHarness(
            http: requests.Answering(model => model == TensorRtRtx ? BuildFailure : null),
            extraFamilies: state => [FakeFoundryModel.FamilyOn(state, Qwen25, (TensorRtRtx, TensorRtRtxProvider), (FamilyCpu, CpuProvider))]);
        harness.Runtime.ExtraEps = [TensorRtRtxProvider];

        harness.Service.Configure(CleanupHarness.FoundryOn(Qwen25));
        await harness.WaitForStatusAsync(status => status is CleanupStatus.Ready or CleanupStatus.Unavailable);

        Assert.Equal(CleanupStatus.Ready, harness.Service.Status);
        Assert.Contains(FamilyCpu, harness.State.LoadedIds());
        Assert.Equal(1, requests.To(TensorRtRtx));
        Assert.True(requests.To(FamilyCpu) > 0);
    }

    [Fact]
    public async Task A_webgpu_shader_failure_is_remembered_in_the_2x_file()
    {
        var requests = new RequestLog();
        await using var harness = new CleanupHarness(
            http: requests.Answering(model => model == WebGpu ? ShaderFailure : null),
            extraFamilies: state => [FakeFoundryModel.FamilyOn(state, Alias, (WebGpu, WebGpuProvider), (Cpu, CpuProvider))]);
        harness.Runtime.ExtraEps = [WebGpuProvider];

        harness.Service.Configure(CleanupHarness.FoundryOn(Alias));
        await harness.WaitForStatusAsync(status => status is CleanupStatus.Ready or CleanupStatus.Unavailable);

        Assert.Equal(CleanupStatus.Ready, harness.Service.Status);
        Assert.Contains(Cpu, harness.State.LoadedIds());
        Assert.Equal(Cpu, ReadDemotions(DemotionsPath(harness))[Alias]);
        Assert.False(File.Exists(LegacyDemotionsPath(harness)), "A 2.x build never writes the 1.x file.");
        Assert.DoesNotContain(harness.Log.Entries, entry => entry.Message.Contains("until Scribe restarts", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_model_a_1x_build_demoted_starts_on_the_build_foundry_local_picks_on_a_new_install()
    {
        var requests = new RequestLog();
        FakeFoundryModel family = null!;
        await using var harness = new CleanupHarness(
            http: requests.Answering(_ => null),
            extraFamilies: state =>
                [family = FakeFoundryModel.FamilyOn(state, Alias, (Cuda, CudaProvider), (WebGpu, WebGpuProvider), (Cpu, CpuProvider))]);
        LeaveWhat051Left(harness, family);

        harness.Service.Configure(CleanupHarness.FoundryOn(Alias));
        await harness.WaitForStatusAsync(status => status is CleanupStatus.Ready or CleanupStatus.Unavailable);

        Assert.Equal(CleanupStatus.Ready, harness.Service.Status);
        Assert.Contains("download:" + Cuda, harness.State.Events);
        Assert.Contains(Cuda, harness.State.LoadedIds());
        Assert.Equal(0, requests.To(WebGpu));
        Assert.Equal(0, requests.To(Cpu));
        Assert.Equal(CleanupOutcome.Cleaned, (await harness.Service.CleanAsync("um so we ship on friday").WaitAsync(Bound)).Outcome);
        Assert.True(requests.To(Cuda) >= 2);
        Assert.Contains(harness.Log.Entries, entry => entry.Message.Contains("An earlier Foundry Local version moved", StringComparison.Ordinal));

        // Settled: a blank 2.x entry, so later starts leave the choice to Foundry Local, and the 1.x file as it was.
        Assert.Equal(string.Empty, ReadDemotions(DemotionsPath(harness))[Alias]);
        Assert.Equal(Cpu, ReadDemotions(LegacyDemotionsPath(harness))[Alias]);

        // A later start neither selects nor downloads again.
        var downloads = harness.State.Events.Count(e => e.StartsWith("download:", StringComparison.Ordinal));
        family.SelectVariant(family.VariantModels[0]);
        await using var next = NextStart(harness);
        next.Configure(CleanupHarness.FoundryOn(Alias));
        await WaitForAsync(next, status => status is CleanupStatus.Ready or CleanupStatus.Unavailable);

        Assert.Equal(CleanupStatus.Ready, next.Status);
        Assert.Equal(downloads, harness.State.Events.Count(e => e.StartsWith("download:", StringComparison.Ordinal)));
        Assert.Single(harness.Log.Entries, entry => entry.Message.Contains("An earlier Foundry Local version moved", StringComparison.Ordinal));
    }

    [Fact]
    public async Task When_that_build_cannot_be_downloaded_the_build_1x_used_serves_until_scribe_restarts()
    {
        var requests = new RequestLog();
        FakeFoundryModel family = null!;
        await using var harness = new CleanupHarness(
            http: requests.Answering(_ => null),
            extraFamilies: state =>
                [family = FakeFoundryModel.FamilyOn(state, Alias, (Cuda, CudaProvider), (WebGpu, WebGpuProvider), (Cpu, CpuProvider))]);
        LeaveWhat051Left(harness, family);
        family.DownloadFailure = new HttpRequestException("No network.");

        harness.Service.Configure(CleanupHarness.FoundryOn(Alias));
        await harness.WaitForStatusAsync(status => status is CleanupStatus.Ready or CleanupStatus.Unavailable);

        Assert.Equal(CleanupStatus.Ready, harness.Service.Status);
        Assert.Contains(Cpu, harness.State.LoadedIds());
        Assert.Equal(0, requests.To(WebGpu));
        Assert.Contains(harness.Log.Entries, entry => entry.Message.Contains("Could not download or load", StringComparison.Ordinal));
        Assert.False(File.Exists(DemotionsPath(harness)), "Nothing is settled while the chosen build has not run.");

        // Until Scribe restarts, starting cleanup again uses that build without trying the download again.
        var tries = harness.State.Events.Count(e => e == "download:" + Cuda);
        harness.Service.Configure(CleanupOptions.Disabled);
        harness.Service.Configure(CleanupHarness.FoundryOn(Alias));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        Assert.Equal(tries, harness.State.Events.Count(e => e == "download:" + Cuda));

        // The next start, online again, chooses it again and settles. Foundry Local prefers the downloaded WebGPU build.
        family.DownloadFailure = null;
        family.SelectVariant(family.VariantModels[1]);
        await using var next = NextStart(harness);
        next.Configure(CleanupHarness.FoundryOn(Alias));
        await WaitForAsync(next, status => status is CleanupStatus.Ready or CleanupStatus.Unavailable);

        Assert.Equal(CleanupStatus.Ready, next.Status);
        Assert.Contains(Cuda, harness.State.LoadedIds());
        Assert.Equal(0, requests.To(WebGpu));
        Assert.Equal(string.Empty, ReadDemotions(DemotionsPath(harness))[Alias]);
    }

    [Fact]
    public async Task When_that_build_cannot_load_the_build_1x_used_serves_and_nothing_is_settled()
    {
        var requests = new RequestLog();
        FakeFoundryModel family = null!;
        await using var harness = new CleanupHarness(
            http: requests.Answering(_ => null),
            extraFamilies: state =>
                [family = FakeFoundryModel.FamilyOn(state, Alias, (Cuda, CudaProvider), (WebGpu, WebGpuProvider), (Cpu, CpuProvider))]);
        LeaveWhat051Left(harness, family);

        // A laptop graphics card too small for the CUDA build, or one a game is using.
        family.LoadFailureById = id => id == Cuda ? new InvalidOperationException("Failed to allocate memory for requested buffer.") : null;

        harness.Service.Configure(CleanupHarness.FoundryOn(Alias));
        await harness.WaitForStatusAsync(status => status is CleanupStatus.Ready or CleanupStatus.Unavailable);

        Assert.Equal(CleanupStatus.Ready, harness.Service.Status);
        Assert.Contains(Cpu, harness.State.LoadedIds());
        Assert.DoesNotContain(Cuda, harness.State.LoadedIds());
        Assert.Equal(0, requests.To(Cuda));
        Assert.Equal(0, requests.To(WebGpu));
        Assert.Equal(CleanupOutcome.Cleaned, (await harness.Service.CleanAsync("um so we ship on friday").WaitAsync(Bound)).Outcome);
        Assert.False(File.Exists(DemotionsPath(harness)), "A build that could not load is chosen again at the next start.");

        // The next start, with the graphics card free, chooses it again: Foundry Local now prefers it, since it is downloaded.
        family.LoadFailureById = null;
        family.SelectVariant(family.VariantModels[0]);
        await using var next = NextStart(harness);
        next.Configure(CleanupHarness.FoundryOn(Alias));
        await WaitForAsync(next, status => status is CleanupStatus.Ready or CleanupStatus.Unavailable);

        Assert.Equal(CleanupStatus.Ready, next.Status);
        Assert.Contains(Cuda, harness.State.LoadedIds());
        Assert.Equal(string.Empty, ReadDemotions(DemotionsPath(harness))[Alias]);
    }

    [Fact]
    public async Task When_that_build_fails_its_first_request_for_any_reason_the_build_1x_used_serves()
    {
        // Not a failure Scribe recognizes as the build's: a timeout, an NPU build, a busy graphics card.
        const string Unrecognized = "{\"error\":{\"message\":\"Inference failed: the device stopped responding.\"}}";
        var requests = new RequestLog();
        FakeFoundryModel family = null!;
        await using var harness = new CleanupHarness(
            http: requests.Answering(model => model == Cuda ? Unrecognized : null),
            extraFamilies: state =>
                [family = FakeFoundryModel.FamilyOn(state, Alias, (Cuda, CudaProvider), (WebGpu, WebGpuProvider), (Cpu, CpuProvider))]);
        LeaveWhat051Left(harness, family);

        harness.Service.Configure(CleanupHarness.FoundryOn(Alias));
        await harness.WaitForStatusAsync(status => status is CleanupStatus.Ready or CleanupStatus.Unavailable);

        Assert.Equal(CleanupStatus.Ready, harness.Service.Status);
        Assert.Contains(Cpu, harness.State.LoadedIds());
        Assert.DoesNotContain(Cuda, harness.State.LoadedIds());
        Assert.Equal(1, requests.To(Cuda));
        Assert.Equal(0, requests.To(WebGpu));
        Assert.Contains(harness.Log.Entries, entry => entry.Message.Contains("failed its first request (", StringComparison.Ordinal));
        Assert.False(File.Exists(DemotionsPath(harness)));

        // The next start tries it again.
        family.SelectVariant(family.VariantModels[0]);
        await using var next = NextStart(harness);
        next.Configure(CleanupHarness.FoundryOn(Alias));
        await WaitForAsync(next, status => status is CleanupStatus.Ready or CleanupStatus.Unavailable);

        Assert.Equal(CleanupStatus.Ready, next.Status);
        Assert.Equal(2, requests.To(Cuda));
        Assert.False(File.Exists(DemotionsPath(harness)));
    }

    [Fact]
    public async Task A_shader_failure_of_the_chosen_build_is_remembered_once_every_provider_registered()
    {
        var requests = new RequestLog();
        FakeFoundryModel family = null!;
        await using var harness = new CleanupHarness(
            http: requests.Answering(model => model == WebGpu ? ShaderFailure : null),
            extraFamilies: state =>
                [family = FakeFoundryModel.FamilyOn(state, Alias, (Cuda, CudaProvider), (WebGpu, WebGpuProvider), (Cpu, CpuProvider))]);
        LeaveWhat051Left(harness, family);

        // A PC with WebGPU and no CUDA, where every provider it has registered: WebGPU is the best build here.
        harness.Runtime.ExtraEps = [WebGpuProvider];

        harness.Service.Configure(CleanupHarness.FoundryOn(Alias));
        await harness.WaitForStatusAsync(status => status is CleanupStatus.Ready or CleanupStatus.Unavailable);

        Assert.Equal(CleanupStatus.Ready, harness.Service.Status);
        Assert.Contains(Cpu, harness.State.LoadedIds());
        Assert.Equal(1, requests.To(WebGpu));
        Assert.DoesNotContain("download:" + Cuda, harness.State.Events);
        Assert.Equal(Cpu, ReadDemotions(DemotionsPath(harness))[Alias]);

        // Settled: the next start uses the CPU build without trying WebGPU again.
        family.SelectVariant(family.VariantModels[1]);
        await using var next = NextStart(harness);
        next.Configure(CleanupHarness.FoundryOn(Alias));
        await WaitForAsync(next, status => status is CleanupStatus.Ready or CleanupStatus.Unavailable);

        Assert.Equal(CleanupStatus.Ready, next.Status);
        Assert.Equal(1, requests.To(WebGpu));
    }

    [Fact]
    public async Task A_start_whose_graphics_card_runtimes_did_not_register_settles_nothing()
    {
        var requests = new RequestLog();
        FakeFoundryModel family = null!;
        await using var harness = new CleanupHarness(
            http: requests.Answering(_ => null),
            extraFamilies: state =>
                [family = FakeFoundryModel.FamilyOn(state, Alias, (Cuda, CudaProvider), (WebGpu, WebGpuProvider), (Cpu, CpuProvider))]);
        LeaveWhat051Left(harness, family);

        // The first 2.x start, offline: Foundry Local's graphics card runtimes are downloads, and none registered.
        harness.Runtime.ExtraEps = [];
        harness.Runtime.FailedEps = [CudaProvider, WebGpuProvider];

        harness.Service.Configure(CleanupHarness.FoundryOn(Alias));
        await harness.WaitForStatusAsync(status => status is CleanupStatus.Ready or CleanupStatus.Unavailable);

        Assert.Equal(CleanupStatus.Ready, harness.Service.Status);
        Assert.Contains(Cpu, harness.State.LoadedIds());
        Assert.Contains(harness.Log.Entries, entry => entry.Message.Contains("setup was incomplete", StringComparison.Ordinal));
        Assert.False(File.Exists(DemotionsPath(harness)), "A start that could not tell which build is best settles nothing.");

        // Online at the next start, every runtime registers, and the model moves to the CUDA build.
        harness.Runtime.ExtraEps = [CudaProvider, WebGpuProvider];
        harness.Runtime.FailedEps = [];
        family.SelectVariant(family.VariantModels[1]);
        await using var next = NextStart(harness);
        next.Configure(CleanupHarness.FoundryOn(Alias));
        await WaitForAsync(next, status => status is CleanupStatus.Ready or CleanupStatus.Unavailable);

        Assert.Equal(CleanupStatus.Ready, next.Status);
        Assert.Contains(Cuda, harness.State.LoadedIds());
        Assert.Equal(0, requests.To(WebGpu));
        Assert.Equal(string.Empty, ReadDemotions(DemotionsPath(harness))[Alias]);
    }

    [Fact]
    public async Task An_exact_build_the_user_chose_is_never_reselected()
    {
        var requests = new RequestLog();
        await using var harness = new CleanupHarness(
            http: requests.Answering(_ => null),
            extraFamilies: state =>
                [FakeFoundryModel.FamilyOn(state, Alias, (Cuda, CudaProvider), (WebGpu, WebGpuProvider), (Cpu, CpuProvider))]);
        harness.Runtime.ExtraEps = [CudaProvider, WebGpuProvider];
        harness.State.SetCached(WebGpu, true);
        WriteDemotions(LegacyDemotionsPath(harness), new() { [WebGpu] = Cpu });

        harness.Service.Configure(CleanupHarness.FoundryOn(WebGpu));
        await harness.WaitForStatusAsync(status => status is CleanupStatus.Ready or CleanupStatus.Unavailable);

        Assert.Equal(CleanupStatus.Ready, harness.Service.Status);
        Assert.Contains(WebGpu, harness.State.LoadedIds());
        Assert.DoesNotContain("download:" + Cuda, harness.State.Events);
        Assert.Equal(0, requests.To(Cuda));
        Assert.Equal(string.Empty, ReadDemotions(DemotionsPath(harness))[WebGpu]);
    }

    // What 0.5.1 left on an NVIDIA PC: the WebGPU build it tried and the CPU build it moved to, both downloaded, and its
    // 1.x demotion. Foundry Local prefers a downloaded build, so it selects the WebGPU build, and on 2.x that build no
    // longer fails its readiness check.
    private static void LeaveWhat051Left(CleanupHarness harness, FakeFoundryModel family)
    {
        harness.Runtime.ExtraEps = [CudaProvider, WebGpuProvider];
        harness.State.SetCached(WebGpu, true);
        harness.State.SetCached(Cpu, true);
        family.SelectVariant(family.VariantModels[1]);
        WriteDemotions(LegacyDemotionsPath(harness), new() { [Alias] = Cpu });
    }

    // Another service over the same profile, catalog and web service: the next time Scribe starts.
    private static TextCleanupService NextStart(CleanupHarness harness) =>
        new(harness.Log, harness.Paths, harness.Host, harness.Storage)
        {
            OpenAIClientOptionsOverride = ScriptedHttpHandler.Install(harness.Http),
            LocalServers = harness.LocalServers,
            LocalModelStartWait = TimeSpan.Zero,
        };

    private static async Task WaitForAsync(TextCleanupService service, Func<CleanupStatus, bool> predicate)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check()
        {
            if (predicate(service.Status))
            {
                reached.TrySetResult();
            }
        }

        service.StatusChanged += Check;
        try
        {
            Check();
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            service.StatusChanged -= Check;
        }
    }

    private static string DemotionsPath(CleanupHarness harness) =>
        Path.Combine(harness.Paths.RootDir, FoundryDemotionReset.FileName);

    private static string LegacyDemotionsPath(CleanupHarness harness) =>
        Path.Combine(harness.Paths.RootDir, FoundryDemotionReset.LegacyFileName);

    private static Dictionary<string, string> ReadDemotions(string path) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!;

    private static void WriteDemotions(string path, Dictionary<string, string> demotions)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(demotions));
    }

    /// <summary>Records the model each request names, and answers it: a failure body with HTTP 500, or a cleaned line.</summary>
    private sealed class RequestLog
    {
        private readonly List<string> _models = [];

        public int To(string model)
        {
            lock (_models)
            {
                return _models.Count(m => m == model);
            }
        }

        public ScriptedHttpHandler Answering(Func<string, string?> failureFor) => new(async (request, ct) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var model = body.RootElement.GetProperty("model").GetString()!;
            lock (_models)
            {
                _models.Add(model);
            }

            return failureFor(model) is { } failure
                ? ScriptedHttpHandler.Json(HttpStatusCode.InternalServerError, failure)
                : ScriptedHttpHandler.ChatCompletion("So we ship on Friday.");
        });
    }
}
