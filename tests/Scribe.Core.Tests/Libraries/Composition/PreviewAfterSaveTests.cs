using Scribe.Core.Libraries;
using Scribe.Core.Models;
using static Scribe.Core.Tests.Libraries.Composition.Lib;

namespace Scribe.Core.Tests.Libraries.Composition;

/// <summary>
/// C-20 (W1b contracts 3.3.2, amended after both reviewers verified C's round 2, C's A6): a preview is exactly the committed
/// composition its Save leaves. A fake store applies each draft's capture to the committed catalog: it writes the content
/// of every library whose <see cref="DraftLibrary.WritesContent"/> is set and accepts the new hash (a reset built-in's
/// document is removed and its hash dropped), deletes the pending deletions, and keeps every other file as committed.
/// </summary>
public sealed class PreviewAfterSaveTests
{
    private static readonly GlossaryBudget Budget = new(Cleanup.CleanupPrompt.MaxGlossaryTermsCloud);
    private static readonly DictionaryEntry[] Dictionary = [DictionaryEntry.New("octo cat", "OctoCat Personal")];

    [Fact]
    public void Across_random_drafts_a_preview_equals_the_committed_composition_its_Save_leaves()
    {
        var random = new Random(20260925);
        var cases = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var round = 0; round < 400; round++)
        {
            var (committed, draft, saved, actions) = RandomSave(random);
            foreach (var action in actions)
            {
                cases[action] = cases.GetValueOrDefault(action) + 1;
            }

            var preview = LibraryComposition.Preview(draft, committed, Dictionary, Budget);
            var after = LibraryComposition.Committed(saved, Dictionary, Budget);

            var differences = Differences(preview, after, saved);
            Assert.True(differences.Count == 0, $"Round {round} ({string.Join(", ", actions)}):\n{string.Join("\n", differences)}");
        }

        // Every kind of draft change was met, the invisible reset of a built-in whose document is not accepted included, and
        // so was each cell of whether the Save writes a library's content against whether the draft shows other content
        // than the committed file: a change the Save does not write (round 4, Astra A7, Grok G2) is the cell no workspace
        // following D-19 builds, and the preview must still equal the Save's result there.
        Assert.All(
            new[]
            {
                "keep", "boxes", "rewrite", "unflagged change", "reset", "reset of an unaccepted document", "invisible reset",
                "pending delete", "brought in",
                "cell: writes, content differs", "cell: writes, content same",
                "cell: writes nothing, content differs", "cell: writes nothing, content same",
            },
            kind => Assert.True(cases.GetValueOrDefault(kind) >= 10, $"{kind}: {cases.GetValueOrDefault(kind)} cases"));
    }

    private static (LibraryCatalog Committed, LibraryDraft Draft, LibraryCatalog Saved, List<string> Actions) RandomSave(Random random)
    {
        var next = 1;
        LibraryContentHash NewHash() => new((next++).ToString("x64"));
        bool Chance(double p) => random.NextDouble() < p;

        var committedLibraries = new List<CatalogLibrary>();
        var accepted = new Dictionary<string, LibraryContentHash>(StringComparer.OrdinalIgnoreCase);
        var invisible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Built-ins: no document, an edits document with a visible edit, or one holding only intents no row shows.
        foreach (var (id, shipped, edited) in BuiltIns)
        {
            var kind = random.Next(3);
            var content = BuiltInLibrary(id, kind == 1 ? [edited, .. shipped.Skip(1)] : shipped);
            LibraryContentHash? hash = kind == 0 ? null : NewHash();
            if (kind == 2)
            {
                invisible.Add(id);
            }

            committedLibraries.Add(Committed(content, hash));
            if (hash is { } document)
            {
                AcceptOrNot(id, document);
            }
            else if (Chance(0.2))
            {
                accepted[id] = NewHash();   // a document that vanished outside Scribe
            }
        }

        foreach (var (id, rows) in Customs.Where(_ => Chance(0.85)))
        {
            var hash = NewHash();
            committedLibraries.Add(Committed(CustomLibrary(id, rows), hash));
            AcceptOrNot(id, hash);
        }

        void AcceptOrNot(string id, LibraryContentHash hash)
        {
            var roll = random.NextDouble();
            if (roll < 0.7)
            {
                accepted[id] = hash;
            }
            else if (roll < 0.85)
            {
                accepted[id] = NewHash();   // replaced outside Scribe, not adopted yet
            }
        }

        var ids = committedLibraries.Select(l => l.Content.Id).ToList();
        var enabled = ids.Where(_ => Chance(0.6)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ai = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            var roll = random.NextDouble();
            if (roll < 0.4)
            {
                ai[id] = true;
            }
            else if (roll < 0.6)
            {
                ai[id] = false;
            }
        }

        var lost = Chance(0.1);
        List<LegacyMarker> markers = Chance(0.5) ? [new LegacyMarker("team", Key("get hub"))] : [];
        var committed = new LibraryCatalog(
            4, committedLibraries, LibraryLocalState.Create(enabled, null, ai, markers, null, LocalStateHealth.Ok, accepted, lost),
            [], [], 0);

        // The draft: each library kept, its boxes changed, rewritten, reset (a built-in with a document) or deleted (a
        // custom library), and a few libraries brought in.
        var actions = new List<string>();
        var draftLibraries = new List<DraftLibrary>();
        var savedLibraries = new List<CatalogLibrary>();
        var draftEnabled = new HashSet<string>(enabled, StringComparer.OrdinalIgnoreCase);
        var draftAi = new Dictionary<string, bool>(ai, StringComparer.OrdinalIgnoreCase);
        var savedAccepted = new Dictionary<string, LibraryContentHash>(accepted, StringComparer.OrdinalIgnoreCase);
        var deleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in committedLibraries)
        {
            var id = library.Content.Id;
            var roll = random.NextDouble();
            if (roll < 0.25)
            {
                actions.Add("keep");
                draftLibraries.Add(new DraftLibrary(library.Content, LibraryOrigin.Existing, library.State, FileName: library.FileName));
                savedLibraries.Add(library);
            }
            else if (roll < 0.42)
            {
                actions.Add("boxes");
                if (Chance(0.5) && !draftEnabled.Remove(id))
                {
                    draftEnabled.Add(id);
                }

                draftAi[id] = !(draftAi.TryGetValue(id, out var chosen) && chosen);
                draftLibraries.Add(new DraftLibrary(library.Content, LibraryOrigin.Existing, library.State, false, true, library.FileName));
                savedLibraries.Add(library);
            }
            else if (roll < 0.6)
            {
                actions.Add("rewrite");
                var rewritten = library.Content.BuiltIn
                    ? library.Content with { Rows = [.. library.Content.Rows, Added("added " + id, "Added " + id)] }
                    : library.Content with { Rows = [.. library.Content.Rows, Custom("argo " + id, "Argo " + id)] };
                var hash = NewHash();
                draftLibraries.Add(new DraftLibrary(rewritten, LibraryOrigin.Existing, library.State, false, true, library.FileName, WritesContent: true));
                savedLibraries.Add(new CatalogLibrary(rewritten, library.State, library.FileName, hash));
                savedAccepted[id] = hash;
            }
            else if (roll < 0.72)
            {
                // The draft shows other content than the file, yet the Save writes nothing: the file stays as committed.
                actions.Add("unflagged change");
                var shown = library.Content.BuiltIn
                    ? library.Content with { Rows = [.. library.Content.Rows, Added("unflagged " + id, "Unflagged " + id)] }
                    : Chance(0.5)
                        ? library.Content with { Rows = [.. library.Content.Rows, Custom("unflagged " + id, "Unflagged " + id)] }
                        : library.Content with { Rows = [Custom("unflagged " + id, "Unflagged " + id)] };
                if (Chance(0.5))
                {
                    draftAi[id] = !(draftAi.TryGetValue(id, out var chosen) && chosen);
                }

                draftLibraries.Add(new DraftLibrary(shown, LibraryOrigin.Existing, library.State, false, true, library.FileName));
                savedLibraries.Add(library);
            }
            else if (library.Content.BuiltIn && library.ContentHash is { } document)
            {
                // Restore all built-in values: the document is removed, and the commit drops its entry.
                actions.Add(invisible.Contains(id) ? "invisible reset" : "reset");
                if (!accepted.TryGetValue(id, out var acceptedDocument) || acceptedDocument != document)
                {
                    actions.Add("reset of an unaccepted document");
                }

                var reset = BuiltInLibrary(id, BuiltIns.Single(b => b.Id == id).Shipped);
                draftLibraries.Add(new DraftLibrary(reset, LibraryOrigin.Existing, library.State, false, true, null, WritesContent: true));
                savedLibraries.Add(new CatalogLibrary(reset, library.State, null, null));
                savedAccepted.Remove(id);
            }
            else if (!library.Content.BuiltIn && Chance(0.6))
            {
                actions.Add("pending delete");
                draftLibraries.Add(new DraftLibrary(library.Content, LibraryOrigin.Existing, library.State, true, true, library.FileName));
                deleted.Add(id);
                savedAccepted.Remove(id);
            }
            else
            {
                actions.Add("keep");
                draftLibraries.Add(new DraftLibrary(library.Content, LibraryOrigin.Existing, library.State, FileName: library.FileName));
                savedLibraries.Add(library);
            }
        }

        LibraryOrigin[] origins = [LibraryOrigin.Created, LibraryOrigin.Imported, LibraryOrigin.Duplicated, LibraryOrigin.Restored];
        for (var n = random.Next(3); n > 0; n--)
        {
            actions.Add("brought in");
            var id = $"custom-new-{n}";
            var content = CustomLibrary(id, Custom("kube", "Kube " + n), Custom("new term " + n, "New Term " + n));
            var hash = NewHash();
            draftLibraries.Add(new DraftLibrary(content, origins[random.Next(origins.Length)], LibraryFileState.Available, false, true, id + ".csv", WritesContent: true));
            savedLibraries.Add(new CatalogLibrary(content, LibraryFileState.Available, id + ".csv", hash));
            savedAccepted[id] = hash;
            if (Chance(0.6))
            {
                draftEnabled.Add(id);
            }

            if (Chance(0.7))
            {
                draftAi[id] = Chance(0.7);
            }
        }

        var draftState = LibraryLocalState.Create(draftEnabled, null, draftAi, markers, null, LocalStateHealth.Ok, accepted, lost);
        var draft = new LibraryDraft(9, committed.Generation, draftLibraries, draftState, []);

        // Which of the four cells each library that survives the Save falls in: the Save writes its content or not, and the
        // draft shows the committed file's content or not (a library the catalog does not hold shows other content).
        foreach (var library in draftLibraries.Where(l => !l.PendingDelete))
        {
            var shownAsCommitted = committed.Find(library.Content.Id) is { } file && SameContent(file.Content, library.Content);
            actions.Add($"cell: {(library.WritesContent ? "writes" : "writes nothing")}, content {(shownAsCommitted ? "same" : "differs")}");
        }
        var savedState = LibraryLocalState.Create(
            draftEnabled.Where(id => !deleted.Contains(id)),
            null,
            draftAi.Where(pair => !deleted.Contains(pair.Key)),
            markers,
            null,
            LocalStateHealth.Ok,
            savedAccepted,
            lost);
        return (committed, draft, new LibraryCatalog(committed.Generation + 1, savedLibraries, savedState, [], [], 0), actions);
    }

    private static List<string> Differences(LibraryComposition preview, LibraryComposition after, LibraryCatalog saved)
    {
        var differences = new List<string>();
        void Compare<T>(string what, IEnumerable<T> previewed, IEnumerable<T> committed)
        {
            var left = previewed.ToList();
            var right = committed.ToList();
            if (!left.SequenceEqual(right))
            {
                differences.Add($"{what}: preview [{string.Join("; ", left)}], after the Save [{string.Join("; ", right)}]");
            }
        }

        Compare("rules", preview.Rules.Select(Rule), after.Rules.Select(Rule));
        Compare("AI entries", preview.AiLibraryEntries.Select(Entry), after.AiLibraryEntries.Select(Entry));
        Compare("AI excluded", preview.AiExcludedLibraryIds.Order(StringComparer.Ordinal), after.AiExcludedLibraryIds.Order(StringComparer.Ordinal));
        Compare("enabled", preview.EnabledLibraries.Select(l => l.Id), after.EnabledLibraries.Select(l => l.Id));
        Compare("any marker active", new[] { preview.AnyLegacyMarkerActive }, new[] { after.AnyLegacyMarkerActive });
        foreach (var library in saved.Libraries)
        {
            foreach (var row in library.Content.Rows)
            {
                Compare(
                    $"status of {library.Content.Id} {row.Key.Value}",
                    new[] { Status(preview.StatusOf(library.Content.Id, row.Key)) },
                    new[] { Status(after.StatusOf(library.Content.Id, row.Key)) });
            }
        }

        return differences;
    }

    private static string Rule(ComposedRule rule) =>
        $"{rule.Key.Value}|{rule.LibraryId}|{rule.Tier}|{rule.LegacyMarkerActive}|{Entry(rule.Entry)}";

    // The oracle's own reading of "the draft shows the file's content", used only to count the cells.
    private static bool SameContent(LibraryContent committed, LibraryContent shown) =>
        committed.Name == shown.Name && committed.Category == shown.Category && committed.Description == shown.Description &&
        committed.BasedOn == shown.BasedOn && committed.Rows.SequenceEqual(shown.Rows);

    private static string Entry(DictionaryEntry entry) => $"{entry.Pattern}={entry.Replacement}{(entry.WholeWord ? string.Empty : " (in words)")}";

    private static string Status(TermStatus status) =>
        $"{status.Marker}|{status.Winner}|{status.WinningLibraryId}|{(status.WinningEntry is { } winning ? Entry(winning) : "-")}|" +
        $"same:{string.Join(",", status.SameResultIn)}|different:{string.Join(",", status.DifferentResultIn)}|" +
        $"{status.LegacyMarkerActive}|{status.Glossary}|{status.Review is not null}";

    private static readonly (string Id, LibraryRow[] Shipped, LibraryRow Edited)[] BuiltIns =
    [
        ("github", [Shipped("get hub", "GitHub"), Shipped("octo cat", "Octocat"), Shipped("kube", "Kubernetes")],
            Edited("get hub", "GitHub", "get hub", "Nightjar Hub")),
        ("microsoft-365", [Shipped("teams", "Teams"), Shipped("outlook", "Outlook")],
            Edited("teams", "Teams", "teams", "Teams Live")),
    ];

    private static readonly (string Id, LibraryRow[] Rows)[] Customs =
    [
        ("team", [Custom("kube", "K8s"), Custom("get hub", "TeamHub")]),
        ("notes", [Custom("north star", "North Star"), Custom("teams", "Teams")]),
        ("alpha", [Custom("argo", "Argo"), Custom("outlook", "Outlook Web", wholeWord: false)]),
    ];
}
