namespace Scribe.Core.Libraries;

/// <summary>
/// The committed library vocabulary for everything outside the Settings window: dictation's post-processing and
/// glossary, quick add, and the usage report. Implemented by the library service; the W-V stream builds its vocabulary
/// generation from it.
/// </summary>
/// <remarks>
/// <para>
/// After the first load, <see cref="Current"/> is a cheap, lock-free read safe from any thread, including the dictation
/// path, which must never wait on library I/O. The first read of <see cref="Current"/> loads synchronously when nothing
/// has been published yet, as <c>IDictionaryLibraryService.GetEnabledLibraryEntries()</c> does today, so the first
/// dictation after startup never runs without its libraries; the app warms it off the dispatcher at startup.
/// </para>
/// <para>
/// It replaces exactly the library-selection seam of release 0.4.4:
/// <c>IDictionaryLibraryService.GetEnabledLibraryEntries(IReadOnlyCollection&lt;string&gt;)</c> and
/// <c>ITextPostProcessor.Reload(IReadOnlyCollection&lt;string&gt;)</c>, through which the post-processor, the AI cleanup
/// glossary, the usage report and quick add each pass the enabled ids of the settings dictation runs on. Once the
/// library state is the source of truth, those ids are only the projection older builds read
/// (<see cref="LibraryStateEncoding.EnabledLibraryIds"/>), so every one of those callers moves here:
/// <see cref="LibraryVocabulary.Entries"/> for local replacement, <see cref="LibraryVocabulary.AiEntries"/> for the
/// glossary and the usage report's shareable labels. The seam's two guarantees hold by construction: nothing re-reads
/// the stored settings document per request, so a document that turns unreadable mid-session changes nothing, and the
/// vocabulary changes only when a new one is published, so a reload of the dictionary alone keeps it.
/// </para>
/// </remarks>
public interface ILibraryVocabularySource
{
    /// <summary>The vocabulary of the stored generation, read through the journal while its files are not all in place, or <see cref="LibraryVocabulary.Empty"/>.</summary>
    LibraryVocabulary Current { get; }

    /// <summary>
    /// The one admission point that orders revocation against outbound AI requests (review question 1). When the
    /// published scope still <see cref="AiVocabularyScope.Covers"/> <paramref name="admitted"/> (every library it
    /// permitted is still permitted, for the same content, A12), runs <paramref name="handOff"/> under the permission
    /// gate and returns true; otherwise runs nothing and returns false, and the request must not be sent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every narrowing of AI permission is published under the same gate, at the latest when it commits: when a Save
    /// that narrows it is prepared (fail closed, before its commit and before its files are in place), when that Save
    /// completes, when a Save that did not commit puts the committed scope back, and when content changes outside Scribe
    /// are adopted. A widening waits for the commit. A change of a library's content narrows too, because the scope pairs
    /// each library with the content its permission covers, so a request admitted for the old content is not sent once
    /// the new content is published, even if the new content is permitted (A12). So every request either was handed over
    /// before a revocation took the gate, and went with the permission it was admitted under, or sees the revocation and
    /// is not sent; nothing can be handed over after the gate has published the narrower scope.
    /// </para>
    /// <para>
    /// <paramref name="handOff"/> must be the last step before the request leaves the pipeline's control: for an HTTP
    /// provider, starting the inner handler's send from a <c>DelegatingHandler</c> placed just before the transport; for
    /// the Copilot runtime, the session's send call. It must start the send and return without waiting for the response,
    /// because the gate is held while it runs, and it must not call back into the library service. A request already
    /// handed over cannot be recalled, and nothing here pretends otherwise: a revocation governs every request not yet
    /// handed over, each chunk, retry, fallback and probe separately.
    /// </para>
    /// </remarks>
    bool TryHandOff(AiVocabularyScope admitted, Action handOff);

    /// <summary>
    /// Raised with the new generation after a new vocabulary is published, on the thread that completed the Save or
    /// recovery; subscribers must be quick. Raised through <see cref="Infrastructure.ResilientEvent.InvokeAll{T}"/>, so
    /// one throwing subscriber never stops the others (pattern P-3).
    /// </summary>
    event Action<long>? Changed;
}
