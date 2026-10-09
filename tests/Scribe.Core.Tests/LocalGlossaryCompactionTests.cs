using System.Net;
using System.Text.Json;
using Scribe.Core.Cleanup;
using Scribe.Core.Diagnostics;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;

namespace Scribe.Core.Tests;

public sealed class LocalGlossaryCompactionTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);
    private const string Model = "gemma4:12b";

    [Fact]
    public void Compaction_keeps_exact_spellings_and_every_normalized_alias_after_selection()
    {
        DictionaryEntry[] entries =
        [
            DictionaryEntry.New("get hub", "GitHub"),
            DictionaryEntry.New("a k s", "AKS"),
            DictionaryEntry.New("git hub", "GitHub"),
            DictionaryEntry.New("github", "GitHub"),
            DictionaryEntry.New("g i t hub", "github"),
            DictionaryEntry.New("off", "Off") with { Enabled = false },
            DictionaryEntry.New("signature", "Best,\nFriend"),
            DictionaryEntry.New("quoted \"alias\"\tform", "Quoted`Term"),
            DictionaryEntry.New("a|bb", "Written"),
            DictionaryEntry.New("b", "Written|a"),
        ];
        var lines = CleanupPrompt.GlossaryLines(entries);
        var compact = CleanupPrompt.CompactGlossaryLines(lines);

        Assert.Equal(
            [
                "- GitHub <- \"get hub\", \"git hub\"",
                "- AKS <- \"a k s\"",
                "- github <- \"g i t hub\"",
                "- QuotedTerm <- \"quoted alias form\"",
                "- Written <- \"a|bb\"",
                "- Written|a <- \"b\"",
            ],
            compact.Select(line => line.Text));
        Assert.All(compact, line => Assert.Equal(TokenEstimate.Vocabulary(line.Text) + 1, line.Tokens));
        Assert.True(CleanupPrompt.Tokens(compact) < CleanupPrompt.Tokens(lines));

        var prefix = lines.Take(1).ToList();
        Assert.Same(prefix, CleanupPrompt.CompactGlossaryLines(prefix));
    }

    [Fact]
    public void Lines_without_normalized_parts_are_never_parsed_or_changed()
    {
        GlossaryLineInfo[] lines = [new("key", "- Literal (transcribed as \"not a delimiter\")", 20)];
        Assert.Same(lines, CleanupPrompt.CompactGlossaryLines(lines));
    }

    [Fact]
    public void Every_shipped_spelling_and_normalized_spoken_form_survives_compaction()
    {
        var lines = CleanupPrompt.GlossaryLines(BuiltInDictionaryLibraries.All.SelectMany(pack => pack.Entries));
        var compact = CleanupPrompt.CompactGlossaryLines(lines);
        var rendered = CleanupPrompt.RenderGlossary(compact);
        foreach (var line in lines)
        {
            Assert.Contains($"- {line.Canonical}", rendered, StringComparison.Ordinal);
            if (line.Spoken is { } spoken)
            {
                Assert.Contains($"\"{spoken}\"", rendered, StringComparison.Ordinal);
            }
        }

        Assert.True(compact.Count < lines.Count);
        Assert.True(CleanupPrompt.Tokens(compact) < CleanupPrompt.Tokens(lines));
    }

    [Fact]
    public void The_compact_whole_glossary_saves_repetition_without_changing_any_selected_mapping()
    {
        var entries = Entries(300);
        var vocabulary = new CleanupVocabulary(entries, AiVocabularyScope.None);
        var legacy = vocabulary.GlossaryFor(CleanupVocabularyMode.Mentioned, true, "ship it today", 131072, int.MaxValue)!;
        var compact = vocabulary.GlossaryFor(
            CleanupVocabularyMode.Mentioned, true, "ship it today", 131072, int.MaxValue, compactAliases: true)!;

        Assert.True(compact.Length < legacy.Length * 0.6);
        foreach (var entry in entries)
        {
            Assert.Contains($"\"{entry.Pattern}\"", compact, StringComparison.Ordinal);
            Assert.Contains($"- {entry.Replacement} <- ", compact, StringComparison.Ordinal);
        }

        Assert.Same(compact, vocabulary.GlossaryFor(
            CleanupVocabularyMode.Mentioned, true, "another dictation", 131072, int.MaxValue, compactAliases: true));
        Assert.Equal(legacy, vocabulary.GlossaryFor(CleanupVocabularyMode.Mentioned, true, "ship it today", 131072, int.MaxValue));
        Assert.True(TokenEstimate.Vocabulary(compact) <= TokenEstimate.Vocabulary(legacy));
    }

    [Theory]
    [InlineData(4096)]
    [InlineData(8192)]
    [InlineData(131072)]
    public void Preparation_has_a_bounded_prefix_of_the_dictation_s_compact_vocabulary(int contextTokens)
    {
        var vocabulary = new CleanupVocabulary(Entries(400), AiVocabularyScope.None);
        var budget = ContextBudget.ReadyingVocabularyTokens(contextTokens, "Rewrite the dictation.");
        var readying = vocabulary.GlossaryFor(
            CleanupVocabularyMode.Mentioned, true, null, budget, int.MaxValue, compactAliases: true)!;
        var dictation = vocabulary.GlossaryFor(
            CleanupVocabularyMode.Mentioned, true, "ship it today", budget, int.MaxValue, compactAliases: true)!;

        Assert.True(TokenEstimate.Vocabulary(readying) <= Math.Min(budget, ContextBudget.MaxPreparationVocabularyTokens));
        Assert.StartsWith(readying, dictation, StringComparison.Ordinal);
        Assert.True(TokenEstimate.Vocabulary(dictation) <= budget);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task The_flag_controls_both_Ollama_routes_without_changing_context_retention_or_dictation_vocabulary(
        bool native, bool compact)
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
            http: http, perfFlags: compact ? PerfFlags.Parse(PerfFlags.CompactLocalGlossary) : PerfFlags.None);
        var service = harness.Service;
        harness.LocalServers.State = new LocalServerState(
            LocalServerReach.Reached, [], [new LocalServerLoadedModel(Model, 1) { ContextTokens = 131072 }]);
        var options = CleanupHarness.Custom(LocalAiServer.OllamaAddress, Model) with
        {
            LocalContextTokens = native ? 131072 : null,
            LocalModelKeepAliveMinutes = 0,
            SendWholeVocabulary = true,
            VocabularyMode = CleanupVocabularyMode.Mentioned,
        };
        service.Configure(options);
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        var entries = Entries(300);
        var vocabulary = new CleanupVocabulary(entries, AiVocabularyScope.None);
        service.ForgetLastModelAnswerForTesting();
        var admitted = service.Admit(vocabulary);
        admitted.Prewarm();
        await service.WaitForPrewarmForTesting().WaitAsync(Bound);
        JsonElement readying;
        lock (requests)
        {
            readying = requests[^1];
        }

        var result = await admitted.CleanAsync("um so we ship on friday").WaitAsync(Bound);
        Assert.Equal(CleanupOutcome.Cleaned, result.Outcome);
        JsonElement[] sent;
        lock (requests)
        {
            sent = [.. requests];
        }

        Assert.True(sent.Length >= 3);
        Assert.DoesNotContain(entries[0].Replacement, SystemMessage(sent[0]), StringComparison.Ordinal);
        var cleanedSystem = SystemMessage(sent[^1]);
        var expected = vocabulary.GlossaryFor(
            CleanupVocabularyMode.Mentioned, true, "um so we ship on friday", 100000, int.MaxValue, compactAliases: compact)!;
        Assert.Contains(expected, cleanedSystem, StringComparison.Ordinal);
        var readyingSystem = SystemMessage(readying);
        Assert.StartsWith(readyingSystem, cleanedSystem, StringComparison.Ordinal);
        if (compact)
        {
            var instructions = TextCleanupService.BuildProbeSystemPrompt(options);
            var glossary = readyingSystem[(instructions.Length + 2)..];
            Assert.True(TokenEstimate.Vocabulary(glossary) <= ContextBudget.MaxPreparationVocabularyTokens);
            Assert.True(readyingSystem.Length < cleanedSystem.Length);
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

    [Theory]
    [InlineData("https://ai.example.invalid/v1")]
    [InlineData("http://localhost:8080/v1")]
    public async Task Other_servers_keep_the_original_glossary_with_the_experiment_enabled(string endpoint)
    {
        var requests = new List<JsonElement>();
        var http = new ScriptedHttpHandler(async (request, ct) =>
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
            lock (requests)
            {
                requests.Add(body);
            }

            return ScriptedHttpHandler.ChatCompletion("So we ship on Friday.");
        });
        await using var harness = new CleanupHarness(http: http, perfFlags: PerfFlags.Parse(PerfFlags.CompactLocalGlossary));
        var options = CleanupHarness.Custom(endpoint, Model) with
        {
            SendWholeVocabulary = true,
            VocabularyMode = CleanupVocabularyMode.All,
        };
        harness.Service.Configure(options);
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var entries = Entries(20);
        var vocabulary = new CleanupVocabulary(entries, AiVocabularyScope.None);
        await harness.Service.Admit(vocabulary).CleanAsync("um so we ship on friday").WaitAsync(Bound);

        JsonElement sent;
        lock (requests)
        {
            sent = requests[^1];
        }

        Assert.Contains(vocabulary.GlossaryFor(CleanupPrompt.GlossaryTermBudget(
            options.PromptStyle, options.Provider, endpoint))!, SystemMessage(sent), StringComparison.Ordinal);
    }

    private static List<DictionaryEntry> Entries(int count)
    {
        var entries = new List<DictionaryEntry>();
        for (var alias = 0; alias < 4; alias++)
        {
            for (var term = 0; term < count; term++)
            {
                entries.Add(DictionaryEntry.New($"heard{term:D4} variant{alias}", $"CanonicalTerm{term:D4}"));
            }
        }

        return entries;
    }

    private static string SystemMessage(JsonElement body) =>
        body.GetProperty("messages").EnumerateArray()
            .First(message => message.GetProperty("role").GetString() == "system")
            .GetProperty("content").GetString()!;
}
