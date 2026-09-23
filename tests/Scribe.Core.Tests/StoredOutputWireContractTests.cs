using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Responses;
using Scribe.Core.Cleanup;

#pragma warning disable OPENAI001

namespace Scribe.Core.Tests;

/// <summary>
/// Pins the package side of the store=false contract: with no override, the pinned OpenAI,
/// Microsoft.Extensions.AI and Agent Framework packages do not send store=false on their own. That
/// is what lets the cleanup service's store=false wire tests measure Scribe's control rather than a
/// package default, and it flags any dependency bump that changes the default. Nothing leaves the
/// machine: the transport is a fake.
/// </summary>
public sealed class StoredOutputWireContractTests
{
    private const string ResponsesReply = """
        {
          "id": "resp_1",
          "object": "response",
          "created_at": 1767225600,
          "status": "completed",
          "error": null,
          "incomplete_details": null,
          "instructions": null,
          "max_output_tokens": null,
          "model": "cleanup-deployment",
          "output": [
            {
              "type": "message",
              "id": "msg_1",
              "status": "completed",
              "role": "assistant",
              "content": [ { "type": "output_text", "text": "Hello world.", "annotations": [] } ]
            }
          ],
          "parallel_tool_calls": true,
          "previous_response_id": null,
          "reasoning": { "effort": null, "summary": null },
          "store": false,
          "temperature": 1.0,
          "text": { "format": { "type": "text" } },
          "tool_choice": "auto",
          "tools": [],
          "top_p": 1.0,
          "truncation": "disabled",
          "usage": {
            "input_tokens": 1,
            "input_tokens_details": { "cached_tokens": 0 },
            "output_tokens": 1,
            "output_tokens_details": { "reasoning_tokens": 0 },
            "total_tokens": 2
          },
          "metadata": {}
        }
        """;

    [Fact]
    public async Task Responses_agent_without_the_override_does_not_send_store_false()
    {
        // If this starts failing after a package bump, a dependency changed its storage default.
        // The override still has to stay in place, because Azure's own Responses default is
        // store=true and a package default can change back just as quietly.
        var transport = new CapturingHandler(ResponsesReply);
        using var http = new HttpClient(transport);
        var agent = CreateClient(http).GetResponsesClient().AsAIAgent(
            model: "cleanup-deployment",
            instructions: "Punctuate the text.",
            name: "ScribeCleanup");

        var result = await agent.RunAsync(
            "hello world",
            options: new ChatClientAgentRunOptions(new ChatOptions { MaxOutputTokens = 64 }));

        Assert.Equal("Hello world.", result.Text);
        Assert.EndsWith("/responses", transport.RequestUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.NotEqual(JsonValueKind.False, StoreProperty(transport));
    }

    private static OpenAIClient CreateClient(HttpClient http) =>
        new(
            new ApiKeyCredential("not-a-real-key"),
            new OpenAIClientOptions
            {
                Endpoint = AzureOpenAIResponsesClientFactory.GetV1Endpoint(new Uri("https://example.invalid/")),
                Transport = new HttpClientPipelineTransport(http),
                RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
            });

    private static JsonValueKind StoreProperty(CapturingHandler transport)
    {
        Assert.NotNull(transport.RequestBody);
        using var body = JsonDocument.Parse(transport.RequestBody!);
        return body.RootElement.TryGetProperty("store", out var store) ? store.ValueKind : JsonValueKind.Undefined;
    }

    private sealed class CapturingHandler(string reply) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(reply, Encoding.UTF8, "application/json"),
            };
        }
    }
}