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

    /// <summary>
    /// A Spoken value is repeated in one library, turned-off rows included. In a built-in, also two rows with one key: a
    /// renamed built-in row keeps the spoken form it shipped or was added with as its key, and an edits document holds
    /// one entry per key, so that form cannot be added beside it.
    /// </summary>
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

    /// <summary>
    /// A typed or pasted name, category, description, spoken or written form holds text that is not well-formed UTF-16:
    /// an unpaired surrogate, half of an emoji a paste or an input method left behind, a high surrogate at the very end
    /// included. No library file can hold it, and the stores refuse it, so the editor refuses it first (3.5.1).
    /// </summary>
    MalformedText,
}

/// <summary>A validation problem, with the row and field to focus.</summary>
/// <param name="LibraryId">The library.</param>
/// <param name="RowId">The row, or null for a problem with the library itself.</param>
/// <param name="Kind">What is wrong.</param>
/// <param name="Field">The term field to focus, or <see cref="TermFields.None"/>.</param>
/// <param name="Metadata">The metadata field to focus, for a name, category or description problem.</param>
/// <param name="OtherRowId">
/// For <see cref="LibraryValidationKind.DuplicateSpoken"/>: the row the term collides with, for Go to existing term. In a
/// built-in that is also a row whose original spoken form is the term's, since a renamed built-in row keeps it as its key.
/// </param>
public sealed record LibraryValidationIssue(
    string LibraryId,
    long? RowId,
    LibraryValidationKind Kind,
    TermFields Field,
    LibraryMetadataField Metadata = LibraryMetadataField.None,
    long? OtherRowId = null);

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
/// what is shown. That is the draft-time mirror of the policy's documented order, not a call to it: an unhealthy state
/// permits nothing, content that is not the accepted one is not permitted (a retired built-in's document included, and a
/// built-in whose document vanished while an accepted entry remains), an explicit choice decides, a lost state denies,
/// then the kind default from the delegate, a built-in's as an existing one and a custom library's as a discovered file.
/// Content the Save writes (<see cref="DraftLibrary.WritesContent"/>) is accepted by that Save, so it is not checked;
/// every other library is judged by its committed content, as the composition after the Save will judge it.
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

        if (!LibraryEditor.IsWellFormed(name))
        {
            return Refused(lib, LibraryValidationKind.MalformedText, LibraryMetadataField.Name);
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

        if (!sameCategory && !LibraryEditor.IsWellFormed(category))
        {
            return Refused(lib, LibraryValidationKind.MalformedText, LibraryMetadataField.Category);
        }

        if (!sameDescription && !LibraryEditor.IsWellFormed(description))
        {
            return Refused(lib, LibraryValidationKind.MalformedText, LibraryMetadataField.Description);
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
    /// What the library's Use in AI cleanup box shows in this draft: the draft-time mirror of the policy's order (see the
    /// class remarks), with Decision 2 from the delegate for a library nothing recorded, the loss's denial, and content the
    /// Save writes (<see cref="DraftLibrary.WritesContent"/>) counted as accepted by that Save. The page renders the box
    /// from this, not from the committed runtime policy, which judges committed content only.
    /// </summary>
    public bool ShowsAiPermission(string libraryId) => ShownAi(_state, Get(libraryId));

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

    /// <summary>
    /// Use this copy instead: the copy on and its original off (<see cref="CopyOriginal"/>), as one undoable step.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The library has no original this draft can act on: it is not a copy, its original is deleted or gone, or its
    /// reference cannot be trusted (see <see cref="CopyOriginal"/>). Nothing is changed.
    /// </exception>
    public void UseCopyInstead(string copyId)
    {
        var copy = GetLive(copyId);
        if (IsReadOnly)
        {
            return;
        }

        var original = CopyOriginal(copy.Header.Id)
            ?? throw new InvalidOperationException("This copy's original can't be told for certain, so nothing was changed.");
        Structural(
            "Use this copy instead",
            _state.WithEnabled(copy.Header.Id, true).WithEnabled(original, false));
    }

    /// <summary>
    /// The library Use this copy instead would turn off for <paramref name="copyId"/>: the live library its based-on
    /// reference names. Null when there is none to act on: the library is not a copy, its original is deleted or gone,
    /// or the reference is one this session cannot trust. That is a saved copy whose file still names the id of a library
    /// the store kept under another id this session (another app took its file): the id now holds the other app's
    /// library, and the repair of the reference (made when the Save was marked saved) was discarded before it was saved.
    /// The page offers the command, and names the library it turns off, from this, so it never turns off a library that
    /// is not the copy's original (review finding A10).
    /// </summary>
    public string? CopyOriginal(string copyId)
    {
        var copy = Get(copyId);
        if (copy.Header.BuiltIn || copy.Header.PendingDelete || copy.Header.BasedOn is not { } basedOn)
        {
            return null;
        }

        var stale = copy.Header.Committed is { } committed
            && string.Equals(committed.Content.BasedOn, basedOn, StringComparison.OrdinalIgnoreCase)
            && _committed.KeptVersions.Any(kept =>
                kept.Kind == LibraryKeptVersionKind.SavedUnderNewId
                && string.Equals(kept.LibraryId, basedOn, StringComparison.OrdinalIgnoreCase));
        return !stale && Find(basedOn) is { } original && !original.Header.PendingDelete ? original.Header.Id : null;
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
    /// id, on when the retired built-in was on, AI permission through Decision 2 from the permission the retired
    /// built-in's edits document had: none unless the committed state accepted that document). Returns the id; asking
    /// again for the same retired built-in returns the library this draft already made.
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

        // The retired built-in's edits document is present, so what it passes on is the permission for that document
        // (review finding A4): one the state never accepted passes nothing on, whatever was chosen for the id.
        var inherited = ShownAi(_state, retired.LibraryId, builtIn: true, ContentAccepted(retired.LibraryId, builtIn: true, retired.ContentHash));
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
    /// growing past <see cref="LibraryLimits.MaxTermsPerLibrary"/> is refused. A built-in refuses a term with no spoken
    /// form, and one whose spoken form is the key of a row it already has: a built-in row is keyed by the spoken form it
    /// shipped with and keeps that key when the user renames it, so "get hub" added after the shipped "get hub" became
    /// "git hub" would be a second row with one key, which no edits document can hold. The refusal names that row.
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

        if (IllFormed(lib.Header.Id, values, null) is { } illFormed)
        {
            return new LibraryEditResult(false, illFormed);
        }

        var committed = LibraryEditor.Commit(values);
        if (lib.Header.BuiltIn)
        {
            var key = LibraryTermKey.From(committed.Spoken);
            if (key.IsEmpty)
            {
                // The overlay keys an added row by its spoken form, so a built-in has no blank placeholder row.
                return committed.Written.Length == 0
                    ? Refused()
                    : new LibraryEditResult(false, new LibraryValidationIssue(
                        lib.Header.Id, null, LibraryValidationKind.WrittenWithoutSpoken, TermFields.Spoken));
            }

            if (lib.Rows.FirstOrDefault(row => row.Row.Key == key) is { } holder)
            {
                return new LibraryEditResult(false, new LibraryValidationIssue(
                    lib.Header.Id, null, LibraryValidationKind.DuplicateSpoken, TermFields.Spoken, OtherRowId: holder.RowId));
            }
        }

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
    /// Edits a term. A custom row is committed whole (<see cref="LibraryEditor.Commit"/>) and re-keyed by its new Spoken
    /// value, so an edited legacy row is regular from then on (review finding A10). A built-in row goes through the overlay
    /// with only the fields the user changed committed and every other field exactly as the row shows it
    /// (<see cref="LibraryEditor.CommitChanges"/>), so an untouched field stays the base's and keeps taking later shipped
    /// values (plan 3.3); it keeps its key. A value edit removes the row's legacy marker. Ill-formed text is refused.
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
        if (IllFormed(lib.Header.Id, values, current.Row.Values) is { } illFormed)
        {
            return new LibraryEditResult(false, illFormed with { RowId = current.RowId });
        }

        var committed = lib.Header.BuiltIn
            ? LibraryEditor.CommitChanges(current.Row.Values, values)
            : LibraryEditor.Commit(values);
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

    /// <summary>
    /// Delete term, for a row the user owns: a custom library's row, or a built-in row with no shipped counterpart (added,
    /// or no longer shipped). Undoable.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The row has shipped values (shipped, edited, pinned, turned off, or an addition a later version ships): those are
    /// turned off or restored, never deleted, since deleting the entry would only bring the shipped row back at the next
    /// load. Or the library's content cannot be edited.
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
        if (lib.Header.BuiltIn && lib.Rows[index].Row.Shipped is not null)
        {
            throw new InvalidOperationException("A built-in term is turned off or restored, never deleted.");
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

        if (IllFormedIn(plan) is { } illFormed)
        {
            return new LibraryEditResult(false, illFormed);
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
            var header = lib.Header;
            var steps = StepsOf(lib, diff.Libraries[header.Id]);
            if (steps.HasFlag(SaveSteps.NotSaveable))
            {
                issues.Add(new LibraryValidationIssue(header.Id, null, LibraryValidationKind.ContentNotSaveable, TermFields.None));
            }

            if (steps.HasFlag(SaveSteps.Delete))
            {
                deletions.Add(new LibraryDeletion(header.Id, header.FileName ?? header.Id + ".csv", header.Committed!.ContentHash!.Value));
            }

            if (steps.HasFlag(SaveSteps.Restore))
            {
                var restored = header.RestoredFrom!;
                actions.Add(new RecentlyDeletedAction(
                    RecentlyDeletedActionKind.Restore, restored.Entry.EntryName, restored.Entry.ContentHash, header.Id));
            }

            if (!steps.HasFlag(SaveSteps.Write))
            {
                continue;
            }

            if (header.BuiltIn && header.Recovery != BuiltInEditsRecovery.None)
            {
                writes.Add(new LibraryWrite(
                    header.Id, true, LibraryOrigin.Existing, header.Committed?.ContentHash, Recovery: header.Recovery));
            }
            else if (header.BuiltIn)
            {
                ValidateRows(lib, issues);
                writes.Add(new LibraryWrite(
                    header.Id, true, LibraryOrigin.Existing, header.Committed?.ContentHash, Edits: diff.Libraries[header.Id].Edits));
            }
            else
            {
                ValidateRows(lib, issues);
                ValidateMetadata(lib, issues);
                var preImage = header.RestoredFrom is { } source ? source.Entry.ContentHash : header.Committed?.ContentHash;
                writes.Add(new LibraryWrite(header.Id, false, header.Origin, preImage, Content: ContentOf(lib)));
            }
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
    /// stays unsaved on top of it, structural ones included: a library the Save created or restored that the user
    /// removed meanwhile is a pending deletion of the saved library, a library it deleted that the user took back is an
    /// unsaved restore of the Recently deleted entry the Save made (review finding A2), and a restore or a permanent
    /// deletion of an entry the Save consumed becomes a new library or is dropped (A8). A library the Save had to keep
    /// under another id (another app took its file name) is the kept library from now on, edits after the capture
    /// included, and a library the user made meanwhile at that id moves to a fresh one (G1). The undo history ends here.
    /// Call it only when the Save stands (<see cref="LibrarySaveStatus.Applied"/> or
    /// <see cref="LibrarySaveStatus.AppliedAwaitingRelease"/>): after any other outcome the draft is still unsaved, and a
    /// newer catalog is taken with <see cref="Rebase"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">No change set was captured at <paramref name="revision"/>.</exception>
    public void MarkSaved(long revision, LibraryCatalog committed)
    {
        ArgumentNullException.ThrowIfNull(committed);
        if (!_captures.TryGetValue(revision, out var capture))
        {
            throw new InvalidOperationException("No change set was captured at that revision.");
        }

        RebaseOnto(Effective(capture), _state, committed);
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
    /// catalog's value, field by field for a library's name and details and row by row for its terms, and every change
    /// the user made stays unsaved on top of it. A library the user edited is then saved over what the newer catalog
    /// holds for it, so use this when the catalog moved for a reason the user has seen (a Save refused as stale, say),
    /// never to write over an outside edit without asking. A library the user edited that the newer catalog no longer has
    /// stays, unsaved, as a new file with the draft's content. The undo history ends here.
    /// </summary>
    public void Rebase(LibraryCatalog committed)
    {
        ArgumentNullException.ThrowIfNull(committed);
        RebaseOnto(_base, _state, committed);
    }

    // One rule takes a committed catalog under the live draft, for MarkSaved (base: the draft as the captured Save
    // committed it) and for Rebase (base: the catalog the draft started from), in three steps, each applied alike to every
    // library and every Recently deleted entry rather than case by case:
    //   1. Identities: which library of the catalog each library of the base and of the live draft is (Identities).
    //   2. The three-way merge undo and redo use: whatever the draft left as the base had it takes the catalog's value,
    //      and everything the draft changed stays, library by library, field by field and row by row (Merge).
    //   3. What the merged draft means against the catalog it now stands on (Settled).
    private void RebaseOnto(State from, State current, LibraryCatalog committed)
    {
        var (baseRenames, draftRenames) = Identities(from, current, committed);
        from = from.Renamed(baseRenames);
        current = current.Renamed(draftRenames);

        var to = FromCatalog(committed, mapFrom: from);
        var merged = Settled(Merge(current, from, to), from, current, to, committed);
        merged = WithReferencesFollowing(merged, baseRenames, draftRenames);

        _committed = committed;
        _base = to;
        ResetHistory();
        Commit(merged);
    }

    // A copy's based-on reference follows its original wherever the identities moved it, by the original's identity
    // (review finding A10): to the id the store kept it under even when the original left the live draft meanwhile, and
    // off an id a library made meanwhile had to give up. A copy the Save itself wrote names the planned id in its file,
    // which the other app's library now holds, so its reference is repaired in the draft and the copy shows as unsaved
    // until the next Save writes the corrected header; until then, CopyOriginal will not act on the reference its file
    // holds.
    private static State WithReferencesFollowing(
        State merged, IReadOnlyDictionary<string, string> baseRenames, IReadOnlyDictionary<string, string> draftRenames)
    {
        if (baseRenames.Count == 0 && draftRenames.Count == 0)
        {
            return merged;
        }

        var moved = new Dictionary<string, string>(baseRenames, StringComparer.OrdinalIgnoreCase);
        foreach (var (oldId, newId) in draftRenames)
        {
            moved[oldId] = newId;
        }

        foreach (var lib in merged.Libraries.Values.ToList())
        {
            if (!lib.Header.BuiltIn
                && !lib.Header.PendingDelete
                && lib.Header.BasedOn is { } basedOn
                && moved.TryGetValue(basedOn, out var movedTo))
            {
                merged = merged.WithLib(lib with { Header = lib.Header with { BasedOn = movedTo } });
            }
        }

        return merged;
    }

    // Step 1. A library keeps its identity: a library of the live draft is the base library with its id, and a base
    // library is the catalog's library with its id, except one the store kept under another id because another app took
    // its planned file (SavedUnderNewId), which is the kept library, in the base and in the live draft alike. A library
    // of the live draft with no base counterpart (made while the Save ran, or left out of the capture as untouched) never
    // shares an id with a different library: when the catalog, or a kept version, holds its id, it moves to a fresh one,
    // since the store invents a kept id at commit and TakenIds cannot know it. The renames are applied all at once, so no
    // library is ever renamed over another (review finding G1).
    private (Dictionary<string, string> Base, Dictionary<string, string> Draft) Identities(
        State from, State current, LibraryCatalog committed)
    {
        var baseRenames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kept in committed.KeptVersions)
        {
            if (kept.Kind == LibraryKeptVersionKind.SavedUnderNewId
                && kept.KeptAsId is { } keptAs
                && from.Libraries.TryGetValue(kept.LibraryId, out var planned)
                && !planned.Header.BuiltIn
                && planned.Header.Committed is null
                && !from.Libraries.ContainsKey(keptAs)
                && committed.Find(keptAs) is not null
                && !baseRenames.ContainsKey(planned.Header.Id)
                && !baseRenames.Values.Contains(keptAs, StringComparer.OrdinalIgnoreCase))
            {
                baseRenames[planned.Header.Id] = keptAs;
            }
        }

        var draftRenames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (planned, keptAs) in baseRenames)
        {
            if (current.Libraries.ContainsKey(planned))
            {
                draftRenames[planned] = keptAs;
            }
        }

        // A library of the live draft the base does not have must not hold an id the catalog uses for another library: a
        // kept version's id first of all (the targets), and any other the catalog brought in (a file placed meanwhile).
        var targets = new HashSet<string>(draftRenames.Values, StringComparer.OrdinalIgnoreCase);
        var taken = new HashSet<string>(TakenIds(includeRecentlyDeleted: true), StringComparer.OrdinalIgnoreCase);
        taken.UnionWith(committed.Libraries.Select(library => library.Content.Id));
        taken.UnionWith(current.Libraries.Keys);
        taken.UnionWith(baseRenames.Values);
        foreach (var lib in current.Libraries.Values.OrderBy(lib => lib.Header.Id, StringComparer.OrdinalIgnoreCase))
        {
            var id = lib.Header.Id;
            var inBase = from.Libraries.ContainsKey(id) && !baseRenames.ContainsKey(id);
            if (!draftRenames.ContainsKey(id) && (targets.Contains(id) || (!inBase && committed.Find(id) is not null)))
            {
                var fresh = LibraryNaming.NewCustomId(lib.Header.Name, taken);
                taken.Add(fresh);
                draftRenames[id] = fresh;
            }
        }

        return (baseRenames, draftRenames);
    }

    // Step 3. What the merged draft means against the catalog it now stands on, applied to every library and entry:
    //   - a library the catalog holds that the base had and the live draft no longer has (the user removed it while the
    //     Save ran) is a pending deletion of the committed library, which the next Save deletes (review finding A2);
    //   - a library the draft keeps whose committed file the catalog no longer has (the Save deleted it and the user took
    //     the deletion back, or it went outside Scribe after the user edited it) is a restore of the entry the Save made,
    //     or a new file with its content; a deletion it still asks for is done (A2);
    //   - a restore of an entry the catalog no longer lists (the Save restored it already, or it was purged) is a new
    //     library with the content the draft holds, and a permanent deletion of such an entry is dropped, so the next
    //     change set never names an entry that is gone (A8);
    //   - every library the catalog holds takes its committed facts from it.
    private State Settled(State merged, State from, State current, State to, LibraryCatalog committed)
    {
        foreach (var (id, saved) in to.Libraries)
        {
            if (!saved.Header.BuiltIn && !merged.Libraries.ContainsKey(id) && from.Libraries.ContainsKey(id))
            {
                merged = merged.WithLib(saved with { Header = saved.Header with { PendingDelete = true } });
            }
        }

        var entries = new Dictionary<string, RecentlyDeletedLibrary>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in committed.RecentlyDeleted)
        {
            entries.TryAdd(entry.EntryName, entry);
        }

        bool Listed(RecentlyDeletedLibrary entry) =>
            entries.TryGetValue(entry.EntryName, out var listed) && listed.ContentHash == entry.ContentHash;

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
                merged = lib.Header.PendingDelete ? merged.WithoutLib(id) : Recreated(merged, lib, current, committed);
            }
            else if (lib.Header.RestoredFrom is { } restoring && !Listed(restoring.Entry))
            {
                merged = merged.WithLib(lib with { Header = lib.Header with { Origin = LibraryOrigin.Created, RestoredFrom = null } });
            }
        }

        var purges = merged.Local.Purges;
        foreach (var (name, entry) in merged.Local.Purges)
        {
            if (!Listed(entry))
            {
                purges = purges.Remove(name);
            }
        }

        var local = merged.Local;
        return ReferenceEquals(purges, local.Purges)
            ? merged
            : merged with { Local = local.Update(local.Enabled, local.Ai, local.Markers, local.Notice, local.Lost, purges) };
    }

    // A library whose committed file the new catalog no longer has, kept by the draft: a restore of the Recently deleted
    // entry the Save that deleted it made (the same bytes: its original id, its hash, and new in this catalog), so
    // nothing but the move back is written; or, with no such entry, a new file with the draft's content. Its enabled
    // state, AI choice, markers and notice stay as the draft shows them.
    private State Recreated(State merged, Lib lib, State current, LibraryCatalog committed)
    {
        var id = lib.Header.Id;
        var gone = lib.Header.Committed!;
        var listed = _committed.RecentlyDeleted.Select(entry => entry.EntryName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entry = gone.ContentHash is { } hash
            ? committed.RecentlyDeleted.FirstOrDefault(candidate =>
                !listed.Contains(candidate.EntryName)
                && string.Equals(candidate.OriginalId, id, StringComparison.OrdinalIgnoreCase)
                && candidate.ContentHash == hash)
            : null;
        var header = lib.Header with
        {
            Origin = entry is null ? LibraryOrigin.Created : LibraryOrigin.Restored,
            FileState = entry is null ? lib.Header.FileState : gone.State,
            FileName = id + ".csv",
            Committed = null,
            RestoredFrom = entry is null ? null : new RecentlyDeletedContent(entry, gone.Content, gone.State),
            Created = null,
        };
        return merged.WithLib(lib with { Header = header }).WithLocalOf(id, current.Local);
    }

    // Ill-formed text in what an import would bring in: the name (a new library's), its details and every row it
    // applies. The codec never decodes bytes into ill-formed text, so this guards a plan made from anything else.
    private static LibraryValidationIssue? IllFormedIn(LibraryImportPlan plan)
    {
        var libraryId = plan.Target is LibraryImportTarget.ExistingLibrary into ? into.LibraryId : string.Empty;
        LibraryValidationIssue Issue(TermFields field, LibraryMetadataField metadata) =>
            new(libraryId, null, LibraryValidationKind.MalformedText, field, metadata);

        if (plan.Target is LibraryImportTarget.NewLibrary)
        {
            if (!LibraryEditor.IsWellFormed(plan.SuggestedName))
            {
                return Issue(TermFields.None, LibraryMetadataField.Name);
            }

            if (!LibraryEditor.IsWellFormed(plan.Category))
            {
                return Issue(TermFields.None, LibraryMetadataField.Category);
            }

            if (!LibraryEditor.IsWellFormed(plan.Description))
            {
                return Issue(TermFields.None, LibraryMetadataField.Description);
            }
        }

        foreach (var operation in plan.Operations.Where(operation => operation.Kind != LibraryImportOperationKind.AlreadyHere))
        {
            if (!LibraryEditor.IsWellFormed(operation.FileRow.Spoken))
            {
                return Issue(TermFields.Spoken, LibraryMetadataField.None);
            }

            if (!LibraryEditor.IsWellFormed(operation.FileRow.Written))
            {
                return Issue(TermFields.Written, LibraryMetadataField.None);
            }
        }

        return null;
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

        if (lib.Header.BuiltIn)
        {
            // An addition must not take a key the built-in already has (a renamed row keeps its original one), or the
            // document could not hold both. The planner meets such a file row with the row instead; this only guards a
            // plan made some other way.
            var keys = lib.Rows.Select(row => row.Row.Key).ToHashSet();
            foreach (var operation in plan.Operations.Where(operation => operation.Kind == LibraryImportOperationKind.Add))
            {
                var key = LibraryTermKey.From(operation.FileRow.Spoken);
                if (key.IsEmpty || !keys.Add(key))
                {
                    var holder = lib.Rows.FirstOrDefault(row => row.Row.Key == key);
                    return new LibraryEditResult(false, new LibraryValidationIssue(
                        lib.Header.Id, null, LibraryValidationKind.DuplicateSpoken, TermFields.Spoken, OtherRowId: holder?.RowId));
                }
            }
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

        if (builtIn)
        {
            // A renamed built-in row is still the term of its original spoken form, which is its key.
            for (var i = 0; i < list.Count; i++)
            {
                byKey.TryAdd(list[i].Row.Key, i);
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
                    var values = LibraryImportPlanner.FilesVersion(existing.Row.Values, operation.FileRow);
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

    // A blank placeholder is a custom row with nothing typed in it yet. A built-in row is never one: every row of a
    // built-in is a shipped or authored term with a key, so a built-in row the user cleared stays a term, shown, counted
    // and validated (it blocks Save until it is typed again or restored), never dropped as if it had not been there.
    private static bool IsBlank(DraftTermRow row) => row.Row.Origin == TermOrigin.Custom && IsBlank(row.Row.Values);

    private static int CountTerms(IEnumerable<DraftTermRow>? rows) => rows?.Count(row => !IsBlank(row)) ?? 0;

    private static bool TextChanged(TermValues before, TermValues after) =>
        !string.Equals(before.Spoken, after.Spoken, StringComparison.Ordinal)
        || !string.Equals(before.Written, after.Written, StringComparison.Ordinal)
        || before.WholeWord != after.WholeWord;

    // The first typed text field that is ill-formed UTF-16, as the issue that refuses it; a field typed exactly as
    // `current` shows it is not checked again. Null when both are well formed.
    private static LibraryValidationIssue? IllFormed(string libraryId, TermValues typed, TermValues? current)
    {
        if (!string.Equals(typed.Spoken, current?.Spoken, StringComparison.Ordinal) && !LibraryEditor.IsWellFormed(typed.Spoken))
        {
            return new LibraryValidationIssue(libraryId, null, LibraryValidationKind.MalformedText, TermFields.Spoken);
        }

        if (!string.Equals(typed.Written, current?.Written, StringComparison.Ordinal) && !LibraryEditor.IsWellFormed(typed.Written))
        {
            return new LibraryValidationIssue(libraryId, null, LibraryValidationKind.MalformedText, TermFields.Written);
        }

        return null;
    }

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
    // libraries; the stems of the files in the libraries folder (every CSV there is a catalog library); the libraries of a
    // Save in flight, which its commit may bring back even after the user removed them from the draft; and, unless a
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
            .Concat(_state.Libraries.Values.Select(lib => lib.Header.Id))
            .Concat(_captures.Values.SelectMany(capture => capture.State.Libraries.Keys));
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

    // The content check is skipped exactly for the libraries the Save writes (WritesContent: that Save records the hash
    // of what it writes) and for libraries the draft added, which have no committed content; every other library is
    // judged as the composition after the Save will judge it, by its committed content (the surface's WritesContent
    // rule, review finding A6 on the composition stream).
    private bool ShownAi(State state, Lib lib) =>
        ShownAi(state, lib.Header.Id, lib.Header.BuiltIn,
            contentAccepted: lib.Header.Committed is not { } committed
                || WritesContent(StepsOf(lib, Diff.Libraries[lib.Header.Id]))
                || ContentAccepted(lib.Header.Id, lib.Header.BuiltIn, committed.ContentHash));

    // What a library's Use in AI cleanup box shows, in the policy's order (see the class remarks): used where a new
    // library inherits a permission and where Use these choices records what is shown. contentAccepted is the content
    // check as the caller decided it for the draft (above).
    private bool ShownAi(State state, string id, bool builtIn, bool contentAccepted)
    {
        if (IsReadOnly || !contentAccepted)
        {
            return false;
        }

        if (state.Local.Ai.TryGetValue(id, out var chosen))
        {
            return chosen;
        }

        // A custom library meets the kind default only as a file nothing recorded, which is Decision 2's for a discovered
        // one, exactly as the policy asks it: never a rule of the workspace's own, so a change to Decision 2 is one change.
        return !state.Local.Lost
            && _defaultAiPermission(builtIn ? LibraryOrigin.Existing : LibraryOrigin.Discovered, builtIn, null);
    }

    // Whether content is what the committed state's choices were made for (review finding A4), as the policy decides it:
    // a built-in with no edits document holds its shipped rows, accepted only while no accepted entry remains for it (one
    // that does means its document disappeared outside Scribe, review finding A5 on the composition stream); an edits
    // document, a retired built-in's included, and every custom file must be the accepted content, and a custom file
    // whose bytes were not read is never accepted.
    private bool ContentAccepted(string id, bool builtIn, LibraryContentHash? content)
    {
        var hasAccepted = _committed.LocalState.AcceptedContent.TryGetValue(id, out var accepted);
        if (content is not { } held)
        {
            return builtIn && !hasAccepted;
        }

        return hasAccepted && accepted == held;
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
                // Rows that repeat a key (an undo brought back a row whose key a later addition took) are no document:
                // the capture refuses them with the reason, and the overlay is never asked to collect them.
                var terms = TermsOf(lib);
                if (terms.Select(row => row.Key).Distinct().Count() != terms.Count)
                {
                    contentChanged = true;
                }
                else
                {
                    var committedEdits = header.Committed?.Edits;
                    edits = _overlay.Collect(ShippedOf(lib), header.EditsReset ? null : committedEdits, terms);
                    contentChanged = !SameEdits(edits, committedEdits);
                }
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

    // What the Save of the draft at this revision does with one library, decided once: the capture builds its change set
    // from it, and the draft tells previews from it whether the library's content is written (WritesContent), so the two
    // can never disagree and no preview has to infer a write from what the rows show (a built-in's document can change
    // with no row changing: Restore all removes an off intent for a term this version does not ship).
    private SaveSteps StepsOf(Lib lib, LibraryChange change)
    {
        var header = lib.Header;
        if (IsReadOnly || change.Untouched)
        {
            return SaveSteps.None;
        }

        if (header.BuiltIn)
        {
            // A recovery the user chose is written on purpose, for a document this version cannot edit. An ordinary change
            // is written only while the content is editable: over a document now paused (newer, unreadable) or held by
            // another app it would replace that document without the recovery, and the pre-image check could not stop it,
            // since the write names that document's current hash (review finding A9).
            if (header.Recovery != BuiltInEditsRecovery.None)
            {
                return SaveSteps.Write;
            }

            return !change.ContentChanged ? SaveSteps.None : ContentEditable(lib) ? SaveSteps.Write : SaveSteps.NotSaveable;
        }

        if (header.PendingDelete)
        {
            return header.Committed?.ContentHash is null ? SaveSteps.NotSaveable : SaveSteps.Delete;
        }

        var steps = header.RestoredFrom is null ? SaveSteps.None : SaveSteps.Restore;
        if (!change.ContentChanged)
        {
            return steps;
        }

        return steps | (ContentEditable(lib) ? SaveSteps.Write : SaveSteps.NotSaveable);
    }

    // Whether the Save writes the library's content: a file or an edits document written or removed, a recovery, or a
    // restore of its Recently deleted entry. Not a change of its enabled state or AI permission alone, a pending deletion,
    // an untouched new library, or anything while the state is read-only.
    private static bool WritesContent(SaveSteps steps) => (steps & (SaveSteps.Write | SaveSteps.Restore)) != 0;

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

    // The same document whatever order its entries come in: keys are unique in a document, and after an upgrade the
    // overlay may collect the same entries in another order than the committed document holds them.
    private static bool SameEdits(BuiltInLibraryEdits? first, BuiltInLibraryEdits? second)
    {
        if (ReferenceEquals(first, second))
        {
            return true;
        }

        if (first is null
            || second is null
            || !string.Equals(first.LibraryId, second.LibraryId, StringComparison.OrdinalIgnoreCase)
            || first.Terms.Count != second.Terms.Count)
        {
            return false;
        }

        var byKey = new Dictionary<LibraryTermKey, BuiltInTermEdit>();
        foreach (var term in second.Terms)
        {
            if (!byKey.TryAdd(term.Key, term))
            {
                return false;
            }
        }

        var seen = new HashSet<LibraryTermKey>();
        return first.Terms.All(term => seen.Add(term.Key) && byKey.TryGetValue(term.Key, out var other) && other == term);
    }

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
        var keyed = new Dictionary<LibraryTermKey, DraftTermRow>();
        foreach (var row in lib.Rows)
        {
            if (IsBlank(row))
            {
                continue;
            }

            committed.TryGetValue(row.RowId, out var before);
            var key = LibraryTermKey.From(row.Row.Values.Spoken);

            // A built-in row keeps the key of the spoken form it was shipped or added with when it is renamed, and an
            // edits document holds one entry per key, so a key two rows share blocks whatever they speak now. Rows that
            // passed that far can only share the key of a spoken form they both speak, which the next check reports.
            if (lib.Header.BuiltIn && !keyed.TryAdd(row.Row.Key, row))
            {
                issues.Add(new LibraryValidationIssue(
                    id, row.RowId, LibraryValidationKind.DuplicateSpoken, TermFields.Spoken, OtherRowId: keyed[row.Row.Key].RowId));
            }
            else if (key.IsEmpty)
            {
                issues.Add(new LibraryValidationIssue(id, row.RowId, LibraryValidationKind.WrittenWithoutSpoken, TermFields.Spoken));
            }
            else if (!first.TryAdd(key, row) && Blocks(lib, row, before, first[key], committed))
            {
                issues.Add(new LibraryValidationIssue(
                    id, row.RowId, LibraryValidationKind.DuplicateSpoken, TermFields.Spoken, OtherRowId: first[key].RowId));
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

        // Every path into the draft refuses ill-formed text; this is the last check before anything is written, for
        // content that came from elsewhere (a restored entry, a retired built-in's rows).
        if (!LibraryEditor.IsWellFormed(values.Spoken))
        {
            issues.Add(new LibraryValidationIssue(id, row.RowId, LibraryValidationKind.MalformedText, TermFields.Spoken));
        }

        if (!LibraryEditor.IsWellFormed(values.Written))
        {
            issues.Add(new LibraryValidationIssue(id, row.RowId, LibraryValidationKind.MalformedText, TermFields.Written));
        }

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
        var sharesKey = lib.Header.BuiltIn
            ? lib.Rows.FirstOrDefault(candidate => candidate.RowId != row.RowId && candidate.Row.Key == row.Row.Key)
            : null;
        if (sharesKey is not null)
        {
            issues.Add(new LibraryValidationIssue(
                lib.Header.Id, row.RowId, LibraryValidationKind.DuplicateSpoken, TermFields.Spoken, OtherRowId: sharesKey.RowId));
        }
        else if (key.IsEmpty)
        {
            issues.Add(new LibraryValidationIssue(lib.Header.Id, row.RowId, LibraryValidationKind.WrittenWithoutSpoken, TermFields.Spoken));
        }
        else
        {
            var other = lib.Rows.FirstOrDefault(candidate =>
                candidate.RowId != row.RowId && !IsBlank(candidate) && LibraryTermKey.From(candidate.Row.Values.Spoken) == key);
            if (other is not null && Blocks(lib, row, before, other, committed))
            {
                issues.Add(new LibraryValidationIssue(
                    lib.Header.Id, row.RowId, LibraryValidationKind.DuplicateSpoken, TermFields.Spoken, OtherRowId: other.RowId));
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

        foreach (var (value, field) in new[]
        {
            (header.Name, LibraryMetadataField.Name),
            (header.Category, LibraryMetadataField.Category),
            (header.Description, LibraryMetadataField.Description),
            (header.BasedOn, LibraryMetadataField.BasedOn),
        })
        {
            if (!LibraryEditor.IsWellFormed(value))
            {
                issues.Add(new LibraryValidationIssue(header.Id, null, LibraryValidationKind.MalformedText, TermFields.None, field));
            }
        }
    }

    private static bool OddQuotes(string? value) => !LibraryMetadata.ReadsBackInOlderVersions(value);

    private LibraryDraft BuildDraft()
    {
        var diff = Diff;
        var ordered = Ordered(_state).ToList();

        // WritesContent is the capture's own decision for each library (StepsOf), so a preview is told exactly which
        // content the Save writes and never infers it from what the rows show.
        var libraries = ordered
            .Select(lib =>
            {
                var change = diff.Libraries[lib.Header.Id];
                return new DraftLibrary(
                    ContentOf(lib),
                    lib.Header.Origin,
                    lib.Header.FileState,
                    lib.Header.PendingDelete,
                    change.Unsaved,
                    lib.Header.BuiltIn ? null : lib.Header.FileName,
                    WritesContent(StepsOf(lib, change)));
            })
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

    // The draft of a catalog with nothing changed. Rows keep the ids they had in mapFrom where they are the same rows
    // (MapRowIds: a custom row by its values or its one spoken form, a built-in's rows by their key), so a Save or a reload
    // does not move the selection and a rebase merges row by row; every other row gets a new id.
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

    // Which row of `previous` each row of `content` is, so a Save, a rebase or a reload keeps the selection, and a rebase
    // merges a custom library row by row (review finding A3). A custom row is met first by its exact values, in order among
    // rows with the same values, then by its spoken form where exactly one row on each side is left with it; a spoken form
    // more rows could be keeps no identity, so its rows meet as a conflict the Save names (a repeated spoken form) rather
    // than one of them being guessed. A built-in's rows are met by their keys. Every other row gets a new id.
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
            var byValues = new Dictionary<TermValues, Queue<long>>();
            foreach (var row in earlier)
            {
                if (!byValues.TryGetValue(row.Row.Values, out var queue))
                {
                    byValues[row.Row.Values] = queue = new Queue<long>();
                }

                queue.Enqueue(row.RowId);
            }

            var matched = new HashSet<long>();
            for (var i = 0; i < content.Rows.Count; i++)
            {
                if (byValues.TryGetValue(content.Rows[i].Values, out var queue) && queue.TryDequeue(out var id))
                {
                    ids[i] = id;
                    matched.Add(id);
                }
            }

            var unmatched = earlier
                .Where(row => !matched.Contains(row.RowId))
                .GroupBy(row => row.Row.Key)
                .Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single().RowId);
            var changed = Enumerable.Range(0, content.Rows.Count)
                .Where(i => ids[i] is null)
                .GroupBy(i => content.Rows[i].Key)
                .Where(group => group.Count() == 1);
            foreach (var group in changed)
            {
                if (unmatched.TryGetValue(group.Key, out var id))
                {
                    ids[group.Single()] = id;
                }
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

            var header = MergeHeader(mine.Header, before.Header, after.Header);
            var rows = mine.Rows;
            if (!ReferenceEquals(before.Rows, after.Rows))
            {
                // Rows the draft left as they were take the other side's list whole, its order included.
                rows = !SameRows(mine.Rows, before.Rows) ? MergeRows(mine.Rows, before.Rows, after.Rows)
                    : SameRows(mine.Rows, after.Rows) ? mine.Rows
                    : after.Rows;
            }

            if (header != mine.Header || !ReferenceEquals(rows, mine.Rows))
            {
                libraries = libraries.SetItem(id, new Lib(header, rows));
            }
        }

        var merged = libraries;
        var local = MergeLocal(current.Local, from.Local, to.Local, marker => SameRowsOfKey(merged, to.Libraries, marker));
        return ReferenceEquals(libraries, current.Libraries) && ReferenceEquals(local, current.Local)
            ? current
            : new State(libraries, local);
    }

    // Each field of a library's header merges on its own (review finding A4): the draft's value where the draft changed
    // that field, the other side's for every field it did not, so a rename a newer catalog brought and a category the user
    // typed both stand. The committed facts merge the same way here, and a rebase then takes them from the new catalog.
    private static Header MergeHeader(Header mine, Header before, Header after)
    {
        if (before == after || mine == after)
        {
            return mine;
        }

        if (mine == before)
        {
            return after;
        }

        var merged = new Header(
            mine.Id,
            mine.BuiltIn,
            Pick(mine.Name, before.Name, after.Name),
            Pick(mine.Category, before.Category, after.Category),
            Pick(mine.Description, before.Description, after.Description),
            Pick(mine.BasedOn, before.BasedOn, after.BasedOn),
            Pick(mine.PendingDelete, before.PendingDelete, after.PendingDelete),
            Pick(mine.Recovery, before.Recovery, after.Recovery),
            Pick(mine.EditsReset, before.EditsReset, after.EditsReset),
            Pick(mine.Origin, before.Origin, after.Origin),
            Pick(mine.FileState, before.FileState, after.FileState),
            Pick(mine.FileName, before.FileName, after.FileName),
            Pick(mine.Committed, before.Committed, after.Committed),
            Pick(mine.RestoredFrom, before.RestoredFrom, after.RestoredFrom),
            Pick(mine.Created, before.Created, after.Created));
        return merged == mine ? mine : merged;
    }

    // One field of a three-way merge: the other side's value unless the draft changed it.
    private static T Pick<T>(T mine, T before, T after) => EqualityComparer<T>.Default.Equals(mine, before) ? after : mine;

    // Whether the rows a legacy marker answers are, after a merge, exactly the rows `to` has for it. A marker changes only
    // together with the state of its row (review finding A5): an undo never brings one back over a correction the user
    // typed after the operation, which would have composition treat that correction as a legacy row behind the built-in.
    private static bool SameRowsOfKey(
        ImmutableDictionary<string, Lib> merged, ImmutableDictionary<string, Lib> to, LegacyMarker marker)
    {
        IEnumerable<DraftTermRow> RowsWithKey(ImmutableDictionary<string, Lib> libraries) =>
            libraries.TryGetValue(marker.LibraryId, out var lib)
                ? lib.Rows.Where(row => !IsBlank(row) && row.Row.Key == marker.Key)
                : [];

        return RowsWithKey(merged).SequenceEqual(RowsWithKey(to));
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

    private static Local MergeLocal(Local current, Local from, Local to, Func<LegacyMarker, bool> followsItsRow)
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

        // A marker comes and goes only with the state of the row it answers.
        var markers = current.Markers;
        if (!ReferenceEquals(from.Markers, to.Markers))
        {
            var fromSet = from.Markers.ToHashSet();
            var toSet = to.Markers.ToHashSet();
            foreach (var marker in fromSet.Where(marker => !toSet.Contains(marker) && followsItsRow(marker)))
            {
                markers = markers.Remove(marker);
            }

            foreach (var marker in to.Markers.Where(marker => !fromSet.Contains(marker) && followsItsRow(marker)))
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

    // What a Save does with one library (StepsOf).
    [Flags]
    private enum SaveSteps
    {
        None = 0,

        // A LibraryWrite: a custom library's file, a built-in's edits document written or removed, or a recovery.
        Write = 1,

        // A restore of the library's Recently deleted entry.
        Restore = 2,

        // The library's file moves to Recently deleted.
        Delete = 4,

        // Its content changed but cannot be saved here (ContentNotSaveable), which blocks the Save.
        NotSaveable = 8,
    }

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

        // Every library named in `renames` under its new id, all at once, with its enabled state, AI choice, markers and
        // notice; a library not written yet takes the file name of its new id. Local entries a library that is gone left
        // at a new id are dropped, since the library renamed there brings its own. A rename never lands on a library that
        // is not itself renamed away (review finding G1): a caller that would do that is wrong, and it throws.
        public State Renamed(IReadOnlyDictionary<string, string> renames)
        {
            var moving = renames
                .Where(pair => Libraries.ContainsKey(pair.Key) && !string.Equals(pair.Key, pair.Value, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            if (moving.Count == 0)
            {
                return this;
            }

            var libraries = Libraries.RemoveRange(moving.Keys);
            foreach (var (oldId, newId) in moving)
            {
                if (libraries.ContainsKey(newId))
                {
                    throw new InvalidOperationException("A library would be renamed over another one.");
                }

                var lib = Libraries[oldId];
                var header = lib.Header with
                {
                    Id = newId,
                    FileName = lib.Header.BuiltIn || lib.Header.Committed is not null ? lib.Header.FileName : newId + ".csv",
                };
                libraries = libraries.Add(newId, lib with { Header = header });
            }

            var arriving = new HashSet<string>(moving.Values, StringComparer.OrdinalIgnoreCase);
            bool Kept(string id) => !arriving.Contains(id) || moving.ContainsKey(id);
            string Map(string id) => moving.TryGetValue(id, out var newId) ? newId : id;
            var enabled = ImmutableHashSet.CreateRange(StringComparer.OrdinalIgnoreCase, Local.Enabled.Where(Kept).Select(Map));
            var ai = ImmutableDictionary.CreateRange(
                StringComparer.OrdinalIgnoreCase,
                Local.Ai.Where(pair => Kept(pair.Key)).Select(pair => new KeyValuePair<string, bool>(Map(pair.Key), pair.Value)));
            var markers = Local.Markers
                .Where(marker => Kept(marker.LibraryId))
                .Select(marker => moving.ContainsKey(marker.LibraryId) ? marker with { LibraryId = Map(marker.LibraryId) } : marker)
                .ToImmutableList();
            var notice = ImmutableHashSet.CreateRange(StringComparer.OrdinalIgnoreCase, Local.Notice.Where(Kept).Select(Map));
            return new State(libraries, Local.Update(enabled, ai, markers, notice, Local.Lost, Local.Purges));
        }

        private State WithLocal(Local local) => ReferenceEquals(local, Local) ? this : this with { Local = local };

        private static bool IsOf(LegacyMarker marker, string id) =>
            string.Equals(marker.LibraryId, id, StringComparison.OrdinalIgnoreCase);
    }
}
