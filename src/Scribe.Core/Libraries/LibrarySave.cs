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
/// nothing, when the stored generation is not <see cref="ExpectedGeneration"/>.
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
    /// leave the document's list as the caller's document has it.
    /// </summary>
    public IReadOnlyList<string>? EnabledLibraryIds { get; }

    /// <summary>The auxiliary rows to write or delete, never the generation row, which <see cref="Generation"/> sets.</summary>
    public IReadOnlyList<LibrarySettingValue> Values { get; }
}

/// <summary>Counts for the Save log line and the notice; never names.</summary>
public readonly record struct LibraryChangeCounts(int Created, int Updated, int Deleted, int Restored, int Terms);

/// <summary>
/// A Save whose files are staged and whose manifest is written (steps 1 and 2 of the Save commit), waiting for the
/// settings transaction. Hand <see cref="Payload"/> to <c>SaveBundle</c>, then pass this to
/// <c>ILibraryCatalogStore.CompleteSave</c> whether or not <c>SaveBundle</c> threw: completion reads the committed
/// generation and finishes or discards accordingly.
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
    AccessDenied,
    DiskFull,
    SharingViolation,
    PathTooLong,
    Other,
}

/// <summary>What <c>ILibraryCatalogStore.PrepareSave</c> did.</summary>
public enum LibraryPrepareStatus
{
    /// <summary>Files staged and the manifest written; commit <see cref="PreparedLibrarySave.Payload"/> next.</summary>
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

    /// <summary>Another Save is between prepare and completion; this one was not started.</summary>
    Busy,

    /// <summary>Staging failed (<see cref="LibraryPrepareResult.Failure"/>); staged files were removed and nothing changed.</summary>
    Failed,
}

/// <summary>The result of preparing a Save.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Save">The prepared Save when <paramref name="Status"/> is <see cref="LibraryPrepareStatus.Prepared"/>.</param>
/// <param name="OutsideEditIds">For <see cref="LibraryPrepareStatus.OutsideEdit"/>: the libraries changed outside Scribe. Empty otherwise.</param>
/// <param name="Failure">For <see cref="LibraryPrepareStatus.Failed"/>: why.</param>
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
    /// Committed; some files wait for another app to release their target, readers use the staged copies, and recovery
    /// finishes later. The Save stands and is never reported as unsaved.
    /// </summary>
    AppliedAwaitingRelease,

    /// <summary>The settings transaction did not commit; the staged files were discarded and nothing changed.</summary>
    NotCommitted,

    /// <summary>
    /// The stored generation is neither the base nor this Save's: something else committed. The staged files were
    /// discarded; reload.
    /// </summary>
    Superseded,
}

/// <summary>The result of completing a Save.</summary>
/// <param name="Status">Where the Save stands.</param>
/// <param name="CommittedGeneration">The stored generation after completion.</param>
/// <param name="FilesAwaitingRelease">Files still waiting for another app.</param>
/// <param name="OutsideVersionIds">
/// Custom libraries created from outside versions found when replacing (a file changed after it was checked), each
/// Off with its own notice. Empty when there were none.
/// </param>
/// <param name="Failure">A file failure met while finishing, when there was one; the Save stands regardless.</param>
public sealed record LibrarySaveOutcome(
    LibrarySaveStatus Status,
    long CommittedGeneration,
    int FilesAwaitingRelease,
    IReadOnlyList<string> OutsideVersionIds,
    LibraryIoFailure Failure = LibraryIoFailure.None);

/// <summary>What recovery found and did, as counts.</summary>
/// <param name="Completed">Manifests of the committed generation finished.</param>
/// <param name="Discarded">Manifests of a generation that never committed, discarded with their staged files.</param>
/// <param name="SetAside">Manifests kept unapplied because the committed generation was lost or older than them.</param>
/// <param name="FilesAwaitingRelease">Files still waiting for another app.</param>
/// <param name="OutsideVersionsKept">Outside versions kept as new custom libraries.</param>
/// <param name="OrphansRemoved">Staged or backup files no manifest referred to, removed.</param>
public sealed record LibraryRecoveryResult(
    int Completed,
    int Discarded,
    int SetAside,
    int FilesAwaitingRelease,
    int OutsideVersionsKept,
    int OrphansRemoved);
