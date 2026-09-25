using System.Globalization;
using System.Text;
using Scribe.Core.PostProcessing;

namespace Scribe.Core.Libraries;

// Everything in this file stands in for another sub-stream's part until the integration commit, which deletes the file
// and points LibraryServiceParts.Default at LibraryCsvCodec, BuiltInLibraryOverlay and LibraryComposer, and J's naming
// calls at LibraryNaming. Each one reproduces what release 0.4.4 does, so this branch runs exactly as today.

/// <summary>
/// Today's library CSV reading and writing behind <see cref="ILibraryCsvCodec"/>: <see cref="DictionaryLibraryCsv"/> for
/// both reads, decoded the way <c>File.ReadAllText</c> decodes, and its writer for both writes (an export adds a byte
/// order mark); no strict rules for marked files and no formula guard.
/// </summary>
internal sealed class InterimCsvCodec : ILibraryCsvCodec
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static InterimCsvCodec Instance { get; } = new();

    private InterimCsvCodec()
    {
    }

    public LibraryCsvDocument ReadManaged(ReadOnlySpan<byte> bytes) => Read(bytes);

    public byte[] WriteManaged(LibraryContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Utf8NoBom.GetBytes(DictionaryLibraryCsv.Export(ToLibrary(content)));
    }

    public LibraryCsvDocument ReadImport(ReadOnlySpan<byte> bytes) => Read(bytes);

    public byte[] WriteExport(LibraryContent content) => [.. Encoding.UTF8.Preamble, .. WriteManaged(content)];

    private static LibraryCsvDocument Read(ReadOnlySpan<byte> bytes)
    {
        var array = bytes.ToArray();
        string text;
        int codePage;
        using (var reader = new StreamReader(new MemoryStream(array), Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            text = reader.ReadToEnd();
            codePage = reader.CurrentEncoding.CodePage;
        }

        var byteOrderMark = HasByteOrderMark(array);
        var invalidReplaced = false;
        if (codePage == Encoding.UTF8.CodePage)
        {
            try
            {
                StrictUtf8.GetString(array);
            }
            catch (DecoderFallbackException)
            {
                invalidReplaced = true;
            }
        }

        var file = DictionaryLibraryCsv.Parse(text);
        return new LibraryCsvDocument(
            file.Name,
            file.Category,
            file.Description,
            BasedOn: null,
            [.. file.Entries.Select(TermValues.FromEntry)],
            [.. file.Errors.Select(ToRowError)],
            new LibraryTextEncoding(codePage, byteOrderMark, AnsiFallback: false, invalidReplaced));
    }

    private static bool HasByteOrderMark(byte[] bytes) =>
        bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) || bytes.AsSpan().StartsWith(Encoding.Unicode.Preamble) ||
        bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.Preamble);

    // Today's parser reports "Line N: ..." strings; the line and the kind are all the document shape carries.
    private static LibraryCsvRowError ToRowError(string error)
    {
        var line = 0;
        const string Prefix = "Line ";
        var colon = error.IndexOf(':', StringComparison.Ordinal);
        if (error.StartsWith(Prefix, StringComparison.Ordinal) && colon > Prefix.Length)
        {
            int.TryParse(error.AsSpan(Prefix.Length, colon - Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out line);
        }

        var kind = error.Contains("closing quote", StringComparison.Ordinal) ? LibraryCsvRowErrorKind.UnclosedQuote
            : error.Contains("is empty", StringComparison.Ordinal) ? LibraryCsvRowErrorKind.EmptySpoken
            : error.Contains("whole_word", StringComparison.Ordinal) ? LibraryCsvRowErrorKind.InvalidWholeWord
            : error.Contains("enabled should", StringComparison.Ordinal) ? LibraryCsvRowErrorKind.InvalidEnabled
            : LibraryCsvRowErrorKind.MissingFields;
        return new LibraryCsvRowError(line, kind);
    }

    internal static DictionaryLibrary ToLibrary(LibraryContent content) => new(
        content.Id,
        content.Name,
        content.Category,
        content.Description,
        content.BuiltIn,
        [.. content.Rows.Select(row => row.Values.ToEntry())]);
}

/// <summary>
/// No built-in edits, as today: every built-in is its shipped rows, any edits document reads as
/// <see cref="LibraryFileState.Unreadable"/> (only a later build writes one), and nothing edits a built-in row.
/// </summary>
internal sealed class InterimOverlay : IBuiltInLibraryOverlay
{
    public static InterimOverlay Instance { get; } = new();

    private InterimOverlay()
    {
    }

    public BuiltInEditsReadResult ReadEdits(string libraryId, ReadOnlySpan<byte> bytes) =>
        new(LibraryFileState.Unreadable, null, null);

    public byte[] WriteEdits(BuiltInLibraryEdits edits) =>
        throw new NotSupportedException("Built-in edits are not written until the overlay is integrated.");

    public IReadOnlyList<LibraryRow> Apply(DictionaryLibrary shipped, BuiltInLibraryEdits? edits)
    {
        ArgumentNullException.ThrowIfNull(shipped);
        return [.. shipped.Entries.Select(entry =>
        {
            var values = TermValues.FromEntry(entry);
            return new LibraryRow(LibraryTermKey.From(values.Spoken), values, TermOrigin.Shipped, Shipped: values);
        })];
    }

    public LibraryRow Edit(LibraryRow row, TermValues values) => throw new NotSupportedException();

    public LibraryRow SetEnabled(LibraryRow row, bool enabled) => throw new NotSupportedException();

    public LibraryRow? RestoreShipped(LibraryRow row) => throw new NotSupportedException();

    public LibraryRow Add(TermValues values) => throw new NotSupportedException();

    public LibraryRow ResolveReview(LibraryRow row, TermReviewChoice choice) => throw new NotSupportedException();

    public BuiltInLibraryEdits? Collect(DictionaryLibrary shipped, BuiltInLibraryEdits? committed, IReadOnlyList<LibraryRow> rows) =>
        committed;

    public IReadOnlyList<TermValues> AuthoredTerms(BuiltInLibraryEdits edits)
    {
        ArgumentNullException.ThrowIfNull(edits);
        return [.. edits.Terms
            .Where(term => term.Intent is BuiltInTermIntent.Edited or BuiltInTermIntent.Pinned or BuiltInTermIntent.Added)
            .Select(term => term.Value)
            .OfType<TermValues>()];
    }
}

/// <summary>
/// Today's composition and selection, as the local state model: the enabled libraries are the ones the settings
/// document's list names (each listed id standing for every library loaded under it, as 0.4.3 applies it), composed
/// first-wins in precedence order by <see cref="DictionaryLibraryComposer.ComposeLibraries"/>; every enabled library is
/// sent to AI cleanup, as today; no state row is written, nothing is adopted and no content is accepted, so nothing reads
/// as replaced. A stored state row can only be a later build's here, and is read as newer, so this build never writes
/// over it.
/// </summary>
internal sealed class InterimComposer : ILibraryComposer
{
    public static InterimComposer Instance { get; } = new();

    private InterimComposer()
    {
    }

    public LibraryLocalState ReadLocalState(
        IReadOnlyList<string>? documentEnabledIds,
        string? storedState,
        IReadOnlyList<LibraryIdentity> libraries,
        LibraryStateContext context)
    {
        ArgumentNullException.ThrowIfNull(libraries);
        var listed = new HashSet<string>(documentEnabledIds ?? [], StringComparer.OrdinalIgnoreCase);
        var enabled = libraries.Where(library => listed.Contains(library.LegacyId)).Select(library => library.Id);
        return LibraryLocalState.Create(
            enabled, documentEnabledIds, aiPermissions: null, legacyMarkers: null, aiUpgradeNotice: null,
            storedState is null ? LocalStateHealth.Absent : LocalStateHealth.Newer);
    }

    public LibraryStateEncoding EncodeLocalState(
        LibraryLocalState state,
        LibraryLocalState? startingState,
        IReadOnlyList<LibraryIdentity> librariesBefore,
        IReadOnlyList<LibraryIdentity> librariesAfter)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(librariesBefore);
        ArgumentNullException.ThrowIfNull(librariesAfter);

        // Today's list is the enabled ids themselves: the list as read, with each library the state turns on added and
        // each legacy id no library of which stays on dropped. An id no library has is kept, as 0.4.3 keeps it.
        var libraries = librariesBefore.Concat(librariesAfter).DistinctBy(library => library.Id, StringComparer.OrdinalIgnoreCase).ToList();
        var list = new HashSet<string>(state.LegacyEnabledIds, StringComparer.OrdinalIgnoreCase);
        foreach (var group in libraries.GroupBy(library => library.LegacyId, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Any(library => state.EnabledIds.Contains(library.Id)))
            {
                list.Add(group.Key);
            }
            else
            {
                list.Remove(group.Key);
            }
        }

        var ordered = LibraryPrecedence.Order(
                libraries.Where(library => list.Contains(library.LegacyId)).DistinctBy(library => library.LegacyId, StringComparer.OrdinalIgnoreCase),
                library => library.Id, library => library.BuiltIn, library => library.FileName)
            .Select(library => library.LegacyId)
            .ToList();
        ordered.AddRange(list.Where(id => !ordered.Contains(id, StringComparer.OrdinalIgnoreCase))
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ThenBy(id => id, StringComparer.Ordinal));
        return new LibraryStateEncoding(ordered, StateValue: null);
    }

    public LibraryAdoption? PlanAdoption(LibraryCatalog catalog, LibraryStateContext context) => null;

    public LibraryVocabulary ComposeVocabulary(LibraryCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var enabled = EnabledLibraries(catalog);
        var entries = DictionaryLibraryComposer.ComposeLibraries(enabled.Select(ToDictionaryLibrary));
        var scope = new AiVocabularyScope(
            catalog.Generation, enabled.Select(library => new KeyValuePair<string, LibraryContentHash?>(library.Content.Id, null)));
        return new LibraryVocabulary(catalog.Generation, entries, entries, scope);
    }

    public bool HasNarrowed(AiVocabularyScope admitted, AiVocabularyScope current)
    {
        ArgumentNullException.ThrowIfNull(admitted);
        ArgumentNullException.ThrowIfNull(current);
        return admitted.PermittedLibraryIds.Any(id => !current.PermittedLibraryIds.Contains(id));
    }

    public AiVocabularyScope ScopeWhileSaving(LibraryCatalog committed, LibraryChangeSet changes)
    {
        ArgumentNullException.ThrowIfNull(committed);
        ArgumentNullException.ThrowIfNull(changes);
        var deleted = changes.Deletions.Select(deletion => deletion.LibraryId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remaining = ComposeVocabulary(committed).AiScope.PermittedLibraryIds
            .Where(id => changes.LocalState.EnabledIds.Contains(id) && !deleted.Contains(id));
        return new AiVocabularyScope(
            committed.Generation, remaining.Select(id => new KeyValuePair<string, LibraryContentHash?>(id, null)));
    }

    private static List<CatalogLibrary> EnabledLibraries(LibraryCatalog catalog) =>
        [.. catalog.Libraries.Where(library =>
            catalog.LocalState.EnabledIds.Contains(library.Content.Id) &&
            library.State is LibraryFileState.Available or LibraryFileState.PartlyReadable or LibraryFileState.AwaitingRelease)];

    private static DictionaryLibrary ToDictionaryLibrary(CatalogLibrary library) =>
        InterimCsvCodec.ToLibrary(library.Content) with { FileName = library.Content.BuiltIn ? null : library.FileName };
}

/// <summary>
/// The id and name rules of <c>LibraryNaming</c> (contract 3.5.4, the deciders' file), copied here until the
/// integration commit points the journal at it. <see cref="Slug"/> is release 0.4.4's <c>Slugify</c>, verbatim.
/// </summary>
internal static class InterimLibraryNaming
{
    /// <summary><c>name + " (changed outside Scribe)"</c>, the display name a kept outside version's id is derived from.</summary>
    public static string ChangedOutsideName(string name) => name + " (changed outside Scribe)";

    /// <summary><c>"custom-" + Slug(name)</c>, then <c>-2</c>, <c>-3</c> while taken, compared without case.</summary>
    public static string NewCustomId(string name, Func<string, bool> taken) => Suffixed("custom-" + Slug(name), taken);

    /// <summary>Release 0.4.4's id for an imported library: <see cref="Slug"/>, then <c>-2</c>, <c>-3</c> while taken.</summary>
    public static string ImportId(string name, Func<string, bool> taken) => Suffixed(Slug(name), taken);

    /// <summary>A candidate, then <c>-2</c>, <c>-3</c> appended while <paramref name="taken"/> says it is.</summary>
    public static string Suffixed(string baseId, Func<string, bool> taken)
    {
        var candidate = baseId;
        for (var n = 2; taken(candidate); n++)
        {
            candidate = baseId + "-" + n.ToString(CultureInfo.InvariantCulture);
        }

        return candidate;
    }

    // Lowercase, alphanumerics kept, every other run collapsed to a single hyphen; a safe file name
    // and stable id derived from the library's display name.
    public static string Slug(string value)
    {
        var sb = new StringBuilder(value.Length);
        var pendingDash = false;
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                if (pendingDash && sb.Length > 0)
                {
                    sb.Append('-');
                }

                sb.Append(ch);
                pendingDash = false;
            }
            else
            {
                pendingDash = true;
            }
        }

        var slug = sb.ToString();
        return slug.Length == 0 ? "library" : slug;
    }
}
