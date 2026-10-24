using Scribe.Core.Models;
using Scribe.Core.PostProcessing;
using Xunit;

namespace Scribe.Core.Tests;

public sealed class AiDictionarySuggesterTests
{
    private static HistoryEntry H(string text) =>
        new(0, DateTimeOffset.UtcNow, text, 0, 0);

    // --- ParseSuggestions ------------------------------------------------------------------

    [Fact]
    public void ParseSuggestions_maps_spoken_to_pattern_and_written_to_replacement()
    {
        var json = """[{"spoken":"a p i m","written":"APIM"},{"spoken":"cosmos db","written":"Cosmos DB"}]""";

        var result = AiDictionarySuggester.ParseSuggestions(json, []);

        Assert.Equal(2, result.Count);
        Assert.Equal("a p i m", result[0].Pattern);
        Assert.Equal("APIM", result[0].Replacement);
        Assert.Equal("cosmos db", result[1].Pattern);
        Assert.Equal("Cosmos DB", result[1].Replacement);
    }

    [Fact]
    public void ParseSuggestions_extracts_the_json_array_from_surrounding_noise()
    {
        var response =
            "<think>let me find the terms</think>\nHere are the terms:\n```json\n" +
            """[{"spoken":"kube control","written":"kubectl"}]""" +
            "\n```\nHope that helps!";

        var result = AiDictionarySuggester.ParseSuggestions(response, []);

        Assert.Single(result);
        Assert.Equal("kube control", result[0].Pattern);
        Assert.Equal("kubectl", result[0].Replacement);
    }

    [Fact]
    public void ParseSuggestions_ignores_brackets_in_thinking_or_preamble_and_accepts_object_envelopes_or_single_objects()
    {
        var withBracketInThink =
            "<think>Checking [1] recent history sample [lines 1-10]</think>\n" +
            """{"suggestions":[{"spoken":"a p i m","written":"APIM"}]}""";
        var fromEnvelope = AiDictionarySuggester.ParseSuggestions(withBracketInThink, []);
        Assert.Single(fromEnvelope);
        Assert.Equal("a p i m", fromEnvelope[0].Pattern);
        Assert.Equal("APIM", fromEnvelope[0].Replacement);

        var singleObject = """{"spoken":"post gres","written":"PostgreSQL"}""";
        var fromSingle = AiDictionarySuggester.ParseSuggestions(singleObject, []);
        Assert.Single(fromSingle);
        Assert.Equal("post gres", fromSingle[0].Pattern);
        Assert.Equal("PostgreSQL", fromSingle[0].Replacement);

        var bareCloseThinkAndTruncatedArray =
            """[{"spoken":"wrong","written":"Wrong"}]</think>[{"spoken":"a p i m","written":"APIM"},{"spoken":"cosmos db","written":"Cosmos DB"},{"spoken":"cut""";
        var fromTruncated = AiDictionarySuggester.ParseSuggestions(bareCloseThinkAndTruncatedArray, []);
        Assert.Equal(2, fromTruncated.Count);
        Assert.Equal("APIM", fromTruncated[0].Replacement);
        Assert.Equal("Cosmos DB", fromTruncated[1].Replacement);
    }

    [Fact]
    public void ParseSuggestions_skips_terms_already_in_the_dictionary()
    {
        var json = """[{"spoken":"azure","written":"Azure"},{"spoken":"a k s","written":"AKS"}]""";
        var existing = new[] { DictionaryEntry.New("azure", "Azure") };

        var result = AiDictionarySuggester.ParseSuggestions(json, existing);

        Assert.Single(result);
        Assert.Equal("a k s", result[0].Pattern);
    }

    [Fact]
    public void ParseSuggestions_self_heals_existing_entries_when_written_form_or_casing_differs()
    {
        var json =
            """
            [
              {"spoken":"azure","written":"Azure"},
              {"spoken":"a p i m","written":"APIM"},
              {"spoken":"kube control","written":"kubectl"}
            ]
            """;
        var existing = new[]
        {
            DictionaryEntry.New("azure", "Azure"),
            DictionaryEntry.New("a p i m", "Apim"),
            DictionaryEntry.New("kube control", "KubeControl"),
        };

        var result = AiDictionarySuggester.ParseSuggestions(json, existing);

        Assert.Equal(2, result.Count);
        Assert.Equal("a p i m", result[0].Pattern);
        Assert.Equal("APIM", result[0].Replacement);
        Assert.Equal("kube control", result[1].Pattern);
        Assert.Equal("kubectl", result[1].Replacement);
    }

    [Fact]
    public void ParseSuggestions_dedupes_by_spoken_form_and_skips_no_ops_and_blanks()
    {
        var json =
            """
            [
              {"spoken":"a p i","written":"API"},
              {"spoken":"a p i","written":"API duplicate"},
              {"spoken":"same","written":"same"},
              {"spoken":"","written":"Empty"},
              {"spoken":"good","written":"Good"}
            ]
            """;

        var result = AiDictionarySuggester.ParseSuggestions(json, []);

        Assert.Equal(2, result.Count); // "a p i" (first wins) and "good"; no-op and blank dropped
        Assert.Equal("a p i", result[0].Pattern);
        Assert.Equal("API", result[0].Replacement);
        Assert.Equal("good", result[1].Pattern);
    }

    [Fact]
    public void ParseSuggestions_returns_empty_for_unparseable_responses()
    {
        Assert.Empty(AiDictionarySuggester.ParseSuggestions("no json here", []));
        Assert.Empty(AiDictionarySuggester.ParseSuggestions(null, []));
        Assert.Empty(AiDictionarySuggester.ParseSuggestions("[ this is not valid json ]", []));
    }

    [Fact]
    public void ParseSuggestions_respects_the_max_cap()
    {
        var items = Enumerable.Range(0, 10)
            .Select(i => $$"""{"spoken":"term {{i}}","written":"Term{{i}}"}""");
        var json = "[" + string.Join(",", items) + "]";

        var result = AiDictionarySuggester.ParseSuggestions(json, [], maxSuggestions: 3);

        Assert.Equal(3, result.Count);
    }

    // --- BuildHistorySample ----------------------------------------------------------------

    [Fact]
    public void BuildHistorySample_dedupes_and_joins_recent_dictations()
    {
        var sample = AiDictionarySuggester.BuildHistorySample(
            [H("deploy to azure"), H("deploy to azure"), H("configure the cluster")]);

        Assert.Equal("deploy to azure\nconfigure the cluster", sample);
    }

    [Fact]
    public void BuildHistorySample_returns_empty_for_no_usable_history()
    {
        Assert.Equal(string.Empty, AiDictionarySuggester.BuildHistorySample([]));
        Assert.Equal(string.Empty, AiDictionarySuggester.BuildHistorySample([H("   ")]));
    }

    [Fact]
    public void BuildHistorySample_bounds_the_output_to_max_chars()
    {
        var entries = Enumerable.Range(0, 200).Select(i => H($"dictation number {i} about kubernetes"));

        var sample = AiDictionarySuggester.BuildHistorySample(entries, maxChars: 100);

        Assert.True(sample.Length <= 100);
        Assert.StartsWith("dictation number 0", sample);
    }

    [Fact]
    public void BuildHistorySample_truncates_a_single_oversized_dictation()
    {
        var sample = AiDictionarySuggester.BuildHistorySample([H(new string('x', 500))], maxChars: 100);

        Assert.Equal(100, sample.Length);
    }

    [Fact]
    public void SystemPrompt_instructs_the_model_to_use_surrounding_context_for_misrewritten_phrases()
    {
        Assert.Contains("Read the surrounding sentence, topic, and nearby dictations carefully", AiDictionarySuggester.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("earlier rewrite got wrong in context", AiDictionarySuggester.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("existing vocabulary rules", AiDictionarySuggester.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFullContextRequest_sends_all_word_packs_dictionary_and_history_beyond_6000_chars_in_a_million_token_window()
    {
        var history = Enumerable.Range(0, 2500)
            .Select(i => H($"dictation entry {i}: we deployed the home chart to the cube cluster for project {i}"))
            .ToList();
        var dictionary = Enumerable.Range(0, 100)
            .Select(i => DictionaryEntry.New($"personal spoken {i}", $"PersonalWritten{i}"))
            .ToList();
        var wordPacks = Enumerable.Range(0, 500)
            .Select(i => DictionaryEntry.New($"pack spoken {i}", $"PackWritten{i}"))
            .ToList();

        var request = AiDictionarySuggester.BuildFullContextRequest(
            history,
            dictionary,
            wordPacks,
            contextTokens: 1_048_576);

        Assert.Equal(AiDictionarySuggester.FullContextMaxSuggestions, request.MaxSuggestions);
        Assert.Equal(600, request.VocabularyTermsIncluded);
        Assert.Equal(2500, request.HistoryEntriesIncluded);
        Assert.True(request.UserMessage.Length > 100_000);
        Assert.Contains("PersonalWritten0", request.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("PackWritten499", request.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains($"Return at most {AiDictionarySuggester.FullContextMaxSuggestions} items, fewer is fine.", request.SystemPrompt, StringComparison.Ordinal);
        Assert.True(AiDictionarySuggester.IsDictionarySuggestionPrompt(AiDictionarySuggester.SystemPrompt));
        Assert.True(AiDictionarySuggester.IsDictionarySuggestionPrompt(request.SystemPrompt));
        Assert.False(AiDictionarySuggester.IsDictionarySuggestionPrompt("Return JSON only."));
    }

    [Fact]
    public async Task Learn_from_history_enables_high_reasoning_across_providers_and_falls_back_when_rejected()
    {
        var bodies = new List<System.Text.Json.JsonElement>();
        var http = new ScriptedHttpHandler(async (request, ct) =>
        {
            var rawText = await request.Content!.ReadAsStringAsync(ct);
            var body = System.Text.Json.JsonDocument.Parse(rawText).RootElement.Clone();
            lock (bodies)
            {
                bodies.Add(body);
            }

            var isProbe = rawText.Contains("<transcript>\\nok\\n</transcript>", StringComparison.Ordinal);
            var content = isProbe ? "ok" : """[{"spoken":"a p i m","written":"APIM"}]""";
            var escaped = content.Replace("\"", "\\\"", StringComparison.Ordinal);
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/chat", StringComparison.Ordinal))
            {
                return ScriptedHttpHandler.Json(
                    System.Net.HttpStatusCode.OK,
                    $$"""{"model":"qwen3:4b","created_at":"2026-09-30T00:00:00Z","message":{"role":"assistant","content":"{{escaped}}"},"done":true,"done_reason":"stop"}""");
            }

            return ScriptedHttpHandler.ChatCompletion(escaped);
        });

        await using var harness = new CleanupHarness(http: http);
        var svc = harness.Service;

        // 1. Foundry Local sends reasoning_effort = "high" and omits /no_think for Learn from history
        svc.Configure(CleanupHarness.FoundryOn());
        await harness.WaitForStatusAsync(Scribe.Core.Cleanup.CleanupStatus.Ready);
        var foundryCompletion = await svc.CompleteAsync(
            AiDictionarySuggester.SystemPrompt,
            "we deployed the a p i m gateway",
            svc.Recipient!);
        Assert.Equal(Scribe.Core.Cleanup.CompletionOutcome.Completed, foundryCompletion.Outcome);
        System.Text.Json.JsonElement foundryBody;
        lock (bodies)
        {
            foundryBody = bodies[^1];
        }

        Assert.Equal("high", foundryBody.GetProperty("reasoning_effort").GetString());
        Assert.DoesNotContain("/no_think", foundryBody.GetProperty("messages")[0].GetProperty("content").GetString(), StringComparison.Ordinal);

        // 2. Native Ollama (/api/chat) sends think = "high" for Learn from history
        svc.Configure(CleanupHarness.Custom(Scribe.Core.Cleanup.LocalAiServer.OllamaAddress, "qwen3:4b") with
        {
            LocalContextTokens = 32768,
        });
        await harness.WaitForStatusAsync(Scribe.Core.Cleanup.CleanupStatus.Ready);
        var ollamaCompletion = await svc.CompleteAsync(
            AiDictionarySuggester.SystemPrompt,
            "we deployed the a p i m gateway",
            svc.Recipient!);
        Assert.Equal(Scribe.Core.Cleanup.CompletionOutcome.Completed, ollamaCompletion.Outcome);
        System.Text.Json.JsonElement ollamaBody;
        lock (bodies)
        {
            ollamaBody = bodies[^1];
        }

        Assert.Equal("high", ollamaBody.GetProperty("think").GetString());

        // 3. TensorSharp sends think = true and reasoning_effort = "high" for Learn from history even when dictation thinking is off
        svc.Configure(CleanupHarness.Custom(Scribe.Core.Cleanup.LocalAiServer.TensorSharpAddress, "qwen3:4b"));
        await harness.WaitForStatusAsync(Scribe.Core.Cleanup.CleanupStatus.Ready);
        var tensorSharpCompletion = await svc.CompleteAsync(
            AiDictionarySuggester.SystemPrompt,
            "we deployed the a p i m gateway",
            svc.Recipient!);
        Assert.Equal(Scribe.Core.Cleanup.CompletionOutcome.Completed, tensorSharpCompletion.Outcome);
        System.Text.Json.JsonElement tensorSharpBody;
        lock (bodies)
        {
            tensorSharpBody = bodies[^1];
        }

        Assert.True(tensorSharpBody.GetProperty("think").GetBoolean());
        Assert.Equal("high", tensorSharpBody.GetProperty("reasoning_effort").GetString());

        // 4. GitHub Copilot forwards reasoningEffort = "high" on session.create for Learn from history
        await using var copilotRuntime = new Scribe.Core.Tests.Vocabulary.FakeCopilotRuntime();
        svc.CopilotRuntimeUrlForTesting = copilotRuntime.Url;
        svc.Configure(Scribe.Core.Tests.Vocabulary.VocabularyHandOffTests.Copilot());
        await harness.WaitForStatusAsync(Scribe.Core.Cleanup.CleanupStatus.Ready);
        var copilotCompletion = await svc.CompleteAsync(
            AiDictionarySuggester.SystemPrompt,
            "we deployed the a p i m gateway",
            svc.Recipient!);
        Assert.Equal(Scribe.Core.Cleanup.CompletionOutcome.Completed, copilotCompletion.Outcome);
        Assert.Equal("high", copilotRuntime.Creates[^1].Params.GetProperty("reasoningEffort").GetString());

        // 5. Fallback when a non-reasoning model rejects reasoning_effort = "high" with HTTP 400
        var rejectHighHttp = new ScriptedHttpHandler(async (request, ct) =>
        {
            var body = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
            lock (bodies)
            {
                bodies.Add(body);
            }

            if (body.TryGetProperty("reasoning_effort", out var effort) &&
                string.Equals(effort.GetString(), "high", StringComparison.Ordinal))
            {
                return ScriptedHttpHandler.Json(
                    System.Net.HttpStatusCode.BadRequest,
                    """{"error":{"message":"Unsupported value: 'high' is not supported by this model."}}""");
            }

            var isProbe = body.GetProperty("messages")[1].GetProperty("content").GetString()!
                .Contains("<transcript>\nok\n</transcript>", StringComparison.Ordinal);
            return ScriptedHttpHandler.ChatCompletion(
                isProbe ? "ok" : """[{\"spoken\":\"cosmos db\",\"written\":\"Cosmos DB\"}]""");
        });

        await using var fallbackHarness = new CleanupHarness(http: rejectHighHttp);
        fallbackHarness.Service.Configure(CleanupHarness.Custom("https://api.example.invalid/v1", "non-reasoning-model") with { CustomApiKey = "sk-test" });
        await fallbackHarness.WaitForStatusAsync(Scribe.Core.Cleanup.CleanupStatus.Ready);
        var fallbackCompletion = await fallbackHarness.Service.CompleteAsync(
            AiDictionarySuggester.SystemPrompt,
            "we migrated to cosmos db",
            fallbackHarness.Service.Recipient!);
        Assert.Equal(Scribe.Core.Cleanup.CompletionOutcome.Completed, fallbackCompletion.Outcome);
        var parsed = AiDictionarySuggester.ParseSuggestions(fallbackCompletion.Text, []);
        Assert.Single(parsed);
        Assert.Equal("Cosmos DB", parsed[0].Replacement);
    }
}
