using System.ClientModel;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
#pragma warning disable OPENAI001, SCME0001
using OpenAI.Responses;
using Scribe.Core.Cleanup;

namespace Scribe.Core.Tests;

/// <summary>
/// <c>AppSettings.AiCleanupPromptCaching</c> on the wire (<see cref="PromptCachePolicy"/>), read from the JSON body each
/// Microsoft Foundry surface actually sent over a fake transport. On, the request is exactly today's. Off, it carries
/// <c>prompt_cache_options</c> set to <c>{"mode":"explicit"}</c> and nothing else changes: the stored-output control, the
/// instructions and the transcript stay where they were, and no breakpoint appears anywhere, which is what makes the
/// explicit mode read and write nothing. Nothing leaves the process.
/// </summary>
/// <remarks>
/// The request this pins is the one Microsoft documents for turning the cache off, at
/// https://learn.microsoft.com/azure/foundry/openai/how-to/prompt-caching, FAQ "Can I disable prompt caching?": "On
/// Standard pay-as-you-go deployments with GPT-5.6 models and later model families, set prompt_cache_options.mode to
/// explicit and don't add any explicit breakpoints. The request doesn't use prompt caching or incur cache-write charges.
/// Earlier models and PTU-M deployments don't support this option; prompt caching remains enabled by default." Under
/// "Configure prompt cache breakpoints": "Models before the GPT-5.6 family don't support prompt_cache_options or
/// prompt_cache_breakpoint. Requests that include these parameters return a 400 error." (PromptCacheServiceTests pins what
/// Scribe does with that 400.)
/// </remarks>
public sealed class PromptCacheWireTests
{
    private sealed record Sent(string Path, string Body);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Responses_carries_the_cache_options_only_when_caching_is_off(bool caching)
    {
        var body = await SendAsync(Surface.Responses, caching);

        Assert.False(body["store"]!.GetValue<bool>());
        AssertCachePolicy(body, caching);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Chat_completions_carries_the_cache_options_only_when_caching_is_off(bool caching)
    {
        var body = await SendAsync(Surface.ChatCompletions, caching);

        Assert.False(body.ContainsKey("store"), "Chat Completions never carries a store field.");
        AssertCachePolicy(body, caching);
    }

    [Theory]
    [InlineData(Surface.Responses)]
    [InlineData(Surface.ChatCompletions)]
    public async Task Turning_caching_off_adds_the_one_field_and_changes_nothing_else(Surface surface)
    {
        var on = await SendAsync(surface, caching: true);
        var off = await SendAsync(surface, caching: false);

        Assert.True(off.Remove(PromptCachePolicy.FieldName));
        Assert.True(JsonNode.DeepEquals(on, off), $"on: {on.ToJsonString()}\noff: {off.ToJsonString()}");
    }

    [Fact]
    public async Task The_default_is_today_s_request()
    {
        // The agents' parameter defaults to caching on, so every caller that does not pass it sends today's body.
        var (client, requests) = FakeAzure(() => ResponsesAnswer("Hello."));
        var agent = TextCleanupService.CreateAzureResponsesAgent(client.GetResponsesClient(), "gpt-6-astra", "Clean up the text.");

        await agent.RunAsync(TextCleanupService.BuildUserMessage("hello"));

        Assert.DoesNotContain(PromptCachePolicy.FieldName, Assert.Single(requests).Body, StringComparison.Ordinal);
    }

    public static TheoryData<int> UpstreamFactories => new() { 0, 1, 2, 3 };

    // What an upstream (a future package version) might put in RawRepresentationFactory.
    private static Func<IChatClient, object?>? Upstream(int kind) => kind switch
    {
        0 => null,
        1 => _ => new CreateResponseOptions { StoredOutputEnabled = true },
        2 => _ => new OpenAI.Chat.ChatCompletionOptions { StoredOutputEnabled = true },
        _ => _ => new object(),
    };

    [Theory]
    [MemberData(nameof(UpstreamFactories))]
    public void Every_options_object_the_wrapper_hands_a_surface_carries_the_cache_options_when_caching_is_off(int upstream)
    {
        var (client, _) = FakeAzure(() => ResponsesAnswer("unused"));
        IChatClient[] surfaces =
        [
            client.GetResponsesClient().AsIChatClient("gpt-6-astra"),
            client.GetChatClient("mai-thinking-1").AsIChatClient(),
            new NamelessClient(), // names neither surface: the fail-closed branches
        ];

        foreach (var surface in surfaces)
        {
            var off = TextCleanupService.WithStoredOutputDisabled(
                new ChatOptions { RawRepresentationFactory = Upstream(upstream) }, promptCaching: false);
            var on = TextCleanupService.WithStoredOutputDisabled(
                new ChatOptions { RawRepresentationFactory = Upstream(upstream) }, promptCaching: true);

            Assert.True(CarriesCacheOptions(off.RawRepresentationFactory!(surface)), $"{surface.GetType().Name}, upstream {upstream}");
            Assert.False(CarriesCacheOptions(on.RawRepresentationFactory!(surface)), $"{surface.GetType().Name}, upstream {upstream}");
        }
    }

    [Fact]
    public void The_cache_options_are_the_explicit_mode_and_nothing_else()
    {
        var options = new CreateResponseOptions();

        PromptCachePolicy.TurnOff(options);

        var json = options.Patch.GetJson("$.prompt_cache_options"u8);
        var value = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(json.ToArray()))!.AsObject();
        Assert.Equal("explicit", value["mode"]!.GetValue<string>());
        Assert.Single(value);
        Assert.Equal(PromptCachePolicy.ExplicitWithoutBreakpoints, value.ToJsonString());
    }

    public enum Surface
    {
        Responses,
        ChatCompletions,
    }

    private static async Task<JsonObject> SendAsync(Surface surface, bool caching)
    {
        var (client, requests) = FakeAzure(() => surface == Surface.Responses
            ? ResponsesAnswer("Hello.")
            : ScriptedHttpHandler.ChatCompletion("Hello."));
        var agent = surface == Surface.Responses
            ? TextCleanupService.CreateAzureResponsesAgent(client.GetResponsesClient(), "gpt-6-astra", "Clean up the text.", caching)
            : TextCleanupService.CreateAzureChatCompletionsAgent(client.GetChatClient("mai-thinking-1"), "Clean up the text.", caching);

        var answer = await agent.RunAsync(TextCleanupService.BuildUserMessage("hello"));

        Assert.Equal("Hello.", answer.Text);
        var sent = Assert.Single(requests);
        Assert.EndsWith(surface == Surface.Responses ? "/openai/v1/responses" : "/openai/v1/chat/completions", sent.Path);
        Assert.DoesNotContain("prompt_cache_breakpoint", sent.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("prompt_cache_key", sent.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("prompt_cache_retention", sent.Body, StringComparison.Ordinal);
        return JsonNode.Parse(sent.Body)!.AsObject();
    }

    private static void AssertCachePolicy(JsonObject body, bool caching)
    {
        if (caching)
        {
            Assert.False(body.ContainsKey(PromptCachePolicy.FieldName), body.ToJsonString());
            return;
        }

        var options = Assert.IsType<JsonObject>(body[PromptCachePolicy.FieldName]);
        Assert.Equal("explicit", options["mode"]!.GetValue<string>());
        Assert.Single(options);
    }

    private static bool CarriesCacheOptions(object? options) => options switch
    {
        CreateResponseOptions responses => responses.Patch.Contains("$.prompt_cache_options"u8),
        OpenAI.Chat.ChatCompletionOptions chat => chat.Patch.Contains("$.prompt_cache_options"u8),
        _ => throw new InvalidOperationException($"Not an options type a surface keeps: {options?.GetType().Name ?? "null"}"),
    };

    // An Azure-shaped client over a fake transport that records every request body it is sent.
    private static (OpenAIClient Client, List<Sent> Requests) FakeAzure(Func<HttpResponseMessage> respond)
    {
        var requests = new List<Sent>();
        var handler = new ScriptedHttpHandler(async (request, ct) =>
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            lock (requests)
            {
                requests.Add(new Sent(request.RequestUri!.AbsolutePath, body));
            }

            return respond();
        });

        var options = new OpenAIClientOptions { Endpoint = new Uri("https://wire-test.example.invalid/openai/v1/") };
        ScriptedHttpHandler.Install(handler)(options);
        return (new OpenAIClient(new ApiKeyCredential("not-a-real-key"), options), requests);
    }

    private static HttpResponseMessage ResponsesAnswer(string text) => ScriptedHttpHandler.Json(
        System.Net.HttpStatusCode.OK,
        "{\"id\":\"resp_test\",\"object\":\"response\",\"created_at\":1700000000,\"status\":\"completed\"," +
        "\"model\":\"test\",\"output\":[{\"type\":\"message\",\"id\":\"msg_test\",\"status\":\"completed\"," +
        "\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"" + text + "\",\"annotations\":[]}]}]," +
        "\"parallel_tool_calls\":false,\"tool_choice\":\"auto\",\"tools\":[]," +
        "\"usage\":{\"input_tokens\":1,\"output_tokens\":1,\"total_tokens\":2}}");

    // A client that names neither surface (IChatClient.GetService answers null for both).
    private sealed class NamelessClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}

#pragma warning restore OPENAI001, SCME0001
