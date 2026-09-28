using System.Reflection;
using System.Reflection.Emit;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests.Libraries.Composition;

/// <summary>
/// What keeping previews per draft stands on. A preview is a function of its draft, the committed catalog, the budget
/// and the enabled dictionary entries, so it can be handed out again only while nothing a draft or a catalog reaches can
/// change once built, and only while everything a composition computes on first use is safe to compute from several
/// threads at once. These tests pin both from the types themselves, so a later change that breaks either fails here
/// rather than in a stale preview.
/// </summary>
public sealed class LibraryPreviewMemoPreconditionTests
{
    private const BindingFlags DeclaredInstance =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    private const BindingFlags Declared = DeclaredInstance | BindingFlags.Static;

    // The collection shapes that expose no way to change what they hold.
    private static readonly Type[] ReadOnlyShapes =
    [
        typeof(IReadOnlyList<>), typeof(IReadOnlySet<>), typeof(IReadOnlyDictionary<,>),
        typeof(System.Collections.Frozen.FrozenSet<>), typeof(System.Collections.Frozen.FrozenDictionary<,>),
    ];

    [Fact]
    public void Nothing_a_draft_or_a_catalog_reaches_has_a_field_that_can_be_assigned_after_construction()
    {
        var types = ModelTypes();

        // The walk reaches every model type a preview reads, so the checks below cover them.
        Assert.Superset(
            new HashSet<Type>
            {
                typeof(LibraryDraft), typeof(DraftLibrary), typeof(LibraryCatalog), typeof(CatalogLibrary), typeof(LibraryContent),
                typeof(LibraryRow), typeof(TermValues), typeof(TermReview), typeof(BuiltInTermEdit), typeof(BuiltInLibraryEdits),
                typeof(LibraryLocalState), typeof(LegacyMarker), typeof(LibraryTermKey), typeof(LibraryContentHash),
                typeof(RecentlyDeletedLibrary), typeof(RetiredBuiltInEdits), typeof(LibraryKeptVersion),
            },
            types.ToHashSet());

        foreach (var type in types)
        {
            Assert.True(type.IsValueType || type.BaseType == typeof(object), $"{type.Name} inherits fields from {type.BaseType}.");
            Assert.True(type.IsValueType || type.IsSealed, $"{type.Name} is not sealed, so a subclass could add state.");
            foreach (var field in type.GetFields(DeclaredInstance))
            {
                Assert.True(field.IsInitOnly, $"{type.Name}.{field.Name} can be assigned after construction.");
            }
        }
    }

    [Fact]
    public void Every_collection_they_hold_is_read_only_or_a_private_lookup_only_their_constructor_fills()
    {
        var types = ModelTypes().ToHashSet();
        var lookups = new List<FieldInfo>();
        foreach (var type in types)
        {
            foreach (var field in type.GetFields(DeclaredInstance))
            {
                var held = Nullable.GetUnderlyingType(field.FieldType) ?? field.FieldType;
                if (held.IsPrimitive || held.IsEnum || held == typeof(string) || held == typeof(DateTimeOffset) || types.Contains(held))
                {
                    continue;
                }

                if (held.IsGenericType && ReadOnlyShapes.Contains(held.GetGenericTypeDefinition()))
                {
                    continue;
                }

                Assert.True(
                    field.IsPrivate && held.IsGenericType && held.GetGenericTypeDefinition() == typeof(Dictionary<,>),
                    $"{type.Name}.{field.Name} holds a {held.Name}, which could change after construction.");
                lookups.Add(field);
            }
        }

        Assert.Equal(
            ["LibraryCatalog._byId", "LibraryDraft._byId"],
            lookups.Select(field => $"{field.DeclaringType!.Name}.{field.Name}").Order(StringComparer.Ordinal));

        // A private field is reachable only from its own type's code (and its nested types'), so these are every method
        // that can touch a lookup: the constructor fills it, and Find only reads it.
        foreach (var lookup in lookups)
        {
            var owner = lookup.DeclaringType!;
            var touching = MethodsOf(owner)
                .Where(method => Fields(method).Contains(lookup))
                .Select(method => method.Name)
                .Order(StringComparer.Ordinal);
            Assert.Equal([".ctor", nameof(LibraryDraft.Find)], touching);

            var find = owner.GetMethod(nameof(LibraryDraft.Find), Declared)!;
            Assert.All(
                MouseButtonRound7Tests.Callees(find).Where(callee => callee.DeclaringType?.IsGenericType == true &&
                    callee.DeclaringType.GetGenericTypeDefinition() == typeof(Dictionary<,>)),
                callee => Assert.Equal(nameof(Dictionary<string, string>.TryGetValue), callee.Name));
        }
    }

    [Fact]
    public void A_draft_and_a_catalog_keep_their_own_read_only_copies_of_the_lists_they_are_given()
    {
        var content = Lib.CustomLibrary("team", Lib.Custom("git hub", "GitHub"));
        var drafted = new List<DraftLibrary> { Lib.Draft(content) };
        var deleted = new List<RecentlyDeletedLibrary>
        {
            new("old.csv", "old", "Old", 1, DateTimeOffset.UnixEpoch, LibraryFileState.Available),
        };
        var committed = new List<CatalogLibrary> { Lib.Committed(content, Lib.H1) };
        var retired = new List<RetiredBuiltInEdits> { new("retired", [new TermValues("a", "A")], Lib.H2) };
        var kept = new List<LibraryKeptVersion> { new("team", LibraryKeptVersionKind.OutsideVersion, "team-2") };
        var state = Lib.State(enabled: ["team"], ai: [("team", true)], markers: [("team", "git hub")], accepted: [("team", Lib.H1)]);
        var draft = new LibraryDraft(1, 5, drafted, state, deleted);
        var catalog = new LibraryCatalog(5, committed, state, deleted, retired, 0, kept);

        drafted.Clear();
        deleted.Clear();
        committed.Clear();
        retired.Clear();
        kept.Clear();

        Assert.Single(draft.Libraries);
        Assert.Single(draft.RecentlyDeleted);
        Assert.Single(catalog.Libraries);
        Assert.Single(catalog.RecentlyDeleted);
        Assert.Single(catalog.RetiredBuiltInEdits);
        Assert.Single(catalog.KeptVersions);
        Assert.Same(draft.Libraries[0], draft.Find("TEAM"));
        Assert.Same(catalog.Libraries[0], catalog.Find("TEAM"));

        ReadOnly(draft.Libraries);
        ReadOnly(draft.RecentlyDeleted);
        ReadOnly(catalog.Libraries);
        ReadOnly(catalog.RecentlyDeleted);
        ReadOnly(catalog.RetiredBuiltInEdits);
        ReadOnly(catalog.KeptVersions);
        ReadOnly(state.EnabledIds);
        ReadOnly(state.LegacyEnabledIds);
        ReadOnly(state.AiPermissions);
        ReadOnly(state.LegacyMarkers);
        ReadOnly(state.AiUpgradeNotice);
        ReadOnly(state.AcceptedContent);
    }

    // The workspace is the only producer of drafts: it hands out one per revision, and a draft it handed out never changes
    // while the workspace moves on, because every edit builds new state and a new draft from it.
    [Fact]
    public void The_workspace_hands_out_one_draft_per_revision_and_never_changes_one_it_handed_out()
    {
        var workspace = new LibraryWorkspace(TeamCatalog(), BuiltInLibraryOverlay.Instance, LibraryDecisions.DefaultAiPermission);
        var first = workspace.Draft;
        Assert.Same(first, workspace.Draft);
        var before = Describe(first);

        var drafts = new List<LibraryDraft> { first };
        void Step(Action edit)
        {
            edit();
            var next = workspace.Draft;
            Assert.DoesNotContain(next, drafts);
            Assert.Same(next, workspace.Draft);
            drafts.Add(next);
        }

        var rowId = workspace.RowsOf("team")[0].RowId;
        Step(() => Assert.True(workspace.AddTerm("team", new TermValues("jay son", "JSON")).Applied));
        Step(() => Assert.True(workspace.EditTerm("team", rowId, new TermValues("git hub", "Github")).Applied));
        Step(() => workspace.SetTermEnabled("team", rowId, false));
        Step(() => workspace.SetEnabled("team", false));
        Step(() => Assert.True(workspace.SetAiPermission("team", false).Applied));
        Step(() => workspace.DeleteTerm("team", rowId));
        Step(workspace.Undo);
        Step(workspace.Redo);

        Assert.Equal(before, Describe(first));
        Assert.NotEqual(before, Describe(drafts[^1]));
    }

    [Fact]
    public void Every_field_of_a_composition_is_readonly_but_the_row_map_it_publishes_by_compare_exchange()
    {
        var source = typeof(LibraryComposition).GetNestedType("Source", BindingFlags.NonPublic)!;
        foreach (var type in new[] { typeof(LibraryComposition), source })
        {
            foreach (var field in type.GetFields(DeclaredInstance).Where(field => field.Name != "_rowsByIdentity"))
            {
                Assert.True(field.IsInitOnly, $"{type.Name}.{field.Name} can be assigned after construction.");
            }
        }

        // The one lazily published field is never stored directly: RowOf builds the map and publishes it with
        // Interlocked.CompareExchange, a full fence, and reads it with Volatile.Read, so a reader sees a finished map or
        // builds its own.
        var rowsByIdentity = source.GetField("_rowsByIdentity", DeclaredInstance)!;
        var stores = CompositionMethods()
            .Where(method => Instructions(method).Any(i => i.Code == OpCodes.Stfld && Resolve(method, i.Token) == rowsByIdentity))
            .ToList();
        Assert.Empty(stores);
        var rowOf = source.GetMethod("RowOf", DeclaredInstance)!;
        var callees = MouseButtonRound7Tests.Callees(rowOf).ToList();
        Assert.Contains(callees, callee => callee.DeclaringType == typeof(Interlocked) && callee.Name == nameof(Interlocked.CompareExchange));
        Assert.Contains(callees, callee => callee.DeclaringType == typeof(Volatile) && callee.Name == nameof(Volatile.Read));
        Assert.Equal(
            ["RowOf"],
            CompositionMethods().Where(method => Fields(method).Contains(rowsByIdentity)).Select(method => method.Name).Distinct());
    }

    // Lazy<T>(Func<T>) is LazyThreadSafetyMode.ExecutionAndPublication: one thread runs the factory, and every thread sees
    // its one result. Every Lazy the composition makes is made that way, in its constructor.
    [Fact]
    public void Every_lazy_of_a_composition_computes_once_and_publishes_to_every_thread()
    {
        var lazyFields = typeof(LibraryComposition).GetFields(DeclaredInstance)
            .Where(field => field.FieldType.IsGenericType && field.FieldType.GetGenericTypeDefinition() == typeof(Lazy<>))
            .ToList();
        Assert.Equal(3, lazyFields.Count);

        var made = CompositionMethods()
            .SelectMany(method => Instructions(method)
                .Where(i => i.Code == OpCodes.Newobj)
                .Select(i => (Method: method, Constructor: (ConstructorInfo)ResolveMethod(method, i.Token))))
            .Where(made => made.Constructor.DeclaringType!.IsGenericType &&
                           made.Constructor.DeclaringType.GetGenericTypeDefinition() == typeof(Lazy<>))
            .ToList();
        Assert.Equal(lazyFields.Count, made.Count);
        Assert.All(made, lazy =>
        {
            Assert.True(lazy.Method.IsConstructor, $"A Lazy is made in {lazy.Method.Name}, not while the composition is built.");
            var parameter = Assert.Single(lazy.Constructor.GetParameters());
            Assert.Equal(typeof(Func<>), parameter.ParameterType.GetGenericTypeDefinition());
        });
    }

    // The legacy markers are the only array a composition writes after the arrays are made: MarkActiveLegacyRows is the only
    // method that stores a bool into an array, and only the constructor calls it, before the composition is returned.
    [Fact]
    public void The_legacy_markers_are_written_only_while_the_composition_is_built()
    {
        var boolStores = CompositionMethods()
            .Where(method => Instructions(method).Any(i => i.Code == OpCodes.Stelem_I1))
            .Select(method => method.Name)
            .Distinct();
        Assert.Equal(["MarkActiveLegacyRows"], boolStores);

        var mark = typeof(LibraryComposition).GetMethod("MarkActiveLegacyRows", DeclaredInstance)!;
        var callers = CompositionMethods().Where(method => MouseButtonRound7Tests.Callees(method).Contains(mark)).ToList();
        var caller = Assert.Single(callers);
        Assert.True(caller.IsConstructor);
    }

    // Many threads reading one composition for the first time at once, which computes every lazy, get what one thread
    // reading another composition of the same inputs gets, and the same published objects.
    [Fact]
    public void Threads_reading_one_composition_for_the_first_time_at_once_see_what_one_thread_sees()
    {
        var (catalog, draft, dictionary, budget) = OverlappingInputs();
        var expected = Read(LibraryComposition.Preview(draft, catalog, dictionary, budget, LibraryDecisions.Precedence));
        const int Threads = 8;
        for (var round = 0; round < 20; round++)
        {
            var composition = LibraryComposition.Preview(draft, catalog, dictionary, budget, LibraryDecisions.Precedence);
            using var start = new Barrier(Threads);
            var results = new (string Digest, IReadOnlyList<DictionaryLibrary> Enabled)[Threads];
            var failures = new Exception?[Threads];
            var threads = Enumerable.Range(0, Threads).Select(index => new Thread(() =>
            {
                try
                {
                    start.SignalAndWait();
                    results[index] = (Read(composition), composition.EnabledLibraries);
                }
                catch (Exception ex)
                {
                    failures[index] = ex;
                }
            })).ToList();
            threads.ForEach(thread => thread.Start());
            threads.ForEach(thread => thread.Join());

            Assert.All(failures, Assert.Null);
            Assert.All(results, result => Assert.Equal(expected, result.Digest));
            Assert.All(results, result => Assert.Same(results[0].Enabled, result.Enabled));
        }
    }

    internal static (LibraryCatalog Catalog, LibraryDraft Draft, IReadOnlyList<DictionaryEntry> Dictionary, GlossaryBudget Budget)
        OverlappingInputs()
    {
        var shipped = Lib.BuiltInLibrary(
            "general", Lib.Shipped("git hub", "GitHub"), Lib.Shipped("jay son", "JSON"), Lib.Shipped("see sharp", "C#"),
            Lib.Shipped("dot net", ".NET"));
        var team = Lib.CustomLibrary(
            "team", Lib.Custom("git hub", "Github"), Lib.Custom("jay son", "JSON"), Lib.Custom("kube", "Kubernetes"),
            Lib.Custom("dot net", "dotnet", enabled: false), Lib.Custom("kube", "K8s"));
        var extra = Lib.CustomLibrary("extra", Lib.Custom("kube", "Kube"), Lib.Custom("see sharp", "CSharp"), Lib.Custom("helm", "Helm"));
        var state = Lib.State(
            enabled: ["general", "team", "extra"],
            ai: [("team", true), ("extra", false)],
            markers: [("team", "git hub")],
            accepted: [("team", Lib.H1), ("extra", Lib.H2)]);
        var catalog = Lib.Catalog(
            state, Lib.Committed(shipped), Lib.Committed(team, Lib.H1), Lib.Committed(extra, Lib.H2));
        var draft = Lib.Draft(7, state, Lib.Draft(shipped), Lib.Draft(team), Lib.Draft(extra));
        DictionaryEntry[] dictionary = [new(1, "helm", "HELM"), new(2, "teams", "Teams"), new(3, "jay son", "Json", Enabled: false)];
        return (catalog, draft, dictionary, new GlossaryBudget(Cleanup.CleanupPrompt.MaxGlossaryTermsCloud));
    }

    // Everything a page reads from a composition, as text: every row's status, the badges, the Save prompt, the filters and
    // the libraries in use.
    internal static string Read(LibraryComposition composition)
    {
        var lines = new List<string>();
        foreach (var library in composition.EnabledLibraries)
        {
            lines.Add($"library {library.Id} {library.FileName}: {string.Join(",", library.Entries.Select(Entry))}");
        }

        foreach (var id in new[] { "general", "team", "extra", "missing" })
        {
            foreach (var spoken in new[] { "git hub", "jay son", "see sharp", "dot net", "kube", "helm", "teams" })
            {
                var status = composition.StatusOf(id, Lib.Key(spoken));
                lines.Add(
                    $"{id}/{spoken}: {status.Marker} {status.Winner} {status.WinningLibraryId} {Entry(status.WinningEntry)} " +
                    $"same=[{string.Join(",", status.SameResultIn)}] different=[{string.Join(",", status.DifferentResultIn)}] " +
                    $"{status.Review is not null} {status.LegacyMarkerActive} {status.Glossary}");
            }

            foreach (var filter in Enum.GetValues<TermFilter>())
            {
                lines.Add($"{id} {filter}: {string.Join(",", composition.Filter(id, filter).Select(key => key.Value))}");
            }
        }

        foreach (var (spoken, coverage) in composition.Coverage().OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            lines.Add($"covers {spoken}: {Entry(coverage.Entry)} {coverage.LibraryId} {coverage.FileName}");
        }

        var report = composition.OverlapReport([new DictionaryEntry(9, "kube", "KUBE"), new DictionaryEntry(10, "helm", "Helm")]);
        lines.Add($"overlaps: {string.Join(",", report.Overlaps)}");
        lines.Add($"rules: {string.Join(",", composition.Rules.Select(rule => $"{rule.LibraryId}:{Entry(rule.Entry)}:{rule.Tier}:{rule.LegacyMarkerActive}"))}");
        lines.Add($"ai: {string.Join(",", composition.AiLibraryEntries.Select(Entry))}");
        lines.Add($"excluded: {string.Join(",", composition.AiExcludedLibraryIds.Order(StringComparer.Ordinal))}");
        lines.Add($"{composition.IsPreview} {composition.Basis} {composition.AnyLegacyMarkerActive}");
        return string.Join("\n", lines);
    }

    private static string Entry(DictionaryEntry? entry) =>
        entry is null ? "-" : $"{entry.Id}|{entry.Pattern}|{entry.Replacement}|{entry.WholeWord}|{entry.Enabled}";

    private static LibraryCatalog TeamCatalog() =>
        Lib.Catalog(
            Lib.State(enabled: ["team"], ai: [("team", true)], accepted: [("team", Lib.H1)]),
            Lib.Committed(Lib.CustomLibrary("team", Lib.Custom("git hub", "GitHub"), Lib.Custom("kube", "Kubernetes")), Lib.H1));

    private static string Describe(LibraryDraft draft) =>
        $"{draft.Revision} {draft.BaseGeneration} " +
        $"on=[{string.Join(",", draft.LocalState.EnabledIds.Order(StringComparer.Ordinal))}] " +
        $"ai=[{string.Join(",", draft.LocalState.AiPermissions.OrderBy(pair => pair.Key, StringComparer.Ordinal))}] " +
        string.Join(";", draft.Libraries.Select(library =>
            $"{library.Content.Id} {library.Unsaved} {library.WritesContent} {library.PendingDelete}: " +
            string.Join(",", library.Content.Rows.Select(row => $"{row.Key.Value}={row.Values}"))));

    private static void ReadOnly<T>(IEnumerable<T> collection)
    {
        var asCollection = Assert.IsAssignableFrom<ICollection<T>>(collection);
        Assert.True(asCollection.IsReadOnly, $"A {collection.GetType().Name} can be changed through the collection interface.");
        Assert.Throws<NotSupportedException>(asCollection.Clear);
    }

    // The model types a draft or a catalog reaches through its fields, found by walking them: Scribe's own types, which
    // the checks above apply to, and not the framework's.
    private static IReadOnlyList<Type> ModelTypes()
    {
        var found = new List<Type>();
        var seen = new HashSet<Type>();
        var pending = new Queue<Type>([typeof(LibraryDraft), typeof(LibraryCatalog)]);
        while (pending.TryDequeue(out var type))
        {
            if (!seen.Add(type))
            {
                continue;
            }

            if (type.IsArray)
            {
                pending.Enqueue(type.GetElementType()!);
                continue;
            }

            if (type.IsGenericType)
            {
                foreach (var argument in type.GetGenericArguments())
                {
                    pending.Enqueue(argument);
                }
            }

            if (type.Assembly != typeof(LibraryDraft).Assembly || type.IsEnum)
            {
                continue;
            }

            found.Add(type);
            foreach (var field in type.GetFields(DeclaredInstance))
            {
                pending.Enqueue(field.FieldType);
            }
        }

        return found;
    }

    private static IEnumerable<MethodBase> MethodsOf(Type type)
    {
        foreach (var method in type.GetMethods(Declared))
        {
            yield return method;
        }

        foreach (var constructor in type.GetConstructors(Declared))
        {
            yield return constructor;
        }

        foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
        {
            foreach (var method in MethodsOf(nested))
            {
                yield return method;
            }
        }
    }

    private static IEnumerable<MethodBase> CompositionMethods() => MethodsOf(typeof(LibraryComposition));

    private static IEnumerable<(OpCode Code, int Token)> Instructions(MethodBase method) => MouseButtonRound7Tests.Instructions(method);

    private static HashSet<FieldInfo> Fields(MethodBase method) =>
        [.. Instructions(method)
            .Where(i => i.Code.OperandType == OperandType.InlineField)
            .Select(i => Resolve(method, i.Token))
            .OfType<FieldInfo>()];

    // A method or constructor a method's IL names, resolved in that method's generic context (a generic helper names
    // members of types built over its own type parameters).
    private static MethodBase ResolveMethod(MethodBase method, int token) =>
        method.Module.ResolveMethod(
            token,
            method.DeclaringType is { IsGenericType: true } type ? type.GetGenericArguments() : null,
            method.IsGenericMethod ? method.GetGenericArguments() : null)!;

    private static FieldInfo? Resolve(MethodBase method, int token)
    {
        if (token == 0)
        {
            return null;
        }

        try
        {
            return method.Module.ResolveField(
                token,
                method.DeclaringType is { IsGenericType: true } type ? type.GetGenericArguments() : null,
                method.IsGenericMethod ? method.GetGenericArguments() : null);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
