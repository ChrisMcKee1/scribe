using Scribe.Core.Libraries;
using static Scribe.Core.Tests.Libraries.Composition.Lib;

namespace Scribe.Core.Tests.Libraries.Composition;

/// <summary>
/// The merge gate for folding the shipped values only when a custom library needs its upgrade markers (combined.md row 3,
/// E.3; sign-off SIG-LANG-01): the planner as it was, which folded them at the start of every plan, copied below, decides
/// every plan (the complete state, the reasons, the counts and the order of the markers) over seeded catalogs and every
/// health, context and reason; and the fold runs only when a plan needs it.
/// </summary>
public sealed class LibraryAdoptionLazyShippedTests
{
    private static readonly LibraryStateContext[] Contexts =
    [
        new(false, false, false),
        new(false, true, false),
        new(false, false, true),
        new(false, false, false, CommitWitnessed: true),
        new(false, true, true, CommitWitnessed: true),
        new(true, false, false),
        new(true, true, true, CommitWitnessed: true),
    ];

    private static readonly LocalStateHealth[] Healths = Enum.GetValues<LocalStateHealth>();

    // Spoken forms the built-ins ship and the custom libraries reuse, in spellings the marker fold takes for the same form.
    private static readonly string[] Spoken = ["get hub", "GET HUB", " get hub ", "kube", "KUBE", "\u212Aube", "helm", "north star", "teams", "istanbul", "\u0130stanbul"];
    private static readonly string[] Written = ["GitHub", "GitHub Enterprise", "Kubernetes", "K8s", "Helm", "North Star", "Teams", "Istanbul", ""];

    [Fact]
    public void Lazy_and_eager_plans_match_for_seeded_catalogs_and_all_reasons()
    {
        var random = new Random(20_260_927);
        var reasons = LibraryAdoptionReasons.None;
        var plans = 0;
        var folds = 0;
        var markerPlans = 0;
        for (var round = 0; round < 400; round++)
        {
            var catalog = RandomCatalog(random);
            foreach (var context in Contexts)
            {
                var calls = 0;
                var expected = EagerPlanner.Plan(catalog, context, LibraryDecisions.DefaultAiPermission);
                var actual = LibraryAdoptionPlanner.Plan(catalog, context, LibraryDecisions.DefaultAiPermission, builtIns =>
                {
                    calls++;
                    return LibraryTiers.ShippedValues(builtIns);
                });

                AssertSamePlan(expected, actual, $"round {round}, context {context}, health {catalog.LocalState.Health}");
                Assert.InRange(calls, 0, 1);
                if (expected is not null)
                {
                    plans++;
                    reasons |= expected.Reasons;
                    if (expected.MarkersAdded > 0)
                    {
                        markerPlans++;
                        Assert.Equal(1, calls);   // a plan that adds a marker folded the values
                    }
                }

                folds += calls;
            }
        }

        // The corpus reaches every reason, plans that add markers, and plans that never needed the fold.
        Assert.Equal(
            LibraryAdoptionReasons.FirstStart | LibraryAdoptionReasons.Discovered | LibraryAdoptionReasons.ContentReplaced | LibraryAdoptionReasons.StateLost,
            reasons);
        Assert.True(markerPlans > 100, $"{markerPlans} plans added markers");
        Assert.True(folds < 400 * Contexts.Length, $"{folds} folds for {400 * Contexts.Length} plans");
        Assert.True(plans > 1_000, $"{plans} plans");
    }

    [Fact]
    public void A_start_that_records_no_custom_librarys_markers_never_folds_the_shipped_values()
    {
        var gitHub = Committed(BuiltInLibrary("github", Shipped("get hub", "GitHub"), Shipped("kube", "Kubernetes")));
        var edited = Committed(BuiltInLibrary("microsoft-365", Shipped("teams", "Teams")), H3);
        var team = Committed(CustomLibrary("team", Custom("kube", "K8s")), H1);
        var healthy = State(enabled: ["github", "team"], ai: [("team", true)], accepted: [("team", H1), ("microsoft-365", H3)]);
        var later = new LibraryStateContext(false, false, GenerationStored: true, CommitWitnessed: true);

        // Built-ins only, at a first start, after a loss and with a healthy state; a healthy state with a custom library
        // it already recorded; and a built-in whose edits document was replaced.
        (LibraryCatalog Catalog, LibraryStateContext Context)[] cases =
        [
            (Catalog(0, LibraryLocalState.Absent, gitHub, edited), new(false, false, false)),
            (Catalog(State(health: LocalStateHealth.Unreadable), gitHub, edited), later),
            (Catalog(healthy, gitHub, edited, team), later),
            (Catalog(healthy, gitHub, Committed(BuiltInLibrary("microsoft-365", Shipped("teams", "Teams")), H4), team), later),
        ];
        foreach (var (catalog, context) in cases)
        {
            var calls = 0;
            var plan = LibraryAdoptionPlanner.Plan(catalog, context, LibraryDecisions.DefaultAiPermission, builtIns =>
            {
                calls++;
                return LibraryTiers.ShippedValues(builtIns);
            });

            AssertSamePlan(EagerPlanner.Plan(catalog, context, LibraryDecisions.DefaultAiPermission), plan, context.ToString());
            Assert.Equal(0, calls);
        }

        // A custom library that needs its markers folds them once, however many libraries need them.
        var placed = Committed(CustomLibrary("placed", Custom("get hub", "Get Hub Placed")), H2);
        var other = Committed(CustomLibrary("other", Custom("kube", "Kube Other")), H4);
        var folds = 0;
        var discovered = LibraryAdoptionPlanner.Plan(Catalog(healthy, gitHub, team, placed, other), later, LibraryDecisions.DefaultAiPermission, builtIns =>
        {
            folds++;
            return LibraryTiers.ShippedValues(builtIns);
        });
        Assert.Equal(LibraryAdoptionReasons.Discovered, discovered!.Reasons);
        Assert.Equal(1, folds);
    }

    private static LibraryCatalog RandomCatalog(Random random)
    {
        var libraries = new List<CatalogLibrary>();
        var builtInIds = new[] { "github", "microsoft-365", "kubernetes" };
        foreach (var id in builtInIds.Where(_ => random.Next(4) != 0))
        {
            var rows = Enumerable.Range(0, random.Next(1, 4))
                .Select(i => Shipped(Spoken[random.Next(Spoken.Length)] + (i == 0 ? string.Empty : " " + i), Written[random.Next(Written.Length)]))
                .DistinctBy(row => row.Key)
                .ToArray();
            var hash = random.Next(3) == 0 ? (LibraryContentHash?)Hash("1234"[random.Next(4)]) : null;
            var fileState = random.Next(6) == 0 ? LibraryFileState.Unreadable : LibraryFileState.Available;
            libraries.Add(Committed(BuiltInLibrary(id, rows), hash, state: fileState));
        }

        foreach (var id in new[] { "team", "notes", "placed", "Zulu" }.Where(_ => random.Next(3) != 0))
        {
            var rows = Enumerable.Range(0, random.Next(0, 5))
                .Select(i => Custom(Spoken[random.Next(Spoken.Length)] + (random.Next(3) == 0 ? " " + i : string.Empty), Written[random.Next(Written.Length)], enabled: random.Next(5) != 0))
                .DistinctBy(row => row.Key)
                .ToArray();
            var hash = random.Next(5) == 0 ? (LibraryContentHash?)null : Hash("1234"[random.Next(4)]);
            libraries.Add(Committed(CustomLibrary(id, rows), hash, state: hash is null ? LibraryFileState.AwaitingRelease : LibraryFileState.Available));
        }

        var ids = libraries.Select(library => library.Content.Id).ToList();
        var health = Healths[random.Next(Healths.Length)];
        var state = health == LocalStateHealth.Absent
            ? LibraryLocalState.Absent
            : State(
                enabled: ids.Where(_ => random.Next(2) == 0),
                ai: ids.Where(_ => random.Next(2) == 0).Select(id => (id, random.Next(2) == 0)),
                markers: ids.Where(_ => random.Next(4) == 0).Select(id => (id, Spoken[random.Next(Spoken.Length)])),
                accepted: ids.Where(_ => random.Next(3) != 0).Select(id => (id, Hash("1234"[random.Next(4)]))),
                lost: random.Next(4) == 0,
                health: health,
                legacy: ids.Where(_ => random.Next(3) == 0),
                notice: ids.Where(_ => random.Next(4) == 0));
        return Catalog(random.Next(3), state, [.. libraries.OrderBy(_ => random.Next())]);
    }

    private static void AssertSamePlan(LibraryAdoption? expected, LibraryAdoption? actual, string context)
    {
        if (expected is null)
        {
            Assert.True(actual is null, $"{context}: planned {actual?.Reasons}, the eager planner planned nothing.");
            return;
        }

        Assert.True(actual is not null, $"{context}: planned nothing, the eager planner planned {expected.Reasons}.");
        Assert.Equal(expected.Reasons, actual!.Reasons);
        Assert.Equal(expected.LibrariesAdopted, actual.LibrariesAdopted);
        Assert.Equal(expected.MarkersAdded, actual.MarkersAdded);
        var (e, a) = (expected.State, actual.State);
        Assert.Equal(e.Health, a.Health);
        Assert.Equal(e.AiPermissionsLost, a.AiPermissionsLost);
        Assert.Equal(e.EnabledIds.Order(StringComparer.Ordinal), a.EnabledIds.Order(StringComparer.Ordinal));
        Assert.Equal(e.LegacyEnabledIds.Order(StringComparer.Ordinal), a.LegacyEnabledIds.Order(StringComparer.Ordinal));
        Assert.Equal(e.AiUpgradeNotice.Order(StringComparer.Ordinal), a.AiUpgradeNotice.Order(StringComparer.Ordinal));
        Assert.Equal(e.AiPermissions.OrderBy(p => p.Key, StringComparer.Ordinal), a.AiPermissions.OrderBy(p => p.Key, StringComparer.Ordinal));
        Assert.Equal(e.AcceptedContent.OrderBy(p => p.Key, StringComparer.Ordinal), a.AcceptedContent.OrderBy(p => p.Key, StringComparer.Ordinal));

        // The markers in order, and by the exact spelling of each id and key, not only as the markers' equality sees them.
        Assert.Equal(
            e.LegacyMarkers.Select(marker => (marker.LibraryId, marker.Key.Value)),
            a.LegacyMarkers.Select(marker => (marker.LibraryId, marker.Key.Value)));
    }

    // LibraryAdoptionPlanner as it was before the shipped values were folded lazily, verbatim but for its name.
    private static class EagerPlanner
    {
        public static LibraryAdoption? Plan(
            LibraryCatalog catalog, LibraryStateContext context, Func<LibraryOrigin, bool, bool?, bool> defaultAiPermission)
        {
            ArgumentNullException.ThrowIfNull(catalog);
            ArgumentNullException.ThrowIfNull(defaultAiPermission);
            var state = catalog.LocalState;
            if (context.RunningOnDefaults || state.Health == LocalStateHealth.Newer)
            {
                return null;
            }

            var shipped = LibraryTiers.ShippedValues(catalog.Libraries.Where(l => l.Content.BuiltIn).Select(l => l.Content));

            var lostAtThisStart = context.DatabaseRepaired || context.GenerationStored || context.CommitWitnessed;
            return state.Health switch
            {
                LocalStateHealth.Unreadable => StateLost(catalog, state, shipped),
                LocalStateHealth.Absent when lostAtThisStart => StateLost(catalog, state, shipped),
                LocalStateHealth.Absent => FirstStart(catalog, state, shipped, defaultAiPermission),
                _ => Update(catalog, state, shipped, defaultAiPermission),
            };
        }

        private static LibraryAdoption StateLost(
            LibraryCatalog catalog, LibraryLocalState state, IReadOnlyDictionary<string, List<TermValues>> shipped)
        {
            var markers = catalog.Libraries
                .Where(library => !library.Content.BuiltIn)
                .SelectMany(library => LibraryTiers.UpgradeMarkers(library.Content, shipped))
                .ToList();
            var accepted = catalog.Libraries
                .Where(library => library.ContentHash is not null)
                .Select(library => new KeyValuePair<string, LibraryContentHash>(library.Content.Id, library.ContentHash!.Value))
                .ToList();
            var adopted = LibraryLocalState.Create(
                state.EnabledIds,
                state.LegacyEnabledIds,
                aiPermissions: null,
                markers,
                aiUpgradeNotice: null,
                LocalStateHealth.Ok,
                accepted,
                aiPermissionsLost: true);
            return new LibraryAdoption(adopted, LibraryAdoptionReasons.StateLost, catalog.Libraries.Count, adopted.LegacyMarkers.Count);
        }

        private static LibraryAdoption FirstStart(
            LibraryCatalog catalog,
            LibraryLocalState state,
            IReadOnlyDictionary<string, List<TermValues>> shipped,
            Func<LibraryOrigin, bool, bool?, bool> defaultAiPermission)
        {
            var permissions = new List<KeyValuePair<string, bool>>();
            var markers = new List<LegacyMarker>();
            var accepted = new List<KeyValuePair<string, LibraryContentHash>>();
            var notice = new List<string>();
            var recorded = 0;
            foreach (var library in catalog.Libraries)
            {
                if (library.ContentHash is not { } hash)
                {
                    continue;
                }

                var id = library.Content.Id;
                accepted.Add(new(id, hash));
                recorded++;
                if (!library.Content.BuiltIn)
                {
                    permissions.Add(new(id, defaultAiPermission(LibraryOrigin.Existing, false, null)));
                    markers.AddRange(LibraryTiers.UpgradeMarkers(library.Content, shipped));
                    notice.Add(id);
                }
            }

            var adopted = LibraryLocalState.Create(
                state.EnabledIds,
                state.LegacyEnabledIds,
                permissions,
                markers,
                notice,
                LocalStateHealth.Ok,
                accepted,
                aiPermissionsLost: false);
            return new LibraryAdoption(adopted, LibraryAdoptionReasons.FirstStart, recorded, adopted.LegacyMarkers.Count);
        }

        private static LibraryAdoption? Update(
            LibraryCatalog catalog,
            LibraryLocalState state,
            IReadOnlyDictionary<string, List<TermValues>> shipped,
            Func<LibraryOrigin, bool, bool?, bool> defaultAiPermission)
        {
            var permissions = new Dictionary<string, bool>(state.AiPermissions, StringComparer.OrdinalIgnoreCase);
            var enabled = new HashSet<string>(state.EnabledIds, StringComparer.OrdinalIgnoreCase);
            var markers = state.LegacyMarkers.ToList();
            var accepted = new Dictionary<string, LibraryContentHash>(state.AcceptedContent, StringComparer.OrdinalIgnoreCase);
            var notice = new HashSet<string>(state.AiUpgradeNotice, StringComparer.OrdinalIgnoreCase);
            var reasons = LibraryAdoptionReasons.None;
            var recorded = 0;

            foreach (var library in catalog.Libraries)
            {
                var id = library.Content.Id;
                if (library.ContentHash is not { } hash)
                {
                    if (library.Content.BuiltIn && library.State == LibraryFileState.Available && accepted.Remove(id))
                    {
                        recorded++;
                        reasons |= LibraryAdoptionReasons.ContentReplaced;
                        permissions[id] = false;
                    }

                    continue;
                }

                var known = accepted.TryGetValue(id, out var previous);
                if (known && previous == hash)
                {
                    continue;
                }

                recorded++;
                accepted[id] = hash;
                if (library.Content.BuiltIn)
                {
                    reasons |= LibraryAdoptionReasons.ContentReplaced;
                    permissions[id] = false;
                    continue;
                }

                markers.RemoveAll(marker => string.Equals(marker.LibraryId, id, StringComparison.OrdinalIgnoreCase));
                markers.AddRange(LibraryTiers.UpgradeMarkers(library.Content, shipped));
                if (known)
                {
                    reasons |= LibraryAdoptionReasons.ContentReplaced;
                    permissions[id] = false;
                    enabled.Remove(id);
                    notice.Remove(id);
                }
                else
                {
                    reasons |= LibraryAdoptionReasons.Discovered;
                    permissions[id] = defaultAiPermission(LibraryOrigin.Discovered, false, null);
                }
            }

            if (reasons == LibraryAdoptionReasons.None)
            {
                return null;
            }

            var before = state.LegacyMarkers.ToHashSet();
            var adopted = LibraryLocalState.Create(
                enabled,
                state.LegacyEnabledIds,
                permissions,
                markers,
                notice,
                LocalStateHealth.Ok,
                accepted,
                state.AiPermissionsLost);
            return new LibraryAdoption(adopted, reasons, recorded, adopted.LegacyMarkers.Count(marker => !before.Contains(marker)));
        }
    }
}
