namespace Scribe.Core.Libraries;

/// <summary>
/// Committed library storage and the Save commit, as the Settings window drives it. Implemented by the library service
/// (<see cref="PostProcessing.DictionaryLibraryService"/>), which is also <see cref="PostProcessing.IDictionaryLibraryService"/>
/// and <see cref="ILibraryVocabularySource"/>.
/// </summary>
/// <remarks>
/// <para>
/// The Save sequence (plan 3.5): on the dispatcher the workspace captures a <see cref="LibraryChangeSet"/>; off it,
/// <see cref="PrepareSave"/> stages every file and writes the manifest for the next generation; on the dispatcher,
/// <c>ISettingsRepository.SaveBundle</c> commits <see cref="PreparedLibrarySave.Payload"/> with the settings; off it,
/// <see cref="CompleteSave"/> finishes or discards by the committed generation, whatever <c>SaveBundle</c> reported,
/// and the new vocabulary is published; back on the dispatcher, the workspace marks the captured revision saved.
/// </para>
/// <para>
/// Every member does file I/O and must run off the dispatcher. One Save at a time: a prepare while another prepared
/// Save has not completed returns <see cref="LibraryPrepareStatus.Busy"/>. Expected failures (outside edits, locked or
/// full disks, unreadable files) are results, never exceptions; only a null argument throws, and
/// <see cref="LoadCatalog"/> when the settings store cannot be read at all. Logs carry counts and enum names only (plan
/// 3.15).
/// </para>
/// </remarks>
public interface ILibraryCatalogStore
{
    /// <summary>
    /// The committed catalog, after running <see cref="Recover"/> if a manifest is pending. Never throws for a file
    /// problem: a file that cannot be used is a <see cref="LibraryFileState"/>.
    /// </summary>
    LibraryCatalog LoadCatalog();

    /// <summary>Steps 1 and 2: checks every pre-image, stages every file and writes the manifest of the next generation.</summary>
    LibraryPrepareResult PrepareSave(LibraryChangeSet changes);

    /// <summary>
    /// Step 4, settled by the stored generation: equal to <see cref="PreparedLibrarySave.Generation"/>, the staged files
    /// replace their targets and the moves run; equal to the base, everything staged is discarded; anything else, the
    /// Save was superseded. Safe to call once per prepared Save, after <c>SaveBundle</c> returned or threw.
    /// </summary>
    LibrarySaveOutcome CompleteSave(PreparedLibrarySave prepared);

    /// <summary>
    /// Finishes, discards or sets aside what an earlier process left in the journal: at startup, and before any read
    /// while a manifest is pending. Every reader sees generation G or G + 1, never a mix.
    /// </summary>
    LibraryRecoveryResult Recover();

    /// <summary>The libraries whose file changed on disk since <paramref name="catalog"/> was loaded, for the before-Save check.</summary>
    IReadOnlyList<string> FindOutsideEdits(LibraryCatalog catalog);
}
