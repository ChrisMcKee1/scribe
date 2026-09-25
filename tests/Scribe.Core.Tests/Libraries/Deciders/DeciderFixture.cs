using System.Security.Cryptography;
using System.Text;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests.Libraries.Deciders;

/// <summary>
/// What the deciders' tests build catalogs, drafts and change sets from: a minimal built-in overlay implementing the
/// 3.2.1 transitions, a pair of fake shipped libraries, Decision 2 as the plan states it, and a fake store that applies
/// a change set to a catalog the way the journal's committed result reads back.
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

    /// <summary>A catalog; the local state accepts every library's content, so nothing reads as replaced.</summary>
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
        IEnumerable<LibraryKeptVersion>? kept = null)
    {
        var list = LibraryPrecedence.Order(
            libraries, library => library.Content.Id, library => library.Content.BuiltIn, library => library.FileName).ToList();
        var accepted = list
            .Where(library => library.ContentHash is not null)
            .Select(library => new KeyValuePair<string, LibraryContentHash>(library.Content.Id, library.ContentHash!.Value));
        var enabledIds = enabled?.ToList();
        var state = LibraryLocalState.Create(enabledIds, enabledIds, ai, markers, notice, health, accepted, lost);
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

    /// <summary>A minimal overlay: the 3.2.1 transitions, and an Apply and a Collect that are each other's inverse.</summary>
    internal sealed class FakeOverlay : IBuiltInLibraryOverlay
    {
        public BuiltInEditsReadResult ReadEdits(string libraryId, ReadOnlySpan<byte> bytes) => throw new NotSupportedException();

        public byte[] WriteEdits(BuiltInLibraryEdits edits) => throw new NotSupportedException();

        public IReadOnlyList<LibraryRow> Apply(DictionaryLibrary shipped, BuiltInLibraryEdits? edits)
        {
            var byKey = (edits?.Terms ?? []).ToDictionary(term => term.Key);
            var shippedKeys = new HashSet<LibraryTermKey>();
            var rows = new List<LibraryRow>();
            foreach (var entry in shipped.Entries)
            {
                var values = TermValues.FromEntry(entry);
                var key = LibraryTermKey.From(values.Spoken);
                shippedKeys.Add(key);
                if (!byKey.TryGetValue(key, out var edit))
                {
                    rows.Add(new LibraryRow(key, values, TermOrigin.Shipped, values));
                }
                else if (edit.Intent == BuiltInTermIntent.Off)
                {
                    rows.Add(new LibraryRow(key, values with { Enabled = false }, TermOrigin.Off, values, edit));
                }
                else
                {
                    var origin = edit.Value == values
                        ? TermOrigin.Pinned
                        : edit.Intent == BuiltInTermIntent.Added ? TermOrigin.Added : TermOrigin.Edited;
                    rows.Add(new LibraryRow(key, edit.Value!, origin, values, edit));
                }
            }

            foreach (var edit in edits?.Terms ?? [])
            {
                if (shippedKeys.Contains(edit.Key))
                {
                    continue;
                }

                if (edit.Intent == BuiltInTermIntent.Added)
                {
                    rows.Add(new LibraryRow(edit.Key, edit.Value!, TermOrigin.Added, null, edit));
                }
                else if (edit.Intent is BuiltInTermIntent.Edited or BuiltInTermIntent.Pinned)
                {
                    rows.Add(new LibraryRow(edit.Key, edit.Value!, TermOrigin.NoLongerShipped, null, edit));
                }
            }

            return rows;
        }

        public LibraryRow Edit(LibraryRow row, TermValues values)
        {
            EnsureBuiltIn(row);
            if (row.Origin is TermOrigin.Added or TermOrigin.NoLongerShipped || row.Shipped is null)
            {
                var intent = row.Edit?.Intent ?? BuiltInTermIntent.Added;
                return row with { Values = values, Edit = new BuiltInTermEdit(row.Key, intent, row.Edit?.Base, values) };
            }

            var pinned = values == row.Shipped;
            return new LibraryRow(
                row.Key, values, pinned ? TermOrigin.Pinned : TermOrigin.Edited, row.Shipped,
                new BuiltInTermEdit(row.Key, pinned ? BuiltInTermIntent.Pinned : BuiltInTermIntent.Edited, row.Shipped, values));
        }

        public LibraryRow SetEnabled(LibraryRow row, bool enabled)
        {
            EnsureBuiltIn(row);
            if (row.Values.Enabled == enabled)
            {
                return row;
            }

            return row.Origin switch
            {
                TermOrigin.Shipped => new LibraryRow(
                    row.Key, row.Values with { Enabled = false }, TermOrigin.Off, row.Shipped,
                    new BuiltInTermEdit(row.Key, BuiltInTermIntent.Off, row.Shipped, null)),
                TermOrigin.Off => new LibraryRow(row.Key, row.Shipped!, TermOrigin.Shipped, row.Shipped),
                _ => Edit(row, row.Values with { Enabled = enabled }),
            };
        }

        public LibraryRow? RestoreShipped(LibraryRow row)
        {
            EnsureBuiltIn(row);
            return row.Shipped is { } shipped ? new LibraryRow(row.Key, shipped, TermOrigin.Shipped, shipped) : null;
        }

        public LibraryRow Add(TermValues values)
        {
            var key = LibraryTermKey.From(values.Spoken);
            return new LibraryRow(key, values, TermOrigin.Added, null, new BuiltInTermEdit(key, BuiltInTermIntent.Added, null, values));
        }

        public LibraryRow ResolveReview(LibraryRow row, TermReviewChoice choice) => row;

        public BuiltInLibraryEdits? Collect(DictionaryLibrary shipped, BuiltInLibraryEdits? committed, IReadOnlyList<LibraryRow> rows)
        {
            var shippedKeys = shipped.Entries.Select(entry => LibraryTermKey.From(entry.Pattern)).ToHashSet();
            var rowKeys = rows.Select(row => row.Key).ToHashSet();
            var entries = rows.Where(row => row.Origin != TermOrigin.Shipped && row.Edit is not null).Select(row => row.Edit!).ToList();
            foreach (var entry in committed?.Terms ?? [])
            {
                if (!rowKeys.Contains(entry.Key)
                    && (entry.Intent == BuiltInTermIntent.Off || (entry.Intent != BuiltInTermIntent.Added && shippedKeys.Contains(entry.Key))))
                {
                    entries.Add(entry);
                }
            }

            return entries.Count == 0 ? null : new BuiltInLibraryEdits(shipped.Id, entries);
        }

        public IReadOnlyList<TermValues> AuthoredTerms(BuiltInLibraryEdits edits) =>
            edits.Terms.Where(term => term.Intent != BuiltInTermIntent.Off && term.Value is not null).Select(term => term.Value!).ToList();

        private static void EnsureBuiltIn(LibraryRow row)
        {
            if (row.Origin == TermOrigin.Custom)
            {
                throw new ArgumentException("A custom row is not the overlay's.", nameof(row));
            }
        }
    }
}
