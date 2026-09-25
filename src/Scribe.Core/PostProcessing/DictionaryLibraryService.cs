using Microsoft.Extensions.Logging;
using Scribe.Core.Diagnostics;
using Scribe.Core.Infrastructure;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.Persistence;

namespace Scribe.Core.PostProcessing;

/// <summary>
/// The library service: every library's storage and the Save commit (<see cref="ILibraryCatalogStore"/>), the committed
/// vocabulary and its admission point (<see cref="ILibraryVocabularySource"/>), and release 0.4.4's library seam
/// (<see cref="IDictionaryLibraryService"/>), which it keeps as that release left it until the vocabulary source replaces
/// it (contract 3.1.1).
/// </summary>
/// <remarks>
/// <para>
/// Storage is the journal of contract 6.6: a Save's files are staged as immutable redo images under a manifest of the
/// next generation, the settings transaction commits the generation with the settings, and completion installs every
/// file from whatever state it finds, keeping every version written outside Scribe rather than overwriting it. Every
/// reader goes through one read path that applies a pending committed manifest whole, so it sees generation G or G + 1,
/// never a mix.
/// </para>
/// <para>
/// One library lock orders <see cref="PrepareSave"/>, <see cref="CompleteSave"/>, <see cref="Recover"/>,
/// <see cref="LoadCatalog"/>, adoption, the wrappers and the janitor; nothing holds it across an await, and
/// <see cref="Current"/> and <see cref="TryHandOff"/> never take it. The permission gate is a small lock of its own
/// under which every narrowing of AI permission is published and every hand-off runs.
/// </para>
/// <para>
/// Logs carry counts, generations, durations, enum names and <see cref="FailureShape"/> text only: never a library's
/// name, id, category, description, term, file name or path (plan 3.15).
/// </para>
/// </remarks>
public sealed class DictionaryLibraryService : IDictionaryLibraryService, ILibraryCatalogStore, ILibraryVocabularySource
{
    private readonly AppPaths _paths;
    private readonly ISettingsRepository _settings;
    private readonly ILogger<DictionaryLibraryService> _logger;
    private readonly LibraryServiceParts _parts;
    private readonly LibraryJournal _journal;
    private readonly CustomLibraryStore _store;

    // Orders every piece of library I/O in this process (contract 3.1.5).
    private readonly Lock _libraryLock = new();

    // The permission gate (contract 2.10): never the library lock, so a hand-off never waits on file I/O.
    private readonly Lock _gate = new();

    private volatile LibraryVocabulary? _vocabulary;

    // Under _gate. The published vocabulary's scope, and while a Save is live the narrower scope that holds from its
    // prepare until its completion; a hand-off must be covered by both.
    private AiVocabularyScope _publishedScope = AiVocabularyScope.None;
    private AiVocabularyScope? _savingScope;

    // Under _libraryLock.
    private LivePreparation? _live;

    // Under _libraryLock. A preparation whose completion could not read the stored generation (round 2, A8): whether it
    // committed is not known, so its manifest stays pending, new Saves and the wrappers are fenced, and the scope while
    // saving stays in force until a read of the generation lets recovery settle it by the journal's own rules.
    private LivePreparation? _unsettled;
    private LibraryRecoveryOutcome? _recovery;

    // Under _libraryLock. The pending manifest last applied whole, with every redo image it needs read and checked
    // against S (round 3, A13): redo images are immutable until retirement (rule R2), so a read of them that fails later
    // reuses these bytes instead of letting the older files stand for the committed generation.
    private PendingImages? _pendingImages;

    // Under _libraryLock. Whether the vocabulary last offered for publication held a library back because the committed
    // generation's content could not be read or told (round 3, A13; round 4, A15). Recover and the janitor publish again
    // whenever it did, even when their attempt changes no file, so a hold-back ends with the attempt that can list and
    // read the journal again rather than at the next load.
    private bool _publishedHeldBack;
    private readonly List<LibraryKeptVersion> _keptVersions = [];
    private readonly Dictionary<string, CustomRead> _lastCustom = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BuiltInRead> _lastBuiltIn = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<long> _pendingChanged = [];
    private (LocalStateHealth Health, bool Lost)? _loggedState;

    // Unchanged signature: every existing test, the W1a fixture and the golden keep constructing it this way.
    public DictionaryLibraryService(AppPaths paths, ISettingsRepository settings, ILogger<DictionaryLibraryService> logger)
        : this(paths, settings, logger, LibraryServiceParts.Default)
    {
    }

    internal DictionaryLibraryService(
        AppPaths paths, ISettingsRepository settings, ILogger<DictionaryLibraryService> logger, LibraryServiceParts parts)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(parts);
        _paths = paths;
        _settings = settings;
        _logger = logger;
        _parts = parts;
        _journal = new LibraryJournal(parts.Files, paths, LogFileFailure);
        _store = new CustomLibraryStore(parts.Files, paths, LogFileFailure, parts.RetiredBuiltInIds);
        Janitor = new LibraryJanitor(RunJanitor);
    }

    public event Action<long>? Changed;

    /// <summary>The retention step storage maintenance runs on its schedule (contract 3.1.6).</summary>
    internal LibraryJanitor Janitor { get; }

    // --- release 0.4.4's library seam (IDictionaryLibraryService) ------------------------------------------------------

    /// <summary>
    /// Every library in precedence order with its effective rows: a paused library has none, an empty custom library is
    /// listed. A custom library is listed under the id 0.4.3 loads it as, its file stem, which is its logical id for every
    /// file but a hand-placed one remapped away from a built-in id; the catalog carries the logical id.
    /// </summary>
    public IReadOnlyList<DictionaryLibrary> GetLibraries() => [.. LoadCatalog().Libraries.Select(ToLegacyLibrary)];

    public IReadOnlyList<DictionaryEntry> GetEnabledLibraryEntries()
    {
        var stored = _settings.Load();
        return _settings.LastLoadFailed ? [] : GetEnabledLibraryEntries(stored.EnabledDictionaryLibraryIds);
    }

    public IReadOnlyList<DictionaryEntry> GetEnabledLibraryEntries(IReadOnlyCollection<string> enabledIds)
    {
        ArgumentNullException.ThrowIfNull(enabledIds);
        if (enabledIds.Count == 0)
        {
            return [];
        }

        // Release 0.4.4's selection, exactly: a listed id selects every library loaded under it, the id 0.4.3 loads it as
        // (a custom file's stem), never a logical id; selection by logical id belongs to the catalog and the vocabulary.
        var ids = new HashSet<string>(enabledIds, StringComparer.OrdinalIgnoreCase);
        var libraries = LoadCatalog().Libraries
            .Where(library => ids.Contains(Identity(library).LegacyId))
            .Select(ToLegacyLibrary);
        return DictionaryLibraryComposer.ComposeLibraries(libraries);
    }

    public DictionaryLibrary Import(string csv, string? suggestedName)
    {
        var file = DictionaryLibraryCsv.Parse(csv);
        if (file.Errors.Count > 0)
        {
            throw new InvalidOperationException(
                "That library contains invalid CSV rows:\n" + string.Join("\n", file.Errors.Take(5)));
        }

        if (file.Entries.Count == 0)
        {
            throw new InvalidOperationException(
                "That file has no usable dictionary rows. Each row needs at least a spoken form and a replacement.");
        }

        // A suggested name that is not well-formed UTF-16 (a file name can hold an unpaired surrogate) is not used, as a
        // blank one is not: nothing would be written for it but U+FFFD.
        var name = file.Name
            ?? (string.IsNullOrWhiteSpace(suggestedName) || !LibraryText.IsWellFormed(suggestedName) ? null : suggestedName.Trim())
            ?? "Imported library";
        var category = file.Category ?? "Custom";

        string id;
        lock (_libraryLock)
        {
            var read = ReadForWrapper();
            var taken = TakenIds(read);
            id = InterimLibraryNaming.ImportId(name, taken.Contains);
            var rows = file.Entries.Select(entry => LibraryRow.Custom(TermValues.FromEntry(entry))).ToList();
            var content = new LibraryContent(id, BuiltIn: false, name, category, file.Description, rows);

            // Imported libraries start off and, by the custom default, not sent to AI cleanup (Decision 2: Imported is off).
            var changes = new LibraryChangeSet(
                read.Catalog.Generation, 0, [new LibraryWrite(id, false, LibraryOrigin.Imported, null, content)], [], [],
                read.Catalog.LocalState, localStateChanged: false);
            CommitWrapper(read, changes);
        }

        RaisePendingChanged();

        // The name is the user's own words and the id is a slug of it, so neither is logged.
        _logger.LogInformation(
            "Imported a custom dictionary library ({Count} entries, name of {NameLength} characters).",
            file.Entries.Count, name.Length);
        return new DictionaryLibrary(id, name, category, file.Description, BuiltIn: false, file.Entries);
    }

    public void Remove(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        if (BuiltInDictionaryLibraries.All.Any(l => string.Equals(l.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Built-in libraries can't be removed. Turn it off instead.");
        }

        // Guard against a caller-supplied id escaping the libraries folder.
        if ((id + ".csv").IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException("That library id is not valid.");
        }

        var removed = false;
        lock (_libraryLock)
        {
            var read = ReadForWrapper();
            var library = read.Catalog.Libraries.FirstOrDefault(candidate =>
                !candidate.Content.BuiltIn &&
                (string.Equals(candidate.Content.Id, id, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(Identity(candidate).LegacyId, id, StringComparison.OrdinalIgnoreCase)));
            if (library is not null)
            {
                if (library.FileName is not { } fileName || library.ContentHash is not { } hash)
                {
                    throw new InvalidOperationException("That library's file can't be read right now, so it was not removed.");
                }

                var changes = new LibraryChangeSet(
                    read.Catalog.Generation, 0, [], [new LibraryDeletion(library.Content.Id, fileName, hash)], [],
                    read.Catalog.LocalState, localStateChanged: false);
                CommitWrapper(read, changes);
                removed = true;
            }
        }

        RaisePendingChanged();
        if (removed)
        {
            _logger.LogInformation("Removed a custom dictionary library.");
        }
    }

    // --- ILibraryCatalogStore -----------------------------------------------------------------------------------------

    public LibraryCatalog LoadCatalog()
    {
        LibraryCatalog catalog;
        lock (_libraryLock)
        {
            catalog = LoadCore(adopt: true, recover: true).Catalog;
        }

        RaisePendingChanged();
        return catalog;
    }

    public LibraryPrepareResult PrepareSave(LibraryChangeSet changes)
    {
        ArgumentNullException.ThrowIfNull(changes);

        // Before any file is touched, the witness included (contract 3.1.5).
        LibraryText.RequireWellFormed(changes);
        LibraryPrepareResult result;
        lock (_libraryLock)
        {
            result = PrepareCore(changes);
        }

        RaisePendingChanged();
        return result;
    }

    public LibrarySaveOutcome CompleteSave(PreparedLibrarySave prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        LibrarySaveOutcome outcome;
        lock (_libraryLock)
        {
            outcome = CompleteCore(prepared, wrapper: false);
        }

        RaisePendingChanged();
        return outcome;
    }

    public LibraryRecoveryResult Recover()
    {
        LibraryRecoveryResult result;
        lock (_libraryLock)
        {
            try
            {
                var settings = ReadSettings();
                var context = KeepWitnessInvariant(settings, ContextFor(settings));
                var kept = new List<LibraryKeptVersion>();
                var outcome = RecoverCore(settings, context, kept);
                if (outcome.ChangedFiles || _publishedHeldBack)
                {
                    TryPublish();
                }

                result = ToResult(outcome, kept);
            }
            catch (Exception ex)
            {
                TryLog(LogLevel.Warning, "Library recovery could not read the settings store ({Failure}).", FailureShape.Describe(ex));
                result = new LibraryRecoveryResult(0, 0, 0, 0, 0, [], LibraryIoFailure.Other);
            }
        }

        RaisePendingChanged();
        return result;
    }

    public IReadOnlyList<string> FindOutsideEdits(LibraryCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        lock (_libraryLock)
        {
            // The same one read path, without recovery or adoption: only what the files hold now, logically.
            var now = LoadCore(adopt: false, recover: false, publish: false).Catalog;
            var changed = new List<string>();
            foreach (var library in catalog.Libraries)
            {
                var current = now.Find(library.Content.Id);
                if (current is null || current.ContentHash != library.ContentHash ||
                    (current.State != library.State &&
                     (current.State == LibraryFileState.Unreadable || library.State == LibraryFileState.Unreadable)))
                {
                    changed.Add(library.Content.Id);
                }
            }

            return changed;
        }
    }

    public RecentlyDeletedContent? ReadRecentlyDeleted(RecentlyDeletedLibrary entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var relative = LibraryManifest.DeletedPrefix + entry.EntryName;
        if (!LibraryManifest.IsDeletedCsv(relative))
        {
            return null;
        }

        lock (_libraryLock)
        {
            return RecentlyDeletedStore.ReadContent(entry, _store.ReadRelative(relative), _parts.Codec);
        }
    }

    // --- ILibraryVocabularySource -------------------------------------------------------------------------------------

    public LibraryVocabulary Current => _vocabulary ?? LoadFirstVocabulary();

    public bool TryHandOff(AiVocabularyScope admitted, Action handOff)
    {
        ArgumentNullException.ThrowIfNull(admitted);
        ArgumentNullException.ThrowIfNull(handOff);
        lock (_gate)
        {
            if (_parts.Composer.HasNarrowed(admitted, _publishedScope) ||
                (_savingScope is { } saving && _parts.Composer.HasNarrowed(admitted, saving)))
            {
                return false;
            }

            handOff();
            return true;
        }
    }

    private LibraryVocabulary LoadFirstVocabulary()
    {
        try
        {
            LoadCatalog();
        }
        catch (Exception ex)
        {
            // Libraries are best effort at the dictation path's first read: none rather than a failed dictation.
            TryLog(LogLevel.Warning, "The library vocabulary could not be loaded ({Failure}).", FailureShape.Describe(ex));
        }

        return _vocabulary ?? LibraryVocabulary.Empty;
    }

    // --- the one read path --------------------------------------------------------------------------------------------

    private sealed record SettingsSnapshot(
        IReadOnlyList<string>? DocumentEnabledIds,
        bool DocumentUnusable,
        long Generation,
        bool GenerationRowPresent,
        string? StateRow,
        string? FileIdsRow)
    {
        /// <summary>The generation row is absent or holds anything but a generation: every manifest is set aside.</summary>
        public bool GenerationLost => Generation == 0;

        public int LibraryRows => (GenerationRowPresent ? 1 : 0) + (StateRow is null ? 0 : 1) + (FileIdsRow is null ? 0 : 1);
    }

    private sealed record CatalogRead(
        LibraryCatalog Catalog,
        SettingsSnapshot Settings,
        LibraryStateContext Context,
        LibraryFolderSnapshot Folder,
        IReadOnlyList<LibraryIdentity> Identities,
        IReadOnlyDictionary<string, string> Remaps,
        LibraryFileIdsHealth FileIdsHealth,
        LibraryRecoveryOutcome? Recovery);

    private sealed record LivePreparation(PreparedLibrarySave Save, LibraryManifest Manifest, long StartedTimestamp);

    private sealed record PendingImages(LibraryManifest Manifest, IReadOnlyDictionary<int, byte[]> Redo);

    private sealed record CustomRead(LibraryContent Content, LibraryContentHash Hash);

    private sealed record BuiltInRead(IReadOnlyList<LibraryRow> Rows, LibraryContentHash Hash, BuiltInLibraryEdits? Edits);

    // Everything of one committed generation (round 2, A6): the document's list beside the state row it was written with.
    private SettingsSnapshot ReadSettings()
    {
        var read = _settings.ReadLibrarySettings();
        return new SettingsSnapshot(
            read.DocumentUnusable ? null : read.DocumentEnabledIds,
            read.DocumentUnusable,
            SettingsRepository.ParseLibraryGeneration(read.GenerationRow),
            read.GenerationRow is not null,
            read.StateRow,
            read.FileIdsRow);
    }

    private LibraryStateContext ContextFor(SettingsSnapshot settings)
    {
        var session = _parts.Context();
        return new LibraryStateContext(
            RunningOnDefaults: session.RunningOnDefaults || settings.DocumentUnusable,
            DatabaseRepaired: session.DatabaseRepaired,
            GenerationStored: session.GenerationStored || settings.GenerationRowPresent,
            CommitWitnessed: session.CommitWitnessed || _journal.WitnessExists());
    }

    // Recovery, then the files as the one read path sees them, the local state, the catalog, adoption and publication.
    private CatalogRead LoadCore(bool adopt, bool recover, bool publish = true)
    {
        var settings = ReadSettings();
        var context = KeepWitnessInvariant(settings, ContextFor(settings));
        var recovery = recover ? RecoverCore(settings, context, []) : _recovery;
        var read = ReadCatalog(settings, context, recovery);
        var stored = read.Catalog;
        if (adopt)
        {
            (read, stored) = Adopt(read);
        }

        if (publish)
        {
            Publish(read.Catalog, stored);
            _publishedHeldBack = read.Folder.HeldBack > 0;
        }

        return read;
    }

    // The witness invariant (contract 6.9): no library row without a witness made durable before it. A start that finds
    // a row without one (only an action outside Scribe causes that) writes it at once, on defaults too, since a witness
    // can only make a missing row read as lost; and a repair at this start writes it before anything else, so the loss
    // it may have caused is on disk before this process could lose it.
    private LibraryStateContext KeepWitnessInvariant(SettingsSnapshot settings, LibraryStateContext context)
    {
        if (context.CommitWitnessed)
        {
            return context;
        }

        if (settings.LibraryRows > 0)
        {
            if (_journal.EnsureWitness() == LibraryIoFailure.None)
            {
                TryLog(LogLevel.Warning, "Library state witness restored beside {Rows} stored library row(s).", settings.LibraryRows);
                return context with { CommitWitnessed = true };
            }
        }
        else if (context.DatabaseRepaired && !context.RunningOnDefaults && _journal.EnsureWitness() == LibraryIoFailure.None)
        {
            return context with { CommitWitnessed = true };
        }

        return context;
    }

    private LibraryRecoveryOutcome RecoverCore(SettingsSnapshot settings, LibraryStateContext context, List<LibraryKeptVersion> kept)
    {
        var outcome = _journal.Recover(
            settings.Generation, settings.GenerationLost, _live?.Manifest.Id, context.RunningOnDefaults, _parts.Time.GetUtcNow(), kept);

        // Readers need the committed generation's logical result (contract 6.6.5), so an attempt that did not see that
        // generation's own manifest is never taken to mean there is none. It did not see it when the pending-manifest
        // listing failed (round 4, A15), when it found the manifest but could not read it (round 3, A13), and never while
        // this process's own preparation of that generation is live after its commit (recovery skips it; ReadCatalog
        // applies it first). A manifest of that generation this process holds is applied whole: an earlier attempt's
        // unresolved one, or a Save whose outcome was unknown, with its redo images from the last complete read of them
        // when they cannot be read either (ApplyPending). Holding none, the service cannot tell whether a Save of that
        // generation is still pending or what it wrote, so every library is held back until an attempt lists and reads
        // the journal: the files as they stand may still hold what that Save replaced. A failed listing of set-aside
        // manifests alone says nothing about what is committed: it fences commits and orphan removal, and holds nothing back.
        var liveCommitted = _live is { } live && !settings.GenerationLost && live.Save.Generation == settings.Generation;
        if (outcome is { Unresolved: null } && !settings.GenerationLost && !liveCommitted &&
            (outcome.PendingUnlisted || outcome.StoredUnreadable is not null))
        {
            var id = outcome.StoredUnreadable?.ManifestId;
            bool Matches(LibraryManifest manifest) =>
                manifest.Generation == settings.Generation && (id is null || string.Equals(manifest.Id, id, StringComparison.OrdinalIgnoreCase));

            if (_recovery?.Unresolved is { } known && Matches(known))
            {
                outcome.Unresolved = known;
                outcome.FilesAwaitingRelease = Math.Max(outcome.FilesAwaitingRelease, _recovery.FilesAwaitingRelease);
            }
            else if (_unsettled is { } committed && Matches(committed.Manifest))
            {
                // Nothing of it is installed yet.
                outcome.Unresolved = committed.Manifest;
                outcome.FilesAwaitingRelease = Math.Max(outcome.FilesAwaitingRelease, Math.Max(1, committed.Manifest.Operations.Count));
            }
            else
            {
                outcome.StoredContentUnavailable = true;
            }
        }

        // A Save whose outcome was unknown is settled once recovery, with the generation read, has finished it, discarded
        // it or left it unresolved; a manifest recovery could not reach yet, or could not read whole, keeps it unsettled.
        if (_unsettled is { } unsettled && !outcome.PendingUnlisted && outcome.StoredUnreadable is null &&
            (outcome.Unresolved?.Id == unsettled.Manifest.Id || !ManifestPending(unsettled.Manifest)))
        {
            _unsettled = null;
        }

        // What was read of a manifest that is no longer pending is spare: this attempt listed every pending manifest and
        // the one read is neither unresolved, unreadable nor live, so recovery retired, discarded or set it aside just now,
        // or it left outside Scribe. Completion releases its own at once (ReleasePendingImages).
        if (_pendingImages is { } cached && !outcome.PendingUnlisted &&
            !string.Equals(outcome.StoredUnreadable?.ManifestId, cached.Manifest.Id, StringComparison.OrdinalIgnoreCase) &&
            !SameManifest(cached.Manifest, outcome.Unresolved) && !SameManifest(cached.Manifest, _live?.Manifest))
        {
            _pendingImages = null;
        }

        _recovery = outcome;
        NoteKept(kept);
        if (outcome.DidAnything)
        {
            TryLog(
                outcome.Held > 0 || outcome.FilesAwaitingRelease > 0 ? LogLevel.Warning : LogLevel.Information,
                "Library recovery: {Completed} completed, {Discarded} discarded, {SetAside} set aside, {Held} held, {Awaiting} awaiting release, {Kept} version(s) kept, {Orphans} orphan(s) removed, {OrphanBackups} orphan backup(s) kept ({Failure}).",
                outcome.Completed, outcome.Discarded, outcome.SetAside, outcome.Held, outcome.FilesAwaitingRelease, kept.Count,
                outcome.OrphansRemoved, outcome.OrphanBackupsKept, outcome.Failure);
            LogKept(kept);
        }

        return outcome;
    }

    private CatalogRead ReadCatalog(SettingsSnapshot settings, LibraryStateContext context, LibraryRecoveryOutcome? recovery)
    {
        var folder = _store.Read();

        // A pending manifest of the stored generation is applied whole: this process's live preparation once its commit
        // has happened, or one recovery could not finish. A held manifest is never applied (contract 6.6.7). When what
        // the stored generation holds cannot be told (round 3, A13; round 4, A15), every library is held back; that
        // verdict belongs to the generation it was reached for, so a later commit, this process's own Save included,
        // never inherits it.
        var pending = _live is { } live && !settings.GenerationLost && live.Save.Generation == settings.Generation
            ? live.Manifest
            : recovery?.Unresolved is { } unresolved && unresolved.Generation == settings.Generation ? unresolved : null;
        if (pending is not null)
        {
            ApplyPending(folder, pending);
        }
        else if (recovery is { StoredContentUnavailable: true } && !settings.GenerationLost && recovery.Generation == settings.Generation)
        {
            folder.CommittedContentUnavailable = true;
            foreach (var name in folder.Custom.Keys.ToList())
            {
                folder.Custom[name] = LibraryFileRead.Unavailable(name, recovery.Failure);
            }

            folder.HeldBack = folder.Custom.Count + BuiltInDictionaryLibraries.All.Count;
            if (recovery.PendingUnlisted)
            {
                TryLog(
                    LogLevel.Warning,
                    "The pending library manifests could not be listed right now ({Failure}); {Libraries} librar(ies) held back until they can.",
                    recovery.Failure, folder.HeldBack);
            }
            else
            {
                TryLog(
                    LogLevel.Warning,
                    "The committed library manifest could not be read right now ({Failure}); {Libraries} librar(ies) held back until it can.",
                    recovery.Failure, folder.HeldBack);
            }
        }

        var recentlyDeleted = RecentlyDeletedStore.List(folder, _parts.Codec);
        var fileIds = LibraryFileIds.Parse(settings.FileIdsRow);
        var ids = CustomLibraryStore.AssignIds(folder.Custom.Keys, fileIds, recentlyDeleted.Select(entry => entry.OriginalId));
        var libraries = new List<CatalogLibrary>();
        foreach (var shipped in BuiltInDictionaryLibraries.All)
        {
            libraries.Add(ReadBuiltIn(shipped, folder));
        }

        foreach (var (fileName, file) in folder.Custom)
        {
            if (ids.TryGetValue(fileName, out var id))
            {
                libraries.Add(ReadCustom(id, fileName, file));
            }
        }

        var ordered = LibraryPrecedence.Order(
            libraries, library => library.Content.Id, library => library.Content.BuiltIn, library => library.FileName);
        var identities = ordered.Select(Identity).ToList();
        var state = _parts.Composer.ReadLocalState(settings.DocumentEnabledIds, settings.StateRow, identities, context);
        LogStateHealth(state);

        var retired = new List<RetiredBuiltInEdits>();
        foreach (var (id, file) in folder.RetiredEdits)
        {
            if (file.Bytes is { } bytes && file.Hash is { } hash &&
                _parts.Overlay.ReadEdits(id, bytes) is { State: LibraryFileState.Available, Edits: { } edits })
            {
                retired.Add(new RetiredBuiltInEdits(id, _parts.Overlay.AuthoredTerms(edits), hash));
            }
        }

        var awaiting = folder.FilesAwaitingRelease + (recovery?.Held ?? 0);
        var catalog = new LibraryCatalog(
            settings.Generation,
            ordered,
            state,
            recentlyDeleted,
            retired,
            awaiting,
            [.. _keptVersions],
            awaiting > 0 ? recovery?.Failure ?? LibraryIoFailure.None : LibraryIoFailure.None);
        TryLog(
            LogLevel.Debug,
            "Loaded {Libraries} dictionary libraries at generation {Generation}: {Custom} custom, {Paused} paused, {Awaiting} awaiting release, {Deleted} in Recently deleted, {Skipped} file(s) skipped for a malformed name.",
            ordered.Count, settings.Generation, ordered.Count(library => !library.Content.BuiltIn),
            ordered.Count(library => library.State is LibraryFileState.Unreadable or LibraryFileState.Newer),
            ordered.Count(library => library.State == LibraryFileState.AwaitingRelease), recentlyDeleted.Count, folder.SkippedNames);

        var remaps = ids.Where(pair => !string.Equals(pair.Value, CustomLibraryStore.Stem(pair.Key), StringComparison.OrdinalIgnoreCase))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        return new CatalogRead(catalog, settings, context, folder, identities, remaps, fileIds.Health, recovery);
    }

    // Contract 6.6.5: written, created and restored targets read their redo images (awaiting release until their
    // operation is done), deleted and removed ones are absent, a deleted library's entry is listed from wherever it is,
    // and purged and restored entries leave Recently deleted. A redo image that is missing or fails its hash makes the
    // whole manifest untrustworthy, and then nothing of it is applied: the files as they stand are what readers see. One
    // that only cannot be read right now (round 3, A13) is taken from this process's last complete read of the manifest,
    // or from its target when that already holds exactly S; otherwise its library is held back, never read from a file
    // that may still hold what the committed generation replaced.
    private void ApplyPending(LibraryFolderSnapshot folder, LibraryManifest manifest)
    {
        var redo = new Dictionary<int, byte[]>();
        var unreadable = new Dictionary<int, LibraryIoFailure>();
        var cached = _pendingImages is { } images && SameManifest(images.Manifest, manifest) ? images.Redo : null;
        foreach (var operation in manifest.Operations.Where(operation => operation.HasPostImage))
        {
            switch (_journal.ReadRedo(manifest, operation, out var bytes, out var failure))
            {
                case RedoImagesState.Intact:
                    redo[operation.Number] = bytes!;
                    break;
                case RedoImagesState.Untrustworthy:
                    TryLog(LogLevel.Warning, "A pending library manifest is not applied: a redo image failed its check ({Failure}).", LibraryIoFailure.Corrupt);
                    return;
                default:
                    if (cached is not null && cached.TryGetValue(operation.Number, out var kept))
                    {
                        redo[operation.Number] = kept;
                    }
                    else
                    {
                        unreadable[operation.Number] = failure;
                    }

                    break;
            }
        }

        if (unreadable.Count == 0)
        {
            _pendingImages = new PendingImages(manifest, redo);
        }

        var installer = _journal.Installer(manifest, []);
        var heldBack = 0;
        foreach (var operation in manifest.Operations)
        {
            var done = installer.IsDone(operation);
            if (!done)
            {
                folder.FilesAwaitingRelease++;
            }

            switch (operation.Kind)
            {
                case LibraryOperationKind.Write or LibraryOperationKind.Create when operation.IsEditsTarget:
                {
                    var id = EditsId(operation.Target);
                    folder.Edits[id] = Committed(operation, folder.Edits.GetValueOrDefault(id), done, ref heldBack);
                    break;
                }

                case LibraryOperationKind.Write or LibraryOperationKind.Create or LibraryOperationKind.Restore:
                    folder.Custom[operation.Target] = Committed(operation, folder.Custom.GetValueOrDefault(operation.Target), done, ref heldBack);
                    if (operation.From is { } from)
                    {
                        folder.Deleted.Remove(from[LibraryManifest.DeletedPrefix.Length..]);
                    }

                    break;
                case LibraryOperationKind.Remove:
                    folder.Edits.Remove(EditsId(operation.Target));
                    if (operation.Replaced == LibraryEditsReplacement.Previous)
                    {
                        folder.PreviousEdits.Add(EditsId(operation.Target));
                    }

                    break;
                case LibraryOperationKind.Delete:
                {
                    var entryName = operation.To![LibraryManifest.DeletedPrefix.Length..];
                    if (folder.Custom.Remove(operation.Target, out var deleted) && !folder.Deleted.ContainsKey(entryName))
                    {
                        folder.Deleted[entryName] = deleted with { Name = operation.To };
                    }

                    if (operation.LibraryId is { } libraryId)
                    {
                        folder.DeletedIds[entryName] = libraryId;
                    }

                    break;
                }

                case LibraryOperationKind.Purge:
                    folder.Deleted.Remove(operation.Target[LibraryManifest.DeletedPrefix.Length..]);
                    break;
            }
        }

        if (heldBack > 0)
        {
            folder.HeldBack += heldBack;
            TryLog(
                LogLevel.Warning,
                "A pending library manifest's redo image(s) could not be read right now ({Failure}); {Libraries} librar(ies) held back until they can.",
                unreadable.Values.First(), heldBack);
        }

        // The committed bytes of a written, created or restored target.
        LibraryFileRead Committed(LibraryManifestOperation operation, LibraryFileRead? onDisk, bool done, ref int held)
        {
            if (redo.TryGetValue(operation.Number, out var committed))
            {
                return LibraryFileRead.Of(operation.Target, committed, !done);
            }

            if (onDisk is { Bytes: { } bytes, Hash: { } hash } && hash == operation.Staged)
            {
                return LibraryFileRead.Of(operation.Target, bytes, !done);
            }

            held++;
            return LibraryFileRead.Unavailable(operation.Target, unreadable.GetValueOrDefault(operation.Number));
        }
    }

    private static bool SameManifest(LibraryManifest manifest, LibraryManifest? other) =>
        other is not null && manifest.Generation == other.Generation && string.Equals(manifest.Id, other.Id, StringComparison.OrdinalIgnoreCase);

    // The redo images read of a pending manifest leave with it, on every path it leaves the pending state by: completion
    // releases its own here, and recovery's retirements, discards and set-asides release in RecoverCore. Never while the
    // manifest may still be the committed generation's: unresolved, live, or a Save whose outcome is unknown.
    private void ReleasePendingImages(LibraryManifest manifest)
    {
        if (_pendingImages is { } cached && SameManifest(cached.Manifest, manifest))
        {
            _pendingImages = null;
        }
    }

    private CatalogLibrary ReadCustom(string id, string fileName, LibraryFileRead file)
    {
        var fallbackName = BuiltInDictionaryLibraries.Humanize(CustomLibraryStore.Stem(fileName));
        if (file.CommittedUnavailable)
        {
            // The committed generation's bytes for this file cannot be read right now, or whether that generation wrote it
            // cannot be told (round 3, A13; round 4, A15): held back, with no rows, out of replacement and AI, and never the
            // file on disk, which may still hold what that Save replaced.
            return new CatalogLibrary(Empty(id, false, fallbackName, "Custom"), LibraryFileState.AwaitingRelease, fileName, null);
        }

        if (file.Bytes is { } bytes && file.Hash is { } hash)
        {
            var document = _parts.Codec.ReadManaged(bytes);
            var content = new LibraryContent(
                id, BuiltIn: false, document.Name ?? fallbackName, document.Category ?? "Custom", document.Description,
                [.. document.Terms.Select(LibraryRow.Custom)], document.BasedOn);
            _lastCustom[id] = new CustomRead(content, hash);
            var state = file.AwaitingRelease ? LibraryFileState.AwaitingRelease
                : document.Errors.Count > 0 ? LibraryFileState.PartlyReadable
                : LibraryFileState.Available;
            return new CatalogLibrary(
                content, state, fileName, hash, ReadErrorCount: state == LibraryFileState.PartlyReadable ? document.Errors.Count : 0);
        }

        if (file.Failure == LibraryIoFailure.SharingViolation)
        {
            // Another app holds the file open (review finding G10): the content this process last read stays in use, and
            // at a start that has read none the library supplies nothing, never a reset.
            return _lastCustom.TryGetValue(id, out var last)
                ? new CatalogLibrary(last.Content, LibraryFileState.AwaitingRelease, fileName, last.Hash)
                : new CatalogLibrary(Empty(id, false, fallbackName, "Custom"), LibraryFileState.AwaitingRelease, fileName, null);
        }

        return new CatalogLibrary(Empty(id, false, fallbackName, "Custom"), LibraryFileState.Unreadable, fileName, null);
    }

    private CatalogLibrary ReadBuiltIn(DictionaryLibrary shipped, LibraryFolderSnapshot folder)
    {
        var previous = folder.PreviousEdits.Contains(shipped.Id);
        if (folder.CommittedContentUnavailable ||
            (folder.Edits.TryGetValue(shipped.Id, out var committed) && committed.CommittedUnavailable))
        {
            // Its committed document, or whether the committed generation wrote one, cannot be read or told right now (round
            // 3, A13; round 4, A15): no rows at all, as for a lock at a fresh start, so neither older rows nor shipped ones
            // stand in for it, and Apply(shipped, null) is never called for a document that may exist.
            return new CatalogLibrary(BuiltInContent(shipped, []), LibraryFileState.AwaitingRelease, null, null, PreviousEditsAvailable: previous);
        }

        if (!folder.Edits.TryGetValue(shipped.Id, out var file))
        {
            if (folder.EditsUnlisted)
            {
                // edits\ could not be listed, so this built-in may have a document: it is read as a lock is (G10), never as
                // having none, which would bring back every row the user turned off (contract 3.1.4).
                return _lastBuiltIn.TryGetValue(shipped.Id, out var known)
                    ? new CatalogLibrary(BuiltInContent(shipped, known.Rows), LibraryFileState.AwaitingRelease, null, known.Hash, known.Edits, previous)
                    : new CatalogLibrary(BuiltInContent(shipped, []), LibraryFileState.AwaitingRelease, null, null, PreviousEditsAvailable: previous);
            }

            return new CatalogLibrary(
                BuiltInContent(shipped, _parts.Overlay.Apply(shipped, null)), LibraryFileState.Available, null, null,
                PreviousEditsAvailable: previous);
        }

        if (file.Bytes is { } bytes && file.Hash is { } hash)
        {
            var read = _parts.Overlay.ReadEdits(shipped.Id, bytes);
            if (read is { State: LibraryFileState.Available, Edits: { } edits })
            {
                var rows = _parts.Overlay.Apply(shipped, edits);
                _lastBuiltIn[shipped.Id] = new BuiltInRead(rows, hash, edits);
                return new CatalogLibrary(
                    BuiltInContent(shipped, rows),
                    file.AwaitingRelease ? LibraryFileState.AwaitingRelease : LibraryFileState.Available,
                    null, hash, edits, previous);
            }

            // Unreadable or newer: only this built-in pauses, and its document is never rewritten without a recovery.
            var paused = read.State is LibraryFileState.Newer ? LibraryFileState.Newer : LibraryFileState.Unreadable;
            return new CatalogLibrary(BuiltInContent(shipped, []), paused, null, hash, PreviousEditsAvailable: previous);
        }

        if (file.Failure == LibraryIoFailure.SharingViolation)
        {
            // Locked at a fresh start: no rows at all, so a term the user turned off never comes back.
            return _lastBuiltIn.TryGetValue(shipped.Id, out var last)
                ? new CatalogLibrary(BuiltInContent(shipped, last.Rows), LibraryFileState.AwaitingRelease, null, last.Hash, last.Edits, previous)
                : new CatalogLibrary(BuiltInContent(shipped, []), LibraryFileState.AwaitingRelease, null, null, PreviousEditsAvailable: previous);
        }

        return new CatalogLibrary(BuiltInContent(shipped, []), LibraryFileState.Unreadable, null, null, PreviousEditsAvailable: previous);
    }

    private static LibraryContent BuiltInContent(DictionaryLibrary shipped, IReadOnlyList<LibraryRow> rows) =>
        new(shipped.Id, BuiltIn: true, shipped.Name, shipped.Category, shipped.Description, rows);

    private static LibraryContent Empty(string id, bool builtIn, string name, string category) =>
        new(id, builtIn, name, category, null, []);

    // --- adoption and publication -------------------------------------------------------------------------------------

    // Contract 3.1.1: adoption is committed as a generation of its own only when no preparation is live, nothing is
    // unresolved or held, and the session runs on the user's settings; otherwise, or when the commit fails, the adopted
    // state is used in memory and planned again at the next load. Returns the read to use and the catalog as stored.
    private (CatalogRead Read, LibraryCatalog Stored) Adopt(CatalogRead read)
    {
        var plan = _parts.Composer.PlanAdoption(read.Catalog, read.Context);
        if (plan is null)
        {
            return (read, read.Catalog);
        }

        var committable = _live is null && _unsettled is null && read.Recovery is not { Unresolved: not null } &&
                          (read.Recovery?.Held ?? 0) == 0 && !read.Context.RunningOnDefaults &&
                          plan.State.Health != LocalStateHealth.Newer && read.Folder.ListingFailure == LibraryIoFailure.None;

        // The witness comes first (contract 6.9); when it cannot be written, nothing is committed at this point.
        if (committable && _journal.EnsureWitness() == LibraryIoFailure.None)
        {
            // Contract 2.10: a narrowing is in force at the latest at its commit. The adopted state's scope becomes the
            // scope every hand-off also needs before the transaction starts (round 2, A5); whatever the adoption widens
            // waits for the publication after the commit, which clears it, as a failed commit's in-memory use does.
            NarrowBeforeCommit(WithState(read.Catalog, plan.State));
            try
            {
                _settings.CommitLibraryState(AdoptionPayload(read, plan));
                TryLog(
                    LogLevel.Information,
                    "Adopted {Libraries} librar(ies) into the library state ({Reasons}; {Markers} legacy marker(s)).",
                    plan.LibrariesAdopted, plan.Reasons, plan.MarkersAdded);
                var settings = ReadSettings();
                var committed = ReadCatalog(settings, KeepWitnessInvariant(settings, ContextFor(settings)), read.Recovery);
                return (committed, committed.Catalog);
            }
            catch (Exception ex)
            {
                TryLog(LogLevel.Warning, "Library state adoption was not committed ({Failure}); it is used in memory.", FailureShape.Describe(ex));
            }
        }

        return (read with { Catalog = WithState(read.Catalog, plan.State) }, read.Catalog);
    }

    // The scope a commit about to happen leaves, in force under the permission gate before it happens; it only ever
    // narrows (a hand-off needs the published scope too). A composition that fails hands nothing over until the next
    // publication, which is the direction permission fails in.
    private void NarrowBeforeCommit(LibraryCatalog after)
    {
        AiVocabularyScope scope;
        try
        {
            scope = _parts.Composer.ComposeVocabulary(after).AiScope;
        }
        catch (Exception ex)
        {
            TryLog(LogLevel.Warning, "The scope of a library state commit could not be computed ({Failure}); nothing is handed over.", FailureShape.Describe(ex));
            scope = AiVocabularyScope.None;
        }

        lock (_gate)
        {
            _savingScope = scope;
        }
    }

    private LibrarySavePayload AdoptionPayload(CatalogRead read, LibraryAdoption plan)
    {
        var encoding = _parts.Composer.EncodeLocalState(plan.State, startingState: null, read.Identities, read.Identities);
        var values = new List<LibrarySettingValue>();
        if (encoding.StateValue is { } state)
        {
            values.Add(new LibrarySettingValue(LibrarySettingKeys.State, state));
        }

        if (read.FileIdsHealth != LibraryFileIdsHealth.Newer)
        {
            values.Add(new LibrarySettingValue(LibrarySettingKeys.FileIds, LibraryFileIds.Write(read.Remaps)));
        }

        // The document's list is patched only when it changes as a set (review finding A5).
        var list = SameIds(encoding.EnabledLibraryIds, read.Settings.DocumentEnabledIds) ? null : encoding.EnabledLibraryIds;
        return new LibrarySavePayload(read.Settings.Generation, read.Settings.Generation + 1, list, values);
    }

    // Publishes the vocabulary of catalog, unless it equals the one published. The published scope never widens without a
    // commit: an adoption held in memory that would grant permission is composed with those grants denied, so what it
    // publishes is covered by the stored state's scope (contract 3.1.1, the permission gate).
    private void Publish(LibraryCatalog catalog, LibraryCatalog stored)
    {
        var vocabulary = _parts.Composer.ComposeVocabulary(catalog);
        if (!ReferenceEquals(catalog, stored))
        {
            vocabulary = WithoutUncommittedGrants(catalog, stored, vocabulary);
        }

        var current = _vocabulary;
        lock (_gate)
        {
            // The scope while saving holds while a preparation is live or its outcome unknown (round 2, A8).
            if (_live is null && _unsettled is null)
            {
                _savingScope = null;
            }

            if (current is not null && SameVocabulary(current, vocabulary))
            {
                return;
            }

            _vocabulary = vocabulary;
            _publishedScope = vocabulary.AiScope;
        }

        _pendingChanged.Add(vocabulary.Generation);
    }

    private LibraryVocabulary WithoutUncommittedGrants(LibraryCatalog adopted, LibraryCatalog stored, LibraryVocabulary vocabulary)
    {
        var before = _parts.Composer.ComposeVocabulary(stored).AiScope;
        var widened = vocabulary.AiScope.PermittedContent
            .Where(pair => !before.PermittedContent.TryGetValue(pair.Key, out var content) || content != pair.Value)
            .Select(pair => pair.Key)
            .ToList();
        if (widened.Count == 0)
        {
            return vocabulary;
        }

        var state = adopted.LocalState;
        var denied = LibraryLocalState.Create(
            state.EnabledIds, state.LegacyEnabledIds,
            state.AiPermissions.Concat(widened.Select(id => new KeyValuePair<string, bool>(id, false))),
            state.LegacyMarkers, state.AiUpgradeNotice, state.Health, state.AcceptedContent, state.AiPermissionsLost);
        return _parts.Composer.ComposeVocabulary(WithState(adopted, denied));
    }

    private static bool SameVocabulary(LibraryVocabulary a, LibraryVocabulary b) =>
        a.Generation == b.Generation && a.Entries.SequenceEqual(b.Entries) && a.AiEntries.SequenceEqual(b.AiEntries) &&
        a.AiScope.Covers(b.AiScope) && b.AiScope.Covers(a.AiScope);

    private static LibraryCatalog WithState(LibraryCatalog catalog, LibraryLocalState state) => new(
        catalog.Generation, catalog.Libraries, state, catalog.RecentlyDeleted, catalog.RetiredBuiltInEdits,
        catalog.FilesAwaitingRelease, catalog.KeptVersions, catalog.PendingFailure);

    private void TryPublish()
    {
        try
        {
            LoadCore(adopt: true, recover: false);
        }
        catch (Exception ex)
        {
            // What changed on disk stands; the vocabulary is published at the next load.
            TryLog(LogLevel.Warning, "The library vocabulary was not published ({Failure}).", FailureShape.Describe(ex));
        }
    }

    // Whether a manifest is still on disk under its pending name; a check that fails answers yes, which keeps it unsettled.
    private bool ManifestPending(LibraryManifest manifest)
    {
        try
        {
            return _parts.Files.Exists(_journal.ManifestPath(manifest));
        }
        catch (Exception ex)
        {
            LogFileFailure(LibraryFileOperation.Read, ex);
            return true;
        }
    }

    private void RaisePendingChanged()
    {
        long[] generations;
        lock (_libraryLock)
        {
            if (_pendingChanged.Count == 0)
            {
                return;
            }

            generations = [.. _pendingChanged];
            _pendingChanged.Clear();
        }

        foreach (var generation in generations)
        {
            ResilientEvent.InvokeAll(Changed, generation, ex =>
                TryLog(LogLevel.Warning, "A library vocabulary subscriber failed ({Failure}).", FailureShape.DescribeWithStack(ex)));
        }
    }

    // --- prepare --------------------------------------------------------------------------------------------------------

    private LibraryPrepareResult PrepareCore(LibraryChangeSet changes)
    {
        if (_live is not null)
        {
            return Refused(LibraryPrepareStatus.Busy);
        }

        CatalogRead read;
        try
        {
            var settings = ReadSettings();
            var context = KeepWitnessInvariant(settings, ContextFor(settings));

            // The next attempt finishes the earlier work first; anything it leaves unresolved or held blocks the Save.
            var recovery = RecoverCore(settings, context, []);
            if (recovery.Unresolved is not null || recovery.Held > 0)
            {
                return Refused(
                    LibraryPrepareStatus.PreviousSaveUnfinished,
                    recovery.Failure == LibraryIoFailure.None ? LibraryIoFailure.Other : recovery.Failure);
            }

            if (_unsettled is not null)
            {
                return Refused(LibraryPrepareStatus.PreviousSaveUnfinished);
            }

            read = ReadCatalog(settings, context, recovery);
        }
        catch (Exception ex)
        {
            if (_unsettled is not null)
            {
                // An earlier Save's outcome is still unknown: it is settled before anything else commits.
                TryLog(LogLevel.Warning, "Library save not prepared ({Status}; {Failure}).", LibraryPrepareStatus.PreviousSaveUnfinished, FailureShape.Describe(ex));
                return Refused(LibraryPrepareStatus.PreviousSaveUnfinished);
            }

            TryLog(LogLevel.Warning, "Library save not prepared ({Status}; {Failure}).", LibraryPrepareStatus.Failed, FailureShape.Describe(ex));
            return Refused(LibraryPrepareStatus.Failed, LibraryIoFailure.Other);
        }

        if (read.Catalog.LocalState.Health == LocalStateHealth.Newer || changes.LocalState.Health == LocalStateHealth.Newer)
        {
            return Refused(LibraryPrepareStatus.ReadOnly);
        }

        if (changes.BaseGeneration != read.Catalog.Generation)
        {
            return Refused(LibraryPrepareStatus.Stale);
        }

        if (changes.IsEmpty)
        {
            return Refused(LibraryPrepareStatus.NothingToSave);
        }

        return PrepareFrom(read, changes, wrapper: false);
    }

    private LibraryPrepareResult PrepareFrom(CatalogRead read, LibraryChangeSet changes, bool wrapper)
    {
        var started = _parts.Time.GetTimestamp();

        // A folder that could not be listed may hold files this read never saw, so no pre-image check could be trusted.
        if (read.Folder.ListingFailure != LibraryIoFailure.None)
        {
            return Refused(LibraryPrepareStatus.Failed, read.Folder.ListingFailure);
        }

        // The witness comes first (contract 6.9): no commit of this version may precede it.
        var witness = _journal.EnsureWitness();
        if (witness != LibraryIoFailure.None)
        {
            return Refused(LibraryPrepareStatus.Failed, witness);
        }

        LibrarySavePlan plan;
        LibrarySavePayload payload;
        try
        {
            plan = LibrarySavePlanner.Plan(
                read.Catalog, read.Folder, read.Remaps, read.Identities, changes, _parts, _store.ReadRelative, _parts.Time.GetUtcNow());
            if (plan.OutsideEditIds.Count > 0)
            {
                return new LibraryPrepareResult(LibraryPrepareStatus.OutsideEdit, null, plan.OutsideEditIds);
            }

            if (plan.ReadFailure != LibraryIoFailure.None || plan.Encoding is not { } encoding)
            {
                return Refused(LibraryPrepareStatus.Failed, plan.ReadFailure == LibraryIoFailure.None ? LibraryIoFailure.Other : plan.ReadFailure);
            }

            var values = new List<LibrarySettingValue>();
            if (encoding.StateValue is { } state)
            {
                values.Add(new LibrarySettingValue(LibrarySettingKeys.State, state));
            }

            if (read.FileIdsHealth != LibraryFileIdsHealth.Newer)
            {
                values.Add(new LibrarySettingValue(LibrarySettingKeys.FileIds, LibraryFileIds.Write(plan.RemapsAfter)));
            }

            // A wrapper commits through CommitLibraryState, which patches the document only when the list changes as a
            // set (review finding A5); a Save always carries the list it writes with the state row (A17).
            var list = wrapper && SameIds(encoding.EnabledLibraryIds, read.Settings.DocumentEnabledIds) ? null : encoding.EnabledLibraryIds;
            payload = new LibrarySavePayload(read.Catalog.Generation, read.Catalog.Generation + 1, list, values);
        }
        catch (Exception ex)
        {
            TryLog(LogLevel.Warning, "Library save not prepared ({Status}; {Failure}).", LibraryPrepareStatus.Failed, FailureShape.Describe(ex));
            return Refused(LibraryPrepareStatus.Failed, LibraryIoFailure.Other);
        }

        var manifest = new LibraryManifest(
            _parts.NewManifestId().ToString("N"), read.Catalog.Generation + 1, read.Catalog.Generation, plan.Prepared, plan.Operations);
        var written = _journal.Write(manifest, plan.RedoImages);
        if (written != LibraryIoFailure.None)
        {
            return Refused(LibraryPrepareStatus.Failed, written);
        }

        var prepared = new PreparedLibrarySave(changes.DraftRevision, payload, plan.Counts);
        _live = new LivePreparation(prepared, manifest, started);

        // The scope while saving is in force before the prepare returns, so a revocation holds before the commit.
        AiVocabularyScope saving;
        try
        {
            saving = _parts.Composer.ScopeWhileSaving(read.Catalog, changes);
        }
        catch (Exception ex)
        {
            TryLog(LogLevel.Warning, "The scope while saving could not be computed ({Failure}); nothing is handed over.", FailureShape.Describe(ex));
            saving = AiVocabularyScope.None;
        }

        lock (_gate)
        {
            _savingScope = saving;
        }

        return new LibraryPrepareResult(LibraryPrepareStatus.Prepared, prepared, []);
    }

    private static LibraryPrepareResult Refused(LibraryPrepareStatus status, LibraryIoFailure failure = LibraryIoFailure.None) =>
        new(status, null, [], failure);

    // --- complete -------------------------------------------------------------------------------------------------------

    private LibrarySaveOutcome CompleteCore(PreparedLibrarySave prepared, bool wrapper)
    {
        var live = _live;
        if (live is null || !ReferenceEquals(live.Save, prepared))
        {
            // Not this process's live preparation (completed already): report by the stored state, installing nothing.
            try
            {
                var catalog = LoadCore(adopt: false, recover: true).Catalog;
                var status = catalog.Generation == prepared.Generation
                    ? catalog.FilesAwaitingRelease > 0 ? LibrarySaveStatus.AppliedAwaitingRelease : LibrarySaveStatus.Applied
                    : catalog.Generation == prepared.BaseGeneration ? LibrarySaveStatus.NotCommitted : LibrarySaveStatus.Superseded;
                return new LibrarySaveOutcome(status, catalog.Generation, catalog.FilesAwaitingRelease, [], catalog.PendingFailure);
            }
            catch (Exception)
            {
                // The stored generation could not be read, so where the Save stands is not known either.
                return new LibrarySaveOutcome(LibrarySaveStatus.CommitUnknown, prepared.BaseGeneration, 0, [], LibraryIoFailure.None);
            }
        }

        long stored;
        try
        {
            stored = SettingsRepository.ParseLibraryGeneration(_settings.Get(LibrarySettingKeys.Generation));
        }
        catch (Exception ex)
        {
            // Whether the commit happened cannot be told now (round 2, A8): never NotCommitted without evidence. The
            // manifest stays pending for recovery to settle by the stored generation once it can be read, the narrowed
            // scope stays, and new Saves and the wrappers are fenced until then.
            _live = null;
            _unsettled = live;
            TryLog(
                LogLevel.Warning,
                "Library save outcome unknown for generation {Generation}: the stored generation could not be read ({Failure}); {Operations} prepared operation(s) kept pending.",
                prepared.Generation, FailureShape.Describe(ex), live.Manifest.Operations.Count);
            return new LibrarySaveOutcome(LibrarySaveStatus.CommitUnknown, prepared.BaseGeneration, 0, [], LibraryIoFailure.None);
        }

        var kept = new List<LibraryKeptVersion>();
        if (stored == prepared.Generation)
        {
            var completion = _journal.Complete(live.Manifest, kept);
            _live = null;
            NoteKept(kept);
            LogKept(kept);
            if (completion.Retired)
            {
                ReleasePendingImages(live.Manifest);
                var counts = prepared.Counts;
                TryLog(
                    wrapper ? LogLevel.Debug : LogLevel.Information,
                    "Saved library changes: generation {Generation}, {Created} created, {Updated} updated, {Deleted} deleted, {Restored} restored, {Terms} terms, {ElapsedMs} ms.",
                    prepared.Generation, counts.Created, counts.Updated, counts.Deleted, counts.Restored, counts.Terms,
                    (long)_parts.Time.GetElapsedTime(live.StartedTimestamp).TotalMilliseconds);
            }
            else
            {
                // Committed and unfinished: the stored generation's manifest keeps the libraries unresolved until an
                // attempt finishes it, and readers see it applied.
                _recovery = new LibraryRecoveryOutcome
                {
                    Unresolved = live.Manifest,
                    FilesAwaitingRelease = Math.Max(1, completion.Pending),
                    Failure = completion.Failure,
                };
                TryLog(
                    LogLevel.Warning,
                    "Library save unfinished at generation {Generation}: {Pending} file(s) not in place ({Failure}).",
                    prepared.Generation, completion.Pending, completion.Failure);
            }

            TryPublish();
            return new LibrarySaveOutcome(
                completion.Retired ? LibrarySaveStatus.Applied : LibrarySaveStatus.AppliedAwaitingRelease,
                stored, completion.Pending, kept, completion.Retired ? LibraryIoFailure.None : completion.Failure);
        }

        // Not committed: the preparation, which never installed anything, is discarded, and the committed scope is back.
        _live = null;
        var discarded = _journal.Discard(live.Manifest, kept, out _);
        ReleasePendingImages(live.Manifest);
        NoteKept(kept);
        var notCommitted = stored == prepared.BaseGeneration;
        TryLog(
            LogLevel.Information,
            "Library save not committed ({Status}); {Operations} prepared operation(s) discarded.",
            notCommitted ? LibrarySaveStatus.NotCommitted : LibrarySaveStatus.Superseded,
            discarded ? live.Manifest.Operations.Count : 0);
        lock (_gate)
        {
            _savingScope = null;
        }

        if (!notCommitted)
        {
            TryPublish();
        }

        return new LibrarySaveOutcome(notCommitted ? LibrarySaveStatus.NotCommitted : LibrarySaveStatus.Superseded, stored, 0, kept);
    }

    // --- the wrappers --------------------------------------------------------------------------------------------------

    // The same rules as a Save (contract 3.1.1): refused while a Save is live or the committed generation is unresolved,
    // while the session runs on defaults (nothing library-related is written automatically then, section 0 rule 8), and
    // while the library state is a newer version's.
    private CatalogRead ReadForWrapper()
    {
        const string Unfinished = "A library change is still being saved.";
        if (_live is not null)
        {
            throw new InvalidOperationException(Unfinished);
        }

        SettingsSnapshot settings;
        try
        {
            settings = ReadSettings();
        }
        catch (Exception) when (_unsettled is not null)
        {
            // An earlier Save's outcome is still unknown (round 2, A8).
            throw new InvalidOperationException(Unfinished);
        }

        var context = KeepWitnessInvariant(settings, ContextFor(settings));
        if (context.RunningOnDefaults)
        {
            throw new InvalidOperationException("Save your settings first.");
        }

        var recovery = RecoverCore(settings, context, []);
        if (recovery.Unresolved is not null || recovery.Held > 0 || _unsettled is not null)
        {
            throw new InvalidOperationException(Unfinished);
        }

        var read = ReadCatalog(settings, context, recovery);
        if (read.Catalog.LocalState.Health == LocalStateHealth.Newer)
        {
            throw new InvalidOperationException("Libraries were changed by a newer version of Scribe.");
        }

        return read;
    }

    // One journal save of one operation, committed with CommitLibraryState rather than a Settings Save, and settled by the
    // stored generation like any Save: a commit that succeeded but reported an error still stands and is not a failure.
    private void CommitWrapper(CatalogRead read, LibraryChangeSet changes)
    {
        LibraryText.RequireWellFormed(changes);
        var prepared = PrepareFrom(read, changes, wrapper: true);
        if (prepared is not { Status: LibraryPrepareStatus.Prepared, Save: { } save })
        {
            throw new InvalidOperationException(prepared.Status == LibraryPrepareStatus.OutsideEdit
                ? "That library changed on disk while it was being saved. Try again."
                : "The library change could not be saved.");
        }

        Exception? commitFailure = null;
        try
        {
            _settings.CommitLibraryState(save.Payload);
        }
        catch (Exception ex)
        {
            commitFailure = ex;
        }

        var outcome = CompleteCore(save, wrapper: true);
        switch (outcome.Status)
        {
            case LibrarySaveStatus.Applied:
            case LibrarySaveStatus.AppliedAwaitingRelease:
                return;

            case LibrarySaveStatus.CommitUnknown:
                // Whether it committed is not known yet (round 2, A8): never reported as a change that failed, which would
                // invite doing it again; the manifest stays pending and a later read of the generation settles it.
                throw new InvalidOperationException(
                    "Scribe couldn't confirm that the library change was saved. It will finish saving it.");

            case LibrarySaveStatus.NotCommitted:
            case LibrarySaveStatus.Superseded:
            default:
                if (commitFailure is not null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(commitFailure);
                }

                throw new InvalidOperationException("The library change could not be saved.");
        }
    }

    // --- the janitor (contract 3.1.6) -----------------------------------------------------------------------------------

    private LibraryJanitorResult RunJanitor(DateTimeOffset nowUtc)
    {
        LibraryJanitorResult result;
        lock (_libraryLock)
        {
            result = RunJanitorCore(nowUtc);
        }

        RaisePendingChanged();
        return result;
    }

    private LibraryJanitorResult RunJanitorCore(DateTimeOffset nowUtc)
    {
        SettingsSnapshot settings;
        LibraryStateContext context;
        try
        {
            settings = ReadSettings();
            context = KeepWitnessInvariant(settings, ContextFor(settings));
        }
        catch (Exception ex)
        {
            TryLog(LogLevel.Warning, "Library retention could not read the settings store ({Failure}).", FailureShape.Describe(ex));
            return new LibraryJanitorResult(0, 0, 1);
        }

        // Retries an unresolved committed manifest and every held one first; recovery follows the defaults rules itself.
        var recovery = RecoverCore(settings, context, []);
        if (recovery.ChangedFiles || _publishedHeldBack)
        {
            TryPublish();
        }

        if (context.RunningOnDefaults)
        {
            return default;
        }

        var deleted = 0;
        var quarantined = 0;
        var failed = 0;
        if (ProtectedEntries(out var protectedEntries))
        {
            foreach (var path in SafeList(_paths.LibraryDeletedDir, "*.csv"))
            {
                var name = Path.GetFileName(path);
                // An entry is expired only when the catalog lists it, which a name that is not well-formed never is.
                if (!protectedEntries.Contains(name) && LibraryText.IsWellFormed(name) &&
                    RecentlyDeletedStore.TryParseEntryName(name, out var deletedUtc, out _, out _) &&
                    LibraryJanitor.RecentlyDeletedExpired(deletedUtc, nowUtc))
                {
                    if (TryDeleteFile(path, LibraryFileOperation.Purge))
                    {
                        deleted++;
                    }
                    else
                    {
                        failed++;
                    }
                }
            }
        }

        foreach (var name in _journal.ListSetAside(out _))
        {
            if (LibraryJanitor.QuarantineExpired(name.Stamp, nowUtc))
            {
                if (_journal.ExpireSetAside(name))
                {
                    quarantined++;
                }
                else
                {
                    failed++;
                }
            }
        }

        if (deleted > 0 || quarantined > 0 || failed > 0)
        {
            TryLog(
                failed > 0 ? LogLevel.Warning : LogLevel.Information,
                "Library retention: {Deleted} Recently deleted entr(ies) and {Quarantined} quarantined manifest(s) removed, {Failed} failed.",
                deleted, quarantined, failed);
        }

        if (deleted > 0)
        {
            TryPublish();
        }

        return new LibraryJanitorResult(deleted, quarantined, failed);
    }

    // Entries a live or pending manifest names are never the janitor's to remove (rule R6). A pending manifest that
    // cannot be read could name any of them, so retention waits until recovery has dealt with it.
    private bool ProtectedEntries(out HashSet<string> names)
    {
        names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var manifests = new List<LibraryManifest>();
        if (_live is { } live)
        {
            manifests.Add(live.Manifest);
        }

        var pending = _journal.ListManifests(out var failure);
        if (failure != LibraryIoFailure.None)
        {
            return false;
        }

        foreach (var name in pending)
        {
            if (_live is { } current && string.Equals(current.Manifest.Id, name.ManifestId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (_journal.Read(name, out _)?.Manifest is not { } manifest)
            {
                return false;
            }

            manifests.Add(manifest);
        }

        foreach (var operation in manifests.SelectMany(manifest => manifest.Operations))
        {
            foreach (var path in (string?[])[operation.To, operation.From, operation.Kind == LibraryOperationKind.Purge ? operation.Target : null])
            {
                if (path is not null && path.StartsWith(LibraryManifest.DeletedPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    names.Add(path[LibraryManifest.DeletedPrefix.Length..]);
                }
            }
        }

        return true;
    }

    // --- helpers --------------------------------------------------------------------------------------------------------

    private static LibraryIdentity Identity(CatalogLibrary library) =>
        new(library.Content.Id, library.Content.BuiltIn, library.Content.BuiltIn ? null : library.FileName);

    private static DictionaryLibrary ToLegacyLibrary(CatalogLibrary library)
    {
        var entries = library.Content.Rows.Select(row => row.Values.ToEntry()).ToList();
        return library.Content.BuiltIn
            ? new DictionaryLibrary(library.Content.Id, library.Content.Name, library.Content.Category, library.Content.Description, true, entries)
            : new DictionaryLibrary(
                Identity(library).LegacyId, library.Content.Name, library.Content.Category, library.Content.Description, false, entries)
            {
                FileName = library.FileName,
            };
    }

    private static string EditsId(string target) =>
        Path.GetFileNameWithoutExtension(target[LibraryManifest.EditsPrefix.Length..]);

    private static bool SameIds(IReadOnlyList<string> encoded, IReadOnlyList<string>? stored) =>
        stored is not null &&
        new HashSet<string>(encoded, StringComparer.OrdinalIgnoreCase).SetEquals(new HashSet<string>(stored, StringComparer.OrdinalIgnoreCase));

    // Every id an imported library may not take: built-in ids, every library's, every file's stem, and every id in
    // Recently deleted, so a restore never meets an import under its old id.
    private static HashSet<string> TakenIds(CatalogRead read)
    {
        var taken = new HashSet<string>(CustomLibraryStore.BuiltInIds, StringComparer.OrdinalIgnoreCase);
        taken.UnionWith(read.Catalog.Libraries.Select(library => library.Content.Id));
        taken.UnionWith(read.Folder.TopLevelNames.Select(Path.GetFileNameWithoutExtension).OfType<string>());
        taken.UnionWith(read.Catalog.RecentlyDeleted.Select(entry => entry.OriginalId));
        return taken;
    }

    private void NoteKept(List<LibraryKeptVersion> kept)
    {
        foreach (var version in kept)
        {
            if (!_keptVersions.Contains(version))
            {
                _keptVersions.Add(version);
            }
        }
    }

    private void LogKept(List<LibraryKeptVersion> kept)
    {
        foreach (var group in kept.GroupBy(version => version.Kind))
        {
            TryLog(LogLevel.Warning, "Kept {Count} library version(s) instead of overwriting them ({Kind}).", group.Count(), group.Key);
        }
    }

    private void LogStateHealth(LibraryLocalState state)
    {
        var shape = (state.Health, state.AiPermissionsLost);
        if (_loggedState == shape)
        {
            return;
        }

        _loggedState = shape;
        if (state.Health is LocalStateHealth.Unreadable or LocalStateHealth.Newer || state.AiPermissionsLost)
        {
            TryLog(
                LogLevel.Warning,
                "Library state is {Health} (permissions lost: {PermissionsLost}); AI cleanup receives no library vocabulary until the user chooses again.",
                state.Health, state.AiPermissionsLost);
        }
    }

    private static LibraryRecoveryResult ToResult(LibraryRecoveryOutcome outcome, List<LibraryKeptVersion> kept) => new(
        outcome.Completed, outcome.Discarded, outcome.SetAside, outcome.FilesAwaitingRelease, outcome.OrphansRemoved,
        [.. kept], outcome.Failure, outcome.Held);

    private List<string> SafeList(string directory, string pattern)
    {
        try
        {
            return [.. _parts.Files.EnumerateFiles(directory, pattern)];
        }
        catch (Exception ex)
        {
            LogFileFailure(LibraryFileOperation.Enumerate, ex);
            return [];
        }
    }

    private bool TryDeleteFile(string path, LibraryFileOperation operation)
    {
        try
        {
            _parts.Files.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            LogFileFailure(operation, ex);
            return false;
        }
    }

    // The one line a failed file operation writes (review finding G8): the operation by name and the failure by its
    // shape, never a path, a file name or an id, which LogCallScanner enforces.
    private void LogFileFailure(LibraryFileOperation operation, Exception exception)
    {
        try
        {
            _logger.LogWarning("Library file operation {Operation} failed: {Failure}", operation, FailureShape.Describe(exception));
        }
        catch (Exception)
        {
            // Diagnostics never turn a file problem into a failed load or save (pattern P-4).
        }
    }

    private void TryLog(LogLevel level, string message, params object?[] args)
    {
        try
        {
            _logger.Log(level, message, args);
        }
        catch (Exception)
        {
            // Diagnostics never turn a file problem into a failed load or save (pattern P-4).
        }
    }
}
