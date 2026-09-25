namespace Scribe.Core.Libraries;

/// <summary>
/// The committed library vocabulary for everything outside the Settings window: dictation's post-processing and
/// glossary, quick add, and the usage report. Implemented by the library service; the W-V stream builds its vocabulary
/// generation from it.
/// </summary>
/// <remarks>
/// After the first load, <see cref="Current"/> is a cheap, lock-free read safe from any thread, including the dictation
/// path, which must never wait on library I/O. The first read of <see cref="Current"/> loads synchronously when nothing
/// has been published yet, as <see cref="PostProcessing.IDictionaryLibraryService.GetEnabledLibraryEntries"/> does
/// today, so the first dictation after startup never runs without its libraries; the app warms it off the dispatcher at
/// startup.
/// </remarks>
public interface ILibraryVocabularySource
{
    /// <summary>The vocabulary of the stored generation, read through the journal while its files are not all in place, or <see cref="LibraryVocabulary.Empty"/>.</summary>
    LibraryVocabulary Current { get; }

    /// <summary>
    /// The one admission point that orders revocation against outbound AI requests (review question 1). When every
    /// library <paramref name="admitted"/> permitted is still permitted, runs <paramref name="handOff"/> under the
    /// permission gate and returns true; otherwise runs nothing and returns false, and the request must not be sent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every narrowing of AI permission is published under the same gate, at the latest when it commits: when a Save
    /// that narrows it is prepared (fail closed, before its commit and before its files are in place), when that Save
    /// completes, and when a Save that did not commit puts the committed scope back. A widening waits for the commit. So
    /// every request either was handed over before a revocation took the gate, and went with the permission it was
    /// admitted under, or sees the revocation and is not sent; nothing can be handed over after the gate has published
    /// the narrower scope.
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
