using Scribe.Core.Models;

namespace Scribe.Core.PostProcessing;

/// <summary>
/// Seed dictionary entries installed on first run (when the user dictionary is empty).
/// <para>
/// Deliberately small. A dictionary entry is a blunt always-replace that overrides the speech
/// model's own context-sensitive casing, so the bar for seeding one is that <b>the lowercase form is
/// never correct English</b>. "api" is always wrong, so seeding it is safe. "azure", "foundry" and
/// "parakeet" are ordinary words, so seeding them corrupts a sentence about a colour, a metalworks
/// or a bird, and the user has no idea why.
/// </para>
/// <para>
/// The asymmetry drives this: a missing entry costs one edit in the dictionary editor, and Scribe
/// also offers history mining, AI suggestions and the opt-in libraries covering roughly 1,700 domain
/// terms. A wrong entry silently corrupts every dictation containing the word. Domain vocabulary
/// belongs in a library the user opts into, not in everyone's dictionary.
/// </para>
/// </summary>
public static class DefaultVocabulary
{
    /// <summary>The seed entries, applied via <c>SeedIfEmpty</c>.</summary>
    public static IReadOnlyList<DictionaryEntry> Entries { get; } =
    [
        // Acronyms nobody writes in lowercase.
        DictionaryEntry.New("api", "API"),
        DictionaryEntry.New("sql", "SQL"),
        DictionaryEntry.New("url", "URL"),
        DictionaryEntry.New("gpt", "GPT"),
        DictionaryEntry.New("llm", "LLM"),

        // Proper noun with a fixed internal capital that dictation reliably flattens.
        DictionaryEntry.New("github", "GitHub"),

        // ".NET": only the spoken two-word form, so the "dotnet" CLI name is left intact.
        DictionaryEntry.New("dot net", ".NET"),
    ];

    /// <summary>
    /// Seed entries earlier versions installed that should not have been. Existing users keep
    /// whatever is in their dictionary (<c>SeedIfEmpty</c> only ever runs on an empty one), so
    /// without this they would go on force-capitalizing ordinary words forever while new users got
    /// the corrected seed.
    /// <para>
    /// These are retired by <b>disabling</b> them, and only when the pattern and replacement are
    /// still exactly what Scribe inserted, so an entry the user edited or deliberately kept is never
    /// touched and nothing is deleted. Anyone who wants one back can re-enable it in the editor.
    /// </para>
    /// </summary>
    public static IReadOnlyList<DictionaryEntry> RetiredEntries { get; } =
    [
        // Ordinary English words: a colour, a metalworks, a bird, and a common verb phrase.
        DictionaryEntry.New("azure", "Azure"),
        DictionaryEntry.New("foundry", "Foundry"),
        DictionaryEntry.New("parakeet", "Parakeet"),
        DictionaryEntry.New("re back", "ReBAC"),

        // Scribe's own implementation vocabulary. Users dictate about their work, not our stack.
        DictionaryEntry.New("onnx", "ONNX"),
        DictionaryEntry.New("wasapi", "WASAPI"),
        DictionaryEntry.New("silero", "Silero"),
        DictionaryEntry.New("sherpa onnx", "sherpa-onnx"),

        // Domain and personal terms that belong in an opt-in library, not everyone's dictionary.
        DictionaryEntry.New("nuget", "NuGet"),
        DictionaryEntry.New("kubernetes", "Kubernetes"),
        DictionaryEntry.New("rebac", "ReBAC"),
        DictionaryEntry.New("bambu", "Bambu"),
    ];
}
