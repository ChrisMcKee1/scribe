using System.Collections.Frozen;
using Scribe.Core.Models;

namespace Scribe.Core.Libraries;

/// <summary>
/// The library content one vocabulary may carry to AI cleanup: every enabled, available library whose AI permission is
/// on (Decision 2), each with the content that permission was granted for. A dictation keeps the scope it was admitted
/// with, and every outbound cleanup request (each chunk, a retry, the Chat Completions fallback, a probe, the usage
/// insight) is handed over only through <see cref="ILibraryVocabularySource.TryHandOff"/> with that scope; if the
/// current scope no longer <see cref="Covers"/> it, the request is not sent and local rules finish the dictation.
/// </summary>
/// <remarks>
/// <para>
/// Consent is bound to content (review finding A4), so the scope is too (A12): it pairs each permitted library id with
/// the hash of the content permission covers, the hash the catalog holds for it (<see cref="CatalogLibrary.ContentHash"/>:
/// a custom library's CSV, a built-in's edits document, or null for a built-in with no document, whose shipped rows
/// cannot change while the process runs). For a permitted library that is its <see cref="LibraryLocalState.AcceptedContent"/>
/// hash, or null for a built-in with no document and no accepted entry: a built-in whose edits document disappeared
/// outside Scribe while an accepted entry remains is not permitted at all (review finding A5 on the composition stream),
/// on defaults too, where no adoption runs, so a request scope always carries the catalog's actual hash. A request
/// admitted for "team" at content H1 is therefore refused after that file was replaced by H2 outside Scribe, even once
/// the user has permitted H2, and so is one admitted before a Save that changed the library's content, or with the terms
/// of a built-in's edits document that has since gone: local rules finish that dictation.
/// </para>
/// <para>Only composition builds one (the constructor is internal to Core). Ids compare case-insensitively.</para>
/// </remarks>
public sealed class AiVocabularyScope
{
    internal AiVocabularyScope(long generation, IEnumerable<KeyValuePair<string, LibraryContentHash?>> permittedContent)
    {
        ArgumentNullException.ThrowIfNull(permittedContent);
        ArgumentOutOfRangeException.ThrowIfNegative(generation);

        var permitted = new Dictionary<string, LibraryContentHash?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, content) in permittedContent)
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                permitted[id.Trim()] = content;
            }
        }

        Generation = generation;
        PermittedContent = permitted.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        PermittedLibraryIds = PermittedContent.Keys.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A scope that permits no library, for a service with nothing loaded yet.</summary>
    public static AiVocabularyScope None { get; } = new(0, []);

    /// <summary>The committed generation the scope was computed from.</summary>
    public long Generation { get; }

    /// <summary>The libraries whose terms may be sent: the keys of <see cref="PermittedContent"/>.</summary>
    public IReadOnlySet<string> PermittedLibraryIds { get; }

    /// <summary>
    /// Each permitted library with the content its permission covers: the hash the catalog holds for it, null for a
    /// built-in with no edits document.
    /// </summary>
    public IReadOnlyDictionary<string, LibraryContentHash?> PermittedContent { get; }

    /// <summary>
    /// Whether this scope still permits everything <paramref name="admitted"/> did: every library it names, with the
    /// same content. False as soon as one of them has lost permission or now holds other content, which is what makes a
    /// request carrying <paramref name="admitted"/>'s vocabulary unsendable. Libraries this scope adds do not matter.
    /// </summary>
    public bool Covers(AiVocabularyScope admitted)
    {
        ArgumentNullException.ThrowIfNull(admitted);
        foreach (var (id, content) in admitted.PermittedContent)
        {
            if (!PermittedContent.TryGetValue(id, out var current) || current != content)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// The library vocabulary of one committed generation, for everything that runs outside the Settings window: the
/// rules the post-processor applies, the subset AI cleanup may carry as vocabulary, and the scope that subset was cut
/// by. The W-V stream wraps it, with the personal dictionary and the compiled rules, into the vocabulary generation a
/// dictation keeps from admission to insertion.
/// </summary>
/// <remarks>
/// <para>
/// Composed only from a <see cref="LibraryCatalog"/> (the constructor is internal to Core), never from a draft, which
/// is what keeps quick add, dictation and the usage report on committed state (review finding R12).
/// </para>
/// <para>
/// <see cref="Entries"/> hold one entry per spoken form (<see cref="LibraryTermKey"/>), each the winner under
/// Decision 1's tiers and legacy markers, in composition order, which is also the order the glossary fills its budget
/// in; paused libraries and turned-off rows contribute nothing. <see cref="AiEntries"/> are the entries of
/// <see cref="Entries"/> whose library <see cref="AiScope"/> permits, in the same order: a filter of the same winners,
/// never a second composition, so the glossary can never teach the model a spelling local replacement would not
/// write. The personal dictionary is merged on top by the consumer, as today
/// (<see cref="PostProcessing.DictionaryLibraryComposer.Merge"/>).
/// </para>
/// </remarks>
public sealed class LibraryVocabulary
{
    internal LibraryVocabulary(
        long generation,
        IReadOnlyList<DictionaryEntry> entries,
        IReadOnlyList<DictionaryEntry> aiEntries,
        AiVocabularyScope aiScope)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(aiEntries);
        ArgumentNullException.ThrowIfNull(aiScope);
        ArgumentOutOfRangeException.ThrowIfNegative(generation);
        Generation = generation;
        Entries = [.. entries];
        AiEntries = [.. aiEntries];
        AiScope = aiScope;
    }

    /// <summary>No library vocabulary: nothing enabled, or nothing loaded yet.</summary>
    public static LibraryVocabulary Empty { get; } = new(0, [], [], AiVocabularyScope.None);

    /// <summary>
    /// The committed generation this vocabulary was composed from. Not a version of the vocabulary: two publications can
    /// share it (see <see cref="ILibraryVocabularySource.Changed"/>), so a consumer compares vocabularies, never only this.
    /// </summary>
    public long Generation { get; }

    /// <summary>The library rules for local replacement, one per spoken form, in composition order.</summary>
    public IReadOnlyList<DictionaryEntry> Entries { get; }

    /// <summary>The entries AI cleanup may carry as vocabulary, in the same order.</summary>
    public IReadOnlyList<DictionaryEntry> AiEntries { get; }

    /// <summary>The libraries <see cref="AiEntries"/> were drawn from.</summary>
    public AiVocabularyScope AiScope { get; }
}
