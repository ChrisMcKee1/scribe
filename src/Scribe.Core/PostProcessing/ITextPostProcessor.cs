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
    /// Applies ONLY the dictionary and library substitutions to a block of text the user selected in
    /// another application, and reports what changed.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="ProcessDetailed"/>. That method is built for a fresh transcript and
    /// does two things that are correct there and destructive here. It normalizes whitespace, which
    /// silently flattens the indentation and column alignment of a document the user is looking at,
    /// and it expands snippets across the whole text first, so a selection that happens to contain a
    /// snippet trigger would have the user's own template pasted into someone else's document. This
    /// entry point skips both, and leaves code spans, URLs and file paths untouched.
    /// </remarks>
    SelectionVocabularyResult ProcessSelection(string text);

    /// <summary>Rebuilds the compiled substitution rules from the dictionary repository.</summary>
    void Reload();
}

/// <summary>
/// Result of a selection-scoped vocabulary pass.
/// </summary>
/// <param name="Text">The text with dictionary substitutions applied.</param>
/// <param name="Replacements">What changed, for the preview diff.</param>
public sealed record SelectionVocabularyResult(
    string Text,
    IReadOnlyList<TextReplacement> Replacements)
{
    /// <summary>True when the pass changed nothing, so the palette can hide the row entirely.</summary>
    public bool IsNoOp => Replacements.Count == 0;
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
