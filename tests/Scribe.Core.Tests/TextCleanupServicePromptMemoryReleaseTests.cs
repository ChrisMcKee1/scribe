using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Scribe.Core.Cleanup;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// The remembered admitted prompt goes with the admitted agents. It holds the glossary, the dictionary terms a dictation
/// was admitted with, and the writing style it ran under, and it used to outlive every invalidation that dropped those
/// agents (turning cleanup off, a configuration that cannot start, a new provider or deployment, a prompt rebuild,
/// disposal), keeping a glossary in a service that had nothing left to send it with. Each of them now empties it, and
/// what it held can be collected.
/// </summary>
public sealed class TextCleanupServicePromptMemoryReleaseTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    private const string Dictated = "hello there";
    private const string Canary = "Zyxquorble";

    public enum Invalidation
    {
        TurnOff,
        CannotStart,
        PromptRebuild,
        OtherProvider,
        OtherDeployment,
        Dispose,
    }

    [Theory]
    [InlineData(Invalidation.TurnOff)]
    [InlineData(Invalidation.CannotStart)]
    [InlineData(Invalidation.PromptRebuild)]
    [InlineData(Invalidation.OtherProvider)]
    [InlineData(Invalidation.OtherDeployment)]
    [InlineData(Invalidation.Dispose)]
    public async Task Every_invalidation_that_drops_the_admitted_agents_releases_the_remembered_prompt_and_its_glossary(
        Invalidation invalidation)
    {
        await using var harness = new CleanupHarness();
        var svc = harness.Service;
        svc.ProviderFactoryForTesting = Connect;
        svc.Configure(Azure());
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        var remembered = await RememberCanaryAsync(svc, "one");
        Assert.True(remembered.Prompt.IsAlive && remembered.Glossary.IsAlive && remembered.WritingStyle.IsAlive);

        await InvalidateAsync(harness, invalidation);

        AssertForgotten(svc);
        AssertCollected(remembered);
    }

    // The reviewer's reproduction: two dictations under different writing styles, then cleanup turned off, then disposal.
    [Fact]
    public async Task After_a_style_change_turning_cleanup_off_and_disposal_leave_no_glossary_behind()
    {
        await using var harness = new CleanupHarness();
        var svc = harness.Service;
        svc.ProviderFactoryForTesting = Connect;
        svc.Configure(Azure());
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        var first = await RememberCanaryAsync(svc, "one");
        var second = await RememberCanaryAsync(svc, "two");
        Assert.Equal(2, svc.AdmittedPromptMemoForTesting.CachedAgents);

        svc.Configure(Azure() with { Enabled = false });
        AssertForgotten(svc);
        await svc.DisposeAsync();
        AssertForgotten(svc);

        AssertCollected(first);
        AssertCollected(second);
    }

    [Fact]
    public async Task The_remembered_prompt_always_names_a_cached_agent_across_the_cache_bound()
    {
        await using var harness = new CleanupHarness();
        var svc = harness.Service;
        svc.ProviderFactoryForTesting = Connect;
        svc.Configure(Azure());
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var vocabulary = Vocabulary();
        var bound = AdmittedAgentBound();

        // Three times past the bound, each dictation under a writing style of its own: every time the cache is full, the
        // clear that makes room empties the memo too, and the prompt remembered next is the one just cached.
        for (var i = 0; i < 3 * bound + 1; i++)
        {
            var style = $"Style number {i}.";
            await svc.Admit(vocabulary).CleanAsync(Dictated, writingStyleOverride: style).WaitAsync(Bound);
            var memo = svc.AdmittedPromptMemoForTesting;
            Assert.True(memo.PromptIsCached);
            Assert.Equal(style, memo.WritingStyle);
            Assert.Equal(i % bound + 1, memo.CachedAgents);
        }
    }

    [Fact]
    public async Task A_factory_that_throws_leaves_no_prompt_remembered_that_no_agent_is_cached_for()
    {
        const string refused = "Refused style.";
        await using var harness = new CleanupHarness();
        var svc = harness.Service;
        svc.ProviderFactoryForTesting = (_, _) => Task.FromResult<Func<string, AIAgent>>(instructions =>
            instructions.Contains(refused, StringComparison.Ordinal)
                ? throw new InvalidOperationException("The factory refused this prompt.")
                : new ChatClientAgent(new AnsweringClient(), instructions: instructions, name: "ScribeCleanup"));
        svc.Configure(Azure());
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var vocabulary = Vocabulary();
        var bound = AdmittedAgentBound();

        // With room in the cache, the prompt remembered before the failure stays remembered, and its agent cached.
        await Clean("Kept style.");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Clean(refused));
        var memo = svc.AdmittedPromptMemoForTesting;
        Assert.Equal("Kept style.", memo.WritingStyle);
        Assert.True(memo.PromptIsCached);
        Assert.Equal(1, memo.CachedAgents);

        // With the cache full, the clear that makes room empties the memo, and the failure after it leaves it empty.
        for (var i = 1; i < bound; i++)
        {
            await Clean($"Style number {i}.");
        }

        Assert.Equal(bound, svc.AdmittedPromptMemoForTesting.CachedAgents);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Clean(refused));
        memo = svc.AdmittedPromptMemoForTesting;
        Assert.Null(memo.Prompt);
        Assert.Null(memo.WritingStyle);
        Assert.Null(memo.Glossary);
        Assert.Equal(0, memo.CachedAgents);

        Task Clean(string style) => svc.Admit(vocabulary).CleanAsync(Dictated, writingStyleOverride: style).WaitAsync(Bound);
    }

    [Fact]
    public void Only_the_clear_that_empties_the_memo_clears_the_admitted_agents_and_only_a_cached_prompt_is_remembered()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Scribe.Core", "Cleanup", "TextCleanupService.cs"));
        var clear = MethodSpan(source, "private void ClearAdmittedAgentsLocked()");
        var admit = MethodSpan(source, "private AIAgent AdmittedAgentLocked(");

        // What changes the cache: the one clear, which empties the memo with it, and the one insert, which the memo follows.
        var changes = Regex.Matches(source, @"_admittedAgents(?:\.(?<member>\w+)|(?<insert>\[))")
            .Where(use => use.Groups["member"].Value is not ("TryGetValue" or "ContainsKey" or "Count" or "Values"))
            .ToList();
        Assert.Equal(2, changes.Count);
        Assert.Single(changes, use => use.Groups["member"].Value == "Clear" && Within(clear, use.Index));
        Assert.Single(changes, use => use.Groups["insert"].Success && Within(admit, use.Index));

        // The memo is written in those two methods and nowhere else.
        var writes = Regex.Matches(source, @"\b_admittedPrompt(?:Parts)?\s*=(?![=>])").ToList();
        Assert.Equal(4, writes.Count);
        Assert.All(writes, write => Assert.True(Within(clear, write.Index) || Within(admit, write.Index)));
        var clearBody = source[clear.Start..clear.End];
        Assert.Contains("_admittedPrompt = null;", clearBody, StringComparison.Ordinal);
        Assert.Contains("_admittedPromptParts = default;", clearBody, StringComparison.Ordinal);
    }

    // Admits a vocabulary of its own whose glossary carries a canary term, cleans one dictation under a writing style of
    // its own, and hands back weak references only, so nothing on the test's stack keeps what the memo holds alive.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference Prompt, WeakReference Glossary, WeakReference WritingStyle)> RememberCanaryAsync(
        TextCleanupService svc, string name)
    {
        var vocabulary = new CleanupVocabulary(
            [DictionaryEntry.New("zix quorble " + name, Canary + name)], AiVocabularyScope.None);
        var style = string.Concat("Formal, for ", Canary, " ", name, ".");
        var result = await svc.Admit(vocabulary).CleanAsync(Dictated, writingStyleOverride: style).WaitAsync(Bound);
        Assert.Equal(CleanupOutcome.Cleaned, result.Outcome);

        var memo = svc.AdmittedPromptMemoForTesting;
        Assert.True(memo.PromptIsCached);
        Assert.NotNull(memo.Prompt);
        Assert.NotNull(memo.Glossary);
        Assert.Contains(Canary + name, memo.Glossary, StringComparison.Ordinal);
        Assert.Contains(memo.Glossary, memo.Prompt, StringComparison.Ordinal);
        Assert.Equal(style, memo.WritingStyle);
        return (new WeakReference(memo.Prompt), new WeakReference(memo.Glossary), new WeakReference(memo.WritingStyle));
    }

    private static async Task InvalidateAsync(CleanupHarness harness, Invalidation invalidation)
    {
        var svc = harness.Service;
        switch (invalidation)
        {
            case Invalidation.TurnOff:
                svc.Configure(Azure() with { Enabled = false });
                Assert.Equal(CleanupStatus.Disabled, svc.Status);
                break;
            case Invalidation.CannotStart:
                svc.Configure(Azure() with { AzureDeployment = null });
                Assert.Equal(CleanupStatus.Unavailable, svc.Status);
                break;
            case Invalidation.PromptRebuild:
                // Only what the prompt says changes, so the default agent is rebuilt in place and cleanup stays Ready.
                var generation = svc.InitGenerationForTesting;
                svc.Configure(Azure() with { WritingStyle = "Plain and short." });
                Assert.Equal(CleanupStatus.Ready, svc.Status);
                Assert.Equal(generation, svc.InitGenerationForTesting);
                break;
            case Invalidation.OtherProvider:
                svc.Configure(CleanupHarness.Custom("http://127.0.0.1:9/v1"));
                await harness.WaitForStatusAsync(CleanupStatus.Ready);
                break;
            case Invalidation.OtherDeployment:
                svc.Configure(Azure() with { AzureDeployment = "gpt-5.4" });
                await harness.WaitForStatusAsync(CleanupStatus.Ready);
                break;
            case Invalidation.Dispose:
                await svc.DisposeAsync();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(invalidation));
        }
    }

    private static void AssertForgotten(TextCleanupService svc)
    {
        var memo = svc.AdmittedPromptMemoForTesting;
        Assert.Null(memo.Prompt);
        Assert.Null(memo.Guardrail);
        Assert.Null(memo.WritingStyle);
        Assert.Null(memo.Glossary);
        Assert.Equal(0, memo.CachedAgents);
    }

    private static void AssertCollected((WeakReference Prompt, WeakReference Glossary, WeakReference WritingStyle) remembered)
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        GC.Collect();
        Assert.False(remembered.Prompt.IsAlive, "The remembered prompt outlived the agents built for it.");
        Assert.False(remembered.Glossary.IsAlive, "The remembered glossary outlived the agents built for it.");
        Assert.False(remembered.WritingStyle.IsAlive, "The remembered writing style outlived the agents built for it.");
    }

    private static int AdmittedAgentBound()
    {
        var field = typeof(TextCleanupService).GetField("MaxAdmittedAgents", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        return Assert.IsType<int>(field.GetValue(null));
    }

    private static CleanupOptions Azure() =>
        new(true, CleanupProvider.AzureFoundry, "qwen3-1.7b", "https://example.openai.azure.com/", "gpt-5.4-mini");

    private static CleanupVocabulary Vocabulary() =>
        new([DictionaryEntry.New("contoso", "Contoso")], AiVocabularyScope.None);

    // Stands in for a remote provider and keeps nothing: every agent it builds answers every call.
    private static Task<Func<string, AIAgent>> Connect(CleanupOptions options, CancellationToken cancellationToken) =>
        Task.FromResult<Func<string, AIAgent>>(static instructions =>
            new ChatClientAgent(new AnsweringClient(), instructions: instructions, name: "ScribeCleanup"));

    private static (int Start, int End) MethodSpan(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{signature} was not found.");
        var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        Assert.True(end > start, $"The end of {signature} was not found.");
        return (start, end);
    }

    private static bool Within((int Start, int End) span, int index) => index > span.Start && index < span.End;

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return root.FullName;
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
