using System.Net;
using System.Text.Json;
using Scribe.Core.Cleanup;

namespace Scribe.Core.Tests;

/// <summary>
/// Foundry Local 2.x on this PC: what each cleanup request asks of it, and a graphics card build that cannot run falling
/// back to its CPU build. Measured for 0.5.2 on an RTX 5080 through Foundry Local 2.1.0's web service.
/// </summary>
public sealed class FoundryLocalRequestTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Every_request_to_foundry_local_asks_for_a_low_temperature_and_no_thinking()
    {
        var bodies = new List<JsonElement>();
        await using var harness = new CleanupHarness(http: Capturing(bodies, _ => ScriptedHttpHandler.ChatCompletion("So we ship on Friday.")));
        var svc = harness.Service;
        svc.Configure(CleanupHarness.FoundryOn());
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        Assert.Equal(CleanupOutcome.Cleaned, (await svc.CleanAsync("um so we ship on friday").WaitAsync(Bound)).Outcome);

        var sent = Snapshot(bodies);
        Assert.True(sent.Count >= 2, $"Expected the readiness probe and a cleanup request, saw {sent.Count}.");
        Assert.All(sent, body =>
        {
            Assert.Equal(0.1, body.GetProperty("temperature").GetDouble(), 3);

            // Foundry Local 2.x turns thinking off for this, as Ollama does; Qwen3.5 4B otherwise thought for 33 s.
            Assert.Equal("none", body.GetProperty("reasoning_effort").GetString());

            // It reads the SDK's own field, so the legacy name Ollama needs is not added.
            Assert.True(body.TryGetProperty("max_completion_tokens", out _));
            Assert.False(body.TryGetProperty("max_tokens", out _));
            Assert.False(body.TryGetProperty("keep_alive", out _));
        });
    }

    [Fact]
    public async Task A_graphics_card_build_that_fails_its_first_request_falls_back_to_its_cpu_build()
    {
        const string Alias = "qwen3-4b";
        const string Gpu = "qwen3-4b-cuda-gpu:2";
        const string Cpu = "qwen3-4b-generic-cpu:3";

        // Foundry Local 2.1.0's answer to Qwen3 4B's CUDA build on an RTX 5080 while cuDNN attention was on.
        const string Failure =
            "{\"error\":{\"code\":null,\"message\":\"Inference failed: onnx_chat_generator.cc:431 " +
            "fl::OnnxChatGenerator::CreatePrepared failed to create generator: Non-zero status code returned while " +
            "running GroupQueryAttention node. Name:'/model/layers.0/attn/GroupQueryAttention_qknorm'\"}}";

        var models = new List<string>();
        var http = new ScriptedHttpHandler(async (request, ct) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var model = body.RootElement.GetProperty("model").GetString()!;
            lock (models)
            {
                models.Add(model);
            }

            return model == Gpu
                ? ScriptedHttpHandler.Json(HttpStatusCode.InternalServerError, Failure)
                : ScriptedHttpHandler.ChatCompletion("So we ship on Friday.");
        });
        await using var harness = new CleanupHarness(
            http: http,
            extraFamilies: state => [FakeFoundryModel.Family(state, Alias, Gpu, Cpu)]);
        var svc = harness.Service;

        svc.Configure(CleanupHarness.FoundryOn(Alias));
        await harness.WaitForStatusAsync(status => status is CleanupStatus.Ready or CleanupStatus.Unavailable);

        Assert.Equal(CleanupStatus.Ready, svc.Status);
        Assert.Contains(Cpu, harness.State.LoadedIds());
        Assert.DoesNotContain(Gpu, harness.State.LoadedIds());
        Assert.Equal(CleanupOutcome.Cleaned, (await svc.CleanAsync("um so we ship on friday").WaitAsync(Bound)).Outcome);
        lock (models)
        {
            Assert.Equal(Gpu, models[0]);
            Assert.Equal(Cpu, models[^1]);
        }

        Assert.Contains(harness.Log.Entries, entry => entry.Message.Contains("failed its first request", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_cpu_build_that_cannot_run_is_not_blamed_on_the_graphics_card()
    {
        // Qwen3.5 2B's text model fails on every build with Foundry Local 2.1.0, its CPU build included.
        const string Failure =
            "{\"error\":{\"code\":null,\"message\":\"Inference failed: onnx_chat_generator.cc:431 " +
            "fl::OnnxChatGenerator::CreatePrepared failed to create generator: Invalid rank for input: position_ids " +
            "Got: 3 Expected: 2 Please fix either the inputs/outputs or the model.\"}}";
        await using var harness = new CleanupHarness(
            http: new ScriptedHttpHandler((_, _) => Task.FromResult(ScriptedHttpHandler.Json(HttpStatusCode.InternalServerError, Failure))),
            extraFamilies: state => [FakeFoundryModel.Family(state, "qwen3.5-2b-text", "qwen3.5-2b-text-generic-cpu:1")]);
        var svc = harness.Service;

        svc.Configure(CleanupHarness.FoundryOn("qwen3.5-2b-text"));
        await harness.WaitForStatusAsync(status => status is CleanupStatus.Ready or CleanupStatus.Unavailable);

        Assert.Equal(CleanupStatus.Unavailable, svc.Status);
        Assert.StartsWith("Foundry Local couldn't run this model on this PC. Pick a different model in Settings.", svc.StatusReason, StringComparison.Ordinal);
        Assert.DoesNotContain("GPU", svc.StatusReason, StringComparison.Ordinal);
        Assert.Contains(harness.Log.Entries, entry => entry.Message.Contains("kind=model-build", StringComparison.Ordinal));
    }

    private static ScriptedHttpHandler Capturing(List<JsonElement> bodies, Func<JsonElement, HttpResponseMessage> answer) =>
        new(async (request, ct) =>
        {
            var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
            lock (bodies)
            {
                bodies.Add(json);
            }

            return answer(json);
        });

    private static List<JsonElement> Snapshot(List<JsonElement> bodies)
    {
        lock (bodies)
        {
            return [.. bodies];
        }
    }
}
