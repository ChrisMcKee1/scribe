namespace Scribe.Core.PostProcessing;

/// <summary>
/// Cleans up decoded transcript text: applies the user dictionary (canonical spellings/casing)
/// and normalizes whitespace. Casing and punctuation are otherwise trusted to the model.
/// </summary>
public interface ITextPostProcessor
{
    /// <summary>Applies dictionary substitutions and whitespace normalization to <paramref name="text"/>.</summary>
    string Process(string text);

    /// <summary>Rebuilds the compiled substitution rules from the dictionary repository.</summary>
    void Reload();
}
