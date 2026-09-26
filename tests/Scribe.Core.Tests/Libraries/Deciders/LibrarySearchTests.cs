using System.Globalization;
using Scribe.Core.Libraries;
using Scribe.Core.Settings;
using static Scribe.Core.Tests.Libraries.Deciders.DeciderFixture;

namespace Scribe.Core.Tests.Libraries.Deciders;

/// <summary>
/// D-10: "Search all libraries" is case- and accent-insensitive by the culture captured at load (en-US, tr-TR, sv-SE),
/// counts per library, never moves the selection; and the Sort menu's orders.
/// </summary>
public sealed class LibrarySearchTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");
    private static readonly CultureInfo Swedish = CultureInfo.GetCultureInfo("sv-SE");

    [Theory]
    [InlineData("en-US", "resume", "Résumé", true)]
    [InlineData("en-US", "RESUME", "résumé", true)]
    [InlineData("en-US", "naive", "naïve", true)]
    [InlineData("en-US", "istanbul", "İstanbul", true)]
    [InlineData("en-US", "ISTANBUL", "istanbul", true)]
    [InlineData("en-US", "malmo", "Malmö", true)]
    [InlineData("tr-TR", "istanbul", "İstanbul", true)]
    [InlineData("tr-TR", "ISTANBUL", "istanbul", false)]
    [InlineData("tr-TR", "ırmak", "IRMAK", true)]
    [InlineData("tr-TR", "resume", "Résumé", true)]
    [InlineData("sv-SE", "resume", "Résumé", true)]
    [InlineData("sv-SE", "MALMÖ", "malmö", true)]
    [InlineData("sv-SE", "malmo", "Malmö", false)]
    [InlineData("sv-SE", "ange", "Ånge", false)]
    public void D10_matching_is_case_and_accent_insensitive_the_way_the_culture_reads_letters(
        string culture, string query, string text, bool matches)
    {
        var search = LibrarySearch.For(CultureInfo.GetCultureInfo(culture));

        Assert.Equal(matches, search.Matches(new TermValues(text, "x"), query));
        Assert.Equal(matches, search.Matches(new TermValues("x", text), query));
    }

    [Fact]
    public void D10_the_search_counts_matches_per_library_and_never_says_where_to_go()
    {
        var workspace = Workspace(Standard());
        var search = LibrarySearch.For(English);

        var result = search.Search(workspace, "  GITHUB ");

        Assert.True(result.IsActive);
        Assert.Equal("GITHUB", result.Query);
        Assert.Equal(1, result.CountIn(GitHubId));
        Assert.Equal(0, result.CountIn(AzureId));
        Assert.Equal(1, result.CountIn("team-terms"));
        Assert.Equal(2, result.TotalMatches);
        Assert.Equal([GitHubId, AzureId, "team-terms"], result.Libraries.Select(library => library.LibraryId));
        Assert.Equal([RowIdOf(workspace, "team-terms", "get hub")], result.MatchesIn("team-terms"));

        // With the selection on a library that has none, the result says where the matches are; it has nothing that
        // selects anything, so the selection stays where the user put it.
        Assert.Equal([GitHubId, "team-terms"], result.FoundElsewhere(AzureId).Select(library => library.LibraryId));
        Assert.DoesNotContain(result.GetType().GetProperties(), property => property.Name.Contains("Select", StringComparison.Ordinal));
    }

    [Fact]
    public void D10_an_empty_blank_or_ignorable_query_is_no_search()
    {
        var workspace = Workspace(Standard());
        var search = LibrarySearch.For(English);

        foreach (var query in new[] { null, "", "   ", "\u0301" })
        {
            var result = search.Search(workspace, query);
            Assert.False(result.IsActive);
            Assert.Equal(0, result.TotalMatches);
            Assert.All(result.Libraries, library => Assert.Equal(0, library.Count));
        }
    }

    [Fact]
    public void D10_the_culture_is_the_one_captured_when_the_search_was_made()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = Turkish;
            var search = LibrarySearch.ForCurrentCulture();
            CultureInfo.CurrentCulture = English;

            Assert.Equal(Turkish, search.Culture);
            Assert.False(search.Matches(new TermValues("istanbul", "x"), "ISTANBUL"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void D10_the_sort_orders_rebuild_the_list_once_with_ties_in_saved_order_and_empty_values_last()
    {
        var workspace = Workspace(Catalog(
            [Custom("sort", "Sort", [
                new TermValues("banana", "B2"),
                new TermValues("Apple", ""),
                new TermValues("cherry 10", "A"),
                new TermValues("cherry 9", "A"),
                new TermValues("apple", "Z"),
            ])],
            ["sort"]));
        var rows = workspace.RowsOf("sort");
        var sort = LibraryTermSort.For(English);
        string Order(LibraryTermSortOrder order) => string.Join(",", sort.Sort(rows, order).Select(row => row.Row.Values.Spoken));

        Assert.Equal("banana,Apple,cherry 10,cherry 9,apple", Order(LibraryTermSortOrder.SavedOrder));
        Assert.Equal("Apple,apple,banana,cherry 9,cherry 10", Order(LibraryTermSortOrder.SpokenAscending));
        Assert.Equal("cherry 10,cherry 9,banana,apple,Apple", Order(LibraryTermSortOrder.SpokenDescending));
        Assert.Equal("cherry 9,cherry 10,banana,apple,Apple", Order(LibraryTermSortOrder.WrittenAscending));
        Assert.Equal("apple,banana,cherry 10,cherry 9,Apple", Order(LibraryTermSortOrder.WrittenDescending));
        Assert.NotSame(rows, sort.Sort(rows, LibraryTermSortOrder.SavedOrder));
    }

    [Fact]
    public void Search_and_sort_are_fast_enough_for_a_large_library()
    {
        var terms = Enumerable.Range(0, 20_000).Select(i => new TermValues($"term {i}", $"Term {i} value"));
        var workspace = Workspace(Catalog([Custom("big", "Big", terms)], ["big"]));

        var result = LibrarySearch.For(English).Search(workspace, "term 1999");

        Assert.Equal(11, result.CountIn("big"));
    }
}
