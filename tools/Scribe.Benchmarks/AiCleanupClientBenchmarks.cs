using BenchmarkDotNet.Attributes;
using Scribe.Core.Cleanup;
using Scribe.Core.Libraries;
using Scribe.Core.Models;

namespace Scribe.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory("Cleanup")]
public class AiCleanupClientBenchmarks
{
    private const string CachedAgent = "agent";
    private readonly Dictionary<string, string> _stringKeyCache = new(StringComparer.Ordinal);
    private readonly Dictionary<AdmittedAgentKey, string> _referenceKeyCache = new();
    private CleanupOptions _options = CleanupOptions.Disabled;
    private CleanupVocabulary _vocabulary = CleanupVocabulary.None;
    private AdmittedAgentKey _key;
    private int _maxTerms;

    [Params(80, 1360)]
    public int Terms { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _options = new CleanupOptions(
            true,
            CleanupProvider.AzureFoundry,
            CleanupModelCatalog.DefaultAlias,
            "https://cleanup.example.test/",
            "cleanup-model");
        _maxTerms = CleanupPrompt.GlossaryTermBudget(_options.PromptStyle, _options.Provider);
        _vocabulary = new CleanupVocabulary(BuildEntries(Terms), AiVocabularyScope.None);
        _key = new AdmittedAgentKey(_vocabulary, WritingStyle: null, _maxTerms);

        var prompt = TextCleanupService.BuildSystemPrompt(_options with { Glossary = _vocabulary.GlossaryFor(_maxTerms) });
        _stringKeyCache[prompt] = CachedAgent;
        _referenceKeyCache[_key] = CachedAgent;
    }

    [Benchmark(Baseline = true)]
    public string StringPromptKeyCacheHit()
    {
        var glossary = _vocabulary.GlossaryFor(_maxTerms);
        var prompt = TextCleanupService.BuildSystemPrompt(_options with { Glossary = glossary });
        return _stringKeyCache[prompt];
    }

    [Benchmark]
    public string ReferenceKeyCacheHit()
    {
        if (_referenceKeyCache.TryGetValue(_key, out var cached))
        {
            return cached;
        }

        var glossary = _vocabulary.GlossaryFor(_maxTerms);
        var prompt = TextCleanupService.BuildSystemPrompt(_options with { Glossary = glossary });
        _referenceKeyCache[_key] = prompt;
        return prompt;
    }

    private static IReadOnlyList<DictionaryEntry> BuildEntries(int count) =>
        Enumerable.Range(0, count)
            .Select(index => DictionaryEntry.New($"term {index:D4}", $"Term {index:D4}"))
            .ToArray();

    private readonly record struct AdmittedAgentKey(CleanupVocabulary Vocabulary, string? WritingStyle, int MaxTerms);
}
