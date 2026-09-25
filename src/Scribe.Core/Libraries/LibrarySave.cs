namespace Scribe.Core.Libraries;

/// <summary>
/// The auxiliary rows of the <c>settings</c> table the libraries own. Every one starts with <see cref="Prefix"/>, which
/// is the only prefix a library save may write; older builds ignore rows they do not know, so none of these changes
/// what 0.4.3 or 0.4.2 reads.
/// </summary>
public static class LibrarySettingKeys
{
    /// <summary>The prefix every library row has.</summary>
    public const string Prefix = "libraries.";

    /// <summary>
    /// The committed library generation, a positive decimal integer in invariant form: the journal's commit point,
    /// written in the same transaction as everything a Save commits. Absent until this version first commits.
    /// </summary>
    public const string Generation = "libraries.generation";

    /// <summary>The library local state (<see cref="LibraryLocalState"/>): versioned JSON whose format composition owns.</summary>
    public const string State = "libraries.state";

    /// <summary>
    /// The ids given to hand-placed custom files whose stem is a built-in id, so a remapped id never changes while its
    /// file exists: versioned JSON whose format the storage stream owns.
    /// </summary>
    public const string FileIds = "libraries.file_ids";
}

/// <summary>One auxiliary settings row a library commit writes; a null <see cref="Value"/> deletes the row.</summary>
/// <param name="Key">The row's key; always starts with <see cref="LibrarySettingKeys.Prefix"/>.</param>
/// <param name="Value">The value, or null to delete the row.</param>
public readonly record struct LibrarySettingValue(string Key, string? Value);

/// <summary>
/// What a library commit adds to the settings transaction: the generation it commits, the enabled list for the
/// settings document, and the library's auxiliary rows. <c>ISettingsRepository.SaveBundle</c> commits it with the
/// settings document, the dictionary and snippets in one BEGIN IMMEDIATE transaction, and refuses it, changing
/// nothing, when the stored generation is not <see cref="ExpectedGeneration"/>. <c>CommitLibraryState</c> commits one
/// on its own for state the service records without a Save (adoption, a lost state's denial); it may patch the stored
/// document's enabled list and nothing else of the document.
/// </summary>
/// <remarks>
/// Only the journal builds one (the constructor is internal to Core), because a generation with no manifest behind it
/// would tell recovery that files were committed when they were not.
/// </remarks>
public sealed class LibrarySavePayload
{
    internal LibrarySavePayload(
        long expectedGeneration,
        long generation,
        IReadOnlyList<string>? enabledLibraryIds,
        IReadOnlyList<LibrarySettingValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedGeneration);
        if (generation != expectedGeneration + 1)
        {
            throw new ArgumentOutOfRangeException(nameof(generation), "A commit moves the generation forward by one.");
        }

        foreach (var value in values)
        {
            if (value.Key is null || !value.Key.StartsWith(LibrarySettingKeys.Prefix, StringComparison.Ordinal) ||
                value.Key == LibrarySettingKeys.Generation)
            {
                throw new ArgumentException("A library commit writes only its own auxiliary rows.", nameof(values));
            }
        }

        ExpectedGeneration = expectedGeneration;
        Generation = generation;
        EnabledLibraryIds = enabledLibraryIds is null ? null : [.. enabledLibraryIds];
        Values = [.. values];
    }

    /// <summary>The generation the stored key must hold for the commit to go ahead; 0 when the key is absent.</summary>
    public long ExpectedGeneration { get; }

    /// <summary>The generation this commit writes to <see cref="LibrarySettingKeys.Generation"/>.</summary>
    public long Generation { get; }

    /// <summary>
    /// The list the settings document's <see cref="Models.AppSettings.EnabledDictionaryLibraryIds"/> takes, or null to
    /// leave the document's list as the caller's document has it (<c>SaveBundle</c>) or as it is stored
    /// (<c>CommitLibraryState</c>, which refuses to patch a document it cannot read or that a repair lost).
    /// </summary>
    public IReadOnlyList<string>? EnabledLibraryIds { get; }

    /// <summary>The auxiliary rows to write or delete, never the generation row, which <see cref="Generation"/> sets.</summary>
    public IReadOnlyList<LibrarySettingValue> Values { get; }
}

/// <summary>Counts for the Save log line and the notice; never names.</summary>
public readonly record struct LibraryChangeCounts(int Created, int Updated, int Deleted, int Restored, int Terms);

/// <summary>
/// A Save whose redo images and manifest are written (steps 1 and 2 of the Save commit), waiting for the settings
/// transaction. It is live from the moment <c>ILibraryCatalogStore.PrepareSave</c> returns it until
/// <c>CompleteSave</c> returns for it: recovery never touches its manifest meanwhile, and nothing else advances the
/// generation. Hand <see cref="Payload"/> to <c>SaveBundle</c>, then pass this to <c>CompleteSave</c> exactly once,
/// whether or not <c>SaveBundle</c> threw (from a <c>finally</c>): completion reads the committed generation and
/// finishes or discards accordingly.
/// </summary>
public sealed class PreparedLibrarySave
{
    internal PreparedLibrarySave(long draftRevision, LibrarySavePayload payload, LibraryChangeCounts counts)
    {
        ArgumentNullException.ThrowIfNull(payload);
        DraftRevision = draftRevision;
        Payload = payload;
        Counts = counts;
    }

    /// <summary>The generation the draft was based on.</summary>
    public long BaseGeneration => Payload.ExpectedGeneration;

    /// <summary>The generation this Save commits.</summary>
    public long Generation => Payload.Generation;

    /// <summary>The draft revision captured, to mark saved once the Save stands.</summary>
    public long DraftRevision { get; }

    /// <summary>What <c>SaveBundle</c> commits with the settings.</summary>
    public LibrarySavePayload Payload { get; }

    /// <summary>What the Save changes, as counts.</summary>
    public LibraryChangeCounts Counts { get; }
}

/// <summary>Why a file operation failed, as a shape the shell can word and the log can carry.</summary>
public enum LibraryIoFailure
{
    None,

    /// <summary>Windows refused access (a read-only file, a permission): lasts until someone changes the file.</summary>
    AccessDenied,

    /// <summary>The disk is full.</summary>
    DiskFull,

    /// <summary>Another app holds the file open. Lasts until that app lets it go; never a reason to reset or restore.</summary>
    SharingViolation,

    /// <summary>A path is too long for this system.</summary>
    PathTooLong,

    /// <summary>A journal file failed its recorded hash or could not be parsed, so what it held cannot be trusted.</summary>
    Corrupt,

    /// <summary>Anything else; the log carries its <c>FailureShape</c>.</summary>
    Other,
}

/// <summary>
/// The journal's file operations, by name: the <c>{Operation}</c> of the log line
/// <c>Library file operation {Operation} failed: {Failure}</c>, so that line can carry what failed without a path, a
/// file name or an id (review finding G8).
/// </summary>
public enum LibraryFileOperation
{
    /// <summary>Reading a library file, an edits document, a Recently deleted entry or a journal file.</summary>
    Read,

    /// <summary>Listing the libraries folder or one of its subfolders.</summary>
    Enumerate,

    /// <summary>Writing a redo image at prepare.</summary>
    WriteRedoImage,

    /// <summary>Writing a manifest at prepare.</summary>
    WriteManifest,

    /// <summary>Copying a redo image to the install copy beside its target.</summary>
    CreateInstallCopy,

    /// <summary><c>File.Replace</c> of a target by its install copy.</summary>
    Replace,

    /// <summary>A step of the checked move that stands in for <c>File.Replace</c>.</summary>
    CheckedReplace,

    /// <summary>Moving an install copy onto an absent target.</summary>
    Install,

    /// <summary>Keeping a version found outside Scribe as a new library, or setting a foreign edits document aside.</summary>
    KeepVersion,

    /// <summary>Keeping a replaced edits document as its last good copy.</summary>
    KeepPrevious,

    /// <summary>Setting an edits document aside with a time stamp.</summary>
    SetAside,

    /// <summary>Moving a custom library into Recently deleted.</summary>
    MoveToRecentlyDeleted,

    /// <summary>Moving a Recently deleted entry back.</summary>
    RestoreFromRecentlyDeleted,

    /// <summary>Deleting a Recently deleted entry for good, by the user's choice or the janitor's retention.</summary>
    Purge,

    /// <summary>Deleting a resolved backup, a spent install copy or a redo image.</summary>
    DeleteSpent,

    /// <summary>Deleting or renaming a manifest when it is retired, discarded or set aside.</summary>
    RetireManifest,

    /// <summary>Removing a staged, backup or redo file no manifest names.</summary>
    RemoveOrphan,

    /// <summary>Writing the witness file after a commit.</summary>
    WriteWitness,
}

/// <summary>What <c>ILibraryCatalogStore.PrepareSave</c> did.</summary>
public enum LibraryPrepareStatus
{
    /// <summary>Redo images and the manifest written, and the preparation live; commit <see cref="PreparedLibrarySave.Payload"/> next.</summary>
    Prepared,

    /// <summary>The change set was empty; nothing was written.</summary>
    NothingToSave,

    /// <summary>
    /// A file changed on disk since the catalog was read; nothing was written. The shell offers Keep editing, Reload
    /// saved version, or Save my draft as a new library.
    /// </summary>
    OutsideEdit,

    /// <summary>The change set was based on a generation that is no longer the committed one; reload and try again.</summary>
    Stale,

    /// <summary>Another Save is between prepare and completion in this process; this one was not started.</summary>
    Busy,

    /// <summary>
    /// The committed generation's files are not all in place yet (<see cref="LibraryPrepareResult.Failure"/> says why,
    /// most often another app holding a file open). Nothing advances the generation until they are, so nothing was
    /// written; the shell asks the user to close the file, and the next attempt finishes the earlier Save first.
    /// </summary>
    PreviousSaveUnfinished,

    /// <summary>
    /// The library state was written by a newer version of Scribe (<see cref="LocalStateHealth.Newer"/>), which makes
    /// the libraries read-only in this one; nothing was written.
    /// </summary>
    ReadOnly,

    /// <summary>Staging failed (<see cref="LibraryPrepareResult.Failure"/>); what was staged was removed and nothing changed.</summary>
    Failed,
}

/// <summary>The result of preparing a Save.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Save">The prepared Save when <paramref name="Status"/> is <see cref="LibraryPrepareStatus.Prepared"/>.</param>
/// <param name="OutsideEditIds">For <see cref="LibraryPrepareStatus.OutsideEdit"/>: the libraries changed outside Scribe. Empty otherwise.</param>
/// <param name="Failure">
/// For <see cref="LibraryPrepareStatus.Failed"/>: why staging failed. For
/// <see cref="LibraryPrepareStatus.PreviousSaveUnfinished"/>: why the earlier Save's files are not in place.
/// </param>
public sealed record LibraryPrepareResult(
    LibraryPrepareStatus Status,
    PreparedLibrarySave? Save,
    IReadOnlyList<string> OutsideEditIds,
    LibraryIoFailure Failure = LibraryIoFailure.None);

/// <summary>Where a Save stands after completion.</summary>
public enum LibrarySaveStatus
{
    /// <summary>Committed and every file in place.</summary>
    Applied,

    /// <summary>
    /// Committed; some files are not in place yet (<see cref="LibrarySaveOutcome.Failure"/> says why, most often another
    /// app holding a file open). Readers use the committed content from the journal, recovery finishes later, and until
    /// it has no other library change can be saved. The Save stands and is never reported as unsaved.
    /// </summary>
    AppliedAwaitingRelease,

    /// <summary>The settings transaction did not commit; the preparation was discarded and nothing changed.</summary>
    NotCommitted,

    /// <summary>
    /// The stored generation is neither the base nor this Save's: something else committed. The preparation was
    /// discarded; reload.
    /// </summary>
    Superseded,
}

/// <summary>What a kept version is.</summary>
public enum LibraryKeptVersionKind
{
    /// <summary>
    /// A custom library's file changed outside Scribe after the pre-image check. Scribe's committed content is in place
    /// under <see cref="LibraryKeptVersion.LibraryId"/>, and the outside bytes, unchanged, are a new custom library
    /// under <see cref="LibraryKeptVersion.KeptAsId"/>, off and not sent to AI cleanup.
    /// </summary>
    OutsideVersion,

    /// <summary>
    /// Another app created a file under the id a new library was being saved as (review finding G3). That file stays
    /// under <see cref="LibraryKeptVersion.LibraryId"/>, where its content does not match what the Save accepted, so it
    /// is treated as newly discovered; Scribe's content is a new custom library under
    /// <see cref="LibraryKeptVersion.KeptAsId"/>, off and not sent to AI cleanup.
    /// </summary>
    SavedUnderNewId,

    /// <summary>
    /// A built-in's edits document changed outside Scribe. Scribe's committed document is in place and the other version
    /// was set aside with a time stamp, never read again (<see cref="LibraryKeptVersion.KeptAsId"/> is null).
    /// </summary>
    EditsSetAside,
}

/// <summary>
/// A version of a library that completion or recovery kept rather than overwrote, so nothing written outside Scribe is
/// lost. The shell shows one notice per kept version; the library it describes stays either way.
/// </summary>
/// <param name="LibraryId">The library the Save wrote.</param>
/// <param name="Kind">What was kept, and where.</param>
/// <param name="KeptAsId">The id of the new custom library that holds the kept version, or null when it was set aside.</param>
public sealed record LibraryKeptVersion(string LibraryId, LibraryKeptVersionKind Kind, string? KeptAsId);

/// <summary>The result of completing a Save.</summary>
/// <param name="Status">Where the Save stands.</param>
/// <param name="CommittedGeneration">The stored generation after completion.</param>
/// <param name="FilesAwaitingRelease">Files of the committed generation not in place yet.</param>
/// <param name="KeptVersions">Versions kept rather than overwritten; empty when there were none.</param>
/// <param name="Failure">Why files are not in place, when some are not; the Save stands regardless.</param>
public sealed record LibrarySaveOutcome(
    LibrarySaveStatus Status,
    long CommittedGeneration,
    int FilesAwaitingRelease,
    IReadOnlyList<LibraryKeptVersion> KeptVersions,
    LibraryIoFailure Failure = LibraryIoFailure.None);

/// <summary>What recovery found and did, as counts, plus the versions it kept.</summary>
/// <param name="Completed">Manifests of the committed generation finished and retired.</param>
/// <param name="Discarded">Manifests of a generation that never committed, discarded with their redo images.</param>
/// <param name="SetAside">
/// Manifests quarantined unapplied because the committed generation was lost, was older than them, or their redo image
/// failed its hash; kept with every file they name for the quarantine retention.
/// </param>
/// <param name="FilesAwaitingRelease">Files of the committed generation still not in place.</param>
/// <param name="OrphansRemoved">Staged, backup or redo files no manifest, live or set aside, names, removed.</param>
/// <param name="KeptVersions">Versions kept rather than overwritten while finishing.</param>
/// <param name="Failure">Why files are not in place, when some are not.</param>
public sealed record LibraryRecoveryResult(
    int Completed,
    int Discarded,
    int SetAside,
    int FilesAwaitingRelease,
    int OrphansRemoved,
    IReadOnlyList<LibraryKeptVersion> KeptVersions,
    LibraryIoFailure Failure = LibraryIoFailure.None);
