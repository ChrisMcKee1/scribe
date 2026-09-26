using Scribe.Core.Models;

namespace Scribe.Core.PostProcessing;

/// <summary>
/// Cleans up decoded transcript text: applies the user dictionary (canonical spellings/casing)
/// and normalizes whitespace. Casing and punctuation are otherwise trusted to the model.
/// </summary>
public interface ITextPostProcessor
{
    /// <summary>Applies dictionary substitutions and whitespace normalization to <paramref name="text"/>.</summary>
    string Process(string text);

    /// <summary>
    /// Applies the same processing as <see cref="Process"/> and reports dictionary, library, and
    /// snippet substitutions as spans in the final text. <paramref name="sourceText"/> may contain
    /// the raw recognizer output so glossary terms canonicalized by AI cleanup are still reported.
    /// </summary>
    TextPostProcessingResult ProcessDetailed(string text, string? sourceText = null);

    /// <summary>
    /// Rebuilds the compiled substitution rules from the dictionary repository and the libraries the last
    /// <see cref="Reload(IReadOnlyCollection{string})"/> named, so a caller that changed only the dictionary keeps the
    /// library selection dictation runs on. Before any selection was given, it uses the libraries the stored settings
    /// switch on (<see cref="IDictionaryLibraryService.GetEnabledLibraryEntries()"/>), and never the defaults standing in
    /// for settings that cannot be used.
    /// </summary>
    void Reload();

    /// <summary>
    /// Rebuilds the compiled substitution rules from the dictionary repository and the libraries
    /// <paramref name="enabledLibraryIds"/> names: the enabled ids of the settings dictation runs on, which later
    /// <see cref="Reload()"/> calls keep. Release 0.4.4's library-selection seam, which dictation no longer uses: it
    /// takes its rules from a <see cref="Vocabulary.VocabularyGeneration"/> built from the library vocabulary source.
    /// </summary>
    void Reload(IReadOnlyCollection<string> enabledLibraryIds);

    /// <summary>
    /// Rebuilds only the snippet rules from the snippet repository, for a caller whose dictionary rules come from a
    /// vocabulary generation instead of this post-processor's own. Raises nothing.
    /// </summary>
    void ReloadSnippets();

    /// <summary>
    /// Compiles one vocabulary's dictionary rules: <paramref name="dictionary"/> (the enabled personal dictionary, in
    /// the order dictation reads it) merged over <paramref name="libraryEntries"/> the way <see cref="Reload()"/> merges
    /// them, the dictionary winning a spoken form. Pure apart from logging a rule the matcher rejects; the result never
    /// changes, so a dictation can keep it while newer rules are compiled.
    /// </summary>
    CompiledDictionaryRules Compile(IReadOnlyList<DictionaryEntry> dictionary, IReadOnlyList<DictionaryEntry> libraryEntries);

    /// <summary>
    /// The processing of <see cref="ProcessDetailed(string, string?)"/> with the dictionary pass taken from
    /// <paramref name="rules"/> rather than this post-processor's own rules; snippets are this post-processor's.
    /// </summary>
    TextPostProcessingResult ProcessDetailed(string text, string? sourceText, CompiledDictionaryRules rules);

    /// <summary>
    /// Raised after <see cref="Reload()"/> or <see cref="Reload(IReadOnlyCollection{string})"/> rebuilt the rules,
    /// with a sequence number that grows with each reload: the dictionary may have changed, so whatever compiles
    /// rules of its own from the dictionary (the vocabulary publisher) builds them again. Raised on the reloading
    /// thread, outside any lock, through <see cref="Infrastructure.ResilientEvent.InvokeAll{T}"/>.
    /// </summary>
    event Action<long>? Reloaded;
}

/// <summary>
/// One vocabulary's dictionary rules, compiled once (<see cref="ITextPostProcessor.Compile"/>): the personal dictionary
/// merged over the library entries, each spoken form a compiled matcher, in the order they compete. Immutable, so a
/// dictation keeps the rules it was admitted with however many newer ones are compiled meanwhile.
/// </summary>
public sealed class CompiledDictionaryRules
{
    internal CompiledDictionaryRules(TextPostProcessor.CompiledRule[] rules) => Rules = rules;

    /// <summary>No dictionary rules.</summary>
    public static CompiledDictionaryRules Empty { get; } = new([]);

    /// <summary>How many rules compiled; an entry the matcher rejected is not one.</summary>
    public int Count => Rules.Length;

    internal TextPostProcessor.CompiledRule[] Rules { get; }
}

public sealed record TextPostProcessingResult(
    string Text,
    IReadOnlyList<TextReplacement> Replacements);

public sealed record TextReplacement(
    int Start,
    int Length,
    string Pattern,
    string Replacement,
    TextReplacementKind Kind);

public enum TextReplacementKind
{
    Dictionary,
    Snippet,
}
