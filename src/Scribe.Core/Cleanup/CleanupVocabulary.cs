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
    public string? GlossaryFor(int maxTerms) => _glossaries.GetOrAdd(
        maxTerms,
        static (budget, entries) =>
        {
            var glossary = CleanupPrompt.BuildGlossary(entries, budget);
            return string.IsNullOrEmpty(glossary) ? null : glossary;
        },
        GlossaryEntries);

    /// <summary>
    /// The glossary block a request for <paramref name="dictation"/> carries under <paramref name="mode"/>: all of it
    /// (<see cref="GlossaryFor(int)"/>), only the entries the dictation appears to mention
    /// (<see cref="VocabularyMentions"/>, within the same budget), or none. Null when there is nothing to add, which is
    /// always the case for <see cref="CleanupVocabularyMode.Mentioned"/> without a dictation (a readying request).
    /// </summary>
    public string? GlossaryFor(int maxTerms, CleanupVocabularyMode mode, string? dictation)
    {
        switch (mode)
        {
            case CleanupVocabularyMode.None:
                return null;
            case CleanupVocabularyMode.Mentioned:
                if (string.IsNullOrWhiteSpace(dictation) || GlossaryEntries.Count == 0)
                {
                    return null;
                }

                var glossary = CleanupPrompt.BuildGlossary(VocabularyMentions.Select(GlossaryEntries, dictation), maxTerms);
                return string.IsNullOrEmpty(glossary) ? null : glossary;
            default:
                return GlossaryFor(maxTerms);
        }
    }

    /// <summary>
    /// Every line of this vocabulary's glossary with no budget, in priority order, each with its estimated tokens
    /// (<see cref="CleanupPrompt.GlossaryLines"/>). Built once, at the first request that fits the glossary into a context.
    /// </summary>
    internal IReadOnlyList<GlossaryLineInfo> Lines =>
        LazyInitializer.EnsureInitialized(ref _lines, () => CleanupPrompt.GlossaryLines(GlossaryEntries));

    private List<GlossaryLineInfo>? _lines;
    private string? _wholeGlossary;

    /// <summary>The estimated tokens of the whole glossary with no budget, its header included; 0 when it is empty.</summary>
    public long WholeGlossaryTokens => Lines.Count == 0 ? 0 : CleanupPrompt.GlossaryHeaderTokens + CleanupPrompt.Tokens(Lines);

    /// <summary>
    /// The glossary block one request to a model on this PC carries, fitted into <paramref name="tokenBudget"/> tokens of
    /// its context (<see cref="ContextBudget"/>, the dictated text and its answer already taken out; see
    /// <see cref="CleanupPrompt.FitGlossary"/>). Null when nothing fits or there is nothing to add.
    /// </summary>
    /// <param name="mode">How much of the vocabulary the request may carry at most.</param>
    /// <param name="everything">
    /// Whether the whole vocabulary goes when it fits (Send your whole vocabulary when it fits). Without a dictation, as a
    /// readying request has, it is the leading run of the whole vocabulary that fits; with it, nothing.
    /// </param>
    /// <param name="dictation">The text the request carries, which the mentioned terms are picked by; null for a readying request.</param>
    /// <param name="maxTerms">The most terms the request may carry, whatever fits.</param>
    public string? GlossaryFor(CleanupVocabularyMode mode, bool everything, string? dictation, int tokenBudget, int maxTerms)
    {
        if (mode == CleanupVocabularyMode.None || GlossaryEntries.Count == 0)
        {
            return null;
        }

        var all = Lines;
        IReadOnlyList<GlossaryLineInfo> lines;
        if (mode == CleanupVocabularyMode.All || string.IsNullOrWhiteSpace(dictation))
        {
            if (mode != CleanupVocabularyMode.All && !everything)
            {
                return null;
            }

            lines = CleanupPrompt.TakeWhileFits(all, (long)tokenBudget - CleanupPrompt.GlossaryHeaderTokens, maxTerms);
        }
        else
        {
            var mentioned = CleanupPrompt.GlossaryLines(VocabularyMentions.Select(GlossaryEntries, dictation));
            lines = CleanupPrompt.FitGlossary(all, mentioned, everything, tokenBudget, maxTerms);
        }

        if (lines.Count == 0)
        {
            return null;
        }

        // The whole glossary is the same text for every request that carries it, so it is rendered once.
        return ReferenceEquals(lines, all)
            ? LazyInitializer.EnsureInitialized(ref _wholeGlossary, () => CleanupPrompt.RenderGlossary(all))
            : CleanupPrompt.RenderGlossary(lines);
    }
}
