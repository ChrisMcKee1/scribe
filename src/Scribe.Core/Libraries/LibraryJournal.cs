using Scribe.Core.Infrastructure;

namespace Scribe.Core.Libraries;

/// <summary>What one recovery attempt did (contract 6.6.6 and 6.6.7).</summary>
internal sealed class LibraryRecoveryOutcome
{
    public int Completed { get; set; }

    public int Discarded { get; set; }

    public int SetAside { get; set; }

    public int Held { get; set; }

    public int FilesAwaitingRelease { get; set; }

    public int OrphansRemoved { get; set; }

    public int OrphanBackupsKept { get; set; }

    public LibraryIoFailure Failure { get; set; }

    /// <summary>A manifest of the stored generation still pending: the libraries are unresolved and it is read logically.</summary>
    public LibraryManifest? Unresolved { get; set; }

    /// <summary>The stored generation this attempt ran against (0 when the row is absent or unparsable).</summary>
    public long Generation { get; set; }

    /// <summary>
    /// The stored generation's own manifest, the only one of it, which this attempt found but could not read whole right
    /// now: its content, or a redo image it needs (round 3, A13). It is committed and trusted, so it is neither set aside
    /// nor resumed on that failure, and readers keep its logical result: with its content read it is
    /// <see cref="Unresolved"/>; without, the service applies what it holds of it, or holds back what it cannot vouch
    /// for (<see cref="StoredContentUnavailable"/>). Never a held manifest, which is one to set aside or discard.
    /// </summary>
    public LibraryJournalName? StoredUnreadable { get; set; }

    /// <summary>
    /// What the committed generation (<see cref="Generation"/>) holds cannot be told: its own manifest could not be read
    /// (round 3, A13), or the pending manifests could not be listed so whether one of it is pending is unknown (round 4,
    /// A15), and the service holds none of it. Every library is held back until an attempt lists and reads the journal,
    /// never read from files that may still hold what that generation's Save replaced. Set by the service.
    /// </summary>
    public bool StoredContentUnavailable { get; set; }

    /// <summary>
    /// A listing of pending or set-aside manifests failed, so this attempt could not see every manifest: counted as held,
    /// which fences every commit, and no orphan was removed (review finding A2 of round 2).
    /// </summary>
    public bool InventoryIncomplete { get; set; }

    /// <summary>
    /// The listing of pending manifests failed, so this attempt saw none of them, the committed generation's own included
    /// (round 4, A15). A failed listing of set-aside manifests alone leaves this false: those are never applied, so what
    /// is committed is still known, and only commits and orphan removal wait.
    /// </summary>
    public bool PendingUnlisted { get; set; }

    /// <summary>Whether anything on disk changed, so the service publishes again.</summary>
    public bool ChangedFiles => Completed > 0 || Discarded > 0 || SetAside > 0;

    public bool DidAnything => ChangedFiles || Held > 0 || FilesAwaitingRelease > 0 || OrphansRemoved > 0 || OrphanBackupsKept > 0;

    public void NoteFailure(LibraryIoFailure failure)
    {
        if (Failure == LibraryIoFailure.None && failure != LibraryIoFailure.None)
        {
            Failure = failure;
        }
    }
}

/// <summary>What the redo images of a manifest were found to be.</summary>
internal enum RedoImagesState
{
    /// <summary>Every image the manifest needs is there and holds its S.</summary>
    Intact,

    /// <summary>One is missing or fails its hash: what the manifest would install cannot be trusted.</summary>
    Untrustworthy,

    /// <summary>One could not be read right now (a lock, a denied read): nothing can be concluded yet.</summary>
    Unreadable,
}

/// <summary>What completing one manifest achieved.</summary>
/// <param name="Pending">Operations not done yet.</param>
/// <param name="Failure">Why the first of them is not.</param>
/// <param name="Retired">The manifest was retired: every operation done, and the manifest deleted.</param>
internal readonly record struct LibraryCompletion(int Pending, LibraryIoFailure Failure, bool Retired);

/// <summary>
/// The library journal's files and lifecycle (contract 6.6): manifests and their redo images written durably at prepare,
/// completion and retirement, recovery by the stored generation with the fence around this process's live preparation,
/// the whole-files step before any set-aside or discard, held manifests, quarantine, orphans, and the witness file.
/// </summary>
/// <remarks>
/// Not thread-safe on its own: the library service calls it under its one library lock. It decides nothing from a name
/// that does not parse whole (<see cref="LibraryJournalNames"/>), and never deletes a library file: only a manifest's own
/// journal files (the manifest, its redo folder, its install copies and its backups) ever leave by deletion, and backups
/// only once they hold P or S (rule R3) or once the whole-files step has preserved what they held.
/// </remarks>
internal sealed class LibraryJournal
{
    internal const string OrphansFolderName = "orphans";
    internal const string WitnessFileName = "state.witness";

    private readonly ILibraryFileSystem _files;
    private readonly string _librariesDir;
    private readonly string _journalDir;
    private readonly string _editsDir;
    private readonly string _deletedDir;
    private readonly string _orphansDir;
    private readonly Action<LibraryFileOperation, Exception> _onFailure;

    public LibraryJournal(ILibraryFileSystem files, AppPaths paths, Action<LibraryFileOperation, Exception> onFailure)
    {
        _files = files;
        _librariesDir = paths.LibrariesDir;
        _journalDir = paths.LibraryJournalDir;
        _editsDir = paths.LibraryEditsDir;
        _deletedDir = paths.LibraryDeletedDir;
        _orphansDir = Path.Combine(_journalDir, OrphansFolderName);
        _onFailure = onFailure;
    }

    public string JournalDir => _journalDir;

    public string WitnessPath => Path.Combine(_journalDir, WitnessFileName);

    // --- the witness (contract 6.9) -------------------------------------------------------------------------------

    /// <summary>
    /// Whether the witness is on disk. A check that fails answers yes: a witness only ever makes a missing library row
    /// read as lost, which is the direction AI permission fails closed in.
    /// </summary>
    public bool WitnessExists()
    {
        try
        {
            return _files.Exists(WitnessPath);
        }
        catch (Exception ex)
        {
            _onFailure(LibraryFileOperation.Read, ex);
            return true;
        }
    }

    /// <summary>
    /// Writes and flushes the witness when it is absent: an empty file, created and never deleted, truncated or replaced.
    /// <see cref="LibraryIoFailure.None"/> when it is on disk afterwards.
    /// </summary>
    public LibraryIoFailure EnsureWitness()
    {
        try
        {
            if (_files.Exists(WitnessPath))
            {
                return LibraryIoFailure.None;
            }

            _files.CreateDirectory(_journalDir);
            _files.WriteAllBytesDurably(WitnessPath, []);
            return LibraryIoFailure.None;
        }
        catch (Exception ex) when (LibraryIoFailures.IsAlreadyExists(ex))
        {
            return LibraryIoFailure.None;
        }
        catch (Exception ex)
        {
            _onFailure(LibraryFileOperation.WriteWitness, ex);
            return LibraryIoFailures.Classify(ex);
        }
    }

    // --- writing a manifest at prepare --------------------------------------------------------------------------------

    /// <summary>
    /// Writes every redo image, then the manifest, each durably (a temporary name, flushed, then renamed into place), so a
    /// manifest that exists is complete and names only redo images already on disk. On a failure, deletes whatever it
    /// wrote and returns why.
    /// </summary>
    public LibraryIoFailure Write(LibraryManifest manifest, IReadOnlyDictionary<int, byte[]> redoImages)
    {
        var redoFolder = RedoFolderPath(manifest);
        var temporary = Path.Combine(_journalDir, LibraryJournalNames.ManifestTemp(manifest.Generation, manifest.Id));
        var step = LibraryFileOperation.WriteRedoImage;
        try
        {
            _files.CreateDirectory(_journalDir);
            _files.CreateDirectory(redoFolder);
            foreach (var operation in manifest.Operations)
            {
                if (operation.HasPostImage)
                {
                    _files.WriteAllBytesDurably(
                        Path.Combine(redoFolder, LibraryJournalNames.RedoImage(operation.Number)), redoImages[operation.Number]);
                }
            }

            step = LibraryFileOperation.WriteManifest;
            _files.WriteAllBytesDurably(temporary, manifest.ToJson());
            _files.Move(temporary, ManifestPath(manifest), overwrite: false);
            return LibraryIoFailure.None;
        }
        catch (Exception ex)
        {
            _onFailure(step, ex);
            DeleteQuietly(ManifestPath(manifest));
            DeleteQuietly(temporary);
            DeleteFolderQuietly(redoFolder);
            return LibraryIoFailures.Classify(ex);
        }
    }

    // --- listing and reading ------------------------------------------------------------------------------------------

    /// <summary>The pending manifests on disk, by name (<c>g&lt;G&gt;-&lt;id&gt;.manifest.json</c>).</summary>
    public IReadOnlyList<LibraryJournalName> ListManifests(out LibraryIoFailure failure) =>
        ListJournal(LibraryJournalNameKind.Manifest, "g*" + LibraryJournalNames.ManifestSuffix, out failure);

    /// <summary>The set-aside manifests on disk, by name.</summary>
    public IReadOnlyList<LibraryJournalName> ListSetAside(out LibraryIoFailure failure) =>
        ListJournal(LibraryJournalNameKind.SetAsideManifest, "g*.set-aside.*.json", out failure);

    /// <summary>
    /// A pending manifest's content. A manifest whose content names another generation or id than its file name, or
    /// that cannot be read at all, is <see cref="LibraryManifestReadStatus.Unreadable"/>; a file that cannot be opened
    /// right now returns null with the failure, and is tried again at the next attempt.
    /// </summary>
    public LibraryManifestRead? Read(LibraryJournalName name, out LibraryIoFailure failure)
    {
        failure = LibraryIoFailure.None;
        byte[] bytes;
        try
        {
            bytes = _files.ReadAllBytes(Path.Combine(_journalDir, LibraryJournalNames.Manifest(name.Generation, name.ManifestId)));
        }
        catch (Exception ex)
        {
            _onFailure(LibraryFileOperation.Read, ex);
            failure = LibraryIoFailures.Classify(ex);
            return null;
        }

        var read = LibraryManifest.Parse(bytes);
        if (read.Manifest is { } manifest &&
            (manifest.Generation != name.Generation ||
             !string.Equals(manifest.Id, name.ManifestId, StringComparison.OrdinalIgnoreCase)))
        {
            return new LibraryManifestRead(LibraryManifestReadStatus.Unreadable, null);
        }

        return read;
    }

    /// <summary>
    /// A redo image's bytes when it is on disk and holds S (<see cref="RedoImagesState.Intact"/>); missing or failing S,
    /// which makes the manifest untrustworthy (<see cref="RedoImagesState.Untrustworthy"/>); or not readable right now,
    /// which says nothing about whether it holds S (<see cref="RedoImagesState.Unreadable"/>, round 3, A13).
    /// </summary>
    public RedoImagesState ReadRedo(LibraryManifest manifest, LibraryManifestOperation operation, out byte[]? bytes, out LibraryIoFailure failure)
    {
        bytes = null;
        failure = LibraryIoFailure.None;
        byte[] read;
        try
        {
            read = _files.ReadAllBytes(Path.Combine(RedoFolderPath(manifest), LibraryJournalNames.RedoImage(operation.Number)));
        }
        catch (Exception ex) when (LibraryIoFailures.IsNotFound(ex))
        {
            return RedoImagesState.Untrustworthy;
        }
        catch (Exception ex)
        {
            _onFailure(LibraryFileOperation.Read, ex);
            var classified = LibraryIoFailures.Classify(ex);
            failure = classified == LibraryIoFailure.None ? LibraryIoFailure.Other : classified;
            return RedoImagesState.Unreadable;
        }

        if (LibraryContentHashing.Of(read) != operation.Staged)
        {
            return RedoImagesState.Untrustworthy;
        }

        bytes = read;
        return RedoImagesState.Intact;
    }

    /// <summary>Whether every redo image the manifest needs is on disk and holds its S (rule R2's premise).</summary>
    public RedoImagesState CheckRedoImages(LibraryManifest manifest, out LibraryIoFailure failure)
    {
        failure = LibraryIoFailure.None;
        foreach (var operation in manifest.Operations.Where(operation => operation.HasPostImage))
        {
            byte[] bytes;
            try
            {
                bytes = _files.ReadAllBytes(Path.Combine(RedoFolderPath(manifest), LibraryJournalNames.RedoImage(operation.Number)));
            }
            catch (Exception ex) when (LibraryIoFailures.IsNotFound(ex))
            {
                return RedoImagesState.Untrustworthy;
            }
            catch (Exception ex)
            {
                _onFailure(LibraryFileOperation.Read, ex);
                var classified = LibraryIoFailures.Classify(ex);
                failure = classified == LibraryIoFailure.None ? LibraryIoFailure.Other : classified;
                return RedoImagesState.Unreadable;
            }

            if (LibraryContentHashing.Of(bytes) != operation.Staged)
            {
                return RedoImagesState.Untrustworthy;
            }
        }

        return RedoImagesState.Intact;
    }

    public LibraryInstaller Installer(LibraryManifest manifest, List<LibraryKeptVersion> kept) =>
        new(_files, _librariesDir, _journalDir, manifest, _onFailure, kept);

    // --- completion and retirement ------------------------------------------------------------------------------------

    /// <summary>
    /// Brings every operation to its committed result (each independent of the others, rule R5) and retires the manifest
    /// once all are done: the manifest is deleted first, the retirement point, then its redo folder and spent install copies.
    /// </summary>
    public LibraryCompletion Complete(LibraryManifest manifest, List<LibraryKeptVersion> kept)
    {
        var installer = Installer(manifest, kept);
        var pending = 0;
        var failure = LibraryIoFailure.None;
        foreach (var operation in manifest.Operations)
        {
            var outcome = installer.Resume(operation);
            if (!outcome.Done)
            {
                pending++;
                if (failure == LibraryIoFailure.None)
                {
                    failure = outcome.Failure;
                }
            }
        }

        if (pending > 0)
        {
            return new LibraryCompletion(pending, failure, Retired: false);
        }

        return Retire(manifest, installer)
            ? new LibraryCompletion(0, LibraryIoFailure.None, Retired: true)
            : new LibraryCompletion(0, LibraryIoFailure.Other, Retired: false);
    }

    private bool Retire(LibraryManifest manifest, LibraryInstaller installer)
    {
        if (!TryDelete(ManifestPath(manifest), LibraryFileOperation.RetireManifest))
        {
            return false;
        }

        // A crash from here leaves files no manifest names, which orphan removal takes. An unversioned purge's evidence
        // goes only now (round 3, A12): before this point a retry must still find it.
        foreach (var operation in manifest.Operations)
        {
            installer.DeletePurgeEvidence(operation);
        }

        DeleteFolderQuietly(RedoFolderPath(manifest));
        foreach (var operation in manifest.Operations)
        {
            installer.DeleteSpentInstallCopy(operation);
        }

        return true;
    }

    /// <summary>
    /// Discards a manifest whose generation never committed, once its files are whole (contract 6.6.7): the manifest is
    /// deleted first, then its redo folder, install copies and spare backups. False, with the manifest left pending and
    /// held, when the files cannot yet be made whole.
    /// </summary>
    public bool Discard(LibraryManifest manifest, List<LibraryKeptVersion> kept, out LibraryIoFailure failure)
    {
        var installer = Installer(manifest, kept);
        if (!MakeWhole(manifest, installer, out failure))
        {
            return false;
        }

        if (!TryDelete(ManifestPath(manifest), LibraryFileOperation.RetireManifest))
        {
            failure = LibraryIoFailure.Other;
            return false;
        }

        DeleteFolderQuietly(RedoFolderPath(manifest));
        foreach (var operation in manifest.Operations)
        {
            installer.DeleteJournalFiles(operation);
        }

        return true;
    }

    /// <summary>
    /// Sets a readable manifest aside unapplied, once its files are whole: one rename to its set-aside name, after which
    /// its own journal files stay where they are, quarantined, until the janitor's expiry. False, with the manifest left
    /// pending and held, when the files cannot yet be made whole (review finding G14).
    /// </summary>
    public bool SetAside(LibraryManifest manifest, DateTimeOffset now, List<LibraryKeptVersion> kept, out LibraryIoFailure failure)
    {
        var installer = Installer(manifest, kept);
        if (!MakeWhole(manifest, installer, out failure))
        {
            return false;
        }

        return RenameAside(manifest.Generation, manifest.Id, now, out failure);
    }

    /// <summary>
    /// Sets aside a manifest whose operations cannot be read, which has nothing to make whole by: first every backup whose
    /// name parses to it (a claimed Recently deleted entry included) is moved into <c>journal\orphans\</c> and kept, never
    /// expired; a move that fails holds it.
    /// </summary>
    public bool SetAsideUnreadable(LibraryJournalName name, DateTimeOffset now, out LibraryIoFailure failure)
    {
        failure = LibraryIoFailure.None;
        foreach (var folder in (string[])[_librariesDir, _editsDir, _deletedDir])
        {
            IEnumerable<string> files;
            try
            {
                files = _files.EnumerateFiles(folder, "~g*" + LibraryJournalNames.BackupSuffix).ToList();
            }
            catch (Exception ex)
            {
                _onFailure(LibraryFileOperation.Enumerate, ex);
                failure = LibraryIoFailures.Classify(ex);
                return false;
            }

            foreach (var path in files)
            {
                if (LibraryJournalNames.TryParse(Path.GetFileName(path), out var parsed) &&
                    parsed.Kind == LibraryJournalNameKind.Backup && parsed.BelongsTo(name.Generation, name.ManifestId) &&
                    !MoveIntoOrphans(_files, path, _orphansDir, _onFailure))
                {
                    failure = LibraryIoFailure.Other;
                    return false;
                }
            }
        }

        return RenameAside(name.Generation, name.ManifestId, now, out failure);
    }

    // The whole-files step for every operation, then the check; only a manifest whose check passes may leave.
    private static bool MakeWhole(LibraryManifest manifest, LibraryInstaller installer, out LibraryIoFailure failure)
    {
        failure = LibraryIoFailure.None;
        foreach (var operation in manifest.Operations)
        {
            var outcome = installer.MakeWhole(operation);
            if (!outcome.Done && failure == LibraryIoFailure.None)
            {
                failure = outcome.Failure;
            }
        }

        foreach (var operation in manifest.Operations)
        {
            if (!installer.IsWhole(operation, out var checkFailure))
            {
                if (failure == LibraryIoFailure.None)
                {
                    failure = checkFailure == LibraryIoFailure.None ? LibraryIoFailure.Other : checkFailure;
                }

                return false;
            }
        }

        failure = LibraryIoFailure.None;
        return true;
    }

    private bool RenameAside(long generation, string manifestId, DateTimeOffset now, out LibraryIoFailure failure)
    {
        failure = LibraryIoFailure.None;
        try
        {
            _files.Move(
                Path.Combine(_journalDir, LibraryJournalNames.Manifest(generation, manifestId)),
                Path.Combine(_journalDir, LibraryJournalNames.SetAside(generation, manifestId, now)),
                overwrite: false);
            return true;
        }
        catch (Exception ex)
        {
            _onFailure(LibraryFileOperation.SetAside, ex);
            failure = LibraryIoFailures.Classify(ex);
            return false;
        }
    }

    // --- recovery (contract 6.6.6) ------------------------------------------------------------------------------------

    /// <summary>
    /// Finishes, discards or quarantines every manifest on disk except this process's live preparation, by the stored
    /// generation: one of it is resumed and retired when done (or stays unresolved); one above it never committed and is
    /// discarded; one below it, a duplicate of it, one that cannot be read, one whose redo image fails its hash, and
    /// every manifest while the generation row is lost, are set aside. Every set-aside and discard waits for the
    /// whole-files check; a manifest that fails it is held. Then, outside a session on defaults, orphans are cleared.
    /// </summary>
    public LibraryRecoveryOutcome Recover(
        long storedGeneration, bool generationLost, string? liveManifestId, bool runningOnDefaults, DateTimeOffset now,
        List<LibraryKeptVersion> kept)
    {
        var outcome = new LibraryRecoveryOutcome { Generation = generationLost ? 0 : storedGeneration };
        var names = ListManifests(out var listFailure)
            .Where(name => liveManifestId is null || !string.Equals(name.ManifestId, liveManifestId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        ListSetAside(out var setAsideListFailure);
        if (listFailure != LibraryIoFailure.None || setAsideListFailure != LibraryIoFailure.None)
        {
            // Unresolved storage until both inventories succeed: nothing may commit past a manifest this attempt cannot see.
            outcome.InventoryIncomplete = true;
            outcome.PendingUnlisted = listFailure != LibraryIoFailure.None;
            outcome.Held++;
            outcome.NoteFailure(listFailure);
            outcome.NoteFailure(setAsideListFailure);
        }

        var ofStored = names.Count(name => name.Generation == storedGeneration);
        foreach (var name in names)
        {
            // The committed generation's own manifest: trusted, so a failure to read it now keeps its logical read (A13).
            var committed = !generationLost && name.Generation == storedGeneration && ofStored == 1;
            var read = Read(name, out var readFailure);
            if (read is null)
            {
                // Journal files are Scribe's own, so only a transient failure keeps one from opening: held, retried.
                outcome.Held++;
                outcome.NoteFailure(readFailure);
                if (committed)
                {
                    outcome.StoredUnreadable = name;
                }

                continue;
            }

            if (read.Manifest is not { } manifest)
            {
                if (SetAsideUnreadable(name, now, out var failure))
                {
                    outcome.SetAside++;
                }
                else
                {
                    outcome.Held++;
                    outcome.NoteFailure(failure);
                }

                continue;
            }

            // A redo image that cannot be read right now says nothing about whether it holds S: the manifest is tried again,
            // never set aside for it (only a missing image or one that fails its hash is untrustworthy). The committed
            // generation's own is unresolved and still read logically, from what readers last read of it (round 3,
            // A13); any other is held.
            var redo = CheckRedoImages(manifest, out var redoFailure);
            if (redo == RedoImagesState.Unreadable)
            {
                outcome.NoteFailure(redoFailure);
                if (committed)
                {
                    outcome.Unresolved = manifest;
                    outcome.StoredUnreadable = name;
                    outcome.FilesAwaitingRelease += Math.Max(1, NotDone(manifest));
                }
                else
                {
                    outcome.Held++;
                }

                continue;
            }

            if (!generationLost && manifest.Generation == storedGeneration && ofStored == 1 && redo == RedoImagesState.Intact)
            {
                var completion = Complete(manifest, kept);
                if (completion.Retired)
                {
                    outcome.Completed++;
                }
                else
                {
                    outcome.Unresolved = manifest;
                    outcome.FilesAwaitingRelease += Math.Max(1, completion.Pending);
                    outcome.NoteFailure(completion.Failure);
                }

                continue;
            }

            if (!generationLost && manifest.Generation > storedGeneration)
            {
                if (Discard(manifest, kept, out var discardFailure))
                {
                    outcome.Discarded++;
                }
                else
                {
                    outcome.Held++;
                    outcome.NoteFailure(discardFailure);
                }

                continue;
            }

            if (SetAside(manifest, now, kept, out var setAsideFailure))
            {
                outcome.SetAside++;
            }
            else
            {
                outcome.Held++;
                outcome.NoteFailure(setAsideFailure);
            }
        }

        if (!runningOnDefaults && !outcome.InventoryIncomplete)
        {
            RemoveOrphans(liveManifestId, outcome);
        }

        return outcome;
    }

    // Install copies, redo folders and temporary manifests whose manifest no live, pending or set-aside manifest is, are
    // removed; a backup of that kind is kept in journal\orphans\, because without a manifest nothing tells a spare copy
    // from someone's only copy (decision 31). Names that do not parse are not the journal's and stay where they are. Both
    // inventories are taken again, after this attempt's changes, and a failure of either removes nothing at all.
    private void RemoveOrphans(string? liveManifestId, LibraryRecoveryOutcome outcome)
    {
        var manifests = ListManifests(out var manifestFailure);
        var setAside = ListSetAside(out var setAsideFailure);
        if (manifestFailure != LibraryIoFailure.None || setAsideFailure != LibraryIoFailure.None)
        {
            outcome.NoteFailure(manifestFailure);
            outcome.NoteFailure(setAsideFailure);
            return;
        }

        var known = new HashSet<(long, string)>();
        foreach (var name in manifests.Concat(setAside))
        {
            known.Add((name.Generation, name.ManifestId.ToLowerInvariant()));
        }

        bool Known(LibraryJournalName name) =>
            known.Contains((name.Generation, name.ManifestId.ToLowerInvariant())) ||
            (liveManifestId is not null && string.Equals(name.ManifestId, liveManifestId, StringComparison.OrdinalIgnoreCase));

        try
        {
            foreach (var path in _files.EnumerateFiles(_journalDir, "g*" + LibraryJournalNames.ManifestTempSuffix).ToList())
            {
                if (LibraryJournalNames.TryParse(Path.GetFileName(path), out var name) &&
                    name.Kind == LibraryJournalNameKind.ManifestTemp && !Known(name) &&
                    TryDelete(path, LibraryFileOperation.RemoveOrphan))
                {
                    outcome.OrphansRemoved++;
                }
            }

            foreach (var path in _files.EnumerateDirectories(_journalDir).ToList())
            {
                if (LibraryJournalNames.TryParse(Path.GetFileName(path), out var name) &&
                    name.Kind == LibraryJournalNameKind.RedoFolder && !Known(name))
                {
                    try
                    {
                        _files.DeleteDirectory(path);
                        outcome.OrphansRemoved++;
                    }
                    catch (Exception ex)
                    {
                        _onFailure(LibraryFileOperation.RemoveOrphan, ex);
                    }
                }
            }

            // deleted\ holds only claims of Recently deleted entries: an orphan one is kept like any backup, never deleted.
            foreach (var folder in (string[])[_librariesDir, _editsDir, _deletedDir])
            {
                foreach (var path in _files.EnumerateFiles(folder, "~g*").ToList())
                {
                    if (!LibraryJournalNames.TryParse(Path.GetFileName(path), out var name) || Known(name))
                    {
                        continue;
                    }

                    if (name.Kind == LibraryJournalNameKind.InstallCopy && TryDelete(path, LibraryFileOperation.RemoveOrphan))
                    {
                        outcome.OrphansRemoved++;
                    }
                    else if (name.Kind == LibraryJournalNameKind.Backup && MoveIntoOrphans(_files, path, _orphansDir, _onFailure))
                    {
                        outcome.OrphanBackupsKept++;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _onFailure(LibraryFileOperation.Enumerate, ex);
        }
    }

    /// <summary>
    /// Moves a backup into <c>journal\orphans\</c> under its own name (or, when that is taken by other bytes, the next
    /// free retry index of its series there); a copy already there with the same bytes makes it spare.
    /// </summary>
    internal static bool MoveIntoOrphans(
        ILibraryFileSystem files, string path, string orphansDir, Action<LibraryFileOperation, Exception> onFailure)
    {
        try
        {
            files.CreateDirectory(orphansDir);
            var name = Path.GetFileName(path);
            if (!LibraryJournalNames.TryParse(name, out var parsed) || parsed.Kind != LibraryJournalNameKind.Backup)
            {
                return false;
            }

            var bytes = files.ReadAllBytes(path);
            var hash = LibraryContentHashing.Of(bytes);
            for (var index = parsed.BackupIndex; index < parsed.BackupIndex + 1_000; index++)
            {
                var destination = Path.Combine(
                    orphansDir, LibraryJournalNames.Backup(parsed.Generation, parsed.ManifestId, parsed.Operation, index));
                if (files.Exists(destination))
                {
                    if (LibraryContentHashing.Of(files.ReadAllBytes(destination)) == hash)
                    {
                        files.Delete(path);
                        return true;
                    }

                    continue;
                }

                try
                {
                    files.Move(path, destination, overwrite: false);
                    return true;
                }
                catch (Exception ex) when (LibraryIoFailures.IsAlreadyExists(ex))
                {
                    index--;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            onFailure(LibraryFileOperation.RemoveOrphan, ex);
            return false;
        }
    }

    // --- quarantine expiry (contract 6.6.7) ---------------------------------------------------------------------------

    /// <summary>
    /// Removes a set-aside manifest's own journal files (its redo folder, install copies and backups beside the targets,
    /// all spare by then), then the manifest. Never a library file: targets, kept versions, set-aside edits documents,
    /// previous copies and Recently deleted entries follow their own rules (review finding G11), and orphan backups in
    /// <c>journal\orphans\</c> are kept for good.
    /// </summary>
    public bool ExpireSetAside(LibraryJournalName name)
    {
        var removed = true;
        try
        {
            _files.DeleteDirectory(Path.Combine(_journalDir, LibraryJournalNames.RedoFolder(name.Generation, name.ManifestId)));
        }
        catch (Exception ex)
        {
            _onFailure(LibraryFileOperation.DeleteSpent, ex);
            removed = false;
        }

        foreach (var folder in (string[])[_librariesDir, _editsDir])
        {
            try
            {
                foreach (var path in _files.EnumerateFiles(folder, "~g*").ToList())
                {
                    if (LibraryJournalNames.TryParse(Path.GetFileName(path), out var parsed) &&
                        parsed.Kind is LibraryJournalNameKind.InstallCopy or LibraryJournalNameKind.Backup &&
                        parsed.BelongsTo(name.Generation, name.ManifestId))
                    {
                        removed &= TryDelete(path, LibraryFileOperation.DeleteSpent);
                    }
                }
            }
            catch (Exception ex)
            {
                _onFailure(LibraryFileOperation.Enumerate, ex);
                removed = false;
            }
        }

        // The manifest goes last, so an interrupted expiry is finished by the next one.
        return removed && TryDelete(
            Path.Combine(_journalDir, LibraryJournalNames.SetAside(name.Generation, name.ManifestId, name.Stamp)),
            LibraryFileOperation.RetireManifest);
    }

    // --- helpers ------------------------------------------------------------------------------------------------------

    // The operations whose done-condition does not hold yet, observed without changing anything.
    private int NotDone(LibraryManifest manifest)
    {
        var installer = Installer(manifest, []);
        return manifest.Operations.Count(operation => !installer.IsDone(operation));
    }

    public string ManifestPath(LibraryManifest manifest) =>
        Path.Combine(_journalDir, LibraryJournalNames.Manifest(manifest.Generation, manifest.Id));

    private string RedoFolderPath(LibraryManifest manifest) =>
        Path.Combine(_journalDir, LibraryJournalNames.RedoFolder(manifest.Generation, manifest.Id));

    private IReadOnlyList<LibraryJournalName> ListJournal(LibraryJournalNameKind kind, string pattern, out LibraryIoFailure failure)
    {
        failure = LibraryIoFailure.None;
        var names = new List<LibraryJournalName>();
        try
        {
            foreach (var path in _files.EnumerateFiles(_journalDir, pattern))
            {
                if (LibraryJournalNames.TryParse(Path.GetFileName(path), out var name) && name.Kind == kind)
                {
                    names.Add(name);
                }
            }
        }
        catch (Exception ex)
        {
            // An inventory is complete or it is nothing: a partial one would make a live manifest's files look orphaned.
            _onFailure(LibraryFileOperation.Enumerate, ex);
            var classified = LibraryIoFailures.Classify(ex);
            failure = classified == LibraryIoFailure.None ? LibraryIoFailure.Other : classified;
            return [];
        }

        return [.. names.OrderBy(name => name.Generation).ThenBy(name => name.ManifestId, StringComparer.OrdinalIgnoreCase)];
    }

    private bool TryDelete(string path, LibraryFileOperation operation)
    {
        try
        {
            _files.Delete(path);
            return true;
        }
        catch (Exception ex) when (LibraryIoFailures.IsNotFound(ex))
        {
            return true;
        }
        catch (Exception ex)
        {
            _onFailure(operation, ex);
            return false;
        }
    }

    private void DeleteQuietly(string path)
    {
        try
        {
            _files.Delete(path);
        }
        catch (Exception)
        {
            // Left for orphan removal: a name no manifest has.
        }
    }

    private void DeleteFolderQuietly(string path)
    {
        try
        {
            _files.DeleteDirectory(path);
        }
        catch (Exception ex)
        {
            _onFailure(LibraryFileOperation.DeleteSpent, ex);
        }
    }
}
