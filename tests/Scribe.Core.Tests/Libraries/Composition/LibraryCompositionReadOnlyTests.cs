using System.Collections.ObjectModel;
using System.Reflection;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;
using static Scribe.Core.Tests.Libraries.Composition.Lib;
using static Scribe.Core.Tests.Libraries.Composition.LibraryPreviewMemoPreconditionTests;

namespace Scribe.Core.Tests.Libraries.Composition;

/// <summary>
/// Every collection a composition exposes or hands out is read-only (review rounds 1 and 2, item 1): Preview keeps
/// compositions and returns the same one to every caller with equal inputs, so a caller able to change one would change
/// what every later caller reads. A caller that casts one to a mutable interface and changes it gets
/// NotSupportedException, through any interface, including ICollection.SyncRoot, which leads back to the collection itself
/// and never to the storage behind it. Coverage and OverlapReport build a new result at every call, which stays the
/// caller's own.
/// </summary>
public sealed class LibraryCompositionReadOnlyTests
{
    public static TheoryData<string> Exposed =>
    [
        "Rules", "LibraryEntries", "AiLibraryEntries", "AiExcludedLibraryIds", "EnabledLibraries",
        "the entries of each library in use", "a status's libraries with the same result",
        "a status's libraries with a different result", "an unknown row's libraries", "a filter's keys",
        "an unknown library's filter",
    ];

    // The reviewer's case: an enabled word pack denied AI cleanup, its exclusion cleared through the set interface, then a
    // preview with unchanged inputs. Before this fix the kept preview came back without the exclusion while its statuses
    // still said NotPermitted.
    [Fact]
    public void A_kept_preview_s_excluded_libraries_cannot_be_cleared_and_stay_as_a_fresh_composition_has_them()
    {
        var (catalog, draft, dictionary, budget) = OverlappingInputs();
        var preview = LibraryComposition.Preview(draft, catalog, dictionary, budget);
        Assert.Equal(new[] { "extra" }, preview.AiExcludedLibraryIds);
        Assert.Equal(GlossaryInclusion.NotPermitted, preview.StatusOf("extra", Key("see sharp")).Glossary);

        Assert.Throws<NotSupportedException>(() => ((ISet<string>)preview.AiExcludedLibraryIds).Clear());

        var again = LibraryComposition.Preview(draft, catalog, dictionary, budget);
        Assert.Same(preview, again);
        Assert.Equal(new[] { "extra" }, again.AiExcludedLibraryIds);
        Assert.Contains("EXTRA", again.AiExcludedLibraryIds);
        Assert.Equal(GlossaryInclusion.NotPermitted, again.StatusOf("extra", Key("see sharp")).Glossary);
        Assert.Equal(Read(Fresh(draft, catalog, dictionary, budget)), Read(again));
    }

    [Theory]
    [MemberData(nameof(Exposed))]
    public void No_collection_a_composition_hands_out_changes_through_a_mutable_interface(string exposed)
    {
        var (catalog, draft, dictionary, budget) = OverlappingInputs();
        var fresh = Read(Fresh(draft, catalog, dictionary, budget));
        var preview = LibraryComposition.Preview(draft, catalog, dictionary, budget);
        var entry = DictionaryEntry.New("zed", "Zed");
        switch (exposed)
        {
            case "Rules":
                CannotChange(preview.Rules, new ComposedRule(entry, "team", Key("zed"), RuleTier.Authored, false));
                break;
            case "LibraryEntries":
                CannotChange(preview.LibraryEntries, entry);
                break;
            case "AiLibraryEntries":
                CannotChange(preview.AiLibraryEntries, entry);
                break;
            case "AiExcludedLibraryIds":
                CannotChange(preview.AiExcludedLibraryIds, "team");
                break;
            case "EnabledLibraries":
                CannotChange(preview.EnabledLibraries, new DictionaryLibrary("zed", "Zed", "Custom", null, false, []));
                break;
            case "the entries of each library in use":
                Assert.NotEmpty(preview.EnabledLibraries);
                foreach (var library in preview.EnabledLibraries)
                {
                    CannotChange(library.Entries, entry);
                }

                break;
            case "a status's libraries with the same result":
                var same = preview.StatusOf("team", Key("jay son")).SameResultIn;
                Assert.Equal(new[] { "general" }, same);
                CannotChange(same, "extra");
                break;
            case "a status's libraries with a different result":
                var different = preview.StatusOf("team", Key("git hub")).DifferentResultIn;
                Assert.Equal(new[] { "general" }, different);
                CannotChange(different, "extra");
                break;
            case "an unknown row's libraries":
                var unknown = preview.StatusOf("missing", Key("git hub"));
                CannotChange(unknown.SameResultIn, "extra");
                CannotChange(unknown.DifferentResultIn, "extra");
                break;
            case "a filter's keys":
                var keys = preview.Filter("team", TermFilter.All);
                Assert.NotEmpty(keys);
                CannotChange(keys, Key("zed"));
                break;
            case "an unknown library's filter":
                CannotChange(preview.Filter("missing", TermFilter.All), Key("zed"));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(exposed), exposed, null);
        }

        var again = LibraryComposition.Preview(draft, catalog, dictionary, budget);
        Assert.Same(preview, again);
        Assert.Equal(fresh, Read(again));
    }

    // The sweep a new collection property falls into by itself: every one, on a committed composition and a preview.
    [Fact]
    public void Every_collection_property_of_every_composition_is_read_only()
    {
        var (catalog, draft, dictionary, budget) = OverlappingInputs();
        var properties = typeof(LibraryComposition).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.PropertyType != typeof(string) &&
                               typeof(System.Collections.IEnumerable).IsAssignableFrom(property.PropertyType))
            .ToList();
        Assert.Superset(
            new HashSet<string> { "Rules", "LibraryEntries", "AiLibraryEntries", "AiExcludedLibraryIds", "EnabledLibraries" },
            properties.Select(property => property.Name).ToHashSet());

        foreach (var composition in new[]
                 {
                     LibraryComposition.Committed(catalog, dictionary, budget), Fresh(draft, catalog, dictionary, budget),
                     LibraryComposition.Preview(draft, catalog, dictionary, budget),
                 })
        {
            foreach (var property in properties)
            {
                var value = property.GetValue(composition)!;
                var collection = value.GetType().GetInterfaces()
                    .Single(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ICollection<>));
                Assert.True((bool)collection.GetProperty(nameof(ICollection<int>.IsReadOnly))!.GetValue(value)!, property.Name);
                var clear = Assert.Throws<TargetInvocationException>(() => collection.GetMethod(nameof(ICollection<int>.Clear))!.Invoke(value, null));
                Assert.IsType<NotSupportedException>(clear.InnerException);
                var root = Assert.IsAssignableFrom<System.Collections.ICollection>(value).SyncRoot;
                Assert.True(ReferenceEquals(value, root), $"{property.Name}'s SyncRoot is a {root.GetType().Name}.");
                if (property.PropertyType.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
                {
                    // Still a ReadOnlyCollection at run time, the IList a binding sees.
                    Assert.True(IsReadOnlyCollection(value.GetType()), $"{property.Name} is a {value.GetType().Name}.");
                }
            }
        }
    }

    // The reviewer's first case: a library's entries are the composition's own entry array, which the rules point at. A
    // plain ReadOnlyCollection handed that array out through SyncRoot, and replacing an entry made a later preview with
    // equal inputs, the same kept object, report the row as another library's and out of the glossary.
    [Fact]
    public void A_library_s_entries_cannot_be_reached_through_SyncRoot_and_a_kept_preview_keeps_its_winners()
    {
        var (catalog, draft, dictionary, budget) = OverlappingInputs();
        var fresh = Read(Fresh(draft, catalog, dictionary, budget));
        var preview = LibraryComposition.Preview(draft, catalog, dictionary, budget);
        var general = preview.EnabledLibraries[0];
        Assert.Equal("general", general.Id);
        Assert.Equal("git hub", general.Entries[0].Pattern);

        var root = ((System.Collections.ICollection)general.Entries).SyncRoot;
        Assert.Same(general.Entries, root);
        Assert.Throws<InvalidCastException>(() => (DictionaryEntry[])((System.Collections.ICollection)general.Entries).SyncRoot);
        Assert.Throws<NotSupportedException>(() => ((System.Collections.IList)root)[0] = DictionaryEntry.New("word", "Other entry"));

        var again = LibraryComposition.Preview(draft, catalog, dictionary, budget);
        Assert.Same(preview, again);
        var status = again.StatusOf("general", Key("git hub"));
        Assert.Equal(TermWinner.ThisRow, status.Winner);
        Assert.Equal(GlossaryInclusion.Included, status.Glossary);
        Assert.Equal(fresh, Read(again));
    }

    // The reviewer's second case: clearing AiLibraryEntries through SyncRoot before the first status made the glossary
    // count no library entry, so an included row read NotEligible.
    [Fact]
    public void The_AI_entries_cannot_be_cleared_through_SyncRoot_before_the_first_status()
    {
        var (catalog, draft, dictionary, budget) = OverlappingInputs();
        var fresh = Read(Fresh(draft, catalog, dictionary, budget));
        var preview = LibraryComposition.Preview(draft, catalog, dictionary, budget);

        var root = ((System.Collections.ICollection)preview.AiLibraryEntries).SyncRoot;
        Assert.Same(preview.AiLibraryEntries, root);
        Assert.False(root is List<DictionaryEntry>, "AiLibraryEntries' SyncRoot is its list.");
        Assert.Throws<NotSupportedException>(((System.Collections.IList)root).Clear);

        var again = LibraryComposition.Preview(draft, catalog, dictionary, budget);
        Assert.Same(preview, again);
        Assert.NotEmpty(again.AiLibraryEntries);
        Assert.Equal(GlossaryInclusion.Included, again.StatusOf("general", Key("git hub")).Glossary);
        Assert.Equal(fresh, Read(again));
    }

    // Apart from SyncRoot, the wrapper is a ReadOnlyCollection like any other, over a list and over an array.
    [Fact]
    public void The_shared_wrapper_is_a_ReadOnlyCollection_but_for_its_SyncRoot()
    {
        List<string> list = ["a", "b", "c"];
        string[] array = ["x", "y"];
        foreach (var (shared, plain) in new (ReadOnlyCollection<string>, ReadOnlyCollection<string>)[]
                 {
                     (new SharedReadOnlyCollection<string>(list), list.AsReadOnly()),
                     (new SharedReadOnlyCollection<string>(array), Array.AsReadOnly(array)),
                 })
        {
            Assert.Equal(plain, shared);
            Assert.Equal(plain.Count, ((System.Collections.ICollection)shared).Count);
            Assert.False(((System.Collections.ICollection)shared).IsSynchronized);
            Assert.Same(shared, ((System.Collections.ICollection)shared).SyncRoot);
            Assert.NotSame(plain, ((System.Collections.ICollection)plain).SyncRoot);

            var fromShared = new object[plain.Count + 1];
            var fromPlain = new object[plain.Count + 1];
            ((System.Collections.ICollection)shared).CopyTo(fromShared, 1);
            ((System.Collections.ICollection)plain).CopyTo(fromPlain, 1);
            Assert.Equal(fromPlain, fromShared);
            var typed = new string[plain.Count];
            ((System.Collections.ICollection)shared).CopyTo(typed, 0);
            Assert.Equal(plain, typed);

            var untyped = (System.Collections.IList)shared;
            Assert.True(untyped.IsReadOnly);
            Assert.True(untyped.IsFixedSize);
            Assert.Equal(plain[1], untyped[1]);
            Assert.Equal(1, untyped.IndexOf(plain[1]));
            Assert.Throws<NotSupportedException>(() => untyped.Add("z"));
        }
    }

    private static bool IsReadOnlyCollection(Type type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(ReadOnlyCollection<>))
            {
                return true;
            }
        }

        return false;
    }

    [Fact]
    public void Coverage_and_the_overlap_report_are_new_at_every_call_and_stay_the_caller_s_own()
    {
        var (catalog, draft, dictionary, budget) = OverlappingInputs();
        var fresh = Read(Fresh(draft, catalog, dictionary, budget));
        var preview = LibraryComposition.Preview(draft, catalog, dictionary, budget);

        var coverage = preview.Coverage();
        var covered = coverage.Count;
        Assert.NotSame(coverage, preview.Coverage());
        ((IDictionary<string, LibraryCoverage>)coverage).Clear();
        Assert.Equal(covered, preview.Coverage().Count);

        DictionaryEntry[] personal = [new(9, "kube", "KUBE")];
        var report = preview.OverlapReport(personal);
        Assert.Single(report.Overlaps);
        Assert.NotSame(report.Overlaps, preview.OverlapReport(personal).Overlaps);
        ((ICollection<DictionaryOverlap>)report.Overlaps).Clear();
        Assert.Single(preview.OverlapReport(personal).Overlaps);

        Assert.Same(preview, LibraryComposition.Preview(draft, catalog, dictionary, budget));
        Assert.Equal(fresh, Read(preview));
    }

    // What a status, a rule, a library in use, a badge or the Save prompt holds cannot be assigned after construction, and
    // holds no collection of a shape that can be changed: the entries the rules point at included.
    [Fact]
    public void Every_record_a_composition_hands_out_is_immutable()
    {
        Type[] mutableShapes =
        [
            typeof(List<>), typeof(Dictionary<,>), typeof(HashSet<>), typeof(IList<>), typeof(ICollection<>), typeof(ISet<>),
            typeof(IDictionary<,>),
        ];
        foreach (var type in new[]
                 {
                     typeof(ComposedRule), typeof(DictionaryEntry), typeof(DictionaryLibrary), typeof(TermStatus), typeof(TermReview),
                     typeof(TermValues), typeof(LibraryCoverage), typeof(LibraryTermKey), typeof(DictionaryOverlapReport),
                     typeof(DictionaryOverlap),
                 })
        {
            Assert.True(type.IsValueType || (type.IsSealed && type.BaseType == typeof(object)), $"{type.Name} could gain state.");
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                Assert.True(field.IsInitOnly, $"{type.Name}.{field.Name} can be assigned after construction.");
                var held = Nullable.GetUnderlyingType(field.FieldType) ?? field.FieldType;
                Assert.False(
                    held.IsArray || (held.IsGenericType && mutableShapes.Contains(held.GetGenericTypeDefinition())),
                    $"{type.Name}.{field.Name} holds a {held.Name}.");
            }
        }
    }

    private static LibraryComposition Fresh(
        LibraryDraft draft, LibraryCatalog catalog, IReadOnlyList<DictionaryEntry> dictionary, GlossaryBudget budget) =>
        LibraryComposition.Preview(draft, catalog, dictionary, budget, LibraryDecisions.Precedence);

    // Every way the mutable interfaces offer to change the collection throws, and it holds what it held.
    private static void CannotChange<T>(IEnumerable<T> exposed, T sample)
    {
        var before = exposed.ToList();
        var collection = Assert.IsAssignableFrom<ICollection<T>>(exposed);
        Assert.True(collection.IsReadOnly, $"A {exposed.GetType().Name} says it can be changed.");
        Assert.Throws<NotSupportedException>(() => collection.Add(sample));
        Assert.Throws<NotSupportedException>(() => collection.Remove(before.Count > 0 ? before[0] : sample));
        Assert.Throws<NotSupportedException>(collection.Clear);
        if (exposed is IList<T> list)
        {
            Assert.Throws<NotSupportedException>(() => list.Insert(0, sample));
            if (before.Count > 0)
            {
                Assert.Throws<NotSupportedException>(() => list.RemoveAt(0));
                Assert.Throws<NotSupportedException>(() => list[0] = sample);
            }
        }

        if (exposed is ISet<T> set)
        {
            Assert.Throws<NotSupportedException>(() => set.Add(sample));
            Assert.Throws<NotSupportedException>(() => set.UnionWith([sample]));
            Assert.Throws<NotSupportedException>(() => set.ExceptWith(before));
            Assert.Throws<NotSupportedException>(() => set.IntersectWith([]));
            Assert.Throws<NotSupportedException>(() => set.SymmetricExceptWith([sample]));
        }

        // An empty array's non-generic Clear has nothing to clear; a filled collection must refuse both.
        if (exposed is System.Collections.IList nonGeneric && before.Count > 0)
        {
            Assert.Throws<NotSupportedException>(() => nonGeneric[0] = sample);
            Assert.Throws<NotSupportedException>(nonGeneric.Clear);
        }

        // ICollection.SyncRoot, the route that needs no reflection: it must lead back to the collection itself, never to
        // the list, array or set behind it. (An empty array, which some results are, is its own root and holds nothing.)
        if (exposed is System.Collections.ICollection untyped)
        {
            var root = untyped.SyncRoot;
            Assert.True(ReferenceEquals(exposed, root), $"A {exposed.GetType().Name}'s SyncRoot is a {root.GetType().Name}.");
            if (root is System.Collections.IList rootList && before.Count > 0)
            {
                Assert.Throws<NotSupportedException>(() => rootList[0] = sample);
                Assert.Throws<NotSupportedException>(rootList.Clear);
            }
        }

        Assert.Equal(before, exposed.ToList());
    }
}
