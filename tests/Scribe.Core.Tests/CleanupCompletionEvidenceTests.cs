using System.ClientModel;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;
using Scribe.Core.Cleanup;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;
using Scribe.Core.Tests.Vocabulary;
using Scribe.Core.Vocabulary;
using static Scribe.Core.Tests.Vocabulary.TestVocabularies;

namespace Scribe.Core.Tests;

public sealed class CleanupCompletionEvidenceTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);
    private const string Dictated = "please send the report today";
    private const string Rewritten = "Please send the report today.";
    private const string CutReason = "AI cleanup's answer was cut off before it finished.";

    [Theory]
    [InlineData(null, "Cut")]
    [InlineData("stop", "Finished")]
    [InlineData("length", "Cut")]
    [InlineData("content_filter", "Cut")]
    public async Task The_pinned_SDK_keeps_the_original_finish_field_beside_its_normalized_value(
        string? finish, string expected)
    {
        var choice = new Dictionary<string, object?>
        {
            ["index"] = 0,
            ["message"] = new { role = "assistant", content = "Please send the report." },
        };
        if (finish is not null)
        {
            choice["finish_reason"] = finish;
        }

        const int ceiling = 2288;
        var json = JsonSerializer.Serialize(new
        {
            id = "synthetic",
            @object = "chat.completion",
            created = 1700000000,
            model = "test",
            choices = new[] { choice },
            usage = new { prompt_tokens = 100, completion_tokens = ceiling, total_tokens = 100 + ceiling },
        });
        using var http = new ScriptedHttpHandler((_, _) =>
            Task.FromResult(ScriptedHttpHandler.Json(HttpStatusCode.OK, json)));
        var options = new OpenAIClientOptions { Endpoint = new Uri("https://completion.example.invalid/v1") };
        ScriptedHttpHandler.Install(http)(options);
        using var client = new OpenAI.Chat.ChatClient("test", new ApiKeyCredential("synthetic-key"), options).AsIChatClient();
        var agent = new ChatClientAgent(client, instructions: "Fix the text.");

        var result = await agent.RunAsync("please send the report", options:
            new ChatClientAgentRunOptions(new ChatOptions { MaxOutputTokens = ceiling }));

        var chat = Assert.IsType<ChatResponse>(result.RawRepresentation);
        var raw = Assert.IsType<OpenAI.Chat.ChatCompletion>(chat.RawRepresentation);
        Assert.Equal(ceiling, result.Usage?.OutputTokenCount);
        Assert.Equal(finish is null or "stop", result.FinishReason == ChatFinishReason.Stop);
#pragma warning disable SCME0001
        Assert.Equal(finish is not null, raw.Patch.TryGetValue("$.choices[0].finish_reason"u8, out string? supplied));
#pragma warning restore SCME0001
        Assert.Equal(finish, supplied);
        Assert.Equal(expected, TextCleanupService.EndOf(result, ceiling).ToString());
    }

    [Theory]
    [InlineData("completed", null, 2288, "Finished")]
    [InlineData("incomplete", "max_output_tokens", 2288, "Cut")]
    [InlineData("incomplete", "content_filter", 50, "Cut")]
    [InlineData("incomplete", null, 50, "Cut")]
    [InlineData(null, null, 2288, "Cut")]
    [InlineData(null, null, 50, "Unconfirmed")]
    public async Task Responses_uses_its_explicit_completion_status_not_a_missing_finish_reason(
        string? status, string? incompleteReason, int generated, string expected)
    {
        var body = new Dictionary<string, object?>
        {
            ["id"] = "synthetic",
            ["object"] = "response",
            ["created_at"] = 1700000000,
            ["model"] = "test",
            ["output"] = new[]
            {
                new
                {
                    type = "message", id = "synthetic-message", status = "completed", role = "assistant",
                    content = new[] { new { type = "output_text", text = "Please send the report.", annotations = Array.Empty<object>() } },
                },
            },
            ["parallel_tool_calls"] = false,
            ["tool_choice"] = "auto",
            ["tools"] = Array.Empty<object>(),
            ["usage"] = new { input_tokens = 100, output_tokens = generated, total_tokens = 100 + generated },
        };
        if (status is not null)
        {
            body["status"] = status;
        }

        if (incompleteReason is not null)
        {
            body["incomplete_details"] = new { reason = incompleteReason };
        }

        using var http = new ScriptedHttpHandler((_, _) =>
            Task.FromResult(ScriptedHttpHandler.Json(HttpStatusCode.OK, JsonSerializer.Serialize(body))));
        var options = new OpenAIClientOptions { Endpoint = new Uri("https://completion.example.invalid/v1") };
        ScriptedHttpHandler.Install(http)(options);
        var client = new OpenAIClient(new ApiKeyCredential("synthetic-key"), options);
#pragma warning disable OPENAI001
        var agent = TextCleanupService.CreateAzureResponsesAgent(client.GetResponsesClient(), "test", "Fix the text.");
#pragma warning restore OPENAI001

        var result = await agent.RunAsync("please send the report", options:
            new ChatClientAgentRunOptions(new ChatOptions { MaxOutputTokens = 2288 }));

        Assert.IsType<ChatResponse>(result.RawRepresentation);
        Assert.Equal(expected, TextCleanupService.EndOf(result, 2288).ToString());
    }

    [Theory]
    [InlineData("custom-chat")]
    [InlineData("custom-responses")]
    [InlineData("azure-chat")]
    [InlineData("azure-responses")]
    [InlineData("foundry")]
    [InlineData("lmstudio")]
    public async Task Production_transports_keep_missing_and_explicit_completion_distinct_without_bypassing_admission(string surface)
    {
        var permitted = Of(1, new Library("synthetic-pack", H1, true, Entry("harbour lanterns", "HarbourLanterns")));
        var source = new TestVocabularySource(permitted);
        await using var harness = new VocabularyCleanupHarness(source);
        var complete = false;
        harness.Network.Respond = async (request, ct) =>
        {
            if (surface == "azure-chat" && request.Path.EndsWith("/responses", StringComparison.Ordinal))
            {
                return CanaryNetwork.Json(HttpStatusCode.BadRequest, """{"error":{"message":"The requested operation is unsupported."}}""");
            }

            return request.IsProbe ? CanaryNetwork.Echo(request) : await ReplyAtCapAsync(request, complete, ct);
        };
        var options = surface switch
        {
            "azure-chat" or "azure-responses" => VocabularyCleanupHarness.Azure(),
            "foundry" => CleanupHarness.FoundryOn(),
            "lmstudio" => CleanupHarness.Custom(LocalAiServer.LmStudioAddress),
            "custom-responses" => VocabularyCleanupHarness.Custom() with { CustomApiStyle = CustomApiStyle.Responses },
            _ => VocabularyCleanupHarness.Custom(),
        };
        await harness.ConfigureAndWaitAsync(options).WaitAsync(Bound);
        var vocabulary = new VocabularyGeneration(1, [], permitted, CompiledDictionaryRules.Empty).Cleanup;
        var admitted = harness.Service.Admit(vocabulary);

        var missing = await admitted.CleanAsync(Dictated).WaitAsync(Bound);

        Assert.Equal(CleanupOutcome.Failed, missing.Outcome);
        Assert.Equal(Dictated, missing.Text);
        Assert.Equal(CutReason, missing.FailureReason);
        Assert.Equal(CutReason, missing.DisplayDetail);
        Assert.Single(harness.Network.Sent, request => !request.IsProbe);
        complete = true;

        var stopped = await admitted.CleanAsync(Dictated).WaitAsync(Bound);

        Assert.Equal(CleanupOutcome.Cleaned, stopped.Outcome);
        Assert.Equal(Rewritten, stopped.Text);
        Assert.Null(stopped.FailureReason);
        var requests = harness.Network.Sent.Count;
        source.Publish(Of(2, new Library("synthetic-pack", H1, false, Entry("harbour lanterns", "HarbourLanterns"))));

        var refused = await admitted.CleanAsync(Dictated).WaitAsync(Bound);

        Assert.Equal(CleanupOutcome.Skipped, refused.Outcome);
        Assert.Equal(Dictated, refused.Text);
        Assert.Null(refused.FailureReason);
        Assert.Equal(requests, harness.Network.Sent.Count);
        Assert.DoesNotContain(Dictated, harness.Log.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(Rewritten, harness.Log.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("HarbourLanterns", harness.Log.AllText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Logging_failure_cannot_turn_an_unconfirmed_capped_answer_into_success(bool failAtIsEnabled)
    {
        var log = new FailingLogger(failAtIsEnabled);
        await using var harness = new VocabularyCleanupHarness(source: null, serviceLog: log);
        harness.Network.Respond = async (request, ct) =>
            request.IsProbe ? CanaryNetwork.Echo(request) : await ReplyAtCapAsync(request, complete: false, ct);
        await harness.ConfigureAndWaitAsync(VocabularyCleanupHarness.Custom()).WaitAsync(Bound);
        log.Armed = true;

        var result = await harness.Service.CleanAsync(Dictated).WaitAsync(Bound);

        Assert.Equal(CleanupOutcome.Failed, result.Outcome);
        Assert.Equal(Dictated, result.Text);
        Assert.Equal(CutReason, result.FailureReason);
        Assert.Equal(CutReason, result.DisplayDetail);
        Assert.True(log.Failures > 0);
        Assert.Single(harness.Network.Sent, request => !request.IsProbe);
    }

    private static async Task<HttpResponseMessage> ReplyAtCapAsync(SentRequest request, bool complete, CancellationToken ct)
    {
        var responses = request.Path.EndsWith("/responses", StringComparison.Ordinal);
        using var sent = JsonDocument.Parse(request.Body);
        var ceiling = sent.RootElement.GetProperty(responses ? "max_output_tokens" : "max_completion_tokens").GetInt32();
        using var reply = responses ? CanaryNetwork.Responses(Rewritten) : CanaryNetwork.Chat(Rewritten);
        var body = JsonNode.Parse(await reply.Content.ReadAsStringAsync(ct))!.AsObject();
        if (responses)
        {
            if (!complete)
            {
                body.Remove("status");
            }

            body["usage"]!["output_tokens"] = ceiling;
        }
        else
        {
            if (!complete)
            {
                body["choices"]![0]!.AsObject().Remove("finish_reason");
            }

            body["usage"]!["completion_tokens"] = ceiling;
        }

        return CanaryNetwork.Json(HttpStatusCode.OK, body.ToJsonString());
    }

    private sealed class FailingLogger(bool failAtIsEnabled) : ILogger<TextCleanupService>
    {
        public bool Armed { get; set; }
        public int Failures { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel)
        {
            if (Armed && failAtIsEnabled)
            {
                Failures++;
                throw new IOException("Synthetic logger failure");
            }

            return true;
        }

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (Armed && !failAtIsEnabled && formatter(state, exception).StartsWith("AI cleanup did not use", StringComparison.Ordinal))
            {
                Failures++;
                throw new IOException("Synthetic logger failure");
            }
        }
    }
}
