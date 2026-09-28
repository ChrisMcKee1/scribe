using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Scribe.Core.Cleanup;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Xunit;
using Xunit.Abstractions;

namespace Scribe.Core.Tests;

/// <summary>
/// The admitted system prompt is built once for the dictations that share a glossary and a writing style, not for each of
/// them: every AI-cleaned dictation used to build the whole prompt, with a glossary of up to 24,000 characters, about
/// 66 KB, only to look up the agent that already carried it. The prompt text itself is exactly the old concatenation's.
/// </summary>
public sealed class TextCleanupServicePromptMemoryTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    private const string Dictated = "hello there";

    [Fact]
    public void The_system_prompt_and_the_probe_prompt_are_the_ones_the_old_concatenation_built()
    {
        var checkedPrompts = 0;
        foreach (var options in Matrix())
        {
            Assert.Equal(OldBuildSystemPrompt(options), TextCleanupService.BuildSystemPrompt(options));
            Assert.Equal(OldBuildSystemPrompt(options with { Glossary = null }), TextCleanupService.BuildProbeSystemPrompt(options));
            checkedPrompts++;
        }

        Assert.True(checkedPrompts > 1_000);
    }

    [Fact]
    public async Task Dictations_that_share_a_glossary_and_a_writing_style_share_one_prompt_and_one_agent()
    {
        await using var harness = new CleanupHarness();
        var provider = new RecordingProvider();
        var svc = harness.Service;
        svc.ProviderFactoryForTesting = provider.Connect;
        svc.Configure(Azure());
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        var vocabulary = Vocabulary("contoso", "fabrikam");
        Assert.Equal(CleanupOutcome.Cleaned, (await svc.Admit(vocabulary).CleanAsync(Dictated).WaitAsync(Bound)).Outcome);
        var prompt = svc.AdmittedPromptForTesting;
        var built = provider.Built.Count;

        await svc.Admit(vocabulary).CleanAsync(Dictated).WaitAsync(Bound);
        // A new vocabulary whose glossary reads the same, as a republished generation's does.
        await svc.Admit(Vocabulary("contoso", "fabrikam")).CleanAsync(Dictated).WaitAsync(Bound);

        Assert.NotNull(prompt);
        Assert.Same(prompt, svc.AdmittedPromptForTesting);
        Assert.Equal(built, provider.Built.Count);
        var glossary = vocabulary.GlossaryFor(CleanupPrompt.MaxGlossaryTermsCloud);
        Assert.Contains("Fabrikam", glossary, StringComparison.Ordinal);
        Assert.Equal(OldBuildSystemPrompt(Azure() with { Glossary = glossary }), prompt);
        Assert.Equal(prompt, provider.Built[^1]);
    }

    [Fact]
    public async Task A_writing_style_equal_in_value_shares_the_prompt_and_a_different_style_or_glossary_gets_its_own()
    {
        await using var harness = new CleanupHarness();
        var provider = new RecordingProvider();
        var svc = harness.Service;
        svc.ProviderFactoryForTesting = provider.Connect;
        svc.Configure(Azure());
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var vocabulary = Vocabulary("contoso");
        const string formal = "Formal and complete.";

        await svc.Admit(vocabulary).CleanAsync(Dictated, writingStyleOverride: formal).WaitAsync(Bound);
        var formalPrompt = svc.AdmittedPromptForTesting;
        await svc.Admit(vocabulary).CleanAsync(Dictated, writingStyleOverride: new string(formal.AsSpan())).WaitAsync(Bound);
        Assert.Same(formalPrompt, svc.AdmittedPromptForTesting);

        await svc.Admit(vocabulary).CleanAsync(Dictated, writingStyleOverride: "Casual.").WaitAsync(Bound);
        var casualPrompt = svc.AdmittedPromptForTesting;
        Assert.NotSame(formalPrompt, casualPrompt);
        var glossary = vocabulary.GlossaryFor(CleanupPrompt.MaxGlossaryTermsCloud);
        Assert.Equal(OldBuildSystemPrompt(Azure() with { WritingStyle = "Casual.", Glossary = glossary }), casualPrompt);
        Assert.Equal(casualPrompt, provider.Built[^1]);

        var other = Vocabulary("northwind");
        await svc.Admit(other).CleanAsync(Dictated, writingStyleOverride: "Casual.").WaitAsync(Bound);
        Assert.Equal(
            OldBuildSystemPrompt(Azure() with { WritingStyle = "Casual.", Glossary = other.GlossaryFor(CleanupPrompt.MaxGlossaryTermsCloud) }),
            svc.AdmittedPromptForTesting);
        Assert.Equal(svc.AdmittedPromptForTesting, provider.Built[^1]);

        // Back to the first style and glossary: the agent built for them is still the one used, as before the memo.
        var builtBefore = provider.Built.Count;
        await svc.Admit(vocabulary).CleanAsync(Dictated, writingStyleOverride: formal).WaitAsync(Bound);
        Assert.Equal(formalPrompt, svc.AdmittedPromptForTesting);
        Assert.Equal(builtBefore, provider.Built.Count);
    }

    [Fact]
    public void The_remembered_prompt_holds_strings_only_and_never_the_options_with_their_credentials()
    {
        var parts = typeof(TextCleanupService).GetNestedType("PromptParts", BindingFlags.NonPublic);
        Assert.NotNull(parts);
        var partFields = parts.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotEmpty(partFields);
        Assert.All(partFields, field => Assert.True(
            field.FieldType == typeof(string) || field.FieldType == typeof(bool),
            $"PromptParts holds a {field.FieldType.Name} in {field.Name}."));

        var memo = typeof(TextCleanupService)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(field => field.Name.StartsWith("_admittedPrompt", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, memo.Count);
        Assert.All(memo, field => Assert.True(field.FieldType == parts || field.FieldType == typeof(string)));
    }

    private static CleanupOptions Azure() =>
        new(true, CleanupProvider.AzureFoundry, "qwen3-1.7b", "https://example.openai.azure.com/", "gpt-5.4-mini");

    // In the collection that runs alone: no other test runs while it measures.
    [Collection(AllocationMeasurementCollection.Name)]
    public sealed class Allocations(ITestOutputHelper output)
    {
        [Fact]
        public async Task A_dictation_that_shares_the_last_prompt_builds_no_prompt()
        {
            await using var harness = new CleanupHarness();
            var provider = new RecordingProvider();
            var svc = harness.Service;
            svc.ProviderFactoryForTesting = provider.Connect;
            svc.Configure(Azure());
            await harness.WaitForStatusAsync(CleanupStatus.Ready);

            // Every word pack's glossary for a cloud model, at the 24,000-character cap.
            var vocabulary = new CleanupVocabulary(CleanupPromptGlossaryMemoryTests.ShippedVocabulary(), AiVocabularyScope.None);
            var glossary = vocabulary.GlossaryFor(CleanupPrompt.MaxGlossaryTermsCloud);
            const string formal = "Formal.";
            const string casual = "Casual.";
            for (var i = 0; i < 4; i++)
            {
                await Clean(casual);
                await Clean(formal);
            }

            // The last dictation remembered the formal prompt, so none of these builds one.
            var shared = await Allocated(async () =>
            {
                for (var i = 0; i < 10; i++)
                {
                    await Clean(formal);
                }
            });

            // Alternating two styles misses the remembered prompt at every dictation, so each one builds the whole prompt
            // again, as every dictation did before; both agents are cached either way.
            var alternating = await Allocated(async () =>
            {
                for (var i = 0; i < 5; i++)
                {
                    await Clean(casual);
                    await Clean(formal);
                }
            });

            var prompt = TextCleanupService.BuildSystemPrompt(Azure() with { WritingStyle = formal, Glossary = glossary });
            var oldPromptBytes = Allocated(() => OldBuildSystemPrompt(Azure() with { WritingStyle = formal, Glossary = glossary }));
            output.WriteLine(
                $"Prompt of {prompt.Length:N0} characters; the old concatenation allocated {oldPromptBytes:N0} bytes for it. " +
                $"10 dictations sharing the prompt allocated {shared:N0} bytes, 10 alternating styles {alternating:N0} bytes.");

            Assert.True(
                alternating - shared >= 10L * prompt.Length * 2 * 9 / 10,
                $"10 dictations sharing the prompt allocated {shared:N0} bytes and 10 alternating styles {alternating:N0}.");

            Task Clean(string style) => svc.Admit(vocabulary).CleanAsync(Dictated, writingStyleOverride: style).WaitAsync(Bound);
        }

        private static async Task<long> Allocated(Func<Task> run)
        {
            var before = GC.GetTotalAllocatedBytes(precise: true);
            await run();
            return GC.GetTotalAllocatedBytes(precise: true) - before;
        }

        private static long Allocated(Func<string> run)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var result = run();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            GC.KeepAlive(result);
            return allocated;
        }
    }

    private static CleanupVocabulary Vocabulary(params string[] terms) =>
        new([.. terms.Select(term => DictionaryEntry.New(term, char.ToUpperInvariant(term[0]) + term[1..]))], AiVocabularyScope.None);

    private static IEnumerable<CleanupOptions> Matrix()
    {
        string?[] styles = [null, "   ", "  Talk like a pirate.  ", "Formal."];
        string?[] frontier = [null, " custom frontier guardrail "];
        string?[] local = [null, "\tcustom local guardrail\n"];
        string?[] glossaries = [null, "  ", " Preferred vocabulary:\n- Azure ", "Preferred vocabulary:\n" + new string('v', 24_000)];
        (CleanupProvider Provider, string Alias, string? Model)[] targets =
        [
            (CleanupProvider.FoundryLocal, "qwen3-1.7b", null),
            (CleanupProvider.FoundryLocal, "QWEN3-4b", null),
            (CleanupProvider.FoundryLocal, "phi-3.5-mini", null),
            (CleanupProvider.AzureFoundry, "qwen3-1.7b", null),
            (CleanupProvider.OpenAiCompatible, "qwen3-1.7b", "qwen3:4b"),
            (CleanupProvider.OpenAiCompatible, "qwen3-1.7b", "llama3.1:8b"),
            (CleanupProvider.OpenAiCompatible, "qwen3-1.7b", null),
            (CleanupProvider.GitHubCopilot, "qwen3-1.7b", null),
        ];

        foreach (var (provider, alias, model) in targets)
        {
            foreach (var promptStyle in new[] { CleanupPromptStyle.Auto, CleanupPromptStyle.Frontier, CleanupPromptStyle.Local })
            {
                foreach (var style in styles)
                {
                    foreach (var frontierPrompt in frontier)
                    {
                        foreach (var localPrompt in local)
                        {
                            foreach (var glossary in glossaries)
                            {
                                yield return new CleanupOptions(
                                    true, provider, alias, null, null, WritingStyle: style, Glossary: glossary,
                                    CustomModel: model, PromptStyle: promptStyle, FrontierPrompt: frontierPrompt,
                                    LocalPrompt: localPrompt);
                            }
                        }
                    }
                }
            }
        }
    }

    // The prompt as it was built before the parts were remembered: three concatenations.
    private static string OldBuildSystemPrompt(CleanupOptions options)
    {
        var style = CleanupPrompt.ResolveWritingStyle(options.WritingStyle);
        var isLocalPrompt = CleanupPrompt.ResolvePromptStyle(options.PromptStyle, options.Provider) == CleanupPromptStyle.Local;
        var guardrail = isLocalPrompt
            ? CleanupPrompt.ResolveLocalPrompt(options.LocalPrompt)
            : CleanupPrompt.ResolveFrontierPrompt(options.FrontierPrompt);
        var prompt = guardrail + "\n\nWriting style:\n" + style;
        if (!string.IsNullOrWhiteSpace(options.Glossary))
        {
            prompt += "\n\n" + options.Glossary.Trim();
        }

        var qwen3 = options.Provider switch
        {
            CleanupProvider.FoundryLocal => options.FoundryModelAlias.StartsWith("qwen3", StringComparison.OrdinalIgnoreCase),
            CleanupProvider.OpenAiCompatible => options.CustomModel?.StartsWith("qwen3", StringComparison.OrdinalIgnoreCase) == true,
            _ => false,
        };
        if (qwen3)
        {
            prompt += " /no_think";
        }

        return prompt;
    }

    // Stands in for a remote provider: every agent it builds answers every call, and it keeps each agent's instructions.
    private sealed class RecordingProvider
    {
        private readonly ConcurrentQueue<string> _built = new();

        public IReadOnlyList<string> Built => _built.ToArray();

        public Task<Func<string, AIAgent>> Connect(CleanupOptions options, CancellationToken cancellationToken) =>
            Task.FromResult<Func<string, AIAgent>>(instructions =>
            {
                _built.Enqueue(instructions);
                return new ChatClientAgent(new AnsweringClient(), instructions: instructions, name: "ScribeCleanup");
            });
    }

    private sealed class AnsweringClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Hello there.")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
