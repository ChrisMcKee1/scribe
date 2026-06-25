using Scribe.Core.Models;

namespace Scribe.Core.PostProcessing;

/// <summary>
/// Seed dictionary entries installed on first run (when the user dictionary is empty). These
/// canonicalize the casing of common technical terms this app's users dictate. Users can edit,
/// disable, or remove any of them from the settings dictionary editor.
/// </summary>
public static class DefaultVocabulary
{
    /// <summary>The seed entries, applied via <c>SeedIfEmpty</c>.</summary>
    public static IReadOnlyList<DictionaryEntry> Entries { get; } =
    [
        // Cloud / platform
        DictionaryEntry.New("azure", "Azure"),
        DictionaryEntry.New("foundry", "Foundry"),
        DictionaryEntry.New("github", "GitHub"),
        DictionaryEntry.New("nuget", "NuGet"),
        DictionaryEntry.New("kubernetes", "Kubernetes"),

        // Acronyms
        DictionaryEntry.New("api", "API"),
        DictionaryEntry.New("sql", "SQL"),
        DictionaryEntry.New("gpt", "GPT"),
        DictionaryEntry.New("llm", "LLM"),
        DictionaryEntry.New("onnx", "ONNX"),
        DictionaryEntry.New("wasapi", "WASAPI"),
        DictionaryEntry.New("rebac", "ReBAC"),
        DictionaryEntry.New("re back", "ReBAC"),

        // Product / model names
        DictionaryEntry.New("bambu", "Bambu"),
        DictionaryEntry.New("parakeet", "Parakeet"),
        DictionaryEntry.New("silero", "Silero"),
        DictionaryEntry.New("sherpa onnx", "sherpa-onnx"),

        // ".NET" — only the spoken two-word form, so the "dotnet" CLI name is left intact.
        DictionaryEntry.New("dot net", ".NET"),
    ];
}
