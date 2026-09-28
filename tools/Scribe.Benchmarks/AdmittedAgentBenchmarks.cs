using System.Reflection;
using BenchmarkDotNet.Attributes;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Cleanup;
using Scribe.Core.Libraries;
using Scribe.Core.Models;

namespace Scribe.Benchmarks;

/// <summary>
/// The cleanup service's per-dictation agent lookup, as it ships: <c>TextCleanupService.AdmittedAgentLocked</c> itself,
/// through a delegate made once in setup, against a real <see cref="ChatClientAgent"/> factory over a client that is never
/// called. It keys agents by the system prompt's text and remembers the last prompt by its parts, so a republished
/// vocabulary whose glossary text is unchanged builds nothing. Replaces a benchmark that compared private copies of two
/// keying schemes, neither of which ships.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("Cleanup")]
public class AdmittedAgentBenchmarks
{
    private const int Generations = 64;
    private Func<Func<string, AIAgent>, CleanupOptions, string?, CleanupVocabulary, AIAgent> _admitted = null!;
    private Func<string, AIAgent> _factory = null!;
    private CleanupOptions _options = CleanupOptions.Disabled;
    private CleanupVocabulary _vocabulary = CleanupVocabulary.None;
    private CleanupVocabulary[] _generations = [];

    [Params(80, 1360)]
    public int Terms { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var service = new TextCleanupService(NullLogger<TextCleanupService>.Instance);
        var method = typeof(TextCleanupService).GetMethod("AdmittedAgentLocked", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("TextCleanupService.AdmittedAgentLocked was not found; update this benchmark.");
        _admitted = method.CreateDelegate<Func<Func<string, AIAgent>, CleanupOptions, string?, CleanupVocabulary, AIAgent>>(service);
        var client = new UnusedChatClient();
        _factory = instructions => new ChatClientAgent(client, instructions: instructions, name: "ScribeCleanup");
        _options = new CleanupOptions(
            true,
            CleanupProvider.AzureFoundry,
            CleanupModelCatalog.DefaultAlias,
            "https://cleanup.example.test/",
            "cleanup-model");
        var budget = CleanupPrompt.GlossaryTermBudget(_options.PromptStyle, _options.Provider);
        var entries = Enumerable.Range(0, Terms)
            .Select(index => DictionaryEntry.New($"term {index:D4}", $"Term {index:D4}"))
            .ToArray();
        _vocabulary = new CleanupVocabulary(entries, AiVocabularyScope.None);
        _generations = [.. Enumerable.Range(0, Generations).Select(_ => new CleanupVocabulary(entries, AiVocabularyScope.None))];

        // Every generation renders its glossary once, whatever the agent cache does; render them all here.
        foreach (var generation in _generations)
        {
            _ = generation.GlossaryFor(budget);
        }

        _ = _admitted(_factory, _options, null, _vocabulary);
    }

    // The next dictation of the same vocabulary generation.
    [Benchmark(Baseline = true)]
    public AIAgent HitSameGeneration() => _admitted(_factory, _options, null, _vocabulary);

    // The first dictation of a new vocabulary generation whose glossary text is unchanged (a republication, or a save
    // that changed nothing the glossary shows), for 64 generations in a row.
    [Benchmark(OperationsPerInvoke = Generations)]
    public AIAgent NewGenerationSameGlossary()
    {
        AIAgent last = null!;
        for (var i = 0; i < Generations; i++)
        {
            last = _admitted(_factory, _options, null, _generations[i]);
        }

        return last;
    }

    private sealed class UnusedChatClient : IChatClient
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
