using System.Net;
using System.Text.Json;
using Scribe.Core.Cleanup;
using Scribe.Core.Diagnostics;
using Scribe.Core.Libraries;
using Scribe.Core.Models;

namespace Scribe.Core.Tests;

public sealed class BoundedLocalPreparationTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);
    private const string Model = "gemma4:12b";

    [Theory]
    [InlineData(4096)]
    [InlineData(8192)]
    [InlineData(131072)]
    public void Preparation_is_bounded_without_changing_the_actual_glossary_or_its_format(int context)
    {
        var vocabulary = new CleanupVocabulary(Entries(), AiVocabularyScope.None);
        var budget = ContextBudget.ReadyingVocabularyTokens(context, "Rewrite the dictation.");
        var legacy = vocabulary.GlossaryFor(CleanupVocabularyMode.Mentioned, true, null, budget, int.MaxValue)!;
        var prepared = vocabulary.GlossaryFor(
            CleanupVocabularyMode.Mentioned, true, null, budget, int.MaxValue, compactAliases: false)!;
        var actual = vocabulary.GlossaryFor(
            CleanupVocabularyMode.Mentioned, true, "ship it today", budget, int.MaxValue, compactAliases: false)!;
        var originalActual = vocabulary.GlossaryFor(
            CleanupVocabularyMode.Mentioned, true, "ship it today", budget, int.MaxValue)!;

        Assert.True(TokenEstimate.Vocabulary(prepared) <= Math.Min(budget, ContextBudget.MaxPreparationVocabularyTokens));
        Assert.StartsWith(prepared, legacy, StringComparison.Ordinal);
        Assert.StartsWith(prepared, actual, StringComparison.Ordinal);
        Assert.Equal(originalActual, actual);
        Assert.DoesNotContain(" <- ", actual, StringComparison.Ordinal);
        Assert.Equal(legacy, vocabulary.GlossaryFor(
            CleanupVocabularyMode.Mentioned, true, null, budget, int.MaxValue, compactAliases: false, boundedPreparation: false));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Both_Ollama_routes_use_the_bounded_original_prefix_by_default_and_the_flag_restores_old_preparation(
        bool native, bool unbounded)
    {
        var requests = new List<JsonElement>();
        var http = new ScriptedHttpHandler(async (request, ct) =>
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
            lock (requests)
            {
                requests.Add(body);
            }
            return native
                ? ScriptedHttpHandler.Json(HttpStatusCode.OK,
                    """{"model":"gemma4:12b","message":{"role":"assistant","content":"So we ship on Friday."},"done":true,"done_reason":"stop","eval_count":7}""")
                : ScriptedHttpHandler.ChatCompletion("So we ship on Friday.");
        });
        await using var harness = new CleanupHarness(
            http: http, perfFlags: unbounded ? PerfFlags.Parse(PerfFlags.UnboundedLocalPreparation) : PerfFlags.None);
        var options = CleanupHarness.Custom(LocalAiServer.OllamaAddress, Model) with
        {
            LocalContextTokens = native ? 131072 : null,
            LocalModelKeepAliveMinutes = 0,
            SendWholeVocabulary = true,
            VocabularyMode = CleanupVocabularyMode.Mentioned,
        };
        harness.LocalServers.State = new LocalServerState(
            LocalServerReach.Reached, [], [new LocalServerLoadedModel(Model, 1) { ContextTokens = 131072 }]);
        harness.Service.Configure(options);
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var vocabulary = new CleanupVocabulary(Entries(), AiVocabularyScope.None);
        harness.Service.ForgetLastModelAnswerForTesting();
        var admitted = harness.Service.Admit(vocabulary);
        admitted.Prewarm();
        await harness.Service.WaitForPrewarmForTesting().WaitAsync(Bound);
        JsonElement prepared;
        lock (requests)
        {
            prepared = requests[^1];
        }
        Assert.Equal(CleanupOutcome.Cleaned,
            (await admitted.CleanAsync("um so we ship on friday").WaitAsync(Bound)).Outcome);
        JsonElement actual;
        JsonElement[] sent;
        lock (requests)
        {
            actual = requests[^1];
            sent = [.. requests];
        }

        var instructions = TextCleanupService.BuildProbeSystemPrompt(options);
        var preparedGlossary = SystemMessage(prepared)[(instructions.Length + 2)..];
        var expected = CleanupPrompt.RenderGlossary(vocabulary.Lines);
        Assert.Equal(expected, SystemMessage(actual)[(instructions.Length + 2)..]);
        Assert.DoesNotContain(" <- ", SystemMessage(actual), StringComparison.Ordinal);
        Assert.StartsWith(SystemMessage(prepared), SystemMessage(actual), StringComparison.Ordinal);
        if (unbounded)
        {
            Assert.Equal(expected, preparedGlossary);
        }
        else
        {
            Assert.True(TokenEstimate.Vocabulary(preparedGlossary) <= ContextBudget.MaxPreparationVocabularyTokens);
            Assert.True(preparedGlossary.Length < expected.Length);
        }
        Assert.All(sent, body =>
        {
            Assert.Equal("-1m", body.GetProperty("keep_alive").GetString());
            if (native)
            {
                Assert.Equal(131072, body.GetProperty("options").GetProperty("num_ctx").GetInt32());
            }
        });
        Assert.Empty(harness.LocalServers.Unloads);
    }

    [Fact]
    public void With_no_whole_vocabulary_preparation_carries_none_and_tiny_vocabularies_are_unchanged()
    {
        var vocabulary = new CleanupVocabulary([DictionaryEntry.New("cube control", "kubectl")], AiVocabularyScope.None);
        Assert.Null(vocabulary.GlossaryFor(
            CleanupVocabularyMode.Mentioned, false, null, 131072, 80, compactAliases: false));
        Assert.Equal(vocabulary.GlossaryFor(CleanupVocabularyMode.Mentioned, true, null, 131072, int.MaxValue),
            vocabulary.GlossaryFor(CleanupVocabularyMode.Mentioned, true, null, 131072, int.MaxValue, compactAliases: false));
    }

    private static List<DictionaryEntry> Entries() => Enumerable.Range(0, 800)
        .Select(index => DictionaryEntry.New($"heard term {index:D4}", $"CanonicalTerm{index:D4}")).ToList();

    private static string SystemMessage(JsonElement body) => body.GetProperty("messages").EnumerateArray()
        .First(message => message.GetProperty("role").GetString() == "system").GetProperty("content").GetString()!;
}
