using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// Regression coverage for the shipped AI vocabulary libraries. These exist because a dictated
/// "GPT five six Terra" came through raw: the spoken decimal forms models are named with were not
/// in any built-in library, and no prompt wording reliably recovers a version number the model has
/// never seen. Asserting on the end-to-end <see cref="TextPostProcessor"/> (not just CSV contents)
/// keeps longest-match ordering and word-boundary behavior honest too.
/// </summary>
public sealed class AiVocabularyLibraryTests
{
    private static TextPostProcessor CreateWith(params string[] libraryIds)
    {
        var db = ScribeDatabase.CreateInMemory();
        var repo = new DictionaryRepository(db);
        var entries = BuiltInDictionaryLibraries.All
            .Where(l => libraryIds.Contains(l.Id, StringComparer.OrdinalIgnoreCase))
            .SelectMany(l => l.Entries)
            .ToArray();

        Assert.NotEmpty(entries);
        return new TextPostProcessor(
            repo, NullLogger<TextPostProcessor>.Instance, snippets: null,
            libraries: new FixedLibraries(entries));
    }

    private sealed class FixedLibraries(IReadOnlyList<DictionaryEntry> entries) : IDictionaryLibraryService
    {
        public IReadOnlyList<DictionaryLibrary> GetLibraries() => [];
        public IReadOnlyList<DictionaryEntry> GetEnabledLibraryEntries() => entries;
        public DictionaryLibrary Import(string csv, string? suggestedName) => throw new NotSupportedException();
        public void Remove(string id) => throw new NotSupportedException();
    }

    [Theory]
    // The originally reported failure, in both the "point" and bare decimal spoken forms.
    [InlineData("running a test for gpt five six terra", "GPT-5.6-Terra")]
    [InlineData("running a test for gpt five point six terra", "GPT-5.6-Terra")]
    [InlineData("switching to gpt five point six sol", "GPT-5.6-Sol")]
    [InlineData("switching to gpt five six luna", "GPT-5.6-Luna")]
    // Longest match must win: "gpt five point four mini" is not "GPT-5.4" followed by "mini".
    [InlineData("we benchmarked gpt five point four mini", "GPT-5.4-mini")]
    [InlineData("we benchmarked gpt four point one", "GPT-4.1")]
    // Competing vendors, including the "cloud" mishear of Claude and "when" for Qwen.
    [InlineData("graded by claude opus four point eight", "Claude Opus 4.8")]
    [InlineData("graded by cloud opus four point eight", "Claude Opus 4.8")]
    [InlineData("ran it on when three fourteen b", "Qwen3-14B")]
    [InlineData("ran it on llama three point one eight b", "Llama 3.1-8B")]
    [InlineData("compare against gemini three point one pro", "Gemini 3.1 Pro")]
    public void Model_names_library_normalizes_spoken_version_numbers(string spoken, string expected)
    {
        var processor = CreateWith("ai-model-names");

        Assert.Contains(expected, processor.Process(spoken), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("we used r a g with h n s w", "RAG")]
    [InlineData("we used r a g with h n s w", "HNSW")]
    [InlineData("the fine tuning run used lora", "fine-tuning")]
    [InlineData("the fine tuning run used lora", "LoRA")]
    [InlineData("a chain of thought prompt", "chain-of-thought")]
    [InlineData("quantized to int8 with gguf", "INT8")]
    [InlineData("quantized to int8 with gguf", "GGUF")]
    [InlineData("scored by llm as judge", "LLM-as-judge")]
    public void Terminology_library_normalizes_common_ai_vocabulary(string spoken, string expected)
    {
        var processor = CreateWith("ai-terminology");

        Assert.Contains(expected, processor.Process(spoken), StringComparison.Ordinal);
    }

    [Fact]
    public void Model_names_library_leaves_ordinary_speech_alone()
    {
        // The libraries lean on multi-word patterns precisely so bare English survives. A bare
        // "terra", "sol" or "luna" must never be rewritten, or the feature costs more than it gives.
        var processor = CreateWith("ai-model-names", "ai-terminology");

        const string sentence = "the sol was warm and the terra firma held under a luna moth";
        Assert.Equal(sentence, processor.Process(sentence));
    }
}
