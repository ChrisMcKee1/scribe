using System.Collections.Frozen;
using Scribe.Core.Models;

namespace Scribe.Core.Libraries;

/// <summary>
/// The library ids whose terms one vocabulary may carry to AI cleanup: every enabled, available library whose AI
/// permission is on (Decision 2). A dictation keeps the scope it was admitted with, and every outbound cleanup request
/// (each chunk, a retry, the Chat Completions fallback, a probe, the usage insight) is handed over only through
/// <see cref="ILibraryVocabularySource.TryHandOff"/> with that scope; if permission has narrowed since, the request is
/// not sent and local rules finish the dictation.
/// </summary>
/// <remarks>Only composition builds one (the constructor is internal to Core). Ids compare case-insensitively.</remarks>
public sealed class AiVocabularyScope
{
    internal AiVocabularyScope(long generation, IEnumerable<string> permittedLibraryIds)
    {
        ArgumentNullException.ThrowIfNull(permittedLibraryIds);
        ArgumentOutOfRangeException.ThrowIfNegative(generation);
        Generation = generation;
        PermittedLibraryIds = permittedLibraryIds.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A scope that permits no library, for a service with nothing loaded yet.</summary>
    public static AiVocabularyScope None { get; } = new(0, []);

    /// <summary>The committed generation the scope was computed from.</summary>
    public long Generation { get; }

    /// <summary>The libraries whose terms may be sent.</summary>
    public IReadOnlySet<string> PermittedLibraryIds { get; }
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

    /// <summary>The committed generation this vocabulary was composed from.</summary>
    public long Generation { get; }

    /// <summary>The library rules for local replacement, one per spoken form, in composition order.</summary>
    public IReadOnlyList<DictionaryEntry> Entries { get; }

    /// <summary>The entries AI cleanup may carry as vocabulary, in the same order.</summary>
    public IReadOnlyList<DictionaryEntry> AiEntries { get; }

    /// <summary>The libraries <see cref="AiEntries"/> were drawn from.</summary>
    public AiVocabularyScope AiScope { get; }
}
