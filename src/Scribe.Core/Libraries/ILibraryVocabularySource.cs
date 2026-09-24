namespace Scribe.Core.Libraries;

/// <summary>
/// The committed library vocabulary for everything outside the Settings window: dictation's post-processing and
/// glossary, quick add, and the usage report. Implemented by the library service; the W-V stream builds its vocabulary
/// generation from it.
/// </summary>
/// <remarks>
/// After the first load, <see cref="Current"/> and <see cref="IsStillPermitted"/> are cheap, lock-free reads safe from
/// any thread, including the dictation path, which must never wait on library I/O. The first read of
/// <see cref="Current"/> loads synchronously when nothing has been published yet, as
/// <see cref="PostProcessing.IDictionaryLibraryService.GetEnabledLibraryEntries"/> does today, so the first dictation
/// after startup never runs without its libraries; the app warms it off the dispatcher at startup.
/// </remarks>
public interface ILibraryVocabularySource
{
    /// <summary>The vocabulary of the newest committed generation whose files are in place, or <see cref="LibraryVocabulary.Empty"/>.</summary>
    LibraryVocabulary Current { get; }

    /// <summary>
    /// Whether a request carrying vocabulary admitted under <paramref name="admitted"/> may still be sent. False once
    /// permission has narrowed, which takes effect as soon as a Save that narrows it is prepared (fail closed), before
    /// its files are in place; a widening takes effect only once the Save has committed.
    /// </summary>
    bool IsStillPermitted(AiVocabularyScope admitted);

    /// <summary>
    /// Raised with the new generation after a new vocabulary is published, on the thread that completed the Save or
    /// recovery; subscribers must be quick. Raised through <see cref="Infrastructure.ResilientEvent.InvokeAll{T}"/>, so
    /// one throwing subscriber never stops the others (pattern P-3).
    /// </summary>
    event Action<long>? Changed;
}
