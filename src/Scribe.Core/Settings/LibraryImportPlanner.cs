using Scribe.Core.Libraries;

namespace Scribe.Core.Settings;

/// <summary>Where an import goes.</summary>
public abstract record LibraryImportTarget
{
    private LibraryImportTarget()
    {
    }

    /// <summary>Create a new library.</summary>
    /// <param name="FileName">The imported file's name, which names the library when its header does not.</param>
    public sealed record NewLibrary(string? FileName) : LibraryImportTarget;

    /// <summary>Add to an existing library; a built-in's rows become edits and additions.</summary>
    public sealed record ExistingLibrary(string LibraryId) : LibraryImportTarget;
}

/// <summary>What an import does with a file row written differently from a term already there.</summary>
public enum ImportConflictChoice
{
    /// <summary>Keep the term as it is (the default).</summary>
    KeepMine,

    /// <summary>Take the file's written form, whole-word and enabled values.</summary>
    UseFilesVersion,
}

/// <summary>What applying one file row means.</summary>
public enum LibraryImportOperationKind
{
    /// <summary>A new term.</summary>
    Add,

    /// <summary>
    /// The spoken form is already there with a different written form, whole-word or enabled value (decision 3: whole
    /// word counts), in the library or earlier in the same file.
    /// </summary>
    WrittenDifferently,

    /// <summary>The term is already there exactly; nothing to do, kept for the counts.</summary>
    AlreadyHere,
}

/// <summary>One validated file row and what applying it means.</summary>
/// <param name="Kind">What applying it does.</param>
/// <param name="FileRow">The row as the file holds it, faithful (imports may keep white space at a value's edges).</param>
/// <param name="ExistingRowId">
/// The workspace row it meets, for <see cref="LibraryImportOperationKind.WrittenDifferently"/> and
/// <see cref="LibraryImportOperationKind.AlreadyHere"/> against a row of the target; null for an added row, a row that
/// meets an earlier row of the same file, and a draft no workspace built.
/// </param>
/// <param name="ExistingValues">The values it meets: the target's row, or the earlier file row.</param>
public sealed record LibraryImportOperation(
    LibraryImportOperationKind Kind, TermValues FileRow, long? ExistingRowId, TermValues? ExistingValues);

/// <summary>
/// An import preview, self-contained (review finding A9): every validated file row in file order with what applying
/// it means, the counts derived from them, the metadata, and the draft revision it was computed against.
/// <c>LibraryWorkspace.ApplyImport</c> applies exactly these rows and refuses a plan for another revision.
/// </summary>
/// <param name="Target">Where it goes.</param>
/// <param name="DraftRevision">The draft revision the plan was computed against.</param>
/// <param name="SuggestedName">
/// For a new library: the <c># name:</c> header, else the file's name, else "Imported library", committed and made
/// unique. A name holding a double quote is shown here and must be edited before the import applies (the shell passes
/// the edited name as <c>plan with { SuggestedName = ... }</c>). For an existing library, its name.
/// </param>
/// <param name="Category">The <c># category:</c> header, committed, or null.</param>
/// <param name="Description">The <c># description:</c> header, committed, or null.</param>
/// <param name="BasedOnTarget">
/// The library the file's <c># based-on:</c> names, when the draft has it: offered as a target while New library stays
/// selected. Null otherwise.
/// </param>
/// <param name="Operations">Every validated file row, in file order.</param>
/// <param name="Adds">Operations that add a term.</param>
/// <param name="WrittenDifferently">Operations written differently, which Keep mine leaves alone.</param>
/// <param name="AlreadyHere">Operations already there exactly.</param>
/// <param name="RemovalRules">Added terms with an empty written form ("2 remove words").</param>
/// <param name="Skipped">Rows that could not be used, imported only through "Import N valid terms".</param>
/// <param name="SkippedRows">Those rows, with the codec's reasons.</param>
/// <param name="Encoding">How the file was decoded.</param>
/// <param name="NonAsciiRows">File rows holding a letter outside ASCII, which the preview lists after an ANSI fallback.</param>
public sealed record LibraryImportPlan(
    LibraryImportTarget Target,
    long DraftRevision,
    string SuggestedName,
    string? Category,
    string? Description,
    string? BasedOnTarget,
    IReadOnlyList<LibraryImportOperation> Operations,
    int Adds,
    int WrittenDifferently,
    int AlreadyHere,
    int RemovalRules,
    int Skipped,
    IReadOnlyList<LibraryCsvRowError> SkippedRows,
    LibraryTextEncoding Encoding,
    IReadOnlyList<TermValues> NonAsciiRows);

/// <summary>
/// The import preview (plan 3.10): which file rows add a term, which are written differently, which are already there,
/// and what the new library would be called.
/// </summary>
/// <remarks>
/// Rows are matched by spoken form trimmed and compared without case (<see cref="LibraryTermKey"/>), the rule
/// <see cref="DictionaryImportMerger"/> applies to the dictionary, and in the same single pass: a row the import adds is
/// what a later row of the file with the same spoken form meets. Two files that differ in one row therefore give plans
/// that differ in that row. Pure; the file was read by the codec already.
/// </remarks>
public static class LibraryImportPlanner
{
    /// <summary>The plan for importing <paramref name="document"/> into <paramref name="target"/> at the draft's revision.</summary>
    /// <exception cref="ArgumentException">An existing-library target the draft does not have, or one deleted in it.</exception>
    public static LibraryImportPlan Plan(LibraryCsvDocument document, LibraryImportTarget target, LibraryDraft draft)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(draft);

        DraftLibrary? existing = null;
        if (target is LibraryImportTarget.ExistingLibrary into)
        {
            existing = draft.Find(into.LibraryId);
            if (existing is null || existing.PendingDelete)
            {
                throw new ArgumentException("The draft has no such library to import into.", nameof(target));
            }
        }

        var rows = existing?.Content.Rows ?? [];
        var known = new Dictionary<LibraryTermKey, (TermValues Values, long? RowId)>();
        for (var i = 0; i < rows.Count; i++)
        {
            var key = LibraryTermKey.From(rows[i].Values.Spoken);
            if (!key.IsEmpty)
            {
                known.TryAdd(key, (rows[i].Values, LibraryWorkspace.RowIdIn(draft, existing!.Content.Id, i)));
            }
        }

        var operations = new List<LibraryImportOperation>(document.Terms.Count);
        var skippedRows = new List<LibraryCsvRowError>(document.Errors);
        var nonAscii = new List<TermValues>();
        foreach (var term in document.Terms)
        {
            var key = LibraryTermKey.From(term.Spoken);
            if (key.IsEmpty)
            {
                // The codec never returns such a row; one here is counted as skipped rather than imported as a rule
                // that could never match.
                skippedRows.Add(new LibraryCsvRowError(0, LibraryCsvRowErrorKind.EmptySpoken));
                continue;
            }

            if (HasNonAsciiLetter(term.Spoken) || HasNonAsciiLetter(term.Written))
            {
                nonAscii.Add(term);
            }

            if (known.TryGetValue(key, out var met))
            {
                var kind = Same(term, met.Values) ? LibraryImportOperationKind.AlreadyHere : LibraryImportOperationKind.WrittenDifferently;
                operations.Add(new LibraryImportOperation(kind, term, met.RowId, met.Values));
            }
            else
            {
                operations.Add(new LibraryImportOperation(LibraryImportOperationKind.Add, term, null, null));
                known[key] = (term, null);
            }
        }

        var names = draft.Libraries.Where(library => !library.PendingDelete).Select(library => library.Content.Name);
        var suggested = existing is not null
            ? existing.Content.Name
            : LibraryNaming.UniqueName(NameFor(document, (LibraryImportTarget.NewLibrary)target), names);
        var basedOn = string.IsNullOrWhiteSpace(document.BasedOn) ? null : draft.Find(document.BasedOn.Trim());
        return new LibraryImportPlan(
            target,
            draft.Revision,
            suggested,
            Committed(document.Category),
            Committed(document.Description),
            basedOn is null || basedOn.PendingDelete ? null : basedOn.Content.Id,
            operations,
            Adds: operations.Count(operation => operation.Kind == LibraryImportOperationKind.Add),
            WrittenDifferently: operations.Count(operation => operation.Kind == LibraryImportOperationKind.WrittenDifferently),
            AlreadyHere: operations.Count(operation => operation.Kind == LibraryImportOperationKind.AlreadyHere),
            RemovalRules: operations.Count(operation =>
                operation.Kind == LibraryImportOperationKind.Add && operation.FileRow.Written.Length == 0),
            Skipped: skippedRows.Count,
            skippedRows,
            document.Encoding,
            nonAscii);
    }

    // The header's name, else the file's name without its extension, else "Imported library".
    private static string NameFor(LibraryCsvDocument document, LibraryImportTarget.NewLibrary target)
    {
        var header = LibraryMetadata.Commit(document.Name);
        if (header.Length > 0)
        {
            return header;
        }

        var file = LibraryMetadata.Commit(string.IsNullOrWhiteSpace(target.FileName)
            ? null
            : Path.GetFileNameWithoutExtension(target.FileName.Trim()));
        return file.Length > 0 ? file : LibraryNaming.ImportedLibraryBaseName;
    }

    private static string? Committed(string? value)
    {
        var committed = LibraryMetadata.Commit(value);
        return committed.Length == 0 ? null : committed;
    }

    // "A different written form" (decision 3): the written form, compared exactly, and the whole-word and enabled
    // values; the spoken forms already match by key.
    private static bool Same(TermValues file, TermValues existing) =>
        string.Equals(file.Written, existing.Written, StringComparison.Ordinal)
        && file.WholeWord == existing.WholeWord
        && file.Enabled == existing.Enabled;

    private static bool HasNonAsciiLetter(string value)
    {
        foreach (var ch in value)
        {
            if (ch > 0x7F && char.IsLetter(ch))
            {
                return true;
            }
        }

        return false;
    }
}
