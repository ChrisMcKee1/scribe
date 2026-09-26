using System.Collections.Concurrent;
using Scribe.Core.Libraries;
using Scribe.Core.Models;

namespace Scribe.Core.Cleanup;

/// <summary>
/// The vocabulary one dictation's cleanup requests may carry: the glossary input of the vocabulary generation the
/// dictation was admitted with (the personal dictionary composed with the library entries AI cleanup may have,
/// <see cref="CleanupPrompt.ComposeVocabulary"/>) and the library scope those library entries were cut by. Every
/// request that carries it is handed over only while the published scope still covers <see cref="Scope"/>
/// (<see cref="ILibraryVocabularySource.TryHandOff"/>), so a revocation after admission stops every request not yet
/// handed over.
/// </summary>
/// <remarks>
/// Built only by a vocabulary generation (the constructor is internal), so the glossary a request carries and the scope
/// it is judged by always come from the same committed snapshot, and no caller can pair library terms with a scope
/// that does not name their libraries.
/// </remarks>
public sealed class CleanupVocabulary
{
    // One rendering per term budget (Local and cloud), so dictations that share a generation share its glossary text.
    private readonly ConcurrentDictionary<int, string?> _glossaries = new();

    internal CleanupVocabulary(IReadOnlyList<DictionaryEntry> glossaryEntries, AiVocabularyScope scope)
    {
        ArgumentNullException.ThrowIfNull(glossaryEntries);
        ArgumentNullException.ThrowIfNull(scope);
        GlossaryEntries = [.. glossaryEntries];
        Scope = scope;
    }

    /// <summary>No vocabulary at all: no glossary, and no library term to permit.</summary>
    public static CleanupVocabulary None { get; } = new([], AiVocabularyScope.None);

    /// <summary>The glossary input, in the order the glossary fills its budget: the dictionary first, then libraries.</summary>
    public IReadOnlyList<DictionaryEntry> GlossaryEntries { get; }

    /// <summary>The libraries whose entries <see cref="GlossaryEntries"/> holds, each with the content it was permitted for.</summary>
    public AiVocabularyScope Scope { get; }

    /// <summary>
    /// The glossary block for <paramref name="maxTerms"/> (<see cref="CleanupPrompt.GlossaryTermBudget"/>), rendered by
    /// <see cref="CleanupPrompt.BuildGlossary"/>; null when there is nothing to add.
    /// </summary>
    public string? GlossaryFor(int maxTerms) => _glossaries.GetOrAdd(maxTerms, budget =>
    {
        var glossary = CleanupPrompt.BuildGlossary(GlossaryEntries, budget);
        return string.IsNullOrEmpty(glossary) ? null : glossary;
    });
}
