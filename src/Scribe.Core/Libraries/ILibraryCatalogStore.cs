namespace Scribe.Core.Libraries;

/// <summary>
/// Committed library storage and the Save commit, as the Settings window drives it. Implemented by the library service
/// (<see cref="PostProcessing.DictionaryLibraryService"/>), which is also <see cref="PostProcessing.IDictionaryLibraryService"/>
/// and <see cref="ILibraryVocabularySource"/>.
/// </summary>
/// <remarks>
/// <para>
/// The Save sequence (plan 3.5): on the dispatcher the workspace captures a <see cref="LibraryChangeSet"/>; off it,
/// <see cref="PrepareSave"/> writes an immutable redo image of every file the Save writes and the manifest of the next
/// generation; on the dispatcher, <c>ISettingsRepository.SaveBundle</c> commits <see cref="PreparedLibrarySave.Payload"/>
/// with the settings; off it, <see cref="CompleteSave"/> installs or discards by the committed generation, whatever
/// <c>SaveBundle</c> reported, and the new vocabulary is published; back on the dispatcher, the workspace marks the
/// captured revision saved.
/// </para>
/// <para>
/// A fence covers a Save's whole lifetime (review finding A1). From the moment <see cref="PrepareSave"/> returns
/// <see cref="LibraryPrepareStatus.Prepared"/> until <see cref="CompleteSave"/> returns for it, the preparation is live:
/// recovery never discards, replays or sets aside its manifest; another prepare returns
/// <see cref="LibraryPrepareStatus.Busy"/>; and nothing else advances the generation (adoption waits and uses its plan
/// in memory, and the <c>Import</c> and <c>Remove</c> wrappers refuse), so <c>SaveBundle</c>'s generation check can
/// only fail for a reason outside this process. A committed generation whose files are not all in place yet (a target
/// another app holds open, access denied, a full disk) is unresolved, and stays so until recovery installs them: until
/// then <see cref="PrepareSave"/> returns <see cref="LibraryPrepareStatus.PreviousSaveUnfinished"/>, adoption still
/// waits, and the wrappers still refuse. There are no chained manifests.
/// </para>
/// <para>
/// One read path (review finding G2). While a manifest of the stored generation is pending, whether it is this process's
/// live preparation after its commit or a manifest recovery could not finish, <see cref="LoadCatalog"/> applies the
/// whole manifest logically over the files as they stand: a written, created or restored library reads its redo image
/// (the committed bytes), a deleted library and a removed edits document are absent, a purged entry is gone from
/// Recently deleted, and a restored entry is gone from it and present as its library. So every reader sees generation
/// G or G + 1, never a mix, and dictation never applies a library a committed Save deleted.
/// </para>
/// <para>
/// While the session runs on defaults (<see cref="LibraryStateContext.RunningOnDefaults"/>), nothing library-related is
/// written automatically: no adoption, no marker, no denial of a lost state, no Recently deleted retention, no orphan
/// removal. Recovery still finishes a manifest of the stored generation and discards one that never committed, because
/// both follow from the generation row alone and the first is the tail of a Save the user made. The only library
/// commit such a session makes is the user's own Save, through <c>SaveBundle</c>, which is also what ends that state.
/// </para>
/// <para>
/// Every member does file I/O, takes the service's one library lock (never across an await), and must run off the
/// dispatcher. Expected failures (outside edits, locked or full disks, unreadable files) are results, never exceptions;
/// only a null argument throws, and <see cref="LoadCatalog"/> when the settings store cannot be read at all. Logs carry
/// counts and enum names only (plan 3.15).
/// </para>
/// </remarks>
public interface ILibraryCatalogStore
{
    /// <summary>
    /// The committed catalog, after running <see cref="Recover"/> when a manifest this process does not own is on disk.
    /// Never throws for a file problem: a file that cannot be used is a <see cref="LibraryFileState"/>.
    /// </summary>
    LibraryCatalog LoadCatalog();

    /// <summary>
    /// Steps 1 and 2: checks every pre-image, writes the redo images and the manifest of the next generation, both
    /// flushed to disk, and makes the preparation live.
    /// </summary>
    LibraryPrepareResult PrepareSave(LibraryChangeSet changes);

    /// <summary>
    /// Step 4, settled by the stored generation. Equal to <see cref="PreparedLibrarySave.Generation"/>: every operation
    /// is brought to its committed result from whatever it finds on disk (each one is idempotent, so a retry, a crash
    /// and a second process start all resume it), versions found outside Scribe are kept rather than overwritten, and
    /// the manifest is retired once every operation is done. Equal to the base: the preparation is discarded. Anything
    /// else: the Save was superseded and the preparation is discarded. Ends the preparation's live state in every case.
    /// Call it exactly once per prepared Save, after <c>SaveBundle</c> returned or threw.
    /// </summary>
    LibrarySaveOutcome CompleteSave(PreparedLibrarySave prepared);

    /// <summary>
    /// Finishes, discards or quarantines what the journal holds that this process's live preparation is not: at
    /// startup, from <see cref="LoadCatalog"/>, and on the storage maintenance schedule while a committed manifest is
    /// unresolved. A manifest of the stored generation is finished; one above it is discarded; one below it, one whose
    /// redo image fails its hash, and any manifest while the generation row is absent or unparsable, are set aside with
    /// every file they name for the quarantine retention; and only files no manifest, live or set aside, names are
    /// removed as orphans.
    /// </summary>
    LibraryRecoveryResult Recover();

    /// <summary>The libraries whose file changed on disk since <paramref name="catalog"/> was loaded, for the before-Save check.</summary>
    IReadOnlyList<string> FindOutsideEdits(LibraryCatalog catalog);

    /// <summary>
    /// The content of a Recently deleted entry, read from its file and checked against
    /// <see cref="RecentlyDeletedLibrary.ContentHash"/>, so the workspace can restore it into a draft, and preview,
    /// export or edit it before Save, without doing file I/O itself (review finding A8). Null when the entry is gone,
    /// cannot be read, or no longer matches its hash (the shell reloads the list then).
    /// </summary>
    RecentlyDeletedContent? ReadRecentlyDeleted(RecentlyDeletedLibrary entry);
}
