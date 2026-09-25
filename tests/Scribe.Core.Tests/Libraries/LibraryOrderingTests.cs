using System.Globalization;
using Scribe.Core.Libraries;

namespace Scribe.Core.Tests.Libraries;

/// <summary>
/// The Libraries list's display order: one A to Z list in the user's regional sort order, digits as numbers, case
/// ignored, with ordinal name and id as tie-breaks. Display only, so nothing here may depend on precedence.
/// </summary>
public sealed class LibraryOrderingTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo Swedish = CultureInfo.GetCultureInfo("sv-SE");
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    [Fact]
    public void Built_in_and_custom_libraries_share_one_A_to_Z_list()
    {
        // The shipped names plus two custom ones, in precedence order: built-ins by category and name, customs last.
        string[] precedence =
        [
            "AI and Machine Learning Terminology", "AI Model Names", "Data and AI", "Data Engineering",
            "Data Science and Machine Learning", "GitHub", "Microsoft 365 and Products", "Microsoft Azure",
            ".NET and C# Development", "Modern Developer Stack", "Software Development",
            "Contoso product names", "My team terms",
        ];

        var sorted = Names(LibraryOrdering.For(English), precedence);

        Assert.Equal(
        [
            ".NET and C# Development", "AI and Machine Learning Terminology", "AI Model Names", "Contoso product names",
            "Data and AI", "Data Engineering", "Data Science and Machine Learning", "GitHub", "Microsoft 365 and Products",
            "Microsoft Azure", "Modern Developer Stack", "My team terms", "Software Development",
        ], sorted);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("sv-SE")]
    [InlineData("tr-TR")]
    public void Digits_compare_as_numbers(string culture)
    {
        var sorted = Names(LibraryOrdering.For(CultureInfo.GetCultureInfo(culture)), ["Release 100 terms", "Release 10 terms", "Release 9 terms"]);

        Assert.Equal(["Release 9 terms", "Release 10 terms", "Release 100 terms"], sorted);
    }

    [Fact]
    public void Case_is_ignored_and_names_it_cannot_tell_apart_fall_back_to_ordinal_name_then_id()
    {
        // The ids run against the ordinal order of the names, so swapping the two tie-breaks would show.
        var ordering = LibraryOrdering.For(English);
        var items = new[]
        {
            ("github tools", "a"), ("Azure", "z"), ("GitHub tools", "b"), ("Team", "team-2"), ("GitHub Tools", "c"), ("Team", "team"),
        };

        var sorted = ordering.Sort(items, i => i.Item1, i => i.Item2);

        Assert.Equal(
            [("Azure", "z"), ("GitHub Tools", "c"), ("GitHub tools", "b"), ("github tools", "a"), ("Team", "team"), ("Team", "team-2")],
            sorted);
    }

    [Fact]
    public void Swedish_puts_a_ring_and_umlauts_after_z_where_English_folds_them_into_a_and_o()
    {
        string[] names = ["Öl", "Zebra", "Åtgärder", "Ärenden", "Apple"];

        Assert.Equal(["Apple", "Zebra", "Åtgärder", "Ärenden", "Öl"], Names(LibraryOrdering.For(Swedish), names));
        Assert.Equal(["Apple", "Ärenden", "Åtgärder", "Öl", "Zebra"], Names(LibraryOrdering.For(English), names));
    }

    [Fact]
    public void Turkish_sorts_a_capital_I_as_dotless_before_a_dotted_i()
    {
        string[] names = ["iCloud terms", "Integration terms"];

        // In Turkish a capital I is the capital of the dotless ı, which sorts before i; English compares c with n.
        Assert.Equal(["Integration terms", "iCloud terms"], Names(LibraryOrdering.For(Turkish), names));
        Assert.Equal(["iCloud terms", "Integration terms"], Names(LibraryOrdering.For(English), names));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("sv-SE")]
    [InlineData("tr-TR")]
    public void The_order_is_total(string culture)
    {
        var ordering = LibraryOrdering.For(CultureInfo.GetCultureInfo(culture));
        (string Name, string Id)[] items =
        [
            ("GitHub", "github"), ("github", "custom-github"), ("GitHub", "custom-github"), ("Release 9", "r9"),
            ("Release 10", "r10"), ("Release 010", "r010"), ("Ärenden", "a1"), ("Arenden", "a2"), ("Öl", "o"),
            ("ılık", "t1"), ("Ilk", "t2"), ("iOS", "t3"), ("İzmir", "t4"), ("izmir", "t5"), (".NET", "dot"),
            ("", "empty"), ("AI Model Names", "ai-model-names"), ("AI and Machine Learning Terminology", "ai-terminology"),
        ];

        int Sign(int value) => Math.Sign(value);
        foreach (var a in items)
        {
            Assert.Equal(0, ordering.Compare(a.Name, a.Id, a.Name, a.Id));
            foreach (var b in items)
            {
                var ab = Sign(ordering.Compare(a.Name, a.Id, b.Name, b.Id));
                Assert.Equal(-ab, Sign(ordering.Compare(b.Name, b.Id, a.Name, a.Id)));
                if (a != b)
                {
                    Assert.NotEqual(0, ab);
                }

                foreach (var c in items)
                {
                    if (ab < 0 && Sign(ordering.Compare(b.Name, b.Id, c.Name, c.Id)) < 0)
                    {
                        Assert.True(ordering.Compare(a.Name, a.Id, c.Name, c.Id) < 0, $"{a} < {b} < {c} but not {a} < {c}");
                    }
                }
            }
        }
    }

    [Fact]
    public void An_ordering_keeps_the_culture_it_was_created_with()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = Swedish;
            var ordering = LibraryOrdering.ForCurrentCulture();

            // The regional format changing after the view loaded must not change how it places the next row.
            CultureInfo.CurrentCulture = English;

            Assert.Equal("sv-SE", ordering.Culture.Name);
            Assert.Equal(["Zebra", "Öl"], Names(ordering, ["Öl", "Zebra"]));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void The_sort_keeps_the_given_order_for_libraries_it_cannot_tell_apart()
    {
        var first = new Row("GitHub", "github", BuiltIn: true);
        var second = new Row("GitHub", "github", BuiltIn: false);

        Assert.Equal([first, second], LibraryOrdering.For(English).Sort([first, second], r => r.Name, r => r.Id));
        Assert.Equal([second, first], LibraryOrdering.For(English).Sort([second, first], r => r.Name, r => r.Id));
    }

    [Fact]
    public void A_new_library_is_placed_where_it_sorts()
    {
        var ordering = LibraryOrdering.For(English);
        var sorted = ordering.Sort(
            new[] { "GitHub", "Data Science and Machine Learning", ".NET and C# Development", "Software Development" }
                .Select(name => new Row(name, name.ToLowerInvariant(), BuiltIn: true)),
            r => r.Name, r => r.Id);

        int Place(string name, string id) => ordering.InsertionIndex(sorted, new Row(name, id, BuiltIn: false), r => r.Name, r => r.Id);

        Assert.Equal(2, Place("Fabrikam support terms", "fabrikam-support-terms"));
        Assert.Equal(0, Place("!Urgent", "urgent"));
        Assert.Equal(4, Place("Zulu", "zulu"));
        Assert.Equal(3, Place("GitHub", "zzz")); // after the library it ties with on name, because its id sorts later
        Assert.Equal(2, Place("GitHub", "aaa"));
        Assert.Equal(3, Place("GitHub", "github")); // a full tie lands after the row already there
        Assert.Equal(0, ordering.InsertionIndex(Array.Empty<Row>(), new Row("Any", "any", false), r => r.Name, r => r.Id));
    }

    [Fact]
    public void A_missing_name_or_id_sorts_as_empty()
    {
        var ordering = LibraryOrdering.For(English);

        Assert.True(ordering.Compare(null, "a", "A", "a") < 0);
        Assert.Equal(0, ordering.Compare(null, null, string.Empty, string.Empty));
    }

    private static IReadOnlyList<string> Names(LibraryOrdering ordering, IEnumerable<string> names) =>
        ordering.Sort(names.Select((name, i) => new Row(name, "id" + i, BuiltIn: false)), r => r.Name, r => r.Id)
            .Select(r => r.Name)
            .ToList();

    private sealed record Row(string Name, string Id, bool BuiltIn);
}
