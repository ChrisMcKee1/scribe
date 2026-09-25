using Scribe.Core.Libraries;
using Scribe.Core.PostProcessing;

namespace Scribe.Core.Vocabulary;

/// <summary>
/// Stream W-V's stand-in for the library vocabulary source until the W1b integration, which deletes this file and its one
/// registration in the app's composition root. The library service (<c>DictionaryLibraryService</c>, stream J) already
/// registers itself as the <see cref="ILibraryVocabularySource"/> in Core, and the app's later registration of this one
/// overrides it until then, so dictation, AI cleanup and quick add keep release 0.4.4's library selection, driven by the
/// settings dictation runs on. The usage report is the exception: <c>UsageReport</c> reads a library service that is a
/// vocabulary source through that service's own snapshot.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Current"/> is the entries of the libraries the settings in use enable
/// (<see cref="IDictionaryLibraryService.GetEnabledLibraryEntries(IReadOnlyCollection{string})"/>, read afresh on every
/// call, as each 0.4.4 consumer read them); <see cref="LibraryVocabulary.AiEntries"/> is the same list, because before
/// W1b every enabled library is sent; its scope names those ids with no content hashes (nothing binds consent to content
/// before W1b), and <see cref="TryHandOff"/> always hands over, under a gate of its own as the real source does, since
/// nothing can narrow AI permission before W1b. <see cref="Changed"/> is never raised: nothing commits a library
/// generation before W1b, and every change the seam knew about (a settings save, a stored-settings reapply) already
/// asks the vocabulary publisher for a new generation.
/// </para>
/// <para>
/// Reading <see cref="Current"/> does library I/O, where the real source is a cached read. Only the vocabulary
/// publisher (off the dispatcher), quick add's conflict check and the usage report read it, each where 0.4.4 read the
/// libraries, so no path gains I/O it did not have; the dictation path reads the publisher's generation, never this.
/// </para>
/// </remarks>
public sealed class InterimLibraryVocabularySource : ILibraryVocabularySource
{
    private readonly IDictionaryLibraryService _libraries;
    private readonly Func<IReadOnlyCollection<string>?> _enabledIdsInUse;
    private readonly object _gate = new();

    /// <param name="libraries">The library service, whose id-selection seam this adapts.</param>
    /// <param name="enabledIdsInUse">
    /// The enabled library ids of the settings dictation runs on (the controller's current settings), never a fresh read
    /// of the stored document, which may have turned unreadable. Null or empty selects no library.
    /// </param>
    public InterimLibraryVocabularySource(
        IDictionaryLibraryService libraries, Func<IReadOnlyCollection<string>?> enabledIdsInUse)
    {
        ArgumentNullException.ThrowIfNull(libraries);
        ArgumentNullException.ThrowIfNull(enabledIdsInUse);
        _libraries = libraries;
        _enabledIdsInUse = enabledIdsInUse;
    }

    public LibraryVocabulary Current
    {
        get
        {
            var ids = _enabledIdsInUse()?.Where(id => !string.IsNullOrWhiteSpace(id)).ToList() ?? [];
            if (ids.Count == 0)
            {
                return LibraryVocabulary.Empty;
            }

            var entries = _libraries.GetEnabledLibraryEntries(ids);
            return new LibraryVocabulary(
                0,
                entries,
                entries,
                new AiVocabularyScope(0, ids.Select(id => KeyValuePair.Create(id, (LibraryContentHash?)null))));
        }
    }

    public bool TryHandOff(AiVocabularyScope admitted, Action handOff)
    {
        ArgumentNullException.ThrowIfNull(admitted);
        ArgumentNullException.ThrowIfNull(handOff);
        lock (_gate)
        {
            handOff();
        }

        return true;
    }

    public event Action<long>? Changed
    {
        add { }
        remove { }
    }
}
