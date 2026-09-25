namespace Scribe.Core.Libraries;

/// <summary>What one attempt at bringing an operation to its committed result came to.</summary>
/// <param name="Done">The operation's done-condition holds (rule R4).</param>
/// <param name="Failure">Why it did not get there, when it did not: a deferral, never an exception.</param>
internal readonly record struct LibraryOperationOutcome(bool Done, LibraryIoFailure Failure)
{
    public static LibraryOperationOutcome Completed { get; } = new(true, LibraryIoFailure.None);

    public static LibraryOperationOutcome Deferred(LibraryIoFailure failure) =>
        new(false, failure == LibraryIoFailure.None ? LibraryIoFailure.Other : failure);
}

/// <summary>
/// The journal's procedures for one manifest, written once over <see cref="ILibraryFileSystem"/> (contract 6.6.3 and
/// 6.6.4): the idempotent resume of every operation from whatever state the files are in, the native and the checked
/// replace, the resolution of backups (rule R3), the one preservation rule (R7), and the whole-files step and check that
/// a set-aside or a discard waits for (6.6.7).
/// </summary>
/// <remarks>
/// <para>
/// Every step observes the files again before it acts, so a crash, a retry or another app touching a file between two
/// steps only changes which row of the resume table the next observation lands on. Nothing is overwritten except
/// through <see cref="ILibraryFileSystem.Replace"/>, which moves the replaced bytes into a backup, and the single-slot
/// previous copy of an edits document, which only ever receives the pre-image (rule R1). A target's bytes leave only by a
/// rename into a backup, a preservation destination or Recently deleted, or by deletion when they equal P or S.
/// </para>
/// <para>
/// Failures are deferrals with a <see cref="LibraryIoFailure"/>, reported through the callback the journal logs with
/// (<c>Library file operation {Operation} failed: {Failure}</c>); nothing here throws for a file problem.
/// </para>
/// </remarks>
internal sealed class LibraryInstaller
{
    // Steps one resume may take before it defers: far above what any row of the table needs, so only a file that keeps
    // changing under the journal reaches it.
    private const int MaxSteps = 32;

    // How many times a checked install may find the target recreated by another app before it defers (contract 6.6.3).
    private const int MaxReappearances = 3;

    // Candidates of one preservation series probed before giving up; each probe is one existence check.
    private const int MaxCandidates = 1_000;

    private const string PreviousSuffix = ".previous.json";
    private const string SetAsideSuffix = ".backup.json";

    private readonly ILibraryFileSystem _files;
    private readonly string _librariesDir;
    private readonly string _deletedDir;
    private readonly string _orphansDir;
    private readonly string _redoFolder;
    private readonly LibraryManifest _manifest;
    private readonly Action<LibraryFileOperation, Exception> _onFailure;
    private readonly List<LibraryKeptVersion> _kept;

    public LibraryInstaller(
        ILibraryFileSystem files,
        string librariesDir,
        string journalDir,
        LibraryManifest manifest,
        Action<LibraryFileOperation, Exception> onFailure,
        List<LibraryKeptVersion> kept)
    {
        _files = files;
        _librariesDir = librariesDir;
        _deletedDir = Path.Combine(librariesDir, Infrastructure.AppPaths.LibraryDeletedFolderName);
        _orphansDir = Path.Combine(journalDir, LibraryJournal.OrphansFolderName);
        _redoFolder = Path.Combine(journalDir, LibraryJournalNames.RedoFolder(manifest.Generation, manifest.Id));
        _manifest = manifest;
        _onFailure = onFailure;
        _kept = kept;
    }

    /// <summary>The redo image of an operation with committed bytes.</summary>
    public string RedoPath(LibraryManifestOperation operation) =>
        Path.Combine(_redoFolder, LibraryJournalNames.RedoImage(operation.Number));

    public string InstallCopyPath(LibraryManifestOperation operation) =>
        Path.Combine(FolderOf(operation), LibraryJournalNames.InstallCopy(_manifest.Generation, _manifest.Id, operation.Number));

    /// <summary>Brings one operation to its committed result, or as far as the files allow (contract 6.6.4).</summary>
    public LibraryOperationOutcome Resume(LibraryManifestOperation operation) => operation.Kind switch
    {
        LibraryOperationKind.Write => ResumeWrite(operation),
        LibraryOperationKind.Create => ResumeCreate(operation),
        LibraryOperationKind.Remove => ResumeRemove(operation),
        LibraryOperationKind.Delete => ResumeDelete(operation),
        LibraryOperationKind.Restore => ResumeRestore(operation),
        LibraryOperationKind.Purge => ResumePurge(operation),
        _ => LibraryOperationOutcome.Deferred(LibraryIoFailure.Corrupt),
    };

    /// <summary>
    /// Whether the operation's done-condition holds, observed without changing anything: what the one read path uses to
    /// say whether a pending operation's target is in place yet.
    /// </summary>
    public bool IsDone(LibraryManifestOperation operation)
    {
        switch (operation.Kind)
        {
            case LibraryOperationKind.Write:
            case LibraryOperationKind.Create:
            {
                var target = Observe(Absolute(operation.Target), LibraryFileOperation.Read, report: false);
                return target is { Failed: false, Present: true } && target.Hash == operation.Staged &&
                    TryListSeries(operation, out var series, out _) && series.Count == 0;
            }

            case LibraryOperationKind.Restore:
            {
                var target = Observe(Absolute(operation.Target), LibraryFileOperation.Read, report: false);
                var entry = Observe(Absolute(operation.From!), LibraryFileOperation.Read, report: false);
                return target is { Failed: false, Present: true } && target.Hash == operation.Staged &&
                    entry is { Failed: false } && (!entry.Present || entry.Hash != operation.FromHash) &&
                    TryListSeries(operation, _deletedDir, out var claims, out _) && claims.Count == 0;
            }

            case LibraryOperationKind.Remove:
                return !SafeExists(Absolute(operation.Target)) && TryListSeries(operation, out var taken, out _) && taken.Count == 0;
            case LibraryOperationKind.Delete:
                return !SafeExists(Absolute(operation.Target)) || SafeExists(Absolute(operation.To!));
            case LibraryOperationKind.Purge:
            {
                if (!TryListSeries(operation, _deletedDir, out var claims, out _))
                {
                    return false;
                }

                if (operation.PreImage is { } expected)
                {
                    var entry = Observe(Absolute(operation.Target), LibraryFileOperation.Read, report: false);
                    return entry is { Failed: false } && (!entry.Present || entry.Hash != expected) && claims.Count == 0;
                }

                // Unversioned: done once the one claim that took the entry holds it (kept until retirement, round 3,
                // A12), or while there was never an entry to take. A file written to the name after the claim is not its.
                if (claims.Count != 0)
                {
                    return claims.Count == 1;
                }

                return !SafeExists(Absolute(operation.Target), out var existsFailure) && existsFailure == LibraryIoFailure.None;
            }

            default:
                return false;
        }
    }

    /// <summary>
    /// The whole-files step for one operation of a manifest about to be set aside or discarded (contract 6.6.7): a target
    /// an interrupted install left absent gets back the bytes it last held, the largest suffix of its backup series; and
    /// every backup holding bytes other than the operation's P and S is preserved at a preservation destination and
    /// reported as a kept version, because the quarantine's expiry must never be what deletes someone's only copy.
    /// </summary>
    public LibraryOperationOutcome MakeWhole(LibraryManifestOperation operation)
    {
        if (operation.Kind is LibraryOperationKind.Restore or LibraryOperationKind.Purge)
        {
            return ReturnEntryClaims(operation);
        }

        for (var step = 0; step < MaxSteps; step++)
        {
            if (!TryListSeries(operation, out var series, out var failure))
            {
                return LibraryOperationOutcome.Deferred(failure);
            }

            if (series.Count == 0)
            {
                return LibraryOperationOutcome.Completed;
            }

            var target = Absolute(operation.Target);
            if (!SafeExists(target, out var existsFailure))
            {
                if (existsFailure != LibraryIoFailure.None)
                {
                    return LibraryOperationOutcome.Deferred(existsFailure);
                }

                // The largest suffix is the version moved aside last: the bytes the target held when the install stopped.
                var moved = TryMove(series[^1].Path, target, overwrite: false, LibraryFileOperation.CheckedReplace, out var moveFailure);
                if (moved is MoveResult.Failed)
                {
                    return LibraryOperationOutcome.Deferred(moveFailure);
                }

                continue;
            }

            foreach (var (_, path) in series)
            {
                var backup = Observe(path, LibraryFileOperation.Read);
                if (backup.Failed)
                {
                    return LibraryOperationOutcome.Deferred(backup.Failure);
                }

                if (!backup.Present || IsOwnCopy(operation, backup.Hash) || FoundAtPreservation(operation, backup.Hash))
                {
                    continue;
                }

                var preserved = PreserveOutsideVersion(operation, backup, path);
                if (!preserved.Done)
                {
                    return preserved;
                }
            }

            return LibraryOperationOutcome.Completed;
        }

        return LibraryOperationOutcome.Deferred(LibraryIoFailure.Other);
    }

    /// <summary>
    /// The check a set-aside or a discard waits for (contract 6.6.7, review finding G14): the operation's backup series
    /// is empty or its target is present, and every backup holds the operation's P or S or bytes found at a
    /// preservation destination. Observes only.
    /// </summary>
    public bool IsWhole(LibraryManifestOperation operation, out LibraryIoFailure failure)
    {
        if (operation.Kind is LibraryOperationKind.Restore or LibraryOperationKind.Purge)
        {
            // A Recently deleted entry the journal took and has not judged yet goes back before anything leaves.
            if (!TryListSeries(operation, _deletedDir, out var claims, out failure))
            {
                return false;
            }

            failure = claims.Count == 0 ? LibraryIoFailure.None : LibraryIoFailure.Other;
            return claims.Count == 0;
        }

        if (!TryListSeries(operation, out var series, out failure))
        {
            return false;
        }

        if (series.Count == 0)
        {
            return true;
        }

        if (!SafeExists(Absolute(operation.Target), out failure))
        {
            failure = failure == LibraryIoFailure.None ? LibraryIoFailure.Other : failure;
            return false;
        }

        foreach (var (_, path) in series)
        {
            var backup = Observe(path, LibraryFileOperation.Read);
            if (backup.Failed)
            {
                failure = backup.Failure;
                return false;
            }

            if (backup.Present && !IsOwnCopy(operation, backup.Hash) && !FoundAtPreservation(operation, backup.Hash))
            {
                failure = LibraryIoFailure.Other;
                return false;
            }
        }

        failure = LibraryIoFailure.None;
        return true;
    }

    /// <summary>Deletes an operation's install copy and every backup of its series, for a discard whose files are whole.</summary>
    public bool DeleteJournalFiles(LibraryManifestOperation operation)
    {
        var deleted = TryDelete(InstallCopyPath(operation), LibraryFileOperation.DeleteSpent);
        if (operation.Kind is LibraryOperationKind.Restore or LibraryOperationKind.Purge)
        {
            // Their claims hold Recently deleted entries, which only judging or returning them ever lets go.
            return deleted;
        }

        if (TryListSeries(operation, out var series, out _))
        {
            foreach (var (_, path) in series)
            {
                deleted &= TryDelete(path, LibraryFileOperation.DeleteSpent);
            }
        }
        else
        {
            deleted = false;
        }

        return deleted;
    }

    /// <summary>Deletes a spent install copy; a failure leaves an orphan that recovery removes later.</summary>
    public void DeleteSpentInstallCopy(LibraryManifestOperation operation) =>
        TryDelete(InstallCopyPath(operation), LibraryFileOperation.DeleteSpent);

    /// <summary>
    /// At retirement, after the manifest is gone: deletes the entry an unversioned purge took, which its claim held as
    /// the purge's evidence until now (round 3, A12). A failure, or a crash before it, leaves a claim no manifest names,
    /// which orphan removal keeps among the orphan backups, as it keeps every backup it cannot judge.
    /// </summary>
    public void DeletePurgeEvidence(LibraryManifestOperation operation)
    {
        if (operation.Kind == LibraryOperationKind.Purge && operation.PreImage is null &&
            TryListSeries(operation, _deletedDir, out var claims, out _) && claims.Count > 0)
        {
            TryDelete(claims[0].Path, LibraryFileOperation.Purge);
        }
    }

    // --- write: W1 to W7 ---------------------------------------------------------------------------------------------

    private LibraryOperationOutcome ResumeWrite(LibraryManifestOperation operation)
    {
        var target = Absolute(operation.Target);
        var nativeRefused = false;
        var reappearances = 0;
        for (var step = 0; step < MaxSteps; step++)
        {
            var observed = Observe(target, LibraryFileOperation.Read);
            if (observed.Failed)
            {
                return LibraryOperationOutcome.Deferred(observed.Failure);
            }

            if (!TryListSeries(operation, out var series, out var listFailure))
            {
                return LibraryOperationOutcome.Deferred(listFailure);
            }

            if (observed.Present && observed.Hash == operation.Staged)
            {
                // W1, W2: in place; every backup of the series is resolved before the operation counts as done.
                var resolved = ResolveBackups(operation, series);
                if (!resolved.Done)
                {
                    return resolved;
                }

                DeleteSpentInstallCopy(operation);
                return LibraryOperationOutcome.Completed;
            }

            if (observed.Present)
            {
                // W3 (P) or W4 (another app's bytes): earlier backups are resolved first, because one may hold the only
                // copy of an outside version, and the native replace would write over a backup name that exists.
                if (series.Count > 0)
                {
                    var resolved = ResolveBackups(operation, series);
                    if (!resolved.Done)
                    {
                        return resolved;
                    }

                    continue;
                }

                var copy = EnsureInstallCopy(operation);
                if (!copy.Done)
                {
                    return copy;
                }

                var backup = BackupPath(operation, 1);
                if (!nativeRefused)
                {
                    try
                    {
                        _files.Replace(InstallCopyPath(operation), target, backup);
                        continue;
                    }
                    catch (Exception ex) when (LibraryIoFailures.IsSharingViolation(ex))
                    {
                        _onFailure(LibraryFileOperation.Replace, ex);
                        return LibraryOperationOutcome.Deferred(LibraryIoFailure.SharingViolation);
                    }
                    catch (Exception ex) when (LibraryIoFailures.IsReplacementLeftBehind(ex))
                    {
                        // 1177: the target is now the backup and the install copy is still in place (W5).
                        _onFailure(LibraryFileOperation.Replace, ex);
                        continue;
                    }
                    catch (Exception ex)
                    {
                        // Refused (the Store package's redirected folder is the case this exists for): the checked
                        // replace takes over, and the files are observed again before it does anything.
                        _onFailure(LibraryFileOperation.Replace, ex);
                        nativeRefused = true;
                        continue;
                    }
                }

                // Checked replace, step 2: one rename takes exactly the bytes the target holds at that instant.
                var renamed = TryMove(target, backup, overwrite: false, LibraryFileOperation.CheckedReplace, out var renameFailure);
                if (renamed is MoveResult.Failed)
                {
                    return LibraryOperationOutcome.Deferred(renameFailure);
                }

                continue;
            }

            // W5, W6, W7: the target is absent (moved aside by step 2 or by 1177, or deleted outside Scribe).
            var install = EnsureInstallCopy(operation);
            if (!install.Done)
            {
                return install;
            }

            var installed = TryMove(InstallCopyPath(operation), target, overwrite: false, LibraryFileOperation.Install, out var installFailure);
            switch (installed)
            {
                case MoveResult.Failed:
                    return LibraryOperationOutcome.Deferred(installFailure);
                case MoveResult.DestinationExists when ++reappearances >= MaxReappearances:
                    // Another app keeps recreating the target: every version it made is in a backup or preserved, and
                    // the operation waits for the next attempt rather than race it (contract 6.6.3, step 4).
                    return LibraryOperationOutcome.Deferred(LibraryIoFailure.Other);
            }
        }

        return LibraryOperationOutcome.Deferred(LibraryIoFailure.Other);
    }

    // --- create: C1 to C3 --------------------------------------------------------------------------------------------

    private LibraryOperationOutcome ResumeCreate(LibraryManifestOperation operation)
    {
        var target = Absolute(operation.Target);
        for (var step = 0; step < MaxSteps; step++)
        {
            var observed = Observe(target, LibraryFileOperation.Read);
            if (observed.Failed)
            {
                return LibraryOperationOutcome.Deferred(observed.Failure);
            }

            if (!TryListSeries(operation, out var series, out var listFailure))
            {
                return LibraryOperationOutcome.Deferred(listFailure);
            }

            if (observed.Present && observed.Hash == operation.Staged)
            {
                // In place; every version this operation took aside is judged before it counts as done.
                if (series.Count > 0)
                {
                    var resolved = ResolveBackups(operation, series);
                    if (!resolved.Done)
                    {
                        return resolved;
                    }

                    continue;
                }

                DeleteSpentInstallCopy(operation);
                return LibraryOperationOutcome.Completed;
            }

            if (!observed.Present)
            {
                var copy = EnsureInstallCopy(operation);
                if (!copy.Done)
                {
                    return copy;
                }

                if (TryMove(InstallCopyPath(operation), target, overwrite: false, LibraryFileOperation.Install, out var failure) is MoveResult.Failed)
                {
                    return LibraryOperationOutcome.Deferred(failure);
                }

                continue;
            }

            // C3: another app created the target after prepare (review finding G3). An edits document is taken, by one
            // rename that never overwrites, into this operation's backups and judged there (rule R3), so only a copy the
            // journal owns is ever deleted (round 2, A4); a custom file is never touched at all.
            if (operation.IsEditsTarget)
            {
                var claimed = Claim(operation, target, FolderOf(operation), series, LibraryFileOperation.CheckedReplace);
                if (!claimed.Done)
                {
                    return claimed;
                }

                continue;
            }

            var saved = PreserveOwnContent(operation);
            if (!saved.Outcome.Done)
            {
                return saved.Outcome;
            }

            Report(operation, LibraryKeptVersionKind.SavedUnderNewId, saved.KeptPath);
            DeleteSpentInstallCopy(operation);
            return LibraryOperationOutcome.Completed;
        }

        return LibraryOperationOutcome.Deferred(LibraryIoFailure.Other);
    }

    // --- remove: M1 to M3 --------------------------------------------------------------------------------------------

    // Whatever the target holds is first taken, by a rename that never overwrites, into the operation's own backups; only
    // there, owned, is it judged (rule R3): P becomes the previous copy or is set aside, anything else is preserved. A
    // version written to the target meanwhile is taken and judged the same way, so none is ever deleted (round 2, A4).
    private LibraryOperationOutcome ResumeRemove(LibraryManifestOperation operation)
    {
        var target = Absolute(operation.Target);
        var reappearances = 0;
        for (var step = 0; step < MaxSteps; step++)
        {
            var observed = Observe(target, LibraryFileOperation.Read);
            if (observed.Failed)
            {
                return LibraryOperationOutcome.Deferred(observed.Failure);
            }

            if (!TryListSeries(operation, out var series, out var listFailure))
            {
                return LibraryOperationOutcome.Deferred(listFailure);
            }

            if (observed.Present)
            {
                if (series.Count > 0 && ++reappearances > MaxReappearances)
                {
                    // Another app keeps recreating the document: every version it wrote is owned or preserved, and the
                    // operation waits for the next attempt rather than race it.
                    return LibraryOperationOutcome.Deferred(LibraryIoFailure.Other);
                }

                var claimed = Claim(operation, target, FolderOf(operation), series, LibraryFileOperation.CheckedReplace);
                if (!claimed.Done)
                {
                    return claimed;
                }

                continue;
            }

            if (series.Count == 0)
            {
                return LibraryOperationOutcome.Completed;
            }

            var resolved = ResolveBackups(operation, series);
            if (!resolved.Done)
            {
                return resolved;
            }
        }

        return LibraryOperationOutcome.Deferred(LibraryIoFailure.Other);
    }

    // --- delete: D1 to D4 --------------------------------------------------------------------------------------------

    private LibraryOperationOutcome ResumeDelete(LibraryManifestOperation operation)
    {
        var target = Absolute(operation.Target);
        var destination = Absolute(operation.To!);
        for (var step = 0; step < MaxSteps; step++)
        {
            if (!SafeExists(target, out var targetFailure))
            {
                return targetFailure == LibraryIoFailure.None
                    ? LibraryOperationOutcome.Completed // D1, D3
                    : LibraryOperationOutcome.Deferred(targetFailure);
            }

            if (SafeExists(destination, out var destinationFailure))
            {
                return LibraryOperationOutcome.Completed; // D4: the target was recreated after the move; it is a new file
            }

            if (destinationFailure != LibraryIoFailure.None)
            {
                return LibraryOperationOutcome.Deferred(destinationFailure);
            }

            if (!TryCreateDirectory(Path.GetDirectoryName(destination)!, out var folderFailure))
            {
                return LibraryOperationOutcome.Deferred(folderFailure);
            }

            // D2: whatever the target holds goes, so Recently deleted keeps an outside version too.
            if (TryMove(target, destination, overwrite: false, LibraryFileOperation.MoveToRecentlyDeleted, out var failure) is MoveResult.Failed)
            {
                return LibraryOperationOutcome.Deferred(failure);
            }
        }

        return LibraryOperationOutcome.Deferred(LibraryIoFailure.Other);
    }

    // --- restore: S1 to S5 -------------------------------------------------------------------------------------------

    private LibraryOperationOutcome ResumeRestore(LibraryManifestOperation operation)
    {
        var target = Absolute(operation.Target);
        var entryPath = Absolute(operation.From!);
        for (var step = 0; step < MaxSteps; step++)
        {
            var observed = Observe(target, LibraryFileOperation.Read);
            if (observed.Failed)
            {
                return LibraryOperationOutcome.Deferred(observed.Failure);
            }

            if (!observed.Present)
            {
                // S4: the restored bytes come from the redo image, never from the entry itself, so the entry is still
                // there, untouched, until the target holds S.
                var copy = EnsureInstallCopy(operation);
                if (!copy.Done)
                {
                    return copy;
                }

                if (TryMove(InstallCopyPath(operation), target, overwrite: false, LibraryFileOperation.RestoreFromRecentlyDeleted, out var failure) is MoveResult.Failed)
                {
                    return LibraryOperationOutcome.Deferred(failure);
                }

                continue;
            }

            if (observed.Hash != operation.Staged)
            {
                // S5: another app created the target. Scribe's content becomes a library of its own.
                var saved = PreserveOwnContent(operation);
                if (!saved.Outcome.Done)
                {
                    return saved.Outcome;
                }

                Report(operation, LibraryKeptVersionKind.SavedUnderNewId, saved.KeptPath);
            }

            DeleteSpentInstallCopy(operation);
            return ConsumeEntry(operation, entryPath);
        }

        return LibraryOperationOutcome.Deferred(LibraryIoFailure.Other);
    }

    // S1 to S3: the entry leaves Recently deleted only when it still holds what was restored from it. It is first taken,
    // by a rename that never overwrites, into this operation's claims in deleted\, and only that owned copy is judged:
    // the restored bytes are deleted, anything else goes back to Recently deleted (round 2, A4).
    private LibraryOperationOutcome ConsumeEntry(LibraryManifestOperation operation, string entryPath)
    {
        for (var step = 0; step < MaxSteps; step++)
        {
            if (!TryListSeries(operation, _deletedDir, out var claims, out var listFailure))
            {
                return LibraryOperationOutcome.Deferred(listFailure);
            }

            var entry = Observe(entryPath, LibraryFileOperation.Read);
            if (entry.Failed)
            {
                return LibraryOperationOutcome.Deferred(entry.Failure);
            }

            if (entry.Present && entry.Hash == operation.FromHash)
            {
                var claimed = Claim(operation, entryPath, _deletedDir, claims, LibraryFileOperation.RestoreFromRecentlyDeleted);
                if (!claimed.Done)
                {
                    return claimed;
                }

                continue;
            }

            // Absent, or changed after prepare and so left as it is: what remains is to judge what was taken.
            var judged = JudgeEntryClaims(operation, entryPath, claims, operation.FromHash, LibraryFileOperation.RestoreFromRecentlyDeleted);
            if (!judged.Done)
            {
                return judged;
            }

            return LibraryOperationOutcome.Completed;
        }

        return LibraryOperationOutcome.Deferred(LibraryIoFailure.Other);
    }

    // --- purge: U1 to U3 ---------------------------------------------------------------------------------------------

    // The entry is taken into this operation's claims first and only the owned copy is deleted, when it holds what the user
    // chose to delete (or, for an entry listed unreadable, whatever it held when taken); an entry changed after prepare is
    // left as it is (U3), and bytes written to its name while it was being taken go back to Recently deleted.
    private LibraryOperationOutcome ResumePurge(LibraryManifestOperation operation)
    {
        var entryPath = Absolute(operation.Target);
        for (var step = 0; step < MaxSteps; step++)
        {
            if (!TryListSeries(operation, _deletedDir, out var claims, out var listFailure))
            {
                return LibraryOperationOutcome.Deferred(listFailure);
            }

            bool take;
            if (operation.PreImage is { } expected)
            {
                var entry = Observe(entryPath, LibraryFileOperation.Read);
                if (entry.Failed)
                {
                    return LibraryOperationOutcome.Deferred(entry.Failure);
                }

                take = entry.Present && entry.Hash == expected;
            }
            else
            {
                // An entry listed unreadable has no hash to compare: the user chose to delete it as it is, so it is taken
                // unread, once. The claim that took it is the only evidence that this purge already did, so it stays,
                // unread, until the manifest retires (round 3, A12): a retry or a restart then finds it and never takes a
                // file written to the entry's name afterwards, which is a new entry and is left alone.
                var exists = SafeExists(entryPath, out var existsFailure);
                if (existsFailure != LibraryIoFailure.None)
                {
                    return LibraryOperationOutcome.Deferred(existsFailure);
                }

                take = exists && claims.Count == 0;
            }

            if (take)
            {
                var claimed = Claim(operation, entryPath, _deletedDir, claims, LibraryFileOperation.Purge);
                if (!claimed.Done)
                {
                    return claimed;
                }

                continue;
            }

            var judged = JudgeEntryClaims(operation, entryPath, claims, operation.PreImage, LibraryFileOperation.Purge);
            if (!judged.Done)
            {
                return judged;
            }

            return LibraryOperationOutcome.Completed; // U1, U2, or U3: changed after prepare, so left as it is
        }

        return LibraryOperationOutcome.Deferred(LibraryIoFailure.Other);
    }

    // Claims of a Recently deleted entry, owned by the journal: the one holding exactly what the operation consumes (a
    // restore's FromHash or a purge's pre-image) is deleted; for an entry listed unreadable the first one taken is what
    // is consumed, and it stays, unread, as the purge's evidence until the manifest retires (round 3, A12); every other
    // goes back to Recently deleted, under the entry's name when it is free and otherwise under the next free name of its
    // stamp.
    private LibraryOperationOutcome JudgeEntryClaims(
        LibraryManifestOperation operation, string entryPath, IReadOnlyList<(int Index, string Path)> claims,
        LibraryContentHash? consumed, LibraryFileOperation fileOperation)
    {
        foreach (var (index, path) in claims)
        {
            if (consumed is null && index == claims[0].Index)
            {
                continue;
            }

            var claim = Observe(path, LibraryFileOperation.Read);
            if (claim.Failed)
            {
                return LibraryOperationOutcome.Deferred(claim.Failure);
            }

            if (!claim.Present)
            {
                continue;
            }

            if (claim.Hash == consumed)
            {
                if (!TryDelete(path, fileOperation))
                {
                    return LibraryOperationOutcome.Deferred(LibraryIoFailure.Other);
                }

                continue;
            }

            var returned = ReturnToRecentlyDeleted(path, entryPath, fileOperation);
            if (!returned.Done)
            {
                return returned;
            }
        }

        return LibraryOperationOutcome.Completed;
    }

    // Bytes the journal took from Recently deleted that are not what it consumes: back under the entry's own name, or, when
    // another file has it by now, under the next free name of the same stamp, never over anything.
    private LibraryOperationOutcome ReturnToRecentlyDeleted(string claimPath, string entryPath, LibraryFileOperation fileOperation)
    {
        var entryName = Path.GetFileName(entryPath);
        for (var attempt = 0; attempt < MaxCandidates; attempt++)
        {
            var destination = entryPath;
            if (attempt > 0)
            {
                if (!RecentlyDeletedStore.TryParseEntryName(entryName, out var stamp, out _, out var originalFileName))
                {
                    return LibraryOperationOutcome.Deferred(LibraryIoFailure.Other);
                }

                var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    taken.UnionWith(_files.EnumerateFiles(_deletedDir, "*").Select(Path.GetFileName).OfType<string>());
                }
                catch (Exception ex)
                {
                    _onFailure(LibraryFileOperation.Enumerate, ex);
                    return LibraryOperationOutcome.Deferred(LibraryIoFailures.Classify(ex));
                }

                destination = Path.Combine(_deletedDir, RecentlyDeletedStore.NextEntryName(originalFileName, stamp, taken));
            }

            switch (TryMove(claimPath, destination, overwrite: false, fileOperation, out var failure))
            {
                case MoveResult.Moved:
                case MoveResult.SourceMissing:
                    return LibraryOperationOutcome.Completed;
                case MoveResult.Failed:
                    return LibraryOperationOutcome.Deferred(failure);
            }
        }

        return LibraryOperationOutcome.Deferred(LibraryIoFailure.Other);
    }

    // Before a set-aside or a discard: every Recently deleted entry this operation took goes back, judged by nothing, since
    // whether its Save committed is not what these paths decide by.
    private LibraryOperationOutcome ReturnEntryClaims(LibraryManifestOperation operation)
    {
        if (!TryListSeries(operation, _deletedDir, out var claims, out var failure))
        {
            return LibraryOperationOutcome.Deferred(failure);
        }

        var entryPath = Absolute(operation.Kind == LibraryOperationKind.Restore ? operation.From! : operation.Target);
        foreach (var (_, path) in claims)
        {
            var returned = ReturnToRecentlyDeleted(path, entryPath, LibraryFileOperation.RestoreFromRecentlyDeleted);
            if (!returned.Done)
            {
                return returned;
            }
        }

        return LibraryOperationOutcome.Completed;
    }

    // Takes whatever a file holds, by one rename that never overwrites, into the operation's own series in `folder` (the
    // next index after the largest there): after it, the bytes are the journal's to judge, and a write landing at `source`
    // meanwhile is a new file the next observation meets (round 2, A4).
    private LibraryOperationOutcome Claim(
        LibraryManifestOperation operation, string source, string folder, IReadOnlyList<(int Index, string Path)> series,
        LibraryFileOperation fileOperation)
    {
        var index = series.Count == 0 ? 1 : series[^1].Index + 1;
        var destination = Path.Combine(folder, LibraryJournalNames.Backup(_manifest.Generation, _manifest.Id, operation.Number, index));
        return TryMove(source, destination, overwrite: false, fileOperation, out var failure) is MoveResult.Failed
            ? LibraryOperationOutcome.Deferred(failure)
            : LibraryOperationOutcome.Completed;
    }

    // --- backups (R3) and preservation (R7) ---------------------------------------------------------------------------

    // Rule R3, for every backup of the operation's series: S or P is Scribe's own copy (P of an edits document becomes
    // its previous copy or is set aside, as the operation says), and anything else is someone's version, preserved and
    // reported before the backup name is free again.
    private LibraryOperationOutcome ResolveBackups(LibraryManifestOperation operation, IReadOnlyList<(int Index, string Path)> series)
    {
        foreach (var (_, path) in series)
        {
            var backup = Observe(path, LibraryFileOperation.Read);
            if (backup.Failed)
            {
                return LibraryOperationOutcome.Deferred(backup.Failure);
            }

            if (!backup.Present)
            {
                continue;
            }

            if (backup.Hash == operation.Staged)
            {
                if (!TryDelete(path, LibraryFileOperation.DeleteSpent))
                {
                    return LibraryOperationOutcome.Deferred(LibraryIoFailure.Other);
                }

                continue;
            }

            if (operation.PreImage is { } preImage && backup.Hash == preImage)
            {
                var resolved = ResolvePreImageBackup(operation, backup, path);
                if (!resolved.Done)
                {
                    return resolved;
                }

                continue;
            }

            var preserved = PreserveOutsideVersion(operation, backup, path);
            if (!preserved.Done)
            {
                return preserved;
            }
        }

        return LibraryOperationOutcome.Completed;
    }

    private LibraryOperationOutcome ResolvePreImageBackup(LibraryManifestOperation operation, Observed backup, string path)
    {
        if (!operation.IsEditsTarget)
        {
            return TryDelete(path, LibraryFileOperation.DeleteSpent)
                ? LibraryOperationOutcome.Completed
                : LibraryOperationOutcome.Deferred(LibraryIoFailure.Other);
        }

        if (operation.Replaced == LibraryEditsReplacement.Previous)
        {
            // The one overwrite rules R1 and R7 allow, and the contract's single stated exception to never overwriting
            // (6.6.4; round 2's G2 was ruled that exception): the previous copy is a single slot, Scribe's one-step undo,
            // and only ever receives the pre-image P. LibraryFaultInjectionTests pins that no other file is written over.
            return TryMove(path, PreviousPath(operation), overwrite: true, LibraryFileOperation.KeepPrevious, out var failure) is MoveResult.Failed
                ? LibraryOperationOutcome.Deferred(failure)
                : LibraryOperationOutcome.Completed;
        }

        // A recovery of a paused document sets it aside by the user's own choice: kept, never read again, and no notice.
        return Preserve(backup, path, EditsCandidate(operation, backup.Hash)).Outcome;
    }

    // Someone else's bytes: kept at the operation's preservation destination and reported, so nothing written outside
    // Scribe is lost (review finding G4). An operation with no destination of its own (only tampering gives a delete or
    // a purge a backup) keeps them among the orphan backups, which are never removed.
    private LibraryOperationOutcome PreserveOutsideVersion(LibraryManifestOperation operation, Observed version, string path)
    {
        if (operation.IsEditsTarget && operation.SetAsideStem is not null)
        {
            var setAside = Preserve(version, path, EditsCandidate(operation, version.Hash));
            if (setAside.Outcome.Done)
            {
                Report(operation, LibraryKeptVersionKind.EditsSetAside, keptPath: null);
            }

            return setAside.Outcome;
        }

        if (operation.KeepAs is not null)
        {
            var kept = Preserve(version, path, CustomCandidate(operation));
            if (kept.Outcome.Done)
            {
                Report(operation, LibraryKeptVersionKind.OutsideVersion, kept.KeptPath);
            }

            return kept.Outcome;
        }

        return MoveToOrphans(path);
    }

    // C3 and S5: Scribe's committed bytes, copied from the redo image through the install copy to the first free
    // candidate of the keepAs series, so the other app's file at the target is never touched. The move counts only once
    // the candidate is observed holding S (round 3, A14): a write landing there as the move returns belongs to the other
    // app, stays where it landed, and S, still in the redo image, goes to the next free candidate.
    private (LibraryOperationOutcome Outcome, string? KeptPath) PreserveOwnContent(LibraryManifestOperation operation)
    {
        var candidate = CustomCandidate(operation);
        var retries = 0;
        for (var k = 1; k <= MaxCandidates; k++)
        {
            var path = candidate(k);
            var existing = Observe(path, LibraryFileOperation.KeepVersion);
            if (existing.Failed)
            {
                return (LibraryOperationOutcome.Deferred(existing.Failure), null);
            }

            if (existing.Present)
            {
                if (existing.Hash == operation.Staged)
                {
                    return (LibraryOperationOutcome.Completed, path);
                }

                continue;
            }

            var copy = EnsureInstallCopy(operation);
            if (!copy.Done)
            {
                return (copy, null);
            }

            var moved = TryMove(InstallCopyPath(operation), path, overwrite: false, LibraryFileOperation.KeepVersion, out var failure);
            if (moved is MoveResult.Failed)
            {
                return (LibraryOperationOutcome.Deferred(failure), null);
            }

            if (moved is MoveResult.Moved)
            {
                var kept = Observe(path, LibraryFileOperation.KeepVersion);
                if (kept.Failed)
                {
                    return (LibraryOperationOutcome.Deferred(kept.Failure), null);
                }

                if (kept.Present && kept.Hash == operation.Staged)
                {
                    return (LibraryOperationOutcome.Completed, path);
                }
            }

            // The candidate appeared meanwhile, the install copy went, or what was moved there is not S any more: observe
            // the same candidate again, which skips it when it holds someone else's bytes and remakes the copy otherwise.
            if (++retries > MaxSteps)
            {
                return (LibraryOperationOutcome.Deferred(LibraryIoFailure.Other), null);
            }

            k--;
        }

        return (LibraryOperationOutcome.Deferred(LibraryIoFailure.Other), null);
    }

    // Rule R7: a deterministic candidate series, hash-checked, never overwriting. A candidate holding the same bytes is
    // done (the same version observed again after a crash lands where it landed before); one holding other bytes, for any
    // reason, is skipped; the version moves to the first free candidate, and a move that fails because the candidate
    // appeared meanwhile observes it again and probes on. The source is always a copy the journal owns (a backup or a
    // claim, round 2, A4), so deleting a spare one never deletes a version written after it was observed; and a move is
    // only counted once the candidate is observed holding the bytes.
    private (LibraryOperationOutcome Outcome, string? KeptPath) Preserve(
        Observed version, string source, Func<int, string> candidate)
    {
        for (var k = 1; k <= MaxCandidates; k++)
        {
            var path = candidate(k);
            var existing = Observe(path, LibraryFileOperation.KeepVersion);
            if (existing.Failed)
            {
                return (LibraryOperationOutcome.Deferred(existing.Failure), null);
            }

            if (existing.Present)
            {
                if (existing.Hash != version.Hash)
                {
                    continue;
                }

                // An identical copy is already kept: the owned source is spare.
                return TryDelete(source, LibraryFileOperation.DeleteSpent)
                    ? (LibraryOperationOutcome.Completed, path)
                    : (LibraryOperationOutcome.Deferred(LibraryIoFailure.Other), null);
            }

            if (!TryCreateDirectory(Path.GetDirectoryName(path)!, out var folderFailure))
            {
                return (LibraryOperationOutcome.Deferred(folderFailure), null);
            }

            switch (TryMove(source, path, overwrite: false, LibraryFileOperation.KeepVersion, out var failure))
            {
                case MoveResult.Failed:
                    return (LibraryOperationOutcome.Deferred(failure), null);
                case MoveResult.DestinationExists:
                    k--;
                    continue;
                case MoveResult.SourceMissing:
                    // The version is gone from the source: already kept by an earlier attempt, or removed by hand.
                    return (LibraryOperationOutcome.Completed, FoundAtPreservation(candidate, version.Hash));
                default:
                    var kept = Observe(path, LibraryFileOperation.KeepVersion);
                    return kept is { Failed: false, Present: true } && kept.Hash == version.Hash
                        ? (LibraryOperationOutcome.Completed, path)
                        : (LibraryOperationOutcome.Deferred(kept.Failed ? kept.Failure : LibraryIoFailure.Other), null);
            }
        }

        return (LibraryOperationOutcome.Deferred(LibraryIoFailure.Other), null);
    }

    private LibraryOperationOutcome MoveToOrphans(string path)
    {
        if (!TryCreateDirectory(_orphansDir, out var folderFailure))
        {
            return LibraryOperationOutcome.Deferred(folderFailure);
        }

        return LibraryJournal.MoveIntoOrphans(_files, path, _orphansDir, _onFailure)
            ? LibraryOperationOutcome.Completed
            : LibraryOperationOutcome.Deferred(LibraryIoFailure.Other);
    }

    private bool IsOwnCopy(LibraryManifestOperation operation, LibraryContentHash hash) =>
        hash == operation.Staged || (operation.PreImage is { } preImage && hash == preImage);

    private bool FoundAtPreservation(LibraryManifestOperation operation, LibraryContentHash hash)
    {
        if (operation.IsEditsTarget && operation.SetAsideStem is not null)
        {
            return FoundAtPreservation(EditsCandidate(operation, hash), hash) is not null;
        }

        return operation.KeepAs is not null && FoundAtPreservation(CustomCandidate(operation), hash) is not null;
    }

    // The first candidate of a series holding exactly these bytes, probing up to the first free candidate: preservation
    // always fills the first free one, so a copy kept earlier is at or before it.
    private string? FoundAtPreservation(Func<int, string> candidate, LibraryContentHash hash)
    {
        for (var k = 1; k <= MaxCandidates; k++)
        {
            var path = candidate(k);
            var existing = Observe(path, LibraryFileOperation.KeepVersion, report: false);
            if (existing.Failed || !existing.Present)
            {
                return null;
            }

            if (existing.Hash == hash)
            {
                return path;
            }
        }

        return null;
    }

    private void Report(LibraryManifestOperation operation, LibraryKeptVersionKind kind, string? keptPath)
    {
        var keptId = kind == LibraryKeptVersionKind.EditsSetAside || keptPath is null
            ? null
            : Path.GetFileNameWithoutExtension(keptPath);
        var version = new LibraryKeptVersion(operation.LibraryId ?? string.Empty, kind, keptId);
        if (!_kept.Contains(version))
        {
            _kept.Add(version);
        }
    }

    // --- install copies and names ------------------------------------------------------------------------------------

    // The install copy holds S before it is used: made from the redo image, which is immutable (rule R2) and checked
    // against S, so a partly written copy from a crash, or a redo image that went bad, never reaches a target.
    private LibraryOperationOutcome EnsureInstallCopy(LibraryManifestOperation operation)
    {
        var path = InstallCopyPath(operation);
        var existing = Observe(path, LibraryFileOperation.CreateInstallCopy);
        if (existing.Failed)
        {
            return LibraryOperationOutcome.Deferred(existing.Failure);
        }

        if (existing.Present)
        {
            if (existing.Hash == operation.Staged)
            {
                return LibraryOperationOutcome.Completed;
            }

            if (!TryDelete(path, LibraryFileOperation.CreateInstallCopy))
            {
                return LibraryOperationOutcome.Deferred(LibraryIoFailure.Other);
            }
        }

        var redo = Observe(RedoPath(operation), LibraryFileOperation.Read);
        if (redo.Failed)
        {
            return LibraryOperationOutcome.Deferred(redo.Failure);
        }

        if (!redo.Present || redo.Hash != operation.Staged)
        {
            return LibraryOperationOutcome.Deferred(LibraryIoFailure.Corrupt);
        }

        // The first edits document a Save writes is also the first file in edits\.
        if (!TryCreateDirectory(Path.GetDirectoryName(path)!, out var folderFailure))
        {
            return LibraryOperationOutcome.Deferred(folderFailure);
        }

        try
        {
            _files.WriteAllBytesDurably(path, redo.Bytes);
        }
        catch (Exception ex) when (!LibraryIoFailures.IsAlreadyExists(ex))
        {
            _onFailure(LibraryFileOperation.CreateInstallCopy, ex);
            TryDelete(path, LibraryFileOperation.CreateInstallCopy);
            return LibraryOperationOutcome.Deferred(LibraryIoFailures.Classify(ex));
        }
        catch (Exception)
        {
            // Created meanwhile: observed again below like any other copy.
        }

        var made = Observe(path, LibraryFileOperation.CreateInstallCopy);
        return made is { Failed: false, Present: true } && made.Hash == operation.Staged
            ? LibraryOperationOutcome.Completed
            : LibraryOperationOutcome.Deferred(made.Failed ? made.Failure : LibraryIoFailure.Other);
    }

    private string Absolute(string relative) => LibraryManifest.ToAbsolute(_librariesDir, relative);

    private string FolderOf(LibraryManifestOperation operation) =>
        Path.GetDirectoryName(Absolute(operation.Target))!;

    private string BackupPath(LibraryManifestOperation operation, int index) =>
        Path.Combine(FolderOf(operation), LibraryJournalNames.Backup(_manifest.Generation, _manifest.Id, operation.Number, index));

    // edits/<id>.json -> edits/<id>.previous.json
    private string PreviousPath(LibraryManifestOperation operation)
    {
        var target = Absolute(operation.Target);
        return Path.Combine(Path.GetDirectoryName(target)!, Path.GetFileNameWithoutExtension(target) + PreviousSuffix);
    }

    // <setAsideStem>.<first 16 hex>.backup.json, then -2, -3 before .backup.json.
    private Func<int, string> EditsCandidate(LibraryManifestOperation operation, LibraryContentHash hash)
    {
        var stem = Absolute(operation.SetAsideStem!) + "." + LibraryContentHashing.Prefix(hash);
        return k => k == 1 ? stem + SetAsideSuffix : stem + "-" + k.ToString(System.Globalization.CultureInfo.InvariantCulture) + SetAsideSuffix;
    }

    // keepAs, then keepAs with -2, -3 before .csv.
    private Func<int, string> CustomCandidate(LibraryManifestOperation operation)
    {
        var keepAs = Absolute(operation.KeepAs!);
        var folder = Path.GetDirectoryName(keepAs)!;
        var stem = Path.GetFileNameWithoutExtension(keepAs);
        var extension = Path.GetExtension(keepAs);
        return k => k == 1 ? keepAs : Path.Combine(folder, stem + "-" + k.ToString(System.Globalization.CultureInfo.InvariantCulture) + extension);
    }

    // The operation's own backups: listed and parsed whole, so operation 1 never takes operation 10's (review finding
    // A19). Sorted by their place in the series, the largest last.
    private bool TryListSeries(
        LibraryManifestOperation operation, out List<(int Index, string Path)> series, out LibraryIoFailure failure) =>
        TryListSeries(operation, FolderOf(operation), out series, out failure);

    // The same for a series kept in another folder: the claims of a Recently deleted entry, in deleted\.
    private bool TryListSeries(
        LibraryManifestOperation operation, string folder, out List<(int Index, string Path)> series, out LibraryIoFailure failure)
    {
        series = [];
        failure = LibraryIoFailure.None;
        try
        {
            foreach (var path in _files.EnumerateFiles(folder, "~g*" + LibraryJournalNames.BackupSuffix))
            {
                if (LibraryJournalNames.TryParse(Path.GetFileName(path), out var name) &&
                    name.Kind == LibraryJournalNameKind.Backup &&
                    name.BelongsTo(_manifest.Generation, _manifest.Id, operation.Number))
                {
                    series.Add((name.BackupIndex, path));
                }
            }
        }
        catch (Exception ex)
        {
            _onFailure(LibraryFileOperation.Enumerate, ex);
            failure = LibraryIoFailures.Classify(ex);
            return false;
        }

        series.Sort((a, b) => a.Index.CompareTo(b.Index));
        return true;
    }

    // --- observation and guarded file operations ---------------------------------------------------------------------

    private readonly record struct Observed(bool Present, LibraryContentHash Hash, byte[] Bytes, bool Failed, LibraryIoFailure Failure);

    private Observed Observe(string path, LibraryFileOperation operation, bool report = true)
    {
        try
        {
            if (!_files.Exists(path))
            {
                return default;
            }

            var bytes = _files.ReadAllBytes(path);
            return new Observed(true, LibraryContentHashing.Of(bytes), bytes, false, LibraryIoFailure.None);
        }
        catch (Exception ex) when (LibraryIoFailures.IsNotFound(ex))
        {
            return default;
        }
        catch (Exception ex)
        {
            if (report)
            {
                _onFailure(operation, ex);
            }

            return new Observed(false, default, [], true, LibraryIoFailures.Classify(ex));
        }
    }

    private bool SafeExists(string path) => SafeExists(path, out _);

    private bool SafeExists(string path, out LibraryIoFailure failure)
    {
        failure = LibraryIoFailure.None;
        try
        {
            return _files.Exists(path);
        }
        catch (Exception ex)
        {
            _onFailure(LibraryFileOperation.Read, ex);
            failure = LibraryIoFailures.Classify(ex);
            return false;
        }
    }

    private enum MoveResult
    {
        Moved,
        DestinationExists,
        SourceMissing,
        Failed,
    }

    private MoveResult TryMove(string source, string destination, bool overwrite, LibraryFileOperation operation, out LibraryIoFailure failure)
    {
        failure = LibraryIoFailure.None;
        try
        {
            _files.Move(source, destination, overwrite);
            return MoveResult.Moved;
        }
        catch (Exception ex) when (!overwrite && LibraryIoFailures.IsAlreadyExists(ex))
        {
            return MoveResult.DestinationExists;
        }
        catch (Exception ex) when (LibraryIoFailures.IsNotFound(ex))
        {
            return MoveResult.SourceMissing;
        }
        catch (Exception ex)
        {
            _onFailure(operation, ex);
            failure = LibraryIoFailures.Classify(ex);
            return MoveResult.Failed;
        }
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

    private bool TryCreateDirectory(string path, out LibraryIoFailure failure)
    {
        failure = LibraryIoFailure.None;
        try
        {
            _files.CreateDirectory(path);
            return true;
        }
        catch (Exception ex)
        {
            _onFailure(LibraryFileOperation.Enumerate, ex);
            failure = LibraryIoFailures.Classify(ex);
            return false;
        }
    }
}
