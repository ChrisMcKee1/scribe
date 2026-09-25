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
    /// <see cref="Reload()"/> calls keep. The dictionary library program replaces this seam with a vocabulary source.
    /// </summary>
    void Reload(IReadOnlyCollection<string> enabledLibraryIds);
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
