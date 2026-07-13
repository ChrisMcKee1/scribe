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

    /// <summary>Rebuilds the compiled substitution rules from the dictionary repository.</summary>
    void Reload();
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
