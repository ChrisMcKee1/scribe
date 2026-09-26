using System.Text.Json;
using System.Text.Json.Nodes;
using GitHub.Copilot;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Scribe.Core.Cleanup;
using Scribe.Core.Libraries;
using Scribe.Core.Vocabulary;
using static Scribe.Core.Tests.Vocabulary.TestVocabularies;

namespace Scribe.Core.Tests.Vocabulary;

/// <summary>
/// <see cref="GitHubCopilotCleanupAgent"/> in place of Agent Framework's <c>GitHubCopilotAgent</c>: the runtime receives
/// what that agent sent, the answer reads the same, and a failure reported by the runtime fails the segment without its
/// text reaching the log. Both agents run over the pinned SDK against a <see cref="FakeCopilotRuntime"/> on loopback.
/// </summary>
public sealed class GitHubCopilotCleanupAgentTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    // Letters and spaces only, so JSON escaping in a request can never hide them.
    private const string LibraryCanary = "Zebraquill";
    private const string LibrarySpoken = "zeb ra quill";
    private const string Dictated = "please ask about the harbour lanterns today";

    [Fact]
    public async Task The_runtime_receives_what_the_agent_framework_agent_sent_and_the_answer_reads_the_same()
    {
        await using var runtime = new FakeCopilotRuntime();
        await using var client = new CopilotClient(new CopilotClientOptions { Connection = RuntimeConnection.ForUri(runtime.Url) });
        await client.StartAsync();
        var instructions = "Clean up the dictation. " + CleanupPrompt.BuildGlossary([Entry(LibrarySpoken, LibraryCanary)]);
        var messages = new[] { new ChatMessage(ChatRole.User, "first line"), new ChatMessage(ChatRole.User, "second line") };

        // The agent Scribe ran before, from Agent Framework 1.20.0, over the configuration the factory builds.
        var reference = client.AsAIAgent(
            GitHubCopilotAgentFactory.BuildSessionConfig(instructions, " cleanup-model "), ownsClient: false, name: "ScribeCleanup");
        var expected = await reference.RunAsync(messages).WaitAsync(Bound);
        var referenceCalls = SessionCalls(runtime);

        var agent = GitHubCopilotAgentFactory.Create(client, instructions, " cleanup-model ", "ScribeCleanup");
        AgentResponse actual;
        using (new CleanupAdmission(CleanupRequestKind.Dictation, AiVocabularyScope.None, null).Enter())
        {
            actual = await agent.RunAsync(messages).WaitAsync(Bound);
        }

        var calls = SessionCalls(runtime).Skip(referenceCalls.Count).ToList();

        // The same calls in the same order, each with the same parameters but for the ids the SDK generates per call.
        Assert.Equal(["session.create", "session.send", "session.destroy"], referenceCalls.Select(call => call.Method));
        Assert.Equal(referenceCalls.Select(call => call.Method), calls.Select(call => call.Method));
        for (var i = 0; i < calls.Count; i++)
        {
            Assert.Equal(WithoutGeneratedIds(referenceCalls[i].Params), WithoutGeneratedIds(calls[i].Params));
        }

        // The configuration went as the factory built it, streamed as that agent streams it, and no tool was offered.
        Assert.Equal("cleanup-model", calls[0].Params.GetProperty("model").GetString());
        Assert.Equal(instructions, calls[0].SystemMessage);
        Assert.True(calls[0].Params.GetProperty("streaming").GetBoolean());
        Assert.False(calls[0].Params.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array);
        Assert.Equal("first line\nsecond line", calls[1].Prompt);

        // The answer reads the same: its text, and its usage down to the additional counts.
        Assert.Equal(expected.Text, actual.Text);
        Assert.False(string.IsNullOrEmpty(actual.Text));
        Assert.Equal(JsonSerializer.Serialize(expected.Usage), JsonSerializer.Serialize(actual.Usage));
        Assert.Equal(12, actual.Usage?.TotalTokenCount);
    }

    [Fact]
    public async Task A_session_error_fails_the_segment_by_its_shape_and_every_session_is_destroyed()
    {
        var permitted = Of(1, new Library("kestrelmoor-private", H1, true, Entry(LibrarySpoken, LibraryCanary)));
        await using var runtime = new FakeCopilotRuntime();
        await using var harness = new VocabularyCleanupHarness(new TestVocabularySource(permitted));
        harness.Service.CopilotRuntimeUrlForTesting = runtime.Url;
        await harness.ConfigureAndWaitAsync(VocabularyHandOffTests.Copilot());

        // What a runtime reports can name an account or a resource, so it never reaches the log.
        const string ProviderText = "quota exhausted for harbourlantern account";
        runtime.SessionError = ProviderText;
        var generation = new VocabularyGeneration(permitted.Generation, [], permitted, Scribe.Core.PostProcessing.CompiledDictionaryRules.Empty);
        var result = await harness.Service.Admit(generation.Cleanup).CleanAsync(Dictated).WaitAsync(Bound);

        Assert.Equal(CleanupOutcome.Failed, result.Outcome);
        Assert.Equal(Dictated, result.Text);
        Assert.Equal(runtime.Creates.Count, runtime.Requests.Count(request => request.Method == "session.destroy"));

        // Surfaced as the provider failure it is, by its shape (the exception's type), never as an empty answer and never
        // with the runtime's words.
        Assert.Contains(
            harness.Log.Entries,
            entry => entry.Message.StartsWith("AI cleanup failed for a segment; using raw text.", StringComparison.Ordinal) &&
                     entry.Message.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
        Assert.DoesNotContain(ProviderText, harness.Log.AllText, StringComparison.OrdinalIgnoreCase);
    }

    private static List<CopilotRuntimeRequest> SessionCalls(FakeCopilotRuntime runtime) =>
        [.. runtime.Requests.Where(request => request.Method.StartsWith("session.", StringComparison.Ordinal))];

    // The session id and the W3C trace context are generated per call; everything else must match.
    private static string WithoutGeneratedIds(JsonElement parameters)
    {
        var node = JsonNode.Parse(parameters.GetRawText())!.AsObject();
        foreach (var generated in new[] { "sessionId", "traceparent", "tracestate" })
        {
            node.Remove(generated);
        }

        return node.ToJsonString();
    }
}
