using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Scribe.Core.Libraries;
using Scribe.Core.PostProcessing;

namespace Scribe.Core.Settings;

/// <summary>
/// One term row as the Libraries editor holds it: a stable identity for selection and undo, the row, and the two facts
/// about it only the editor tracks.
/// </summary>
/// <param name="RowId">Unique within the workspace and stable across edits; never reused. Not stored anywhere.</param>
/// <param name="Row">The row as the draft holds it.</param>
/// <param name="RemovalIntent">
/// The user chose "Use as a removal rule" for a row whose Written value is empty, so the empty value is meant (review
/// finding I1). Only matters while Written is empty.
/// </param>
/// <param name="LegacyEmpty">
/// The row came from a saved file or an import with an empty Written value, which is a removal rule already and stays
/// valid; it stops counting as one once the user types a Written value.
/// </param>
public sealed record DraftTermRow(long RowId, LibraryRow Row, bool RemovalIntent, bool LegacyEmpty);

/// <summary>Why a draft library cannot be saved as it is, or why an edit was refused.</summary>
public enum LibraryValidationKind
{
    /// <summary>A row has a Written value and no Spoken value.</summary>
    WrittenWithoutSpoken,

    /// <summary>A Spoken value is repeated in one library, turned-off rows included.</summary>
    DuplicateSpoken,

    /// <summary>A new or cleared Written value is left empty without "Use as a removal rule" (review finding I1).</summary>
    EmptyWrittenWithoutIntent,

    /// <summary>A new or changed Spoken or Written value is longer than <see cref="LibraryLimits.MaxFieldLength"/>.</summary>
    FieldTooLong,

    /// <summary>A library would grow past <see cref="LibraryLimits.MaxTermsPerLibrary"/> terms.</summary>
    TooManyTerms,

    /// <summary>A typed library name is blank.</summary>
    EmptyName,

    /// <summary>A typed library name is already another library's.</summary>
    DuplicateName,

    /// <summary>A typed name, category or description holds a double quote (review finding A11).</summary>
    MetadataDoubleQuote,

    /// <summary>The header a Save would write for this library does not read back in 0.4.3 (review finding A11).</summary>
    MetadataUnreadableInOlder,

    /// <summary>
    /// The content of a library that is partly readable, awaiting release, unreadable or newer cannot be edited, because
    /// saving it would rewrite a file this version cannot reproduce whole.
    /// </summary>
    ContentNotSaveable,
}

/// <summary>A validation problem, with the row and field to focus.</summary>
/// <param name="LibraryId">The library.</param>
/// <param name="RowId">The row, or null for a problem with the library itself.</param>
/// <param name="Kind">What is wrong.</param>
/// <param name="Field">The term field to focus, or <see cref="TermFields.None"/>.</param>
/// <param name="Metadata">The metadata field to focus, for a name, category or description problem.</param>
public sealed record LibraryValidationIssue(
    string LibraryId,
    long? RowId,
    LibraryValidationKind Kind,
    TermFields Field,
    LibraryMetadataField Metadata = LibraryMetadataField.None);

/// <summary>What an edit did.</summary>
/// <param name="Applied">
/// The draft now holds what was asked for (true also when it already did). False when the edit was refused: the state
/// is read-only, the library's content cannot be saved, a typed name or detail was refused, or an import plan was
/// computed for another revision.
/// </param>
/// <param name="Issue">
/// The problem to show beside the field: why the edit was refused, or, for an applied term edit, the first problem the
/// row now has, which blocks Save until it is fixed. Null when there is none.
/// </param>
public sealed record LibraryEditResult(bool Applied, LibraryValidationIssue? Issue);

/// <summary>What capturing the draft for a Save produced.</summary>
/// <param name="ChangeSet">The change set, or null when <paramref name="Issues"/> block the Save.</param>
/// <param name="Issues">Every problem in the libraries the Save would write, in precedence and row order.</param>
public sealed record LibraryCaptureResult(LibraryChangeSet? ChangeSet, IReadOnlyList<LibraryValidationIssue> Issues);

/// <summary>
/// The Libraries page's unsaved state: a draft over one committed catalog, the user's operations on it with their undo
/// history, and the change set a Save commits. The only producer of <see cref="LibraryDraft"/> and
/// <see cref="LibraryChangeSet"/> (review finding R12).
/// </summary>
/// <remarks>
/// <para>
/// Pure and single-threaded: no I/O, no clock, no logging; the shell drives it on the dispatcher. Every operation that
/// changes the draft moves <see cref="Revision"/> forward, so a preview or an import plan computed for an earlier revision
/// is recognizably stale; an operation that changes nothing moves nothing.
/// </para>
/// <para>
/// A Save is the sequence of plan 3.5: <see cref="CaptureChangeSet"/> validates the libraries the change set writes (never
/// an untouched legacy library) and returns an immutable change set with the revision it captured; after the commit,
/// <see cref="MarkSaved"/> takes the new catalog and keeps every edit made after that revision unsaved (review finding
/// R7). <see cref="Reload"/> discards the draft; <see cref="Rebase"/> takes a newer catalog and keeps the draft's edits.
/// </para>
/// <para>
/// Undo covers the structural operations (delete, turn off or on, restore built-in values, removal intent, import into a
/// library, use this copy instead, and the dictionary cleanup's switch-offs) until Save or Cancel. Undoing one puts back
/// what it changed wherever nothing changed it since, and never overwrites a later change: a term the user edited after
/// turning it off keeps the edit, and a step with nothing left it could put back drops out of the history, so
/// <see cref="UndoLabel"/> names what Undo will do. Typed values are not in the history; the text box has its own undo
/// while it is being edited.
/// </para>
/// <para>
/// Decision 2 arrives as a delegate (<c>LibraryDecisions.DefaultAiPermission</c> in production), so this type never
/// encodes which libraries AI cleanup may use by default. The one place it needs the permission a library currently has
/// is where a new library inherits it (a duplicate, a retired built-in's kept rows) and where Use these choices records
/// what is shown; that follows the policy's documented order (an unhealthy state permits nothing, content that is not the
/// accepted one is not permitted, an explicit choice decides, a lost state denies, then the kind default: a built-in's
/// from the delegate, a custom library's off), which the integration commit can point at <c>AiVocabularyPolicy</c>.
/// </para>
/// </remarks>
public sealed class LibraryWorkspace
{
    private const string DefaultCategory = "Custom";

    private readonly IBuiltInLibraryOverlay _overlay;
    private readonly Func<LibraryOrigin, bool, bool?, bool> _defaultAiPermission;
    private readonly Func<string, DictionaryLibrary?> _shippedLibrary;
    private readonly List<UndoEntry> _undo = [];
    private readonly List<UndoEntry> _redo = [];
    private readonly Dictionary<long, Capture> _captures = [];

    private LibraryCatalog _committed;
    private State _base;
    private State _state;
    private long _revision = 1;
    private long _nextRowId = 1;
    private LibraryDraft? _draft;
    private Differences? _differences;

    /// <summary>A workspace over <paramref name="committed"/>, with Decision 2 as <paramref name="defaultAiPermission"/>.</summary>
    /// <param name="committed">The committed catalog the draft starts from.</param>
    /// <param name="overlay">Every change to a built-in row goes through it.</param>
    /// <param name="defaultAiPermission">
    /// Decision 2: the AI permission a library starts with, from its origin, whether it is a built-in, and the permission
    /// of the library it came from (a duplicate's original, a retired built-in), or null.
    /// </param>
    public LibraryWorkspace(
        LibraryCatalog committed, IBuiltInLibraryOverlay overlay, Func<LibraryOrigin, bool, bool?, bool> defaultAiPermission)
        : this(committed, overlay, defaultAiPermission, ShippedLibrary)
    {
    }

    /// <summary>For tests: the shipped libraries come from <paramref name="shippedLibrary"/> instead of the embedded CSVs.</summary>
    internal LibraryWorkspace(
        LibraryCatalog committed,
        IBuiltInLibraryOverlay overlay,
        Func<LibraryOrigin, bool, bool?, bool> defaultAiPermission,
        Func<string, DictionaryLibrary?> shippedLibrary)
    {
        ArgumentNullException.ThrowIfNull(committed);
        ArgumentNullException.ThrowIfNull(overlay);
        ArgumentNullException.ThrowIfNull(defaultAiPermission);
        ArgumentNullException.ThrowIfNull(shippedLibrary);
        _overlay = overlay;
        _defaultAiPermission = defaultAiPermission;
        _shippedLibrary = shippedLibrary;
        _committed = committed;
        _base = FromCatalog(committed, mapFrom: null);
        _state = _base;
    }

    /// <summary>The draft's revision; every change, undo and redo moves it forward, and it never goes back.</summary>
    public long Revision => _revision;

    /// <summary>The draft at <see cref="Revision"/>, for previews. A new instance whenever the revision moves.</summary>
    public LibraryDraft Draft => _draft ??= BuildDraft();

    /// <summary>
    /// Whether a Save would change anything: a library differs from the committed catalog (content, metadata, enabled
    /// state, AI permission or legacy markers), or the upgrade notice, the lost-permissions notice or Recently deleted
    /// does. An untouched new library is no change.
    /// </summary>
    public bool HasUnsavedChanges => Diff.HasChanges;

    /// <summary>
    /// The committed library state comes from a newer version of Scribe (decision 6): every operation that would change
    /// the change set is refused.
    /// </summary>
    public bool IsReadOnly => _committed.LocalState.Health == LocalStateHealth.Newer;

    /// <summary>The libraries that differ from the committed catalog, in precedence order.</summary>
    public IReadOnlyList<string> UnsavedLibraryIds => Diff.UnsavedIds;

    /// <summary>A library's rows in saved order, blank placeholders included.</summary>
    public IReadOnlyList<DraftTermRow> RowsOf(string libraryId) => Get(libraryId).Rows;

    /// <summary>
    /// Whether the content of a library (its rows, name, category and description) can be edited here: it exists, is not
    /// deleted in the draft, the state is not read-only, its file is available, and no recovery is waiting for Save. A
    /// library for which this is false can still be turned on or off and have its AI permission changed.
    /// </summary>
    public bool CanEditContent(string libraryId) => ContentEditable(Get(libraryId));

    /// <summary>Whether <see cref="Undo"/> would change something.</summary>
    public bool CanUndo => FindApplicable(_undo, undoing: true) >= 0;

    /// <summary>Whether <see cref="Redo"/> would change something.</summary>
    public bool CanRedo => FindApplicable(_redo, undoing: false) >= 0;

    /// <summary>The operation <see cref="Undo"/> would undo ("Delete term", "Turn off library"), for the notice's Undo; null when none.</summary>
    public string? UndoLabel
    {
        get
        {
            var index = FindApplicable(_undo, undoing: true);
            return index < 0 ? null : _undo[index].Label;
        }
    }

    /// <summary>
    /// New library: "New library" made unique, an id fixed from it (<see cref="LibraryNaming.NewCustomId"/>), on, AI
    /// permission from Decision 2. Until the user changes it, it is not saved and is no change.
    /// </summary>
    /// <exception cref="InvalidOperationException">The state is read-only.</exception>
    public string CreateLibrary()
    {
        EnsureWritable();
        var name = LibraryNaming.NewLibraryName(Names(exceptId: null));
        var id = LibraryNaming.NewCustomId(name, TakenIds(includeRecentlyDeleted: true));
        var ai = _defaultAiPermission(LibraryOrigin.Created, false, null);
        var header = NewHeader(id, name, DefaultCategory, description: null, basedOn: null, LibraryOrigin.Created)
            with { Created = new Creation(name, ai) };
        Change(_state.WithLib(new Lib(header, [])).WithEnabled(id, true).WithAi(id, ai));
        return id;
    }

    /// <summary>
    /// Renames a custom library, the name committed with <see cref="LibraryMetadata.Commit"/>. Refused for a typed double
    /// quote, a blank name and a name another library has; the id and the file name never change. Passing the name it
    /// has changes nothing, so an untouched legacy name is kept as it is.
    /// </summary>
    public LibraryEditResult Rename(string libraryId, string name)
    {
        var lib = GetCustomForMetadata(libraryId);
        if (IsReadOnly)
        {
            return Refused();
        }

        if (string.Equals(name, lib.Header.Name, StringComparison.Ordinal))
        {
            return Accepted();
        }

        if (!ContentEditable(lib))
        {
            return Refused(lib, LibraryValidationKind.ContentNotSaveable);
        }

        if (LibraryMetadata.CheckTyped(name) == LibraryMetadataProblem.DoubleQuote)
        {
            return Refused(lib, LibraryValidationKind.MetadataDoubleQuote, LibraryMetadataField.Name);
        }

        var committed = LibraryMetadata.Commit(name);
        if (committed.Length == 0)
        {
            return Refused(lib, LibraryValidationKind.EmptyName, LibraryMetadataField.Name);
        }

        if (string.Equals(committed, lib.Header.Name, StringComparison.Ordinal))
        {
            return Accepted();
        }

        if (LibraryNaming.IsNameTaken(committed, Names(exceptId: lib.Header.Id)))
        {
            return Refused(lib, LibraryValidationKind.DuplicateName, LibraryMetadataField.Name);
        }

        Change(_state.WithLib(lib with { Header = lib.Header with { Name = committed } }));
        return Accepted();
    }

    /// <summary>
    /// Sets a custom library's category (blank means "Custom") and description (blank means none), a changed value
    /// committed with <see cref="LibraryMetadata.Commit"/> and refused when it holds a typed double quote. A value passed
    /// as the library has it is kept as it is, so changing one detail never forces a rewrite of an untouched legacy other.
    /// </summary>
    public LibraryEditResult SetDetails(string libraryId, string category, string? description)
    {
        var lib = GetCustomForMetadata(libraryId);
        if (IsReadOnly)
        {
            return Refused();
        }

        var header = lib.Header;
        var sameCategory = string.Equals(category, header.Category, StringComparison.Ordinal);
        var sameDescription = string.Equals(description ?? string.Empty, header.Description ?? string.Empty, StringComparison.Ordinal);
        if (sameCategory && sameDescription)
        {
            return Accepted();
        }

        if (!ContentEditable(lib))
        {
            return Refused(lib, LibraryValidationKind.ContentNotSaveable);
        }

        if (!sameCategory && LibraryMetadata.CheckTyped(category) == LibraryMetadataProblem.DoubleQuote)
        {
            return Refused(lib, LibraryValidationKind.MetadataDoubleQuote, LibraryMetadataField.Category);
        }

        if (!sameDescription && LibraryMetadata.CheckTyped(description) == LibraryMetadataProblem.DoubleQuote)
        {
            return Refused(lib, LibraryValidationKind.MetadataDoubleQuote, LibraryMetadataField.Description);
        }

        var committedCategory = sameCategory ? header.Category : LibraryMetadata.Commit(category);
        if (committedCategory.Length == 0)
        {
            committedCategory = DefaultCategory;
        }

        var committedDescription = sameDescription ? header.Description : LibraryMetadata.Commit(description);
        var updated = header with
        {
            Category = committedCategory,
            Description = string.IsNullOrEmpty(committedDescription) ? null : committedDescription,
        };
        if (updated != header)
        {
            Change(_state.WithLib(lib with { Header = updated }));
        }

        return Accepted();
    }

    /// <summary>Use this library: on or off, by logical id, for any library whatever its file state. Undoable.</summary>
    public void SetEnabled(string libraryId, bool enabled)
    {
        var lib = GetLive(libraryId);
        if (IsReadOnly)
        {
            return;
        }

        Structural(enabled ? "Turn on library" : "Turn off library", _state.WithEnabled(lib.Header.Id, enabled));
    }

    /// <summary>
    /// Use in AI cleanup, recorded as the library's explicit choice. Refused while read-only. Allowed for a library whose
    /// content cannot be edited.
    /// </summary>
    public LibraryEditResult SetAiPermission(string libraryId, bool permitted)
    {
        var lib = GetLive(libraryId);
        if (IsReadOnly)
        {
            return Refused();
        }

        Change(_state.WithAi(lib.Header.Id, permitted));
        return Accepted();
    }

    /// <summary>
    /// Use these choices, after the AI permissions were lost (review finding A3): records an explicit choice for every
    /// library, as its check box shows it (unchecked for one with no choice), and clears the loss. The only way the loss
    /// is cleared; nothing while no loss is recorded.
    /// </summary>
    public void ConfirmAiPermissions()
    {
        if (IsReadOnly || !_state.Local.Lost)
        {
            return;
        }

        var ai = _state.Local.Ai.ToBuilder();
        foreach (var lib in _state.Libraries.Values)
        {
            ai[lib.Header.Id] = ShownAi(_state, lib);
        }

        Change(_state with { Local = _state.Local with { Ai = ai.ToImmutable(), Lost = false } });
    }

    /// <summary>
    /// Duplicate: a custom copy of the library's rows as the draft holds them (turned-off rows included), named
    /// "GitHub - Copy" made unique, off, <c># based-on:</c> the original, and AI permission inherited through Decision 2.
    /// </summary>
    /// <exception cref="InvalidOperationException">The state is read-only, or the library is deleted in the draft.</exception>
    public string Duplicate(string libraryId)
    {
        EnsureWritable();
        var source = GetLive(libraryId);
        var name = LibraryNaming.CopyName(source.Header.Name, Names(exceptId: null));
        var id = LibraryNaming.NewCustomId(name, TakenIds(includeRecentlyDeleted: true));
        var rows = source.Rows
            .Where(row => !IsBlank(row))
            .Select(row => NewRow(LibraryRow.Custom(row.Row.Values), legacyEmpty: row.LegacyEmpty || row.RemovalIntent))
            .ToImmutableList();
        var ai = _defaultAiPermission(LibraryOrigin.Duplicated, false, ShownAi(_state, source));
        var header = NewHeader(
            id, name, source.Header.Category, source.Header.Description, basedOn: source.Header.Id, LibraryOrigin.Duplicated);
        Change(_state.WithLib(new Lib(header, rows)).WithEnabled(id, false).WithAi(id, ai));
        return id;
    }

    /// <summary>Use this copy instead: the copy on and its original off, as one undoable step.</summary>
    /// <exception cref="InvalidOperationException">The library is not a copy of one the draft has.</exception>
    public void UseCopyInstead(string copyId)
    {
        var copy = GetLive(copyId);
        if (IsReadOnly)
        {
            return;
        }

        var original = copy.Header.BuiltIn || copy.Header.BasedOn is null ? null : Find(copy.Header.BasedOn);
        if (original is null || original.Header.PendingDelete)
        {
            throw new InvalidOperationException("Only a copy of a library in this draft can be used instead of it.");
        }

        Structural(
            "Use this copy instead",
            _state.WithEnabled(copy.Header.Id, true).WithEnabled(original.Header.Id, false));
    }

    /// <summary>
    /// Delete library, for a custom library: a committed one moves to Recently deleted when the draft is saved; one the
    /// draft added leaves the draft (a restore is cancelled, so its entry stays in Recently deleted). Undoable.
    /// </summary>
    /// <exception cref="InvalidOperationException">The library is a built-in.</exception>
    public void DeleteLibrary(string libraryId)
    {
        var lib = Get(libraryId);
        if (lib.Header.BuiltIn)
        {
            throw new InvalidOperationException("Built-in libraries can't be deleted. Turn them off instead.");
        }

        if (IsReadOnly || lib.Header.PendingDelete)
        {
            return;
        }

        Structural(
            "Delete library",
            lib.Header.Committed is null
                ? _state.WithoutLib(lib.Header.Id)
                : _state.WithLib(lib with { Header = lib.Header with { PendingDelete = true } }));
    }

    /// <summary>
    /// Restore library, from the content <c>ILibraryCatalogStore.ReadRecentlyDeleted</c> read and checked (review finding
    /// A8): added to the draft as <see cref="LibraryOrigin.Restored"/> under its original id, or a new one when that is
    /// taken now, never over another library; both check boxes off. It can be previewed, exported and edited before Save.
    /// </summary>
    /// <exception cref="ArgumentException">The entry is not in the committed Recently deleted, or has no hash.</exception>
    /// <exception cref="InvalidOperationException">The state is read-only, or the entry is already restored or deleted.</exception>
    public string RestoreDeleted(RecentlyDeletedContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        EnsureWritable();
        var entry = content.Entry;
        if (entry.ContentHash is null)
        {
            throw new ArgumentException("A restore needs the hash of the entry it was read from.", nameof(content));
        }

        var listed = FindRecentlyDeleted(entry.EntryName)
            ?? throw new ArgumentException("That entry is not in Recently deleted.", nameof(content));
        if (IsRestoring(listed.EntryName) || _state.Local.Purges.ContainsKey(listed.EntryName))
        {
            throw new InvalidOperationException("That entry is already restored or deleted in this draft.");
        }

        var originalId = entry.OriginalId?.Trim() ?? string.Empty;
        var taken = new HashSet<string>(TakenIds(includeRecentlyDeleted: false), StringComparer.OrdinalIgnoreCase);
        var id = originalId.Length > 0 && !taken.Contains(originalId)
            ? originalId
            : LibraryNaming.NewCustomId(content.Content.Name, TakenIds(includeRecentlyDeleted: true));
        var rows = content.Content.Rows
            .Select(row => NewRow(
                row.Origin == TermOrigin.Custom ? row : LibraryRow.Custom(row.Values),
                legacyEmpty: row.Values.Written.Length == 0))
            .ToImmutableList();
        var header = NewHeader(
                id, content.Content.Name, content.Content.Category, content.Content.Description, content.Content.BasedOn,
                LibraryOrigin.Restored)
            with { FileState = content.State, RestoredFrom = content };
        var ai = _defaultAiPermission(LibraryOrigin.Restored, false, null);
        Change(_state.WithLib(new Lib(header, rows)).WithEnabled(id, false).WithAi(id, ai));
        return id;
    }

    /// <summary>Delete permanently: the entry is removed from Recently deleted when the draft is saved.</summary>
    /// <exception cref="ArgumentException">The entry is not in the committed Recently deleted.</exception>
    /// <exception cref="InvalidOperationException">The entry is being restored in this draft.</exception>
    public void DeletePermanently(RecentlyDeletedLibrary entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var listed = FindRecentlyDeleted(entry.EntryName)
            ?? throw new ArgumentException("That entry is not in Recently deleted.", nameof(entry));
        if (IsReadOnly || _state.Local.Purges.ContainsKey(listed.EntryName))
        {
            return;
        }

        if (IsRestoring(listed.EntryName))
        {
            throw new InvalidOperationException("That entry is being restored in this draft.");
        }

        Change(_state with { Local = _state.Local with { Purges = _state.Local.Purges.SetItem(listed.EntryName, listed) } });
    }

    /// <summary>
    /// Discard unsaved changes to this library: back to the committed version, with its enabled state, AI permission,
    /// legacy markers and upgrade notice; a library the draft added leaves the draft.
    /// </summary>
    public void DiscardLibrary(string libraryId)
    {
        var lib = Get(libraryId);
        if (IsReadOnly)
        {
            return;
        }

        var id = lib.Header.Id;
        Change(_base.Libraries.TryGetValue(id, out var committed)
            ? _state.WithLib(committed).WithLocalOf(id, _base.Local)
            : _state.WithoutLib(id));
    }

    /// <summary>Dismisses the one-time notice listing the custom libraries kept on for AI cleanup at the upgrade.</summary>
    public void DismissAiUpgradeNotice()
    {
        if (IsReadOnly || _state.Local.Notice.IsEmpty)
        {
            return;
        }

        Change(_state with { Local = _state.Local with { Notice = _state.Local.Notice.Clear() } });
    }

    /// <summary>
    /// Keeps the rows the user authored in a built-in that no longer ships as a new custom library (based on the retired
    /// id, AI permission inherited through Decision 2, on when the retired built-in was on). Returns the id; asking again
    /// for the same retired built-in returns the library this draft already made.
    /// </summary>
    /// <exception cref="ArgumentException">The catalog has no retired built-in with that id.</exception>
    public string KeepRetiredBuiltIn(string retiredLibraryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(retiredLibraryId);
        EnsureWritable();
        var retired = _committed.RetiredBuiltInEdits.FirstOrDefault(edits =>
                string.Equals(edits.LibraryId, retiredLibraryId.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("The catalog has no retired built-in with that id.", nameof(retiredLibraryId));
        var kept = _state.Libraries.Values.FirstOrDefault(lib =>
            lib.Header.Origin == LibraryOrigin.RetiredBuiltIn
            && string.Equals(lib.Header.BasedOn, retired.LibraryId, StringComparison.OrdinalIgnoreCase));
        if (kept is not null)
        {
            return kept.Header.Id;
        }

        var name = LibraryNaming.UniqueName(BuiltInDictionaryLibraries.Humanize(retired.LibraryId), Names(exceptId: null));
        var id = LibraryNaming.NewCustomId(name, TakenIds(includeRecentlyDeleted: true));
        var rows = retired.AuthoredTerms
            .Select(values => NewRow(LibraryRow.Custom(values), legacyEmpty: values.Written.Length == 0))
            .ToImmutableList();
        var inherited = ShownAi(_state, retired.LibraryId, builtIn: true, committed: null, rewritten: false);
        var ai = _defaultAiPermission(LibraryOrigin.RetiredBuiltIn, false, inherited);
        var enabled = _committed.LocalState.EnabledIds.Contains(retired.LibraryId);
        var header = NewHeader(id, name, DefaultCategory, description: null, basedOn: retired.LibraryId, LibraryOrigin.RetiredBuiltIn);
        Change(_state.WithLib(new Lib(header, rows)).WithEnabled(id, enabled).WithAi(id, ai));
        return id;
    }

    /// <summary>
    /// A recovery for a paused built-in (its edits document unreadable or from a newer version): Restore the previous
    /// copy or Back up and reset built-in edits, applied by the Save; <see cref="BuiltInEditsRecovery.None"/> cancels
    /// one. Never offered for a document another app holds open.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The library is not a paused built-in, or Restore the previous copy was asked for with no previous copy.
    /// </exception>
    public void RecoverBuiltIn(string libraryId, BuiltInEditsRecovery recovery)
    {
        var lib = GetLive(libraryId);
        if (!lib.Header.BuiltIn)
        {
            throw new InvalidOperationException("Only a built-in library's edits can be recovered.");
        }

        if (IsReadOnly)
        {
            return;
        }

        if (recovery != BuiltInEditsRecovery.None && lib.Header.FileState is not (LibraryFileState.Unreadable or LibraryFileState.Newer))
        {
            throw new InvalidOperationException("Only a paused built-in library can be recovered.");
        }

        if (recovery == BuiltInEditsRecovery.RestorePrevious && lib.Header.Committed?.PreviousEditsAvailable != true)
        {
            throw new InvalidOperationException("There is no previous copy of this library's edits to restore.");
        }

        Change(_state.WithLib(lib with { Header = lib.Header with { Recovery = recovery } }));
    }

    /// <summary>
    /// Applies the dictionary cleanup's plan to the libraries: every library in <paramref name="switchingOff"/> that the
    /// plan does not keep on (<see cref="LibrarySwitchOffCopy.Result.KeepsOn"/>) is turned off, by logical id, as
    /// ordinary enabled-state changes in one undoable step; a library the plan keeps on keeps its tick. The plan's copies
    /// go into the dictionary rows, never into a library. Returns the ids turned off, in the order given.
    /// </summary>
    /// <exception cref="ArgumentException">A library in <paramref name="switchingOff"/> is not in the draft.</exception>
    public IReadOnlyList<string> ApplyDictionaryCleanup(IEnumerable<LibraryUsage> switchingOff, LibrarySwitchOffCopy.Result plan)
    {
        ArgumentNullException.ThrowIfNull(switchingOff);
        ArgumentNullException.ThrowIfNull(plan);
        var requested = switchingOff.Where(usage => usage is not null).ToList();
        var libraries = new List<Lib>(requested.Count);
        foreach (var usage in requested)
        {
            var lib = Find(usage.Id);
            if (lib is null || lib.Header.BuiltIn != usage.BuiltIn)
            {
                throw new ArgumentException("The cleanup names a library this draft does not have.", nameof(switchingOff));
            }

            libraries.Add(lib);
        }

        if (IsReadOnly)
        {
            return [];
        }

        var next = _state;
        var switchedOff = new List<string>();
        foreach (var lib in libraries)
        {
            var id = lib.Header.Id;
            if (plan.KeepsOn(id, lib.Header.BuiltIn) || lib.Header.PendingDelete || !next.Local.Enabled.Contains(id))
            {
                continue;
            }

            next = next.WithEnabled(id, false);
            switchedOff.Add(id);
        }

        Structural("Turn off unused libraries", next);
        return switchedOff;
    }

    /// <summary>
    /// Adds a term, its values committed (<see cref="LibraryEditor.Commit"/>): a custom row, or through the overlay an
    /// added row of a built-in. A problem the new row has (an empty Written value, a repeat) is returned and blocks Save;
    /// growing past <see cref="LibraryLimits.MaxTermsPerLibrary"/> is refused.
    /// </summary>
    public LibraryEditResult AddTerm(string libraryId, TermValues values, bool removalIntent = false)
    {
        ArgumentNullException.ThrowIfNull(values);
        var lib = GetLive(libraryId);
        if (IsReadOnly)
        {
            return Refused();
        }

        if (!ContentEditable(lib))
        {
            return Refused(lib, LibraryValidationKind.ContentNotSaveable);
        }

        var committed = LibraryEditor.Commit(values);
        if (!IsBlank(committed) && CountTerms(lib.Rows) + 1 > TermLimit(lib))
        {
            return Refused(lib, LibraryValidationKind.TooManyTerms);
        }

        var row = NewRow(
            lib.Header.BuiltIn ? _overlay.Add(committed) : LibraryRow.Custom(committed),
            legacyEmpty: false,
            removalIntent: removalIntent && committed.Written.Length == 0);
        var updated = lib with { Rows = lib.Rows.Add(row) };
        Change(_state.WithLib(updated));
        return new LibraryEditResult(true, RowIssue(updated, row));
    }

    /// <summary>
    /// Edits a term, its values committed (<see cref="LibraryEditor.Commit"/>). A custom row is re-keyed by its new Spoken
    /// value; a built-in row goes through the overlay and keeps its key. A value edit removes the row's legacy marker.
    /// <paramref name="removalIntent"/> true is "Use as a removal rule" for an empty Written value; an intent already
    /// given stays while Written stays empty. Setting only the intent is undoable.
    /// </summary>
    public LibraryEditResult EditTerm(string libraryId, long rowId, TermValues values, bool removalIntent = false)
    {
        ArgumentNullException.ThrowIfNull(values);
        var lib = GetLive(libraryId);
        var index = IndexOfRow(lib, rowId);
        if (IsReadOnly)
        {
            return Refused();
        }

        if (!ContentEditable(lib))
        {
            return Refused(lib, LibraryValidationKind.ContentNotSaveable);
        }

        var current = lib.Rows[index];
        var committed = LibraryEditor.Commit(values);
        var emptyWritten = committed.Written.Length == 0;
        var intent = (removalIntent || current.RemovalIntent) && emptyWritten;
        if (committed == current.Row.Values)
        {
            if (intent == current.RemovalIntent)
            {
                return new LibraryEditResult(true, RowIssue(lib, current));
            }

            var withIntent = current with { RemovalIntent = intent };
            var marked = lib with { Rows = lib.Rows.SetItem(index, withIntent) };
            Structural("Use as a removal rule", _state.WithLib(marked));
            return new LibraryEditResult(true, RowIssue(marked, withIntent));
        }

        var edited = current with
        {
            Row = lib.Header.BuiltIn ? _overlay.Edit(current.Row, committed) : LibraryRow.Custom(committed),
            RemovalIntent = intent,
            LegacyEmpty = current.LegacyEmpty && emptyWritten,
        };
        var updated = lib with { Rows = lib.Rows.SetItem(index, edited) };
        var next = _state.WithLib(updated);
        if (!lib.Header.BuiltIn && TextChanged(current.Row.Values, committed))
        {
            next = next.WithoutMarker(lib.Header.Id, current.Row.Key);
        }

        Change(next);
        return new LibraryEditResult(true, RowIssue(updated, edited));
    }

    /// <summary>Delete term, for a row the user owns: a custom library's row, or an added or no-longer-shipped built-in row. Undoable.</summary>
    /// <exception cref="InvalidOperationException">
    /// The row is a shipped, edited, pinned or turned-off built-in row (those are turned off, never deleted), or the
    /// library's content cannot be edited.
    /// </exception>
    public void DeleteTerm(string libraryId, long rowId)
    {
        var lib = GetLive(libraryId);
        var index = IndexOfRow(lib, rowId);
        if (IsReadOnly)
        {
            return;
        }

        EnsureContentEditable(lib);
        if (lib.Header.BuiltIn && lib.Rows[index].Row.Origin is not (TermOrigin.Added or TermOrigin.NoLongerShipped))
        {
            throw new InvalidOperationException("A built-in term is turned off, never deleted.");
        }

        Structural("Delete term", _state.WithLib(lib with { Rows = lib.Rows.RemoveAt(index) }));
    }

    /// <summary>Turn off term or Turn on term; a built-in row through the overlay. Undoable.</summary>
    /// <exception cref="InvalidOperationException">The library's content cannot be edited.</exception>
    public void SetTermEnabled(string libraryId, long rowId, bool enabled)
    {
        var lib = GetLive(libraryId);
        var index = IndexOfRow(lib, rowId);
        if (IsReadOnly)
        {
            return;
        }

        EnsureContentEditable(lib);
        var current = lib.Rows[index];
        if (current.Row.Values.Enabled == enabled)
        {
            return;
        }

        var row = lib.Header.BuiltIn
            ? _overlay.SetEnabled(current.Row, enabled)
            : LibraryRow.Custom(current.Row.Values with { Enabled = enabled });
        Structural(
            enabled ? "Turn on term" : "Turn off term",
            _state.WithLib(lib with { Rows = lib.Rows.SetItem(index, current with { Row = row }) }));
    }

    /// <summary>Restore built-in values: an authored built-in row back to what this version ships. Undoable.</summary>
    /// <exception cref="InvalidOperationException">
    /// The library is custom, its content cannot be edited, or the row has no shipped counterpart (delete it instead).
    /// </exception>
    public void RestoreBuiltInValues(string libraryId, long rowId)
    {
        var lib = GetLive(libraryId);
        var index = IndexOfRow(lib, rowId);
        if (!lib.Header.BuiltIn)
        {
            throw new InvalidOperationException("Only a built-in library's terms have built-in values.");
        }

        if (IsReadOnly)
        {
            return;
        }

        EnsureContentEditable(lib);
        var current = lib.Rows[index];
        var restored = _overlay.RestoreShipped(current.Row)
            ?? throw new InvalidOperationException("This term has no built-in values to restore; delete it instead.");
        if (restored == current.Row)
        {
            return;
        }

        var row = current with { Row = restored, RemovalIntent = false, LegacyEmpty = restored.Values.Written.Length == 0 };
        Structural("Restore built-in values", _state.WithLib(lib with { Rows = lib.Rows.SetItem(index, row) }));
    }

    /// <summary>
    /// Restore all built-in values: every row back to what this version ships, added rows gone, and the Save removes the
    /// edits document rather than writing one, so entries with no row here (an off intent for a row this version does
    /// not ship) go too. Edits made afterwards start a new document. Undoable.
    /// </summary>
    /// <exception cref="InvalidOperationException">The library is custom, or its content cannot be edited.</exception>
    public void RestoreAllBuiltInValues(string libraryId)
    {
        var lib = GetLive(libraryId);
        if (!lib.Header.BuiltIn)
        {
            throw new InvalidOperationException("Only a built-in library has built-in values.");
        }

        if (IsReadOnly)
        {
            return;
        }

        EnsureContentEditable(lib);
        var rows = ImmutableList.CreateBuilder<DraftTermRow>();
        foreach (var row in lib.Rows)
        {
            if (row.Row.Origin == TermOrigin.Shipped)
            {
                rows.Add(row);
            }
            else if (_overlay.RestoreShipped(row.Row) is { } restored)
            {
                rows.Add(row with { Row = restored, RemovalIntent = false, LegacyEmpty = restored.Values.Written.Length == 0 });
            }
        }

        var reset = lib with { Header = lib.Header with { EditsReset = true }, Rows = rows.ToImmutable() };
        if (SameRows(reset.Rows, lib.Rows) && (lib.Header.EditsReset || lib.Header.Committed?.Edits is null))
        {
            return;
        }

        Structural("Restore all built-in values", _state.WithLib(reset));
    }

    /// <summary>Keep my changes or Use updated values, for a built-in row whose shipped values changed where the user's did.</summary>
    /// <exception cref="InvalidOperationException">The library is custom, or its content cannot be edited.</exception>
    public void ResolveReview(string libraryId, long rowId, TermReviewChoice choice)
    {
        var lib = GetLive(libraryId);
        var index = IndexOfRow(lib, rowId);
        if (!lib.Header.BuiltIn)
        {
            throw new InvalidOperationException("Only a built-in library's terms have reviews.");
        }

        if (IsReadOnly)
        {
            return;
        }

        EnsureContentEditable(lib);
        var current = lib.Rows[index];
        var resolved = _overlay.ResolveReview(current.Row, choice);
        if (resolved == current.Row)
        {
            return;
        }

        Change(_state.WithLib(lib with { Rows = lib.Rows.SetItem(index, current with { Row = resolved }) }));
    }

    /// <summary>
    /// Use my spelling: removes the row's legacy marker (Decision 1), so its own value competes as an authored term. Only
    /// the local state changes, so it is allowed for a library whose content cannot be edited.
    /// </summary>
    public void UseMySpelling(string libraryId, long rowId)
    {
        var lib = GetLive(libraryId);
        var row = lib.Rows[IndexOfRow(lib, rowId)];
        if (IsReadOnly)
        {
            return;
        }

        Change(_state.WithoutMarker(lib.Header.Id, row.Row.Key));
    }

    /// <summary>
    /// Turn off in these libraries: every enabled row of <paramref name="otherLibraryIds"/> with this row's spoken form is
    /// turned off, as one undoable step. The dictionary is not touched.
    /// </summary>
    /// <exception cref="ArgumentException">An id is not in the draft.</exception>
    /// <exception cref="InvalidOperationException">One of the libraries' content cannot be edited.</exception>
    public void TurnOffInOtherLibraries(string libraryId, long rowId, IReadOnlyList<string> otherLibraryIds)
    {
        ArgumentNullException.ThrowIfNull(otherLibraryIds);
        var source = GetLive(libraryId);
        var key = LibraryTermKey.From(source.Rows[IndexOfRow(source, rowId)].Row.Values.Spoken);
        var targets = otherLibraryIds
            .Where(id => !string.IsNullOrWhiteSpace(id) && !string.Equals(id.Trim(), source.Header.Id, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(GetLive)
            .ToList();
        foreach (var target in targets)
        {
            EnsureContentEditable(target);
        }

        if (IsReadOnly || key.IsEmpty)
        {
            return;
        }

        var next = _state;
        foreach (var target in targets)
        {
            var rows = target.Rows;
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row.Row.Values.Enabled && LibraryTermKey.From(row.Row.Values.Spoken) == key)
                {
                    var off = target.Header.BuiltIn
                        ? _overlay.SetEnabled(row.Row, false)
                        : LibraryRow.Custom(row.Row.Values with { Enabled = false });
                    rows = rows.SetItem(i, row with { Row = off });
                }
            }

            if (!ReferenceEquals(rows, target.Rows))
            {
                next = next.WithLib(target with { Rows = rows });
            }
        }

        Structural("Turn off in other libraries", next);
    }

    /// <summary>
    /// Applies an import plan computed for the current revision: into a new library (named by the plan, off, AI
    /// permission from Decision 2) or into an existing one (a built-in's rows through the overlay, as edits and
    /// additions), with written-differently rows kept or taken from the file by <paramref name="choice"/>. Applies exactly
    /// the rows the plan lists, never the file again. Undoable. Refused for a plan of another revision (plan again), a
    /// name with a double quote, a blank or taken name, a library whose content cannot be edited, or too many terms.
    /// </summary>
    public LibraryEditResult ApplyImport(LibraryImportPlan plan, ImportConflictChoice choice)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (IsReadOnly || plan.DraftRevision != _revision)
        {
            return Refused();
        }

        return plan.Target switch
        {
            LibraryImportTarget.NewLibrary => ImportAsNewLibrary(plan, choice),
            LibraryImportTarget.ExistingLibrary existing => ImportIntoLibrary(plan, choice, GetLive(existing.LibraryId)),
            _ => throw new ArgumentException("Unknown import target.", nameof(plan)),
        };
    }

    /// <summary>
    /// Undoes the latest structural operation that can still be undone: each thing it changed goes back unless it was
    /// changed again since (a term edited after it was turned off keeps its edit). An operation nothing of which can go
    /// back any more is dropped, and the one before it is undone instead, so <see cref="UndoLabel"/> always names what
    /// happens.
    /// </summary>
    public void Undo()
    {
        var index = FindApplicable(_undo, undoing: true);
        if (index < 0)
        {
            _undo.Clear();
            return;
        }

        var entry = _undo[index];
        _undo.RemoveRange(index, _undo.Count - index);
        Commit(Merge(_state, entry.After, entry.Before));
        _redo.Add(entry);
    }

    /// <summary>Redoes the latest undone operation, under the same rule as <see cref="Undo"/>.</summary>
    public void Redo()
    {
        var index = FindApplicable(_redo, undoing: false);
        if (index < 0)
        {
            _redo.Clear();
            return;
        }

        var entry = _redo[index];
        _redo.RemoveRange(index, _redo.Count - index);
        Commit(Merge(_state, entry.Before, entry.After));
        _undo.Add(entry);
    }

    /// <summary>
    /// Validates the libraries the change set writes (never an untouched legacy library) and returns the immutable change
    /// set of the draft at <see cref="Revision"/>, or the problems that block it. Blank placeholder rows are dropped and an
    /// untouched new library is left out. While the state is read-only the change set is empty.
    /// </summary>
    public LibraryCaptureResult CaptureChangeSet()
    {
        if (IsReadOnly)
        {
            return new LibraryCaptureResult(
                new LibraryChangeSet(_committed.Generation, _revision, [], [], [], _committed.LocalState, localStateChanged: false),
                []);
        }

        var diff = Diff;
        var issues = new List<LibraryValidationIssue>();
        var writes = new List<LibraryWrite>();
        var deletions = new List<LibraryDeletion>();
        var actions = new List<RecentlyDeletedAction>();
        foreach (var lib in Ordered(_state))
        {
            var change = diff.Libraries[lib.Header.Id];
            var header = lib.Header;
            if (change.Untouched)
            {
                continue;
            }

            if (header.BuiltIn)
            {
                if (header.Recovery != BuiltInEditsRecovery.None)
                {
                    writes.Add(new LibraryWrite(
                        header.Id, true, LibraryOrigin.Existing, header.Committed?.ContentHash, Recovery: header.Recovery));
                }
                else if (change.ContentChanged)
                {
                    ValidateRows(lib, issues);
                    writes.Add(new LibraryWrite(
                        header.Id, true, LibraryOrigin.Existing, header.Committed?.ContentHash, Edits: change.Edits));
                }

                continue;
            }

            if (header.PendingDelete)
            {
                if (header.Committed?.ContentHash is { } preImage)
                {
                    deletions.Add(new LibraryDeletion(header.Id, header.FileName ?? header.Id + ".csv", preImage));
                }
                else
                {
                    issues.Add(new LibraryValidationIssue(header.Id, null, LibraryValidationKind.ContentNotSaveable, TermFields.None));
                }

                continue;
            }

            if (header.RestoredFrom is { } restored)
            {
                actions.Add(new RecentlyDeletedAction(
                    RecentlyDeletedActionKind.Restore, restored.Entry.EntryName, restored.Entry.ContentHash, header.Id));
            }

            if (!change.ContentChanged)
            {
                continue;
            }

            if (!ContentEditable(lib))
            {
                issues.Add(new LibraryValidationIssue(header.Id, null, LibraryValidationKind.ContentNotSaveable, TermFields.None));
                continue;
            }

            ValidateRows(lib, issues);
            ValidateMetadata(lib, issues);
            var preImageOfWrite = header.RestoredFrom is { } source ? source.Entry.ContentHash : header.Committed?.ContentHash;
            writes.Add(new LibraryWrite(header.Id, false, header.Origin, preImageOfWrite, Content: ContentOf(lib)));
        }

        foreach (var purge in _state.Local.Purges.Values)
        {
            actions.Add(new RecentlyDeletedAction(RecentlyDeletedActionKind.DeletePermanently, purge.EntryName, purge.ContentHash));
        }

        if (issues.Count > 0)
        {
            return new LibraryCaptureResult(null, issues);
        }

        var local = CapturedLocal(diff);
        var changes = new LibraryChangeSet(
            _committed.Generation, _revision, writes, deletions, actions,
            LibraryLocalState.Create(
                local.Enabled, _committed.LocalState.LegacyEnabledIds, local.Ai, local.Markers, local.Notice,
                LocalStateHealth.Ok, _committed.LocalState.AcceptedContent, local.Lost),
            diff.LocalChanged);
        _captures[_revision] = new Capture(_state, local, diff.UntouchedIds);
        foreach (var old in _captures.Keys.Where(revision => revision < _revision).OrderBy(r => r).SkipLast(MaxCaptures - 1).ToList())
        {
            _captures.Remove(old);
        }

        return new LibraryCaptureResult(changes, []);
    }

    /// <summary>
    /// After the Save of the change set captured at <paramref name="revision"/> committed: <paramref name="committed"/> is
    /// the new catalog, everything the Save wrote is committed, and every edit made after <paramref name="revision"/>
    /// stays unsaved on top of it. A library the Save had to keep under another id (another app took its file name) is
    /// the kept library from now on, edits after the capture included. The undo history ends here. Call it only when
    /// the Save stands (<see cref="LibrarySaveStatus.Applied"/> or <see cref="LibrarySaveStatus.AppliedAwaitingRelease"/>):
    /// after any other outcome the draft is still unsaved, and a newer catalog is taken with <see cref="Rebase"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">No change set was captured at <paramref name="revision"/>.</exception>
    public void MarkSaved(long revision, LibraryCatalog committed)
    {
        ArgumentNullException.ThrowIfNull(committed);
        if (!_captures.TryGetValue(revision, out var capture))
        {
            throw new InvalidOperationException("No change set was captured at that revision.");
        }

        var from = Effective(capture);
        var current = _state;
        foreach (var kept in committed.KeptVersions)
        {
            if (kept.Kind == LibraryKeptVersionKind.SavedUnderNewId
                && kept.KeptAsId is { } keptAs
                && from.Libraries.TryGetValue(kept.LibraryId, out var planned)
                && planned.Header.Origin != LibraryOrigin.Existing
                && planned.Header.Committed is null
                && !from.Libraries.ContainsKey(keptAs)
                && committed.Find(keptAs) is not null)
            {
                from = from.Reidentified(planned.Header.Id, keptAs);
                current = current.Reidentified(planned.Header.Id, keptAs);
            }
        }

        RebaseOnto(from, current, committed);
    }

    /// <summary>Reload saved version: the draft is discarded and starts again from <paramref name="committed"/>.</summary>
    public void Reload(LibraryCatalog committed)
    {
        ArgumentNullException.ThrowIfNull(committed);
        var fresh = FromCatalog(committed, mapFrom: _base);
        _committed = committed;
        _base = fresh;
        ResetHistory();
        Commit(fresh);
    }

    /// <summary>
    /// Takes a newer committed catalog and keeps the draft's edits: whatever the user did not change takes the new
    /// catalog's value, and every change the user made stays unsaved on top of it. A library the user edited is then
    /// saved over what the newer catalog holds for it, so use this when the catalog moved for a reason the user has seen
    /// (a Save refused as stale, say), never to write over an outside edit without asking. The undo history ends here.
    /// </summary>
    public void Rebase(LibraryCatalog committed)
    {
        ArgumentNullException.ThrowIfNull(committed);
        RebaseOnto(_base, _state, committed);
    }

    private void RebaseOnto(State from, State current, LibraryCatalog committed)
    {
        var to = FromCatalog(committed, mapFrom: from);

        // A library the user added after the capture must never take the id of one the new catalog brought in (a version
        // kept under a name chosen when the Save was prepared), or its rows would be saved into that library's file.
        foreach (var lib in current.Libraries.Values.ToList())
        {
            var id = lib.Header.Id;
            if (lib.Header.Committed is null && !from.Libraries.ContainsKey(id) && to.Libraries.ContainsKey(id))
            {
                var taken = TakenIds(includeRecentlyDeleted: true)
                    .Concat(to.Libraries.Keys)
                    .Concat(current.Libraries.Keys);
                current = current.Reidentified(id, LibraryNaming.NewCustomId(lib.Header.Name, taken));
            }
        }

        var merged = Merge(current, from, to);
        foreach (var lib in merged.Libraries.Values.ToList())
        {
            var id = lib.Header.Id;
            if (to.Libraries.TryGetValue(id, out var saved))
            {
                var header = lib.Header with
                {
                    Origin = saved.Header.Origin,
                    FileState = saved.Header.FileState,
                    FileName = saved.Header.FileName,
                    Committed = saved.Header.Committed,
                    RestoredFrom = saved.Header.RestoredFrom,
                    Created = saved.Header.Created,
                };

                // A library left exactly as saved becomes the saved instance again, so the checks that compare the
                // draft with the committed catalog skip it at once instead of comparing every row.
                merged = header == saved.Header && SameRows(lib.Rows, saved.Rows)
                    ? merged.WithLib(saved)
                    : header != lib.Header ? merged.WithLib(lib with { Header = header }) : merged;
            }
            else if (lib.Header.Committed is not null)
            {
                merged = merged.WithoutLib(id);
            }
        }

        _committed = committed;
        _base = to;
        ResetHistory();
        Commit(merged);
    }

    private LibraryEditResult ImportAsNewLibrary(LibraryImportPlan plan, ImportConflictChoice choice)
    {
        if (LibraryMetadata.CheckTyped(plan.SuggestedName) == LibraryMetadataProblem.DoubleQuote)
        {
            return Refused(string.Empty, LibraryValidationKind.MetadataDoubleQuote, LibraryMetadataField.Name);
        }

        var name = LibraryMetadata.Commit(plan.SuggestedName);
        if (name.Length == 0)
        {
            return Refused(string.Empty, LibraryValidationKind.EmptyName, LibraryMetadataField.Name);
        }

        if (LibraryNaming.IsNameTaken(name, Names(exceptId: null)))
        {
            return Refused(string.Empty, LibraryValidationKind.DuplicateName, LibraryMetadataField.Name);
        }

        var rows = ApplyOperations([], builtIn: false, plan.Operations, choice, out _);
        if (CountTerms(rows) > LibraryLimits.MaxTermsPerLibrary)
        {
            return Refused(string.Empty, LibraryValidationKind.TooManyTerms, LibraryMetadataField.None);
        }

        var category = LibraryMetadata.Commit(plan.Category);
        var description = LibraryMetadata.Commit(plan.Description);
        var id = LibraryNaming.NewCustomId(name, TakenIds(includeRecentlyDeleted: true));
        var header = NewHeader(
            id, name, category.Length == 0 ? DefaultCategory : category, description.Length == 0 ? null : description,
            basedOn: null, LibraryOrigin.Imported);
        var ai = _defaultAiPermission(LibraryOrigin.Imported, false, null);
        Structural("Import terms", _state.WithLib(new Lib(header, rows)).WithEnabled(id, false).WithAi(id, ai));
        return Accepted();
    }

    private LibraryEditResult ImportIntoLibrary(LibraryImportPlan plan, ImportConflictChoice choice, Lib lib)
    {
        if (!ContentEditable(lib))
        {
            return Refused(lib, LibraryValidationKind.ContentNotSaveable);
        }

        var rows = ApplyOperations(lib.Rows, lib.Header.BuiltIn, plan.Operations, choice, out var rewrittenKeys);
        if (CountTerms(rows) > TermLimit(lib))
        {
            return Refused(lib, LibraryValidationKind.TooManyTerms);
        }

        var next = _state.WithLib(lib with { Rows = rows });
        if (!lib.Header.BuiltIn)
        {
            foreach (var key in rewrittenKeys)
            {
                next = next.WithoutMarker(lib.Header.Id, key);
            }
        }

        Structural("Import terms", next);
        return Accepted();
    }

    private ImmutableList<DraftTermRow> ApplyOperations(
        ImmutableList<DraftTermRow> rows,
        bool builtIn,
        IReadOnlyList<LibraryImportOperation> operations,
        ImportConflictChoice choice,
        out IReadOnlyList<LibraryTermKey> rewrittenKeys)
    {
        var list = rows.ToBuilder();
        var positions = new Dictionary<long, int>();
        var byKey = new Dictionary<LibraryTermKey, int>();
        for (var i = 0; i < list.Count; i++)
        {
            positions[list[i].RowId] = i;
            if (!IsBlank(list[i]))
            {
                byKey.TryAdd(LibraryTermKey.From(list[i].Row.Values.Spoken), i);
            }
        }

        var added = new Dictionary<LibraryTermKey, int>();
        var rewritten = new List<LibraryTermKey>();
        foreach (var operation in operations)
        {
            switch (operation.Kind)
            {
                case LibraryImportOperationKind.Add:
                    var fileRow = operation.FileRow;
                    added.TryAdd(LibraryTermKey.From(fileRow.Spoken), list.Count);
                    list.Add(NewRow(
                        builtIn ? _overlay.Add(fileRow) : LibraryRow.Custom(fileRow),
                        legacyEmpty: fileRow.Written.Length == 0));
                    break;

                case LibraryImportOperationKind.WrittenDifferently when choice == ImportConflictChoice.UseFilesVersion:
                    // The plan names the row it met by id when a workspace built its draft; otherwise the row with the
                    // spoken form, which at the plan's own revision is the same row, or a row this import added.
                    var key = LibraryTermKey.From(operation.FileRow.Spoken);
                    var found = operation.ExistingRowId is { } existingId && positions.TryGetValue(existingId, out var at) ? at
                        : byKey.TryGetValue(key, out var keyed) ? keyed
                        : added.TryGetValue(key, out var addedAt) ? addedAt
                        : -1;
                    if (found < 0)
                    {
                        break;
                    }

                    var existing = list[found];
                    var values = existing.Row.Values with
                    {
                        Written = operation.FileRow.Written,
                        WholeWord = operation.FileRow.WholeWord,
                        Enabled = operation.FileRow.Enabled,
                    };
                    list[found] = existing with
                    {
                        Row = builtIn ? _overlay.Edit(existing.Row, values) : LibraryRow.Custom(values),
                        RemovalIntent = false,
                        LegacyEmpty = values.Written.Length == 0,
                    };
                    rewritten.Add(existing.Row.Key);
                    break;
            }
        }

        rewrittenKeys = rewritten;
        return list.Count == rows.Count && rewritten.Count == 0 ? rows : list.ToImmutable();
    }

    private const int MaxCaptures = 8;

    private (long Revision, int Count, int Index) _undoProbe = (-1, -1, -1);
    private (long Revision, int Count, int Index) _redoProbe = (-1, -1, -1);

    private static DictionaryLibrary? ShippedLibrary(string id) =>
        BuiltInDictionaryLibraries.All.FirstOrDefault(library =>
            string.Equals(library.Id, id, StringComparison.OrdinalIgnoreCase));

    private static LibraryEditResult Accepted() => new(true, null);

    private static LibraryEditResult Refused() => new(false, null);

    private static LibraryEditResult Refused(
        Lib lib, LibraryValidationKind kind, LibraryMetadataField field = LibraryMetadataField.None) =>
        Refused(lib.Header.Id, kind, field);

    private static LibraryEditResult Refused(string libraryId, LibraryValidationKind kind, LibraryMetadataField field) =>
        new(false, new LibraryValidationIssue(libraryId, null, kind, TermFields.None, field));

    private static bool IsBlank(TermValues values) => values.Spoken.Length == 0 && values.Written.Length == 0;

    private static bool IsBlank(DraftTermRow row) => IsBlank(row.Row.Values);

    private static int CountTerms(IEnumerable<DraftTermRow>? rows) => rows?.Count(row => !IsBlank(row)) ?? 0;

    private static bool TextChanged(TermValues before, TermValues after) =>
        !string.Equals(before.Spoken, after.Spoken, StringComparison.Ordinal)
        || !string.Equals(before.Written, after.Written, StringComparison.Ordinal)
        || before.WholeWord != after.WholeWord;

    private static bool SameRows(IReadOnlyList<DraftTermRow> first, IReadOnlyList<DraftTermRow> second)
    {
        if (ReferenceEquals(first, second))
        {
            return true;
        }

        if (first.Count != second.Count)
        {
            return false;
        }

        for (var i = 0; i < first.Count; i++)
        {
            if (first[i] != second[i])
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameLibrary(Lib first, Lib second) =>
        ReferenceEquals(first, second) || (first.Header == second.Header && SameRows(first.Rows, second.Rows));

    private static Lib? LibOf(State state, string id) => state.Libraries.TryGetValue(id, out var lib) ? lib : null;

    private static bool? AiOf(Local local, string id) => local.Ai.TryGetValue(id, out var value) ? value : null;

    private static IEnumerable<Lib> Ordered(State state) =>
        LibraryPrecedence.Order(
            state.Libraries.Values,
            lib => lib.Header.Id,
            lib => lib.Header.BuiltIn,
            lib => lib.Header.BuiltIn ? null : lib.Header.FileName);

    private static IReadOnlyList<LibraryRow> TermsOf(Lib lib) =>
        lib.Rows.Where(row => !IsBlank(row)).Select(row => row.Row).ToList();

    private static LibraryContent ContentOf(Lib lib)
    {
        var header = lib.Header;
        return new LibraryContent(
            header.Id, header.BuiltIn, header.Name, header.Category, header.Description, TermsOf(lib), header.BasedOn);
    }

    private static Header NewHeader(
        string id, string name, string category, string? description, string? basedOn, LibraryOrigin origin) =>
        new(id, BuiltIn: false, name, category, description, basedOn, PendingDelete: false, BuiltInEditsRecovery.None,
            EditsReset: false, origin, LibraryFileState.Available, FileName: id + ".csv", Committed: null, RestoredFrom: null,
            Created: null);

    private DraftTermRow NewRow(LibraryRow row, bool legacyEmpty, bool removalIntent = false) =>
        new(_nextRowId++, row, removalIntent, legacyEmpty);

    private void Commit(State next)
    {
        _state = next;
        _revision++;
        _draft = null;
        _differences = null;
    }

    // A change that is not in the undo history. It still ends the redo history, as any new edit does.
    private void Change(State next)
    {
        if (next == _state)
        {
            return;
        }

        Commit(next);
        _redo.Clear();
    }

    private void Structural(string label, State next)
    {
        if (next == _state)
        {
            return;
        }

        var entry = new UndoEntry(label, _state, next);
        Commit(next);
        _undo.Add(entry);
        _redo.Clear();
    }

    private void ResetHistory()
    {
        _undo.Clear();
        _redo.Clear();
        _captures.Clear();
    }

    // The top-most entry that would still change something, cached per revision and stack size because the shell asks
    // after every edit.
    private int FindApplicable(List<UndoEntry> stack, bool undoing)
    {
        ref var probe = ref undoing ? ref _undoProbe : ref _redoProbe;
        if (probe.Revision == _revision && probe.Count == stack.Count)
        {
            return probe.Index;
        }

        var index = -1;
        for (var i = stack.Count - 1; i >= 0; i--)
        {
            var entry = stack[i];
            var merged = undoing ? Merge(_state, entry.After, entry.Before) : Merge(_state, entry.Before, entry.After);
            if (!ReferenceEquals(merged, _state))
            {
                index = i;
                break;
            }
        }

        probe = (_revision, stack.Count, index);
        return index;
    }

    private Lib? Find(string? id) =>
        id is not null && _state.Libraries.TryGetValue(id.Trim(), out var lib) ? lib : null;

    private Lib Get(string libraryId)
    {
        ArgumentNullException.ThrowIfNull(libraryId);
        return Find(libraryId) ?? throw new ArgumentException("The draft has no library with that id.", nameof(libraryId));
    }

    // A library the page shows: one deleted in the draft is gone from it, so an operation on it is a misuse.
    private Lib GetLive(string libraryId)
    {
        var lib = Get(libraryId);
        if (lib.Header.PendingDelete)
        {
            throw new InvalidOperationException("That library is deleted in this draft.");
        }

        return lib;
    }

    private Lib GetCustomForMetadata(string libraryId)
    {
        var lib = GetLive(libraryId);
        if (lib.Header.BuiltIn)
        {
            throw new InvalidOperationException("A built-in library's name and details are read-only; duplicate it instead.");
        }

        return lib;
    }

    private void EnsureWritable()
    {
        if (IsReadOnly)
        {
            throw new InvalidOperationException("Libraries were changed by a newer version of Scribe.");
        }
    }

    private bool ContentEditable(Lib lib) =>
        !IsReadOnly
        && !lib.Header.PendingDelete
        && lib.Header.FileState == LibraryFileState.Available
        && lib.Header.Recovery == BuiltInEditsRecovery.None;

    private void EnsureContentEditable(Lib lib)
    {
        if (!ContentEditable(lib))
        {
            throw new InvalidOperationException("That library's content can't be edited here.");
        }
    }

    private static int IndexOfRow(Lib lib, long rowId)
    {
        var index = lib.Rows.FindIndex(row => row.RowId == rowId);
        return index >= 0 ? index : throw new ArgumentException("The library has no row with that id.", nameof(rowId));
    }

    private int TermLimit(Lib lib) =>
        Math.Max(LibraryLimits.MaxTermsPerLibrary, CountTerms(LibOf(_base, lib.Header.Id)?.Rows));

    private IEnumerable<string> Names(string? exceptId) =>
        _state.Libraries.Values
            .Where(lib => !lib.Header.PendingDelete
                && (exceptId is null || !string.Equals(lib.Header.Id, exceptId, StringComparison.OrdinalIgnoreCase)))
            .Select(lib => lib.Header.Name);

    // Every id a new library must not take (3.5.4): built-in ids, shipped and retired; the catalog's and the draft's
    // libraries; the stems of the files in the libraries folder (every CSV there is a catalog library); and, unless a
    // restore is asking for its own old id, the original ids of Recently deleted entries.
    private IEnumerable<string> TakenIds(bool includeRecentlyDeleted)
    {
        var ids = LibraryPrecedence.BuiltInOrder
            .Concat(LibraryPrecedence.RetiredBuiltInIds)
            .Concat(_committed.RetiredBuiltInEdits.Select(edits => edits.LibraryId))
            .Concat(_committed.Libraries.Select(library => library.Content.Id))
            .Concat(_committed.Libraries
                .Where(library => !string.IsNullOrEmpty(library.FileName))
                .Select(library => Path.GetFileNameWithoutExtension(library.FileName!)))
            .Concat(_state.Libraries.Values.Select(lib => lib.Header.Id));
        return includeRecentlyDeleted
            ? ids.Concat(_committed.RecentlyDeleted.Select(entry => entry.OriginalId))
            : ids;
    }

    private RecentlyDeletedLibrary? FindRecentlyDeleted(string? entryName) =>
        entryName is null
            ? null
            : _committed.RecentlyDeleted.FirstOrDefault(entry =>
                string.Equals(entry.EntryName, entryName, StringComparison.OrdinalIgnoreCase));

    private bool IsRestoring(string entryName) =>
        _state.Libraries.Values.Any(lib =>
            lib.Header.RestoredFrom is { } restored
            && string.Equals(restored.Entry.EntryName, entryName, StringComparison.OrdinalIgnoreCase));

    private bool ShownAi(State state, Lib lib) =>
        ShownAi(state, lib.Header.Id, lib.Header.BuiltIn, lib.Header.Committed,
            rewritten: !lib.Header.PendingDelete && Diff.Libraries.TryGetValue(lib.Header.Id, out var change) && change.ContentChanged);

    // What a library's Use in AI cleanup box shows, in the policy's order (see the class remarks): used where a new
    // library inherits a permission and where Use these choices records what is shown. Content the Save rewrites is
    // accepted by that Save, so only an unchanged committed file is checked against the accepted content.
    private bool ShownAi(State state, string id, bool builtIn, CatalogLibrary? committed, bool rewritten)
    {
        if (IsReadOnly)
        {
            return false;
        }

        if (committed is not null && !rewritten && !ContentAccepted(id, builtIn, committed))
        {
            return false;
        }

        if (state.Local.Ai.TryGetValue(id, out var chosen))
        {
            return chosen;
        }

        return !state.Local.Lost && builtIn && _defaultAiPermission(LibraryOrigin.Existing, true, null);
    }

    private bool ContentAccepted(string id, bool builtIn, CatalogLibrary committed)
    {
        var accepted = _committed.LocalState.AcceptedContent;
        if (builtIn)
        {
            return committed.ContentHash is not { } document
                || (accepted.TryGetValue(id, out var acceptedDocument) && acceptedDocument == document);
        }

        return committed.ContentHash is { } file && accepted.TryGetValue(id, out var acceptedFile) && acceptedFile == file;
    }

    private Differences Diff => _differences ??= ComputeDifferences();

    private Differences ComputeDifferences()
    {
        var libraries = new Dictionary<string, LibraryChange>(StringComparer.OrdinalIgnoreCase);
        var unsaved = new List<string>();
        var untouched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var localChanged = false;
        foreach (var lib in Ordered(_state))
        {
            var change = Analyze(lib);
            libraries[lib.Header.Id] = change;
            if (change.Untouched)
            {
                untouched.Add(lib.Header.Id);
            }

            if (change.Unsaved)
            {
                unsaved.Add(lib.Header.Id);
            }

            localChanged |= change.LocalChanged;
        }

        var global = !_state.Local.Notice.SetEquals(_base.Local.Notice) || _state.Local.Lost != _base.Local.Lost;
        return new Differences(
            libraries, unsaved, untouched, localChanged || global,
            unsaved.Count > 0 || global || !_state.Local.Purges.IsEmpty);
    }

    private LibraryChange Analyze(Lib lib)
    {
        var header = lib.Header;
        var id = header.Id;
        if (IsUntouched(lib))
        {
            return new LibraryChange(Untouched: true, ContentChanged: false, Edits: null, LocalChanged: false, Unsaved: false);
        }

        var committedLib = LibOf(_base, id);
        var unchanged = committedLib is not null && ReferenceEquals(lib, committedLib);
        var contentChanged = false;
        BuiltInLibraryEdits? edits = null;
        if (header.BuiltIn)
        {
            if (header.Recovery != BuiltInEditsRecovery.None)
            {
                contentChanged = true;
            }
            else if (!unchanged && (header.EditsReset || committedLib is null || !SameRows(lib.Rows, committedLib.Rows)))
            {
                var committedEdits = header.Committed?.Edits;
                edits = _overlay.Collect(ShippedOf(lib), header.EditsReset ? null : committedEdits, TermsOf(lib));
                contentChanged = !SameEdits(edits, committedEdits);
            }
        }
        else if (!header.PendingDelete && !unchanged)
        {
            var committedContent = header.RestoredFrom?.Content ?? header.Committed?.Content;
            contentChanged = committedContent is null || !SameContent(lib, committedContent);
        }

        var localChanged = LocalDiffers(lib, committedLib);
        var unsaved = header.Committed is null
            || header.PendingDelete
            || header.Recovery != BuiltInEditsRecovery.None
            || contentChanged
            || localChanged;
        return new LibraryChange(false, contentChanged, edits, localChanged, unsaved);
    }

    private bool IsUntouched(Lib lib)
    {
        var header = lib.Header;
        return header.Origin == LibraryOrigin.Created
            && header.Created is { } created
            && !header.PendingDelete
            && string.Equals(header.Name, created.Name, StringComparison.Ordinal)
            && string.Equals(header.Category, DefaultCategory, StringComparison.Ordinal)
            && header.Description is null
            && header.BasedOn is null
            && CountTerms(lib.Rows) == 0
            && _state.Local.Enabled.Contains(header.Id)
            && AiOf(_state.Local, header.Id) == created.Ai
            && !_state.Local.Markers.Any(marker => string.Equals(marker.LibraryId, header.Id, StringComparison.OrdinalIgnoreCase));
    }

    private bool LocalDiffers(Lib lib, Lib? committedLib)
    {
        var id = lib.Header.Id;
        return _state.Local.Enabled.Contains(id) != _base.Local.Enabled.Contains(id)
            || AiOf(_state.Local, id) != AiOf(_base.Local, id)
            || !LiveMarkers(_state.Local, lib).SetEquals(LiveMarkers(_base.Local, committedLib));
    }

    // The markers of a library that still apply to one of its rows: the ones a capture keeps (3.5.1: markers of rows or
    // libraries the draft no longer has are pruned). Comparing these, rather than the raw lists, keeps a marker left over
    // for a row that was already gone from counting as a change.
    private static HashSet<LegacyMarker> LiveMarkers(Local local, Lib? lib)
    {
        var markers = new HashSet<LegacyMarker>();
        if (lib is null || lib.Header.PendingDelete || lib.Header.BuiltIn)
        {
            return markers;
        }

        HashSet<LibraryTermKey>? keys = null;
        foreach (var marker in local.Markers)
        {
            if (string.Equals(marker.LibraryId, lib.Header.Id, StringComparison.OrdinalIgnoreCase))
            {
                keys ??= lib.Rows.Where(row => !IsBlank(row)).Select(row => row.Row.Key).ToHashSet();
                if (keys.Contains(marker.Key))
                {
                    markers.Add(marker);
                }
            }
        }

        return markers;
    }

    private static bool SameContent(Lib lib, LibraryContent content)
    {
        var header = lib.Header;
        if (!string.Equals(header.Name, content.Name, StringComparison.Ordinal)
            || !string.Equals(header.Category, content.Category, StringComparison.Ordinal)
            || !string.Equals(header.Description, content.Description, StringComparison.Ordinal)
            || !string.Equals(header.BasedOn, content.BasedOn, StringComparison.Ordinal))
        {
            return false;
        }

        var index = 0;
        foreach (var row in lib.Rows)
        {
            if (IsBlank(row))
            {
                continue;
            }

            if (index >= content.Rows.Count || row.Row.Values != content.Rows[index].Values)
            {
                return false;
            }

            index++;
        }

        return index == content.Rows.Count;
    }

    private static bool SameEdits(BuiltInLibraryEdits? first, BuiltInLibraryEdits? second) =>
        ReferenceEquals(first, second)
        || (first is not null
            && second is not null
            && string.Equals(first.LibraryId, second.LibraryId, StringComparison.OrdinalIgnoreCase)
            && first.Terms.SequenceEqual(second.Terms));

    // The shipped library a built-in's edits are collected against; a test catalog's built-in the embedded CSVs do not
    // have is rebuilt from the shipped values its rows carry.
    private DictionaryLibrary ShippedOf(Lib lib)
    {
        var header = lib.Header;
        return _shippedLibrary(header.Id)
            ?? new DictionaryLibrary(
                header.Id, header.Name, header.Category, header.Description, BuiltIn: true,
                (header.Committed?.Content.Rows ?? [])
                    .Where(row => row.Shipped is not null)
                    .Select(row => row.Shipped!.ToEntry())
                    .ToList());
    }

    private Local CapturedLocal(Differences diff)
    {
        var untouched = diff.UntouchedIds;
        var local = _state.Local;
        var markers = ImmutableList.CreateBuilder<LegacyMarker>();
        foreach (var group in local.Markers.GroupBy(marker => marker.LibraryId, StringComparer.OrdinalIgnoreCase))
        {
            var live = LiveMarkers(local, Find(group.Key));
            markers.AddRange(group.Where(live.Contains));
        }

        return new Local(
            untouched.Count == 0 ? local.Enabled : local.Enabled.Except(untouched),
            untouched.Count == 0 ? local.Ai : local.Ai.RemoveRange(untouched),
            markers.ToImmutable(),
            local.Notice,
            local.Lost,
            local.Purges);
    }

    // The draft as the Save captured at a revision committed it: without the libraries it left out and the blank rows it
    // dropped, and with the local state the change set carried.
    private static State Effective(Capture capture)
    {
        var libraries = capture.State.Libraries;
        foreach (var id in capture.Untouched)
        {
            libraries = libraries.Remove(id);
        }

        foreach (var lib in libraries.Values.ToList())
        {
            if (lib.Rows.Any(IsBlank))
            {
                libraries = libraries.SetItem(lib.Header.Id, lib with { Rows = lib.Rows.RemoveAll(IsBlank) });
            }
        }

        return new State(libraries, capture.Local);
    }

    private void ValidateRows(Lib lib, List<LibraryValidationIssue> issues)
    {
        var id = lib.Header.Id;
        var committed = CommittedRows(lib);
        var first = new Dictionary<LibraryTermKey, DraftTermRow>();
        foreach (var row in lib.Rows)
        {
            if (IsBlank(row))
            {
                continue;
            }

            committed.TryGetValue(row.RowId, out var before);
            var key = LibraryTermKey.From(row.Row.Values.Spoken);
            if (key.IsEmpty)
            {
                issues.Add(new LibraryValidationIssue(id, row.RowId, LibraryValidationKind.WrittenWithoutSpoken, TermFields.Spoken));
            }
            else if (!first.TryAdd(key, row) && Blocks(lib, row, before, first[key], committed))
            {
                issues.Add(new LibraryValidationIssue(id, row.RowId, LibraryValidationKind.DuplicateSpoken, TermFields.Spoken));
            }

            AddValueIssues(id, row, before, issues);
        }

        if (CountTerms(lib.Rows) > TermLimit(lib))
        {
            issues.Add(new LibraryValidationIssue(id, null, LibraryValidationKind.TooManyTerms, TermFields.None));
        }
    }

    // A repeated spoken form blocks a custom library's Save, legacy repeats included. In a built-in it blocks only when
    // the user made one of the two rows speak that form: two rows that already did are the shipped data's collision, not
    // the user's (3.2.1).
    private static bool Blocks(
        Lib lib, DraftTermRow row, DraftTermRow? before, DraftTermRow other, IReadOnlyDictionary<long, DraftTermRow> committed) =>
        !lib.Header.BuiltIn
        || SpokenChanged(row, before)
        || SpokenChanged(other, committed.TryGetValue(other.RowId, out var otherBefore) ? otherBefore : null);

    private static bool SpokenChanged(DraftTermRow row, DraftTermRow? before) =>
        before is null || LibraryTermKey.From(before.Row.Values.Spoken) != LibraryTermKey.From(row.Row.Values.Spoken);

    private static void AddValueIssues(string id, DraftTermRow row, DraftTermRow? before, List<LibraryValidationIssue> issues)
    {
        var values = row.Row.Values;
        if (values.Written.Length == 0 && !row.RemovalIntent && !row.LegacyEmpty)
        {
            issues.Add(new LibraryValidationIssue(id, row.RowId, LibraryValidationKind.EmptyWrittenWithoutIntent, TermFields.Written));
        }

        if (values.Spoken.Length > LibraryLimits.MaxFieldLength
            && !string.Equals(before?.Row.Values.Spoken, values.Spoken, StringComparison.Ordinal))
        {
            issues.Add(new LibraryValidationIssue(id, row.RowId, LibraryValidationKind.FieldTooLong, TermFields.Spoken));
        }

        if (values.Written.Length > LibraryLimits.MaxFieldLength
            && !string.Equals(before?.Row.Values.Written, values.Written, StringComparison.Ordinal))
        {
            issues.Add(new LibraryValidationIssue(id, row.RowId, LibraryValidationKind.FieldTooLong, TermFields.Written));
        }
    }

    private Dictionary<long, DraftTermRow> CommittedRows(Lib lib)
    {
        var rows = new Dictionary<long, DraftTermRow>();
        if (LibOf(_base, lib.Header.Id) is { } committed)
        {
            foreach (var row in committed.Rows)
            {
                rows[row.RowId] = row;
            }
        }

        return rows;
    }

    // The first problem one row has now, for the result of the edit that made it: the capture's checks, for one row.
    private LibraryValidationIssue? RowIssue(Lib lib, DraftTermRow row)
    {
        if (IsBlank(row))
        {
            return null;
        }

        var committed = CommittedRows(lib);
        committed.TryGetValue(row.RowId, out var before);
        var issues = new List<LibraryValidationIssue>();
        var key = LibraryTermKey.From(row.Row.Values.Spoken);
        if (key.IsEmpty)
        {
            issues.Add(new LibraryValidationIssue(lib.Header.Id, row.RowId, LibraryValidationKind.WrittenWithoutSpoken, TermFields.Spoken));
        }
        else
        {
            var other = lib.Rows.FirstOrDefault(candidate =>
                candidate.RowId != row.RowId && !IsBlank(candidate) && LibraryTermKey.From(candidate.Row.Values.Spoken) == key);
            if (other is not null && Blocks(lib, row, before, other, committed))
            {
                issues.Add(new LibraryValidationIssue(lib.Header.Id, row.RowId, LibraryValidationKind.DuplicateSpoken, TermFields.Spoken));
            }
        }

        AddValueIssues(lib.Header.Id, row, before, issues);
        return issues.Count == 0 ? null : issues[0];
    }

    private void ValidateMetadata(Lib lib, List<LibraryValidationIssue> issues)
    {
        var header = lib.Header;
        var committedName = (header.RestoredFrom?.Content ?? header.Committed?.Content)?.Name;
        if (LibraryMetadata.Commit(header.Name).Length == 0)
        {
            issues.Add(new LibraryValidationIssue(header.Id, null, LibraryValidationKind.EmptyName, TermFields.None, LibraryMetadataField.Name));
        }
        else if (!string.Equals(header.Name, committedName, StringComparison.Ordinal)
            && LibraryNaming.IsNameTaken(header.Name, Names(exceptId: header.Id)))
        {
            issues.Add(new LibraryValidationIssue(header.Id, null, LibraryValidationKind.DuplicateName, TermFields.None, LibraryMetadataField.Name));
        }

        if (!LibraryMetadata.ReadsBackInOlderVersions(header.Name, header.Category, header.Description, header.BasedOn))
        {
            var field = OddQuotes(header.Name) ? LibraryMetadataField.Name
                : OddQuotes(header.Category) ? LibraryMetadataField.Category
                : OddQuotes(header.Description) ? LibraryMetadataField.Description
                : LibraryMetadataField.BasedOn;
            issues.Add(new LibraryValidationIssue(header.Id, null, LibraryValidationKind.MetadataUnreadableInOlder, TermFields.None, field));
        }
    }

    private static bool OddQuotes(string? value) => !LibraryMetadata.ReadsBackInOlderVersions(value);

    private LibraryDraft BuildDraft()
    {
        var diff = Diff;
        var ordered = Ordered(_state).ToList();
        var libraries = ordered
            .Select(lib => new DraftLibrary(
                ContentOf(lib),
                lib.Header.Origin,
                lib.Header.FileState,
                lib.Header.PendingDelete,
                diff.Libraries[lib.Header.Id].Unsaved,
                lib.Header.BuiltIn ? null : lib.Header.FileName))
            .ToList();
        var local = _state.Local;
        var state = LibraryLocalState.Create(
            local.Enabled, _committed.LocalState.LegacyEnabledIds, local.Ai, local.Markers, local.Notice,
            IsReadOnly ? LocalStateHealth.Newer : LocalStateHealth.Ok, _committed.LocalState.AcceptedContent, local.Lost);
        var recentlyDeleted = _committed.RecentlyDeleted
            .Where(entry => !IsRestoring(entry.EntryName) && !local.Purges.ContainsKey(entry.EntryName))
            .ToList();
        var draft = new LibraryDraft(_revision, _committed.Generation, libraries, state, recentlyDeleted);
        var rowIds = new Dictionary<string, long[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var lib in ordered)
        {
            rowIds[lib.Header.Id] = lib.Rows.Where(row => !IsBlank(row)).Select(row => row.RowId).ToArray();
        }

        DraftRowIds.AddOrUpdate(draft, rowIds);
        return draft;
    }

    // Which workspace row each row of a draft's content is, so the import planner, which is handed only the draft, can
    // name the existing row an imported row meets. A draft's rows are its libraries' rows minus blank placeholders, in
    // saved order.
    private static readonly ConditionalWeakTable<LibraryDraft, Dictionary<string, long[]>> DraftRowIds = new();

    /// <summary>The workspace row id of row <paramref name="index"/> of a library's content in <paramref name="draft"/>, or null for a draft no workspace built.</summary>
    internal static long? RowIdIn(LibraryDraft draft, string libraryId, int index) =>
        DraftRowIds.TryGetValue(draft, out var ids) && ids.TryGetValue(libraryId, out var rows) && index >= 0 && index < rows.Length
            ? rows[index]
            : null;

    // The draft of a catalog with nothing changed. Rows keep the ids they had in mapFrom where the rows are the same ones
    // (a custom library read back exactly as it was saved, a built-in's rows by their key), so a Save or a reload does not
    // move the selection; every other row gets a new id.
    private State FromCatalog(LibraryCatalog catalog, State? mapFrom)
    {
        var libraries = ImmutableDictionary.CreateBuilder<string, Lib>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in catalog.Libraries)
        {
            var content = library.Content;
            if (libraries.ContainsKey(content.Id))
            {
                continue;
            }

            var header = new Header(
                content.Id, content.BuiltIn, content.Name, content.Category, content.Description, content.BasedOn,
                PendingDelete: false, BuiltInEditsRecovery.None, EditsReset: false, LibraryOrigin.Existing, library.State,
                content.BuiltIn ? null : library.FileName ?? content.Id + ".csv", library, RestoredFrom: null, Created: null);
            var ids = MapRowIds(content, mapFrom is null ? null : LibOf(mapFrom, content.Id));
            var rows = ImmutableList.CreateBuilder<DraftTermRow>();
            for (var i = 0; i < content.Rows.Count; i++)
            {
                var row = content.Rows[i];
                rows.Add(new DraftTermRow(ids?[i] ?? _nextRowId++, row, RemovalIntent: false, LegacyEmpty: row.Values.Written.Length == 0));
            }

            libraries[content.Id] = new Lib(header, rows.ToImmutable());
        }

        var state = catalog.LocalState;
        return new State(
            libraries.ToImmutable(),
            new Local(
                ImmutableHashSet.CreateRange(StringComparer.OrdinalIgnoreCase, state.EnabledIds),
                ImmutableDictionary.CreateRange(StringComparer.OrdinalIgnoreCase, state.AiPermissions),
                [.. state.LegacyMarkers],
                ImmutableHashSet.CreateRange(StringComparer.OrdinalIgnoreCase, state.AiUpgradeNotice),
                state.AiPermissionsLost || state.Health == LocalStateHealth.Unreadable,
                ImmutableDictionary.Create<string, RecentlyDeletedLibrary>(StringComparer.OrdinalIgnoreCase)));
    }

    private static long?[]? MapRowIds(LibraryContent content, Lib? previous)
    {
        if (previous is null || previous.Header.BuiltIn != content.BuiltIn)
        {
            return null;
        }

        var earlier = previous.Rows.Where(row => !IsBlank(row)).ToList();
        var ids = new long?[content.Rows.Count];
        if (!content.BuiltIn)
        {
            if (earlier.Count != content.Rows.Count)
            {
                return null;
            }

            for (var i = 0; i < earlier.Count; i++)
            {
                if (earlier[i].Row.Values != content.Rows[i].Values)
                {
                    return null;
                }

                ids[i] = earlier[i].RowId;
            }

            return ids;
        }

        var byKey = earlier.GroupBy(row => row.Row.Key).Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.First().RowId);
        var repeated = content.Rows.GroupBy(row => row.Key).Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
        for (var i = 0; i < content.Rows.Count; i++)
        {
            var key = content.Rows[i].Key;
            if (!repeated.Contains(key) && byKey.TryGetValue(key, out var id))
            {
                ids[i] = id;
            }
        }

        return ids;
    }

    // The three-way merge undo, redo and a rebase share: every part of the state that `from` and `to` hold differently
    // takes `to`'s value where `current` still holds `from`'s, and keeps `current`'s where it changed since. The parts
    // are a library's presence and header, each row by its id, and each entry of the local state. Returns `current`
    // itself when nothing changes.
    private static State Merge(State current, State from, State to)
    {
        var libraries = current.Libraries;
        foreach (var id in from.Libraries.Keys.Union(to.Libraries.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var before = LibOf(from, id);
            var after = LibOf(to, id);
            if (ReferenceEquals(before, after))
            {
                continue;
            }

            var mine = libraries.TryGetValue(id, out var existing) ? existing : null;
            if (before is null)
            {
                if (mine is null)
                {
                    libraries = libraries.SetItem(after!.Header.Id, after);
                }

                continue;
            }

            if (after is null)
            {
                if (mine is not null && SameLibrary(mine, before))
                {
                    libraries = libraries.Remove(id);
                }

                continue;
            }

            if (mine is null)
            {
                continue;
            }

            var header = before.Header != after.Header && mine.Header == before.Header ? after.Header : mine.Header;
            var rows = ReferenceEquals(before.Rows, after.Rows) ? mine.Rows : MergeRows(mine.Rows, before.Rows, after.Rows);
            if (header != mine.Header || !ReferenceEquals(rows, mine.Rows))
            {
                libraries = libraries.SetItem(id, new Lib(header, rows));
            }
        }

        var local = MergeLocal(current.Local, from.Local, to.Local);
        return ReferenceEquals(libraries, current.Libraries) && ReferenceEquals(local, current.Local)
            ? current
            : new State(libraries, local);
    }

    private static ImmutableList<DraftTermRow> MergeRows(
        ImmutableList<DraftTermRow> current, ImmutableList<DraftTermRow> from, ImmutableList<DraftTermRow> to)
    {
        var fromById = new Dictionary<long, DraftTermRow>(from.Count);
        foreach (var row in from)
        {
            fromById[row.RowId] = row;
        }

        var toById = new Dictionary<long, DraftTermRow>(to.Count);
        foreach (var row in to)
        {
            toById[row.RowId] = row;
        }

        var changed = false;
        var result = new List<DraftTermRow>(current.Count);
        foreach (var row in current)
        {
            if (fromById.TryGetValue(row.RowId, out var before) && row == before)
            {
                if (!toById.TryGetValue(row.RowId, out var after))
                {
                    changed = true;
                    continue;
                }

                if (after != before)
                {
                    result.Add(after);
                    changed = true;
                    continue;
                }
            }

            result.Add(row);
        }

        // Rows `to` has and `from` did not come back after the row that precedes them in `to` and is still here, in
        // `to`'s order; a row whose predecessors are all gone goes first.
        var present = result.Select(row => row.RowId).ToHashSet();
        var head = new List<DraftTermRow>();
        var insertAfter = new Dictionary<long, List<DraftTermRow>>();
        long? anchor = null;
        foreach (var row in to)
        {
            if (present.Contains(row.RowId))
            {
                anchor = row.RowId;
                continue;
            }

            if (fromById.ContainsKey(row.RowId))
            {
                continue;
            }

            if (anchor is { } previous)
            {
                if (!insertAfter.TryGetValue(previous, out var list))
                {
                    insertAfter[previous] = list = [];
                }

                list.Add(row);
            }
            else
            {
                head.Add(row);
            }
        }

        if (head.Count == 0 && insertAfter.Count == 0)
        {
            return changed ? [.. result] : current;
        }

        var merged = ImmutableList.CreateBuilder<DraftTermRow>();
        merged.AddRange(head);
        foreach (var row in result)
        {
            merged.Add(row);
            if (insertAfter.TryGetValue(row.RowId, out var inserted))
            {
                merged.AddRange(inserted);
            }
        }

        return merged.ToImmutable();
    }

    private static Local MergeLocal(Local current, Local from, Local to)
    {
        var enabled = MergeSet(current.Enabled, from.Enabled, to.Enabled);
        var notice = MergeSet(current.Notice, from.Notice, to.Notice);

        var ai = current.Ai;
        if (!ReferenceEquals(from.Ai, to.Ai))
        {
            foreach (var id in from.Ai.Keys.Union(to.Ai.Keys, StringComparer.OrdinalIgnoreCase))
            {
                var before = AiOf(from, id);
                var after = AiOf(to, id);
                if (before != after && (ai.TryGetValue(id, out var mine) ? mine : (bool?)null) == before)
                {
                    ai = after is { } value ? ai.SetItem(id, value) : ai.Remove(id);
                }
            }
        }

        var markers = current.Markers;
        if (!ReferenceEquals(from.Markers, to.Markers))
        {
            var fromSet = from.Markers.ToHashSet();
            var toSet = to.Markers.ToHashSet();
            foreach (var marker in fromSet.Where(marker => !toSet.Contains(marker)))
            {
                markers = markers.Remove(marker);
            }

            foreach (var marker in to.Markers.Where(marker => !fromSet.Contains(marker)))
            {
                if (!markers.Contains(marker))
                {
                    markers = markers.Add(marker);
                }
            }
        }

        var purges = current.Purges;
        if (!ReferenceEquals(from.Purges, to.Purges))
        {
            foreach (var name in from.Purges.Keys.Union(to.Purges.Keys, StringComparer.OrdinalIgnoreCase))
            {
                var before = from.Purges.TryGetValue(name, out var fromEntry) ? fromEntry : null;
                var after = to.Purges.TryGetValue(name, out var toEntry) ? toEntry : null;
                var mine = purges.TryGetValue(name, out var currentEntry) ? currentEntry : null;
                if (before != after && mine == before)
                {
                    purges = after is null ? purges.Remove(name) : purges.SetItem(name, after);
                }
            }
        }

        var lost = from.Lost != to.Lost && current.Lost == from.Lost ? to.Lost : current.Lost;
        return current.Update(enabled, ai, markers, notice, lost, purges);
    }

    private static ImmutableHashSet<string> MergeSet(
        ImmutableHashSet<string> current, ImmutableHashSet<string> from, ImmutableHashSet<string> to)
    {
        if (ReferenceEquals(from, to))
        {
            return current;
        }

        var result = current;
        foreach (var id in from.Except(to))
        {
            result = result.Remove(id);
        }

        foreach (var id in to.Except(from))
        {
            result = result.Add(id);
        }

        return result;
    }

    private sealed record UndoEntry(string Label, State Before, State After);

    private sealed record Capture(State State, Local Local, IReadOnlySet<string> Untouched);

    private sealed record Differences(
        IReadOnlyDictionary<string, LibraryChange> Libraries,
        IReadOnlyList<string> UnsavedIds,
        IReadOnlySet<string> UntouchedIds,
        bool LocalChanged,
        bool HasChanges);

    private sealed record LibraryChange(
        bool Untouched, bool ContentChanged, BuiltInLibraryEdits? Edits, bool LocalChanged, bool Unsaved);

    private sealed record Creation(string Name, bool Ai);

    // User fields (name to edits reset) merge like any other part of the state; the rest are the committed facts about
    // the library, which a rebase always takes from the new catalog.
    private sealed record Header(
        string Id,
        bool BuiltIn,
        string Name,
        string Category,
        string? Description,
        string? BasedOn,
        bool PendingDelete,
        BuiltInEditsRecovery Recovery,
        bool EditsReset,
        LibraryOrigin Origin,
        LibraryFileState FileState,
        string? FileName,
        CatalogLibrary? Committed,
        RecentlyDeletedContent? RestoredFrom,
        Creation? Created);

    private sealed record Lib(Header Header, ImmutableList<DraftTermRow> Rows);

    private sealed record Local(
        ImmutableHashSet<string> Enabled,
        ImmutableDictionary<string, bool> Ai,
        ImmutableList<LegacyMarker> Markers,
        ImmutableHashSet<string> Notice,
        bool Lost,
        ImmutableDictionary<string, RecentlyDeletedLibrary> Purges)
    {
        // A new instance only when something changed, so "nothing changed" stays reference equality all the way up.
        public Local Update(
            ImmutableHashSet<string> enabled,
            ImmutableDictionary<string, bool> ai,
            ImmutableList<LegacyMarker> markers,
            ImmutableHashSet<string> notice,
            bool lost,
            ImmutableDictionary<string, RecentlyDeletedLibrary> purges) =>
            ReferenceEquals(enabled, Enabled) && ReferenceEquals(ai, Ai) && ReferenceEquals(markers, Markers)
                && ReferenceEquals(notice, Notice) && lost == Lost && ReferenceEquals(purges, Purges)
                ? this
                : new Local(enabled, ai, markers, notice, lost, purges);
    }

    private sealed record State(ImmutableDictionary<string, Lib> Libraries, Local Local)
    {
        public State WithLib(Lib lib)
        {
            var libraries = Libraries.SetItem(lib.Header.Id, lib);
            return ReferenceEquals(libraries, Libraries) ? this : this with { Libraries = libraries };
        }

        // A library leaves the draft with its enabled state, AI choice, markers and upgrade notice.
        public State WithoutLib(string id)
        {
            var libraries = Libraries.Remove(id);
            var markers = Local.Markers.Any(marker => IsOf(marker, id))
                ? Local.Markers.RemoveAll(marker => IsOf(marker, id))
                : Local.Markers;
            var local = Local.Update(
                Local.Enabled.Remove(id), Local.Ai.Remove(id), markers, Local.Notice.Remove(id), Local.Lost, Local.Purges);
            return ReferenceEquals(libraries, Libraries) && ReferenceEquals(local, Local)
                ? this
                : new State(libraries, local);
        }

        public State WithEnabled(string id, bool enabled) =>
            WithLocal(Local.Update(
                enabled ? Local.Enabled.Add(id) : Local.Enabled.Remove(id),
                Local.Ai, Local.Markers, Local.Notice, Local.Lost, Local.Purges));

        public State WithAi(string id, bool permitted) =>
            WithLocal(Local.Update(
                Local.Enabled, Local.Ai.SetItem(id, permitted), Local.Markers, Local.Notice, Local.Lost, Local.Purges));

        public State WithoutMarker(string id, LibraryTermKey key) =>
            WithLocal(Local.Update(
                Local.Enabled, Local.Ai, Local.Markers.Remove(new LegacyMarker(id, key)), Local.Notice, Local.Lost, Local.Purges));

        // The library's enabled state, AI choice, markers and upgrade notice as `source` has them.
        public State WithLocalOf(string id, Local source)
        {
            var markers = Local.Markers.RemoveAll(marker => IsOf(marker, id))
                .AddRange(source.Markers.Where(marker => IsOf(marker, id)));
            if (markers.SequenceEqual(Local.Markers))
            {
                markers = Local.Markers;
            }

            return WithLocal(Local.Update(
                source.Enabled.Contains(id) ? Local.Enabled.Add(id) : Local.Enabled.Remove(id),
                source.Ai.TryGetValue(id, out var ai) ? Local.Ai.SetItem(id, ai) : Local.Ai.Remove(id),
                markers,
                source.Notice.Contains(id) ? Local.Notice.Add(id) : Local.Notice.Remove(id),
                Local.Lost,
                Local.Purges));
        }

        // The library with `oldId` under `newId`, its local entries moved with it; a library not written yet takes the
        // file name of its new id.
        public State Reidentified(string oldId, string newId)
        {
            if (!Libraries.TryGetValue(oldId, out var lib) || string.Equals(oldId, newId, StringComparison.OrdinalIgnoreCase))
            {
                return this;
            }

            var header = lib.Header with
            {
                Id = newId,
                FileName = lib.Header.BuiltIn || lib.Header.Committed is not null ? lib.Header.FileName : newId + ".csv",
            };
            var libraries = Libraries.Remove(oldId).SetItem(newId, lib with { Header = header });
            var enabled = Local.Enabled.Contains(oldId) ? Local.Enabled.Remove(oldId).Add(newId) : Local.Enabled;
            var ai = Local.Ai.TryGetValue(oldId, out var permitted) ? Local.Ai.Remove(oldId).SetItem(newId, permitted) : Local.Ai;
            var markers = Local.Markers.Any(marker => IsOf(marker, oldId))
                ? Local.Markers.Select(marker => IsOf(marker, oldId) ? marker with { LibraryId = newId } : marker).ToImmutableList()
                : Local.Markers;
            var notice = Local.Notice.Contains(oldId) ? Local.Notice.Remove(oldId).Add(newId) : Local.Notice;
            return new State(libraries, Local.Update(enabled, ai, markers, notice, Local.Lost, Local.Purges));
        }

        private State WithLocal(Local local) => ReferenceEquals(local, Local) ? this : this with { Local = local };

        private static bool IsOf(LegacyMarker marker, string id) =>
            string.Equals(marker.LibraryId, id, StringComparison.OrdinalIgnoreCase);
    }
}
