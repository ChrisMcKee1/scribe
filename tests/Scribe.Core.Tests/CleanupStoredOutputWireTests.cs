using System.ClientModel;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
#pragma warning disable OPENAI001
using OpenAI.Responses;
#pragma warning restore OPENAI001
using Scribe.Core.Cleanup;

namespace Scribe.Core.Tests;

/// <summary>
/// Cloud cleanup stores nothing, pinned on the wire rather than on an options object. Each wire test
/// builds the Azure agent exactly as the service does (the same factory methods), puts a fake
/// transport under it, and reads the JSON body the SDK actually sent. Nothing leaves the process.
/// </summary>
/// <remarks>
/// The two surfaces carry the control differently, on purpose:
/// <list type="bullet">
/// <item>Responses defaults to store=true on Azure, so every Responses request says store=false, and
/// an options object the wrapper does not recognise is replaced rather than trusted (fail closed).</item>
/// <item>Chat Completions never sets store. Azure stores a chat completion only when store is true
/// ("To enable stored completions for your Azure OpenAI deployment set the store parameter to True",
/// learn.microsoft.com/azure/foundry-classic/openai/how-to/stored-completions), so leaving the field off is
/// already the non-storing default. The fallback exists for deployments such as MAI-Thinking-1,
/// measured working without the field, and some non-OpenAI deployments reject parameters they do not
/// know, so sending it would risk breaking cleanup for no privacy gain. What must hold is that it is
/// never true.</item>
/// </list>
/// </remarks>
public sealed class CleanupStoredOutputWireTests
{
    private sealed record SentRequest(string Path, string Body);

    // An Azure-shaped client over a fake transport that records every request body it is sent.
    private static (OpenAIClient Client, List<SentRequest> Requests) FakeAzure(Func<HttpResponseMessage> respond)
    {
        var requests = new List<SentRequest>();
        var handler = new ScriptedHttpHandler(async (request, ct) =>
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            lock (requests)
            {
                requests.Add(new SentRequest(request.RequestUri!.AbsolutePath, body));
            }

            return respond();
        });

        var options = new OpenAIClientOptions { Endpoint = new Uri("https://wire-test.example.invalid/openai/v1/") };
        ScriptedHttpHandler.Install(handler)(options);
        return (new OpenAIClient(new ApiKeyCredential("not-a-real-key"), options), requests);
    }

    private static void AssertStoreFalse(string body)
    {
        using var document = JsonDocument.Parse(body);
        Assert.True(document.RootElement.TryGetProperty("store", out var store), $"No \"store\" field was sent: {body}");
        Assert.Equal(JsonValueKind.False, store.ValueKind);
    }

    // The Chat Completions contract: never store=true. The field is also expected to be absent, the
    // wire shape Scribe has always sent there; see the class remarks for why it is not sent.
    private static void AssertNeverStoreTrue(string body)
    {
        using var document = JsonDocument.Parse(body);
        var hasStore = document.RootElement.TryGetProperty("store", out var store);
        Assert.False(hasStore && store.ValueKind == JsonValueKind.True, $"A chat completion asked Azure to store it: {body}");
        Assert.False(hasStore, $"Chat Completions is not meant to carry a \"store\" field at all: {body}");
    }

    // A minimal Responses API answer carrying one output message.
    private static HttpResponseMessage ResponsesAnswer(string text) => ScriptedHttpHandler.Json(
        System.Net.HttpStatusCode.OK,
        "{\"id\":\"resp_test\",\"object\":\"response\",\"created_at\":1700000000,\"status\":\"completed\"," +
        "\"model\":\"test\",\"output\":[{\"type\":\"message\",\"id\":\"msg_test\",\"status\":\"completed\"," +
        "\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"" + text + "\",\"annotations\":[]}]}]," +
        "\"parallel_tool_calls\":false,\"tool_choice\":\"auto\",\"tools\":[]," +
        "\"usage\":{\"input_tokens\":1,\"output_tokens\":1,\"total_tokens\":2}}");

    [Fact]
    public async Task Chat_completions_agent_never_sends_store_true()
    {
        var (client, requests) = FakeAzure(() => ScriptedHttpHandler.ChatCompletion("Hello."));
        var agent = TextCleanupService.CreateAzureChatCompletionsAgent(client.GetChatClient("mai-thinking-1"), "Clean up the text.");

        var answer = await agent.RunAsync(TextCleanupService.BuildUserMessage("hello"));

        Assert.Equal("Hello.", answer.Text);
        var sent = Assert.Single(requests);
        Assert.EndsWith("/openai/v1/chat/completions", sent.Path);
        AssertNeverStoreTrue(sent.Body);
    }

    [Fact]
    public async Task Chat_completions_agent_never_sends_store_true_even_when_upstream_asks_for_it()
    {
        // What a future package version, or a caller, might hand the agent per run.
        var (client, requests) = FakeAzure(() => ScriptedHttpHandler.ChatCompletion("Hello."));
        var agent = TextCleanupService.CreateAzureChatCompletionsAgent(client.GetChatClient("mai-thinking-1"), "Clean up the text.");
        var upstreamCalls = 0;
        var run = new ChatClientAgentRunOptions(new ChatOptions
        {
            RawRepresentationFactory = _ =>
            {
                Interlocked.Increment(ref upstreamCalls);
                return new OpenAI.Chat.ChatCompletionOptions { StoredOutputEnabled = true };
            },
        });

        var answer = await agent.RunAsync(TextCleanupService.BuildUserMessage("hello"), session: null, options: run);

        Assert.Equal("Hello.", answer.Text);
        Assert.True(upstreamCalls > 0, "The upstream factory never reached the wrapper, so this proved nothing.");
        AssertNeverStoreTrue(Assert.Single(requests).Body);
    }

    [Fact]
    public async Task Responses_agent_sends_store_false()
    {
        var (client, requests) = FakeAzure(() => ResponsesAnswer("Hello."));
#pragma warning disable OPENAI001
        var agent = TextCleanupService.CreateAzureResponsesAgent(client.GetResponsesClient(), "gpt-6-astra", "Clean up the text.");
#pragma warning restore OPENAI001

        var answer = await agent.RunAsync(TextCleanupService.BuildUserMessage("hello"));

        Assert.Equal("Hello.", answer.Text);
        var sent = Assert.Single(requests);
        Assert.EndsWith("/openai/v1/responses", sent.Path);
        AssertStoreFalse(sent.Body);
    }

    [Fact]
    public async Task Responses_agent_sends_store_false_even_when_upstream_asks_for_true()
    {
        var (client, requests) = FakeAzure(() => ResponsesAnswer("Hello."));
#pragma warning disable OPENAI001
        var agent = TextCleanupService.CreateAzureResponsesAgent(client.GetResponsesClient(), "gpt-6-astra", "Clean up the text.");
        var upstreamCalls = 0;
        var run = new ChatClientAgentRunOptions(new ChatOptions
        {
            RawRepresentationFactory = _ =>
            {
                Interlocked.Increment(ref upstreamCalls);
                return new CreateResponseOptions { StoredOutputEnabled = true };
            },
        });
#pragma warning restore OPENAI001

        var answer = await agent.RunAsync(TextCleanupService.BuildUserMessage("hello"), session: null, options: run);

        Assert.Equal("Hello.", answer.Text);
        Assert.True(upstreamCalls > 0, "The upstream factory never reached the wrapper, so this proved nothing.");
        AssertStoreFalse(Assert.Single(requests).Body);
    }

    public static TheoryData<int> UpstreamFactories => new() { 0, 1, 2, 3 };

    // What an upstream (a future package version) might put in RawRepresentationFactory.
    private static Func<IChatClient, object?>? Upstream(int kind) => kind switch
    {
        0 => null,
#pragma warning disable OPENAI001
        1 => _ => new CreateResponseOptions { StoredOutputEnabled = true },
#pragma warning restore OPENAI001
        2 => _ => new OpenAI.Chat.ChatCompletionOptions { StoredOutputEnabled = true },
        _ => _ => new object(),
    };

    [Theory]
    [MemberData(nameof(UpstreamFactories))]
    public void The_chat_completions_surface_always_gets_chat_options_that_never_set_store(int upstream)
    {
        var (client, _) = FakeAzure(() => ScriptedHttpHandler.ChatCompletion("unused"));
        var surface = client.GetChatClient("mai-thinking-1").AsIChatClient();

        var options = TextCleanupService.WithStoredOutputDisabled(new ChatOptions { RawRepresentationFactory = Upstream(upstream) });

        var raw = Assert.IsType<OpenAI.Chat.ChatCompletionOptions>(options.RawRepresentationFactory!(surface));
        Assert.Null(raw.StoredOutputEnabled);
    }

    [Theory]
    [MemberData(nameof(UpstreamFactories))]
    public void The_responses_surface_always_gets_response_options_with_the_flag_off(int upstream)
    {
        var (client, _) = FakeAzure(() => ResponsesAnswer("unused"));
#pragma warning disable OPENAI001
        var surface = client.GetResponsesClient().AsIChatClient("gpt-6-astra");

        var options = TextCleanupService.WithStoredOutputDisabled(new ChatOptions { RawRepresentationFactory = Upstream(upstream) });

        var raw = Assert.IsType<CreateResponseOptions>(options.RawRepresentationFactory!(surface));
        Assert.False(raw.StoredOutputEnabled);
#pragma warning restore OPENAI001
    }

    [Fact]
    public void A_chat_options_object_from_upstream_is_kept_with_only_store_cleared()
    {
        var (client, _) = FakeAzure(() => ScriptedHttpHandler.ChatCompletion("unused"));
        var surface = client.GetChatClient("mai-thinking-1").AsIChatClient();
        var upstream = new OpenAI.Chat.ChatCompletionOptions { StoredOutputEnabled = true, Temperature = 0.3f };

        var options = TextCleanupService.WithStoredOutputDisabled(new ChatOptions { RawRepresentationFactory = _ => upstream });

        Assert.Same(upstream, options.RawRepresentationFactory!(surface));
        Assert.Null(upstream.StoredOutputEnabled);
        Assert.Equal(0.3f, upstream.Temperature);
    }
}
