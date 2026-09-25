using System.Security.Cryptography;
using System.Text;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests.Libraries.Deciders;

/// <summary>
/// What the deciders' tests build catalogs, drafts and change sets from: an overlay following the amended 3.2.1 rules
/// (the real one is another stream's until integration), a pair of fake shipped libraries, Decision 2 as the plan states
/// it, and a fake store that applies a change set to a catalog the way the journal's committed result reads back.
/// </summary>
internal static class DeciderFixture
{
    public const string GitHubId = "github";
    public const string AzureId = "microsoft-azure";

    public static readonly IReadOnlyList<DictionaryLibrary> Shipped =
    [
        new DictionaryLibrary(GitHubId, "GitHub", "Developer", "GitHub terms", BuiltIn: true,
        [
            DictionaryEntry.New("get hub", "GitHub"),
            DictionaryEntry.New("copilot", "Copilot"),
            DictionaryEntry.New("octo cat", "Octocat"),
        ]),
        new DictionaryLibrary(AzureId, "Microsoft Azure", "Microsoft", null, BuiltIn: true,
        [
            DictionaryEntry.New("a k s", "AKS"),
            DictionaryEntry.New("copilot", "Azure Copilot"),
        ]),
    ];

    public static readonly FakeOverlay TestOverlay = new();

    /// <summary>Decision 2 as the plan states it: built-ins on, existing on, duplicates and retired rows inherit, the rest off.</summary>
    public static bool DefaultAi(LibraryOrigin origin, bool builtIn, bool? source) =>
        builtIn || origin switch
        {
            LibraryOrigin.Existing => true,
            LibraryOrigin.Duplicated or LibraryOrigin.RetiredBuiltIn => source ?? false,
            _ => false,
        };

    public static DictionaryLibrary? ShippedLibrary(string id) =>
        Shipped.FirstOrDefault(library => string.Equals(library.Id, id, StringComparison.OrdinalIgnoreCase));

    public static LibraryWorkspace Workspace(LibraryCatalog catalog) =>
        new(catalog, TestOverlay, DefaultAi, ShippedLibrary);

    public static LibraryContentHash Hash(string text) =>
        new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant());

    public static LibraryContentHash HashOf(LibraryContent content) =>
        Hash(string.Join("\n", new[] { content.Name, content.Category, content.Description ?? "", content.BasedOn ?? "" }
            .Concat(content.Rows.Select(row => $"{row.Values.Spoken}|{row.Values.Written}|{row.Values.WholeWord}|{row.Values.Enabled}"))));

    public static LibraryContentHash HashOf(BuiltInLibraryEdits edits) =>
        Hash(edits.LibraryId + "\n" + string.Join("\n", edits.Terms.Select(term => $"{term.Key.Value}|{term.Intent}|{term.Value}")));

    public static LibraryContent CustomContent(
        string id, string name, IEnumerable<TermValues> rows, string category = "Custom", string? description = null, string? basedOn = null) =>
        new(id, false, name, category, description, rows.Select(LibraryRow.Custom).ToList(), basedOn);

    public static CatalogLibrary Custom(
        string id, string name, IEnumerable<TermValues> rows, string category = "Custom", string? description = null,
        string? basedOn = null, LibraryFileState state = LibraryFileState.Available, string? fileName = null)
    {
        var content = CustomContent(id, name, rows, category, description, basedOn);
        return new CatalogLibrary(
            content, state, fileName ?? id + ".csv", state == LibraryFileState.Unreadable ? null : HashOf(content),
            ReadErrorCount: state == LibraryFileState.PartlyReadable ? 1 : 0);
    }

    public static CatalogLibrary BuiltIn(
        string id, BuiltInLibraryEdits? edits = null, LibraryFileState state = LibraryFileState.Available, bool previousEdits = false)
    {
        var shipped = ShippedLibrary(id)!;
        var paused = state is LibraryFileState.Unreadable or LibraryFileState.Newer;
        var rows = paused ? [] : TestOverlay.Apply(shipped, edits);
        var content = new LibraryContent(id, true, shipped.Name, shipped.Category, shipped.Description, rows);
        LibraryContentHash? hash = paused ? Hash("paused " + id) : edits is null ? null : HashOf(edits);
        return new CatalogLibrary(content, state, null, hash, paused ? null : edits, previousEdits);
    }

    /// <summary>
    /// A catalog; the local state accepts every library's content, so nothing reads as replaced, plus the entries of
    /// <paramref name="accepted"/> (a retired built-in's document, say), which override a library's own.
    /// </summary>
    public static LibraryCatalog Catalog(
        IEnumerable<CatalogLibrary> libraries,
        IEnumerable<string>? enabled = null,
        IEnumerable<KeyValuePair<string, bool>>? ai = null,
        IEnumerable<LegacyMarker>? markers = null,
        IEnumerable<string>? notice = null,
        LocalStateHealth health = LocalStateHealth.Ok,
        bool lost = false,
        IEnumerable<RecentlyDeletedLibrary>? recentlyDeleted = null,
        IEnumerable<RetiredBuiltInEdits>? retired = null,
        long generation = 3,
        IEnumerable<LibraryKeptVersion>? kept = null,
        IEnumerable<KeyValuePair<string, LibraryContentHash>>? accepted = null)
    {
        var list = LibraryPrecedence.Order(
            libraries, library => library.Content.Id, library => library.Content.BuiltIn, library => library.FileName).ToList();
        var acceptedContent = list
            .Where(library => library.ContentHash is not null)
            .Select(library => new KeyValuePair<string, LibraryContentHash>(library.Content.Id, library.ContentHash!.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var (id, hash) in accepted ?? [])
        {
            acceptedContent[id] = hash;
        }

        var enabledIds = enabled?.ToList();
        var state = LibraryLocalState.Create(enabledIds, enabledIds, ai, markers, notice, health, acceptedContent, lost);
        return new LibraryCatalog(
            generation, list, state, (recentlyDeleted ?? []).ToList(), (retired ?? []).ToList(), filesAwaitingRelease: 0,
            (kept ?? []).ToList());
    }

    /// <summary>A small catalog every test can start from: both fake built-ins and one custom library, "Team terms".</summary>
    public static LibraryCatalog Standard(IEnumerable<string>? enabled = null) =>
        Catalog(
            [
                BuiltIn(GitHubId),
                BuiltIn(AzureId),
                Custom("team-terms", "Team terms", [new TermValues("kube", "Kubernetes"), new TermValues("get hub", "GitHub Enterprise")]),
            ],
            enabled ?? [GitHubId, "team-terms"],
            ai: [new("team-terms", true)]);

    public static RecentlyDeletedContent Deleted(string entryName, string originalId, string name, params TermValues[] rows)
    {
        var content = CustomContent(originalId, name, rows);
        var hash = HashOf(content);
        return new RecentlyDeletedContent(
            new RecentlyDeletedLibrary(entryName, originalId, name, rows.Length, new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero),
                LibraryFileState.Available, hash),
            content);
    }

    public static LibraryCsvDocument Document(string? name, params TermValues[] rows) =>
        new(name, null, null, null, rows, [], new LibraryTextEncoding(65001, false, false, false));

    public static long RowIdOf(LibraryWorkspace workspace, string libraryId, string spoken) =>
        workspace.RowsOf(libraryId).Single(row => row.Row.Values.Spoken == spoken).RowId;

    public static IReadOnlyList<TermValues> ValuesOf(LibraryWorkspace workspace, string libraryId) =>
        workspace.Draft.Find(libraryId)!.Content.Rows.Select(row => row.Values).ToList();

    /// <summary>
    /// Whether the draft at the workspace's revision says its Save writes the library's content
    /// (<see cref="DraftLibrary.WritesContent"/>), read from the draft, where a preview reads it.
    /// </summary>
    public static bool WritesContent(LibraryWorkspace workspace, string libraryId) =>
        workspace.Draft.Find(libraryId)!.WritesContent;

    /// <summary>Whether a change set writes the library's content: a write for it, or a restore of its Recently deleted entry.</summary>
    public static bool Writes(LibraryChangeSet changes, string libraryId) =>
        changes.Writes.Any(write => string.Equals(write.LibraryId, libraryId, StringComparison.OrdinalIgnoreCase))
        || changes.RecentlyDeletedActions.Any(action =>
            action.Kind == RecentlyDeletedActionKind.Restore
            && string.Equals(action.RestoreAsId, libraryId, StringComparison.OrdinalIgnoreCase));

    public static LibraryChangeSet Capture(LibraryWorkspace workspace)
    {
        var result = workspace.CaptureChangeSet();
        Assert.Empty(result.Issues);
        return Assert.IsType<LibraryChangeSet>(result.ChangeSet);
    }

    /// <summary>
    /// A change set applied to <paramref name="catalog"/> the way the committed result reads back: custom writes stored
    /// as written, built-in documents applied through the overlay, deletions moved to Recently deleted, restores brought
    /// back (with the draft's edit when it made one), the local state with the accepted content of every file written.
    /// </summary>
    public static LibraryCatalog Apply(
        LibraryCatalog catalog, LibraryChangeSet changes, IDictionary<string, RecentlyDeletedContent>? store = null)
    {
        store ??= new Dictionary<string, RecentlyDeletedContent>(StringComparer.OrdinalIgnoreCase);
        var libraries = catalog.Libraries.ToDictionary(library => library.Content.Id, StringComparer.OrdinalIgnoreCase);
        var recentlyDeleted = catalog.RecentlyDeleted.ToList();
        var accepted = changes.LocalState.AcceptedContent.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var stamp = catalog.Generation;

        foreach (var deletion in changes.Deletions)
        {
            var library = libraries[deletion.LibraryId];
            Assert.Equal(library.ContentHash, deletion.ExpectedPreImage);
            libraries.Remove(deletion.LibraryId);
            var entry = new RecentlyDeletedLibrary(
                $"20260925T0000{stamp:00}Z.{deletion.FileName}", deletion.LibraryId, library.Content.Name, library.Content.Rows.Count,
                new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero), LibraryFileState.Available, library.ContentHash);
            recentlyDeleted.Insert(0, entry);
            store[entry.EntryName] = new RecentlyDeletedContent(entry, library.Content);
        }

        foreach (var action in changes.RecentlyDeletedActions)
        {
            var entry = recentlyDeleted.Single(candidate => candidate.EntryName == action.EntryName);
            Assert.Equal(entry.ContentHash, action.ExpectedHash);
            recentlyDeleted.Remove(entry);
            if (action.Kind == RecentlyDeletedActionKind.Restore)
            {
                var content = store[action.EntryName].Content with { Id = action.RestoreAsId! };
                libraries[action.RestoreAsId!] = new CatalogLibrary(content, LibraryFileState.Available, action.RestoreAsId + ".csv", HashOf(content));
                accepted[action.RestoreAsId!] = HashOf(content);
            }
        }

        foreach (var write in changes.Writes)
        {
            if (!write.BuiltIn)
            {
                var content = write.Content!;
                var fileName = libraries.TryGetValue(write.LibraryId, out var existing) ? existing.FileName : write.LibraryId + ".csv";
                libraries[write.LibraryId] = new CatalogLibrary(content, LibraryFileState.Available, fileName, HashOf(content));
                accepted[write.LibraryId] = HashOf(content);
                continue;
            }

            var edits = write.Recovery == BuiltInEditsRecovery.None ? write.Edits : null;
            libraries[write.LibraryId] = BuiltIn(write.LibraryId, edits);
            if (edits is null)
            {
                accepted.Remove(write.LibraryId);
            }
            else
            {
                accepted[write.LibraryId] = HashOf(edits);
            }
        }

        var local = changes.LocalState;
        var ids = libraries.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var state = LibraryLocalState.Create(
            local.EnabledIds.Where(ids.Contains),
            local.LegacyEnabledIds,
            local.AiPermissions.Where(pair => ids.Contains(pair.Key)),
            local.LegacyMarkers.Where(marker => ids.Contains(marker.LibraryId)),
            local.AiUpgradeNotice.Where(ids.Contains),
            LocalStateHealth.Ok,
            accepted.Where(pair => ids.Contains(pair.Key)),
            local.AiPermissionsLost);
        return new LibraryCatalog(
            catalog.Generation + 1,
            LibraryPrecedence.Order(libraries.Values, library => library.Content.Id, library => library.Content.BuiltIn, library => library.FileName).ToList(),
            state,
            recentlyDeleted,
            catalog.RetiredBuiltInEdits,
            filesAwaitingRelease: 0);
    }

    /// <summary>
    /// An overlay that follows the amended 3.2.1 rules, written from the contract rather than taken from the overlay
    /// stream: one definition of a row per entry (<see cref="RowOf"/>), which Apply, every command and Collect's check go
    /// through; per-field authorship, a changed field based on the shipped value in use; an edit of an off row keeping the
    /// off authored; Turn on against a disabled shipped row authoring an edited turn-on; reviews raised and resolved; and a
    /// Collect that refuses a custom row, a repeated key and any row it would not give itself, and writes its entries in
    /// the one order.
    /// </summary>
    internal sealed class FakeOverlay : IBuiltInLibraryOverlay
    {
        private const TermFields All = TermFields.Spoken | TermFields.Written | TermFields.WholeWord | TermFields.Enabled;

        private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        public BuiltInEditsReadResult ReadEdits(string libraryId, ReadOnlySpan<byte> bytes) => throw new NotSupportedException();

        public byte[] WriteEdits(BuiltInLibraryEdits edits) => throw new NotSupportedException();

        public IReadOnlyList<LibraryRow> Apply(DictionaryLibrary shipped, BuiltInLibraryEdits? edits)
        {
            var byKey = new Dictionary<LibraryTermKey, BuiltInTermEdit>();
            foreach (var edit in edits?.Terms ?? [])
            {
                if (!byKey.TryAdd(edit.Key, edit))
                {
                    throw new ArgumentException("A document holds one entry per key.", nameof(edits));
                }
            }

            var (order, shippedValues) = ShippedValues(shipped);
            var rows = new List<LibraryRow>();
            foreach (var key in order)
            {
                var values = shippedValues[key];
                rows.Add(byKey.TryGetValue(key, out var edit) ? RowOf(values, edit)! : ShippedRow(key, values));
            }

            foreach (var edit in edits?.Terms ?? [])
            {
                if (!shippedValues.ContainsKey(edit.Key) && RowOf(null, edit) is { } row)
                {
                    rows.Add(row);
                }
            }

            return rows;
        }

        public LibraryRow Edit(LibraryRow row, TermValues values)
        {
            EnsureCanonical(row);
            EnsureText(values);
            if (row.Values == values)
            {
                return row;
            }

            var changed = Differ(row.Values, values);
            return changed == TermFields.Enabled ? SetEnabled(row, values.Enabled) : EditValues(row, values, changed);
        }

        public LibraryRow SetEnabled(LibraryRow row, bool enabled)
        {
            EnsureCanonical(row);
            if (row.Values.Enabled == enabled)
            {
                return row;
            }

            if (row.Origin == TermOrigin.Shipped && !enabled)
            {
                return RowOf(row.Shipped, new BuiltInTermEdit(row.Key, BuiltInTermIntent.Off, row.Shipped, null))!;
            }

            // Turn on removes an off entry only while this version ships the row on; against a disabled shipped row it
            // is the user's own choice, an edited turn-on.
            if (row.Edit?.Intent == BuiltInTermIntent.Off && row.Shipped!.Enabled)
            {
                return ShippedRow(row.Key, row.Shipped);
            }

            return EditValues(row, row.Values with { Enabled = enabled }, TermFields.Enabled);
        }

        public LibraryRow? RestoreShipped(LibraryRow row)
        {
            EnsureCanonical(row);
            return row.Shipped is not { } shipped ? null : row.Origin == TermOrigin.Shipped ? row : ShippedRow(row.Key, shipped);
        }

        public LibraryRow Add(TermValues values)
        {
            // As the real overlay: an added row is keyed by its spoken form, so a blank one is a caller's bug.
            ArgumentException.ThrowIfNullOrWhiteSpace(values.Spoken);
            EnsureText(values);
            return RowOf(null, new BuiltInTermEdit(LibraryTermKey.From(values.Spoken), BuiltInTermIntent.Added, null, values))!;
        }

        public LibraryRow ResolveReview(LibraryRow row, TermReviewChoice choice)
        {
            EnsureCanonical(row);
            if (row.Review is null)
            {
                return row;
            }

            var shipped = row.Shipped!;
            return choice == TermReviewChoice.KeepMine
                ? RowOf(shipped, row.Edit! with { Acknowledged = shipped })!
                : RowOf(shipped, new BuiltInTermEdit(row.Key, BuiltInTermIntent.Pinned, shipped, shipped))!;
        }

        public BuiltInLibraryEdits? Collect(DictionaryLibrary shipped, BuiltInLibraryEdits? committed, IReadOnlyList<LibraryRow> rows)
        {
            var (order, shippedValues) = ShippedValues(shipped);
            var byKey = new Dictionary<LibraryTermKey, LibraryRow>();
            foreach (var row in rows)
            {
                // As the real overlay: a custom row, two rows with one key, or a row it would not give for this shipped
                // library is no document (an authored row would be saved as something other than what the user saw).
                if (row.Origin == TermOrigin.Custom || !byKey.TryAdd(row.Key, row))
                {
                    throw new ArgumentException("Rows the overlay did not give, or rows that repeat a key.", nameof(rows));
                }

                if (!IsCanonical(row, shippedValues.TryGetValue(row.Key, out var values) ? values : null))
                {
                    throw new ArgumentException("A row the overlay would not give for this shipped library.", nameof(rows));
                }
            }

            var committedByKey = new Dictionary<LibraryTermKey, BuiltInTermEdit>();
            foreach (var entry in committed?.Terms ?? [])
            {
                committedByKey.TryAdd(entry.Key, entry);
            }

            // Shipped rows in shipped order (a row left out keeps its committed entry, but an addition), then the rows this
            // version does not ship in the order given, then kept off entries whose row this version does not ship.
            var entries = new List<BuiltInTermEdit>();
            foreach (var key in order)
            {
                if (byKey.TryGetValue(key, out var row))
                {
                    if (row.Edit is { } edit)
                    {
                        entries.Add(edit);
                    }
                }
                else if (committedByKey.TryGetValue(key, out var entry) && entry.Intent != BuiltInTermIntent.Added)
                {
                    entries.Add(entry);
                }
            }

            entries.AddRange(rows.Where(row => !shippedValues.ContainsKey(row.Key) && row.Edit is not null).Select(row => row.Edit!));
            entries.AddRange((committed?.Terms ?? []).Where(entry =>
                entry.Intent == BuiltInTermIntent.Off && !shippedValues.ContainsKey(entry.Key) && !byKey.ContainsKey(entry.Key)));
            return entries.Count == 0 ? null : new BuiltInLibraryEdits(shipped.Id, entries);
        }

        public IReadOnlyList<TermValues> AuthoredTerms(BuiltInLibraryEdits edits) =>
            edits.Terms.Where(term => term.Intent != BuiltInTermIntent.Off && term.Value is not null).Select(term => term.Value!).ToList();

        // The one definition of a built-in row: an entry against the shipped values in use, or null when this version does
        // not ship its key and the entry is an off one, which then shows no row.
        internal static LibraryRow? RowOf(TermValues? shipped, BuiltInTermEdit edit)
        {
            switch (edit.Intent)
            {
                case BuiltInTermIntent.Off:
                    return shipped is null ? null : new LibraryRow(edit.Key, shipped with { Enabled = false }, TermOrigin.Off, shipped, edit);

                case BuiltInTermIntent.Added:
                    var added = edit.Value!;
                    return new LibraryRow(
                        edit.Key, added, shipped is not null && added == shipped ? TermOrigin.Pinned : TermOrigin.Added, shipped, edit);

                case BuiltInTermIntent.Edited when shipped is not null:
                {
                    // Per field: a field the user left at its base takes the shipped value; a field both changed keeps the
                    // user's and asks, unless the user already kept it against this shipped value.
                    var user = edit.Value!;
                    var authored = Differ(user, edit.Base!);
                    var merged = Take(user, shipped, ~authored & All);
                    var asks = authored & Differ(shipped, edit.Base!) & Differ(user, shipped)
                        & (edit.Acknowledged is { } acknowledged ? Differ(acknowledged, shipped) : All);
                    return new LibraryRow(
                        edit.Key, merged, merged == shipped ? TermOrigin.Pinned : TermOrigin.Edited, shipped, edit,
                        asks == TermFields.None ? null : new TermReview(merged, shipped, Differ(merged, shipped)));
                }

                case BuiltInTermIntent.Pinned when shipped is not null:
                {
                    var user = edit.Value!;
                    var asks = Differ(shipped, user) & (edit.Acknowledged is { } acknowledged ? Differ(shipped, acknowledged) : All);
                    return new LibraryRow(
                        edit.Key, user, user == shipped ? TermOrigin.Pinned : TermOrigin.Edited, shipped, edit,
                        asks == TermFields.None ? null : new TermReview(user, shipped, Differ(user, shipped)));
                }

                default:
                    return new LibraryRow(edit.Key, edit.Value!, TermOrigin.NoLongerShipped, null, edit);
            }
        }

        private static LibraryRow EditValues(LibraryRow row, TermValues values, TermFields changed)
        {
            var shipped = row.Shipped;
            var edit = row.Edit;
            if (edit is null)
            {
                // A shipped row: an edited entry based on the shipped values, the user's values equal to them but in the
                // changed fields.
                return RowOf(shipped, new BuiltInTermEdit(row.Key, BuiltInTermIntent.Edited, shipped, values))!;
            }

            switch (edit.Intent)
            {
                case BuiltInTermIntent.Off:
                    // The off stays authored: Enabled off against a base that is on, unless this edit turns the row on.
                    return RowOf(shipped, new BuiltInTermEdit(
                        row.Key, BuiltInTermIntent.Edited, values.Enabled ? shipped : shipped! with { Enabled = true }, values))!;

                case BuiltInTermIntent.Edited when shipped is not null:
                    // A changed field takes the typed value against the shipped value in use; the others keep theirs.
                    return RowOf(shipped, edit with { Base = Take(edit.Base!, shipped, changed), Value = Take(edit.Value!, values, changed) })!;

                case BuiltInTermIntent.Pinned when shipped is not null:
                    // A pinned entry keeps its base and acknowledges the shipped value in use for the changed fields.
                    var acknowledged = Take(edit.Acknowledged ?? edit.Value!, shipped, changed);
                    return RowOf(shipped, edit with { Value = values, Acknowledged = acknowledged == values ? null : acknowledged })!;

                default:
                    return RowOf(shipped, edit with { Value = Take(edit.Value!, values, changed) })!;
            }
        }

        // The shipped rows by key in shipped order; a key shipped twice counts once, as its first row.
        private static (List<LibraryTermKey> Order, Dictionary<LibraryTermKey, TermValues> Values) ShippedValues(DictionaryLibrary shipped)
        {
            var order = new List<LibraryTermKey>();
            var values = new Dictionary<LibraryTermKey, TermValues>();
            foreach (var entry in shipped.Entries)
            {
                var row = TermValues.FromEntry(entry);
                var key = LibraryTermKey.From(row.Spoken);
                if (values.TryAdd(key, row))
                {
                    order.Add(key);
                }
            }

            return (order, values);
        }

        private static LibraryRow ShippedRow(LibraryTermKey key, TermValues shipped) => new(key, shipped, TermOrigin.Shipped, shipped);

        // Rebuilt from the shipped values in use and its own entry, the row comes out the same.
        private static bool IsCanonical(LibraryRow row, TermValues? shipped) =>
            row.Origin != TermOrigin.Custom
            && row.Shipped == shipped
            && (row.Origin == TermOrigin.Shipped
                ? shipped is not null && row == ShippedRow(row.Key, shipped)
                : row.Edit is { } edit && edit.Key == row.Key && RowOf(shipped, edit) == row);

        private static void EnsureCanonical(LibraryRow row)
        {
            if (!IsCanonical(row, row.Shipped))
            {
                throw new ArgumentException("A row the overlay did not give.", nameof(row));
            }
        }

        // As the real overlay: values that are not well-formed text (an unpaired surrogate) are refused, judged by a
        // strict encoder rather than by the editor's own check.
        private static void EnsureText(TermValues values)
        {
            try
            {
                Strict.GetByteCount(values.Spoken);
                Strict.GetByteCount(values.Written);
            }
            catch (EncoderFallbackException)
            {
                throw new ArgumentException("Values that are not well-formed text.", nameof(values));
            }
        }

        private static TermFields Differ(TermValues first, TermValues second) =>
            (string.Equals(first.Spoken, second.Spoken, StringComparison.Ordinal) ? TermFields.None : TermFields.Spoken)
            | (string.Equals(first.Written, second.Written, StringComparison.Ordinal) ? TermFields.None : TermFields.Written)
            | (first.WholeWord == second.WholeWord ? TermFields.None : TermFields.WholeWord)
            | (first.Enabled == second.Enabled ? TermFields.None : TermFields.Enabled);

        private static TermValues Take(TermValues into, TermValues from, TermFields fields) => new(
            fields.HasFlag(TermFields.Spoken) ? from.Spoken : into.Spoken,
            fields.HasFlag(TermFields.Written) ? from.Written : into.Written,
            fields.HasFlag(TermFields.WholeWord) ? from.WholeWord : into.WholeWord,
            fields.HasFlag(TermFields.Enabled) ? from.Enabled : into.Enabled);
    }
}
