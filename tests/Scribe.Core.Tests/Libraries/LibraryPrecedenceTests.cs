using System.Text.Json;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;

namespace Scribe.Core.Tests.Libraries;

/// <summary>
/// The order libraries compete in for a spoken form: a frozen list of built-in ids, then custom libraries by file name.
/// It is what dictation's winners, the glossary's order, the badges and the Save prompt follow, so these tests pin it
/// independently of names, categories, cultures and the order a caller happens to hold.
/// </summary>
public sealed class LibraryPrecedenceTests
{
    private const string ResourcePrefix = "Scribe.Core.PostProcessing.Libraries.";

    /// <summary>
    /// The order 0.4.3 composed the eleven built-ins in, category then name (ordinal, case-insensitive), as
    /// <c>BuiltInDictionaryLibraries.All</c> returned it at d42d683. Captured once; never edit it.
    /// </summary>
    private static readonly string[] CapturedFrom043 =
    [
        "ai-terminology",
        "ai-model-names",
        "data-and-ai",
        "data-engineering",
        "data-science-machine-learning",
        "github",
        "microsoft-365",
        "microsoft-azure",
        "dotnet-development",
        "modern-developer-stack",
        "software-development",
    ];

    [Fact]
    public void The_order_0_4_3_composed_in_is_kept_and_new_built_ins_can_only_be_appended()
    {
        Assert.True(LibraryPrecedence.BuiltInOrder.Count >= CapturedFrom043.Length);
        Assert.Equal(CapturedFrom043, LibraryPrecedence.BuiltInOrder.Take(CapturedFrom043.Length));
    }

    [Fact]
    public void Every_shipped_built_in_appears_exactly_once()
    {
        var shipped = ShippedIds();
        var listed = LibraryPrecedence.BuiltInOrder;
        var retired = LibraryPrecedence.RetiredBuiltInIds;

        var duplicates = listed.GroupBy(id => id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        var missing = shipped.Where(id => !listed.Contains(id, StringComparer.OrdinalIgnoreCase)).ToList();
        var stray = listed
            .Where(id => !shipped.Contains(id, StringComparer.OrdinalIgnoreCase) && !retired.Contains(id, StringComparer.OrdinalIgnoreCase))
            .ToList();

        Assert.True(duplicates.Count == 0, "Listed more than once: " + string.Join(", ", duplicates));
        Assert.True(missing.Count == 0,
            "These built-in libraries ship but are not in LibraryPrecedence.BuiltInOrder, so their precedence would fall " +
            "back to their id. Append each id at the end of the list and of tests/fixtures/libraries/built-in-precedence.json: " +
            string.Join(", ", missing));
        Assert.True(stray.Count == 0,
            "Listed but not shipped. If the library was retired, keep its id in the order and add it to " +
            "LibraryPrecedence.RetiredBuiltInIds and to the fixture's \"retired\"; otherwise it is a typo: " + string.Join(", ", stray));
    }

    [Fact]
    public void A_retired_built_in_keeps_its_place_and_is_retired_only_while_it_does_not_ship()
    {
        var shipped = ShippedIds();
        var listed = LibraryPrecedence.BuiltInOrder;
        var retired = LibraryPrecedence.RetiredBuiltInIds;

        var unlisted = retired.Where(id => !listed.Contains(id, StringComparer.OrdinalIgnoreCase)).ToList();
        var shipping = retired.Where(id => shipped.Contains(id, StringComparer.OrdinalIgnoreCase)).ToList();
        var duplicates = retired.GroupBy(id => id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        Assert.True(unlisted.Count == 0,
            "A retired id stays in LibraryPrecedence.BuiltInOrder for good; put it back where it was: " + string.Join(", ", unlisted));
        Assert.True(shipping.Count == 0,
            "These ids are marked retired but ship again; take them out of RetiredBuiltInIds: " + string.Join(", ", shipping));
        Assert.True(duplicates.Count == 0, "Retired more than once: " + string.Join(", ", duplicates));
    }

    // The fixture is the copy of these lists meant for the macOS port, which does not read it yet: it will in stream M1.
    // Until then this test only keeps the fixture equal to the C# lists, and nothing checks the Swift order at all.
    [Fact]
    public void The_lists_match_the_fixture_the_macOS_port_will_read()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath()));
        List<string> Read(string property) =>
            document.RootElement.GetProperty(property).EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList();

        Assert.Equal(LibraryPrecedence.BuiltInOrder, Read("order"));
        Assert.Equal(LibraryPrecedence.RetiredBuiltInIds, Read("retired"));
    }

    [Fact]
    public void The_shipped_catalog_is_returned_in_that_order()
    {
        Assert.Equal(LibraryPrecedence.BuiltInOrder, BuiltInDictionaryLibraries.All.Select(l => l.Id));
    }

    [Fact]
    public void Renaming_or_recategorizing_a_built_in_never_moves_it()
    {
        // The shipped order was category then name. Were it still computed that way, these names and categories would
        // put software-development first and ai-terminology last.
        var renamed = new[]
        {
            Library("ai-terminology", builtIn: true, name: "Zeta", category: "Zzz"),
            Library("software-development", builtIn: true, name: "Aardvark", category: "Aaa"),
            Library("github", builtIn: true, name: "Middle", category: "Mmm"),
        };

        var ordered = LibraryPrecedence.Order(renamed).Select(l => l.Id);

        Assert.Equal(["ai-terminology", "github", "software-development"], ordered);
    }

    [Fact]
    public void Custom_libraries_follow_their_file_names_so_a_second_import_keeps_winning()
    {
        // "team-terms-2.csv" is the file a second import of "Team terms" gets, and the loader has always read it before
        // "team-terms.csv" because '-' sorts before '.'. Comparing the bare ids would put it second and hand the older
        // copy every spoken form the two disagree about.
        var libraries = new[]
        {
            Library("team-terms", builtIn: false),
            Library("Zulu Notes", builtIn: false),
            Library("team-terms-2", builtIn: false),
            Library("release-9", builtIn: false),
            Library("alpha", builtIn: false),
            Library("release-10", builtIn: false),
        };

        var ordered = LibraryPrecedence.Order(libraries).Select(l => l.Id);

        Assert.Equal(["alpha", "release-10", "release-9", "team-terms-2", "team-terms", "Zulu Notes"], ordered);
    }

    [Fact]
    public void Custom_order_is_the_order_the_loader_reads_files_in()
    {
        // The rule is only right if it reproduces the loader's own order, so this compares it with that order on real
        // files, including the names where bare ids and file names disagree.
        var ids = new[] { "a", "a-b", "a.b", "a_b", "a b", "a~b", "A1", "a10", "a9", "team", "team-2", "team 2", "Ärger", "zeta" };
        var folder = Path.Combine(Path.GetTempPath(), "scribe-precedence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            foreach (var id in ids)
            {
                File.WriteAllText(Path.Combine(folder, id + ".csv"), "x,X\n");
            }

            // DictionaryLibraryService.LoadCustom: every *.csv, full paths sorted ordinal and case-insensitive.
            var files = Directory.GetFiles(folder, "*.csv");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            var loaderOrder = files.Select(Path.GetFileNameWithoutExtension).ToList();

            var ordered = LibraryPrecedence.Order(Enumerable.Reverse(ids).Select(id => Library(id, builtIn: false))).Select(l => l.Id);

            Assert.Equal(loaderOrder, ordered);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Built_ins_come_first_even_when_a_hand_placed_file_reuses_a_built_in_id()
    {
        var libraries = new[]
        {
            Library("aaa", builtIn: false),
            Library("github", builtIn: false),
            Library("software-development", builtIn: true),
            Library("github", builtIn: true),
        };

        var ordered = LibraryPrecedence.Order(libraries).Select(l => (l.Id, l.BuiltIn));

        Assert.Equal([("github", true), ("software-development", true), ("aaa", false), ("github", false)], ordered);
    }

    [Fact]
    public void A_built_in_the_list_does_not_name_goes_after_the_named_ones_and_before_custom_libraries()
    {
        var libraries = new[]
        {
            Library("custom-first", builtIn: false),
            Library("zz-unlisted", builtIn: true),
            Library("aa-unlisted", builtIn: true),
            Library("software-development", builtIn: true),
            Library("ai-terminology", builtIn: true),
        };

        var ordered = LibraryPrecedence.Order(libraries).Select(l => l.Id);

        Assert.Equal(["ai-terminology", "software-development", "aa-unlisted", "zz-unlisted", "custom-first"], ordered);
    }

    [Fact]
    public void Built_in_ids_are_matched_without_regard_to_case()
    {
        var ordered = LibraryPrecedence.Order([Library("GitHub", builtIn: true), Library("AI-Terminology", builtIn: true)]);

        Assert.Equal(["AI-Terminology", "GitHub"], ordered.Select(l => l.Id));
    }

    [Fact]
    public void The_order_is_the_same_whatever_order_the_libraries_arrive_in()
    {
        var libraries = BuiltInDictionaryLibraries.All
            .Concat(new[] { "team-terms", "team-terms-2", "Team-Terms", "alpha", "release-10", "release-9" }.Select(id => Library(id, builtIn: false)))
            .ToList();
        var expected = LibraryPrecedence.Order(libraries).Select(l => (l.Id, l.BuiltIn)).ToList();

        var random = new Random(4817);
        for (var round = 0; round < 50; round++)
        {
            var shuffled = libraries.OrderBy(_ => random.Next()).ToList();
            Assert.Equal(expected, LibraryPrecedence.Order(shuffled).Select(l => (l.Id, l.BuiltIn)));
        }
    }

    [Fact]
    public void Enabled_keeps_precedence_order_whatever_the_order_and_case_of_the_stored_ids()
    {
        var libraries = new[]
        {
            Library("release-9", builtIn: false),
            Library("github", builtIn: true),
            Library("alpha", builtIn: false),
            Library("ai-model-names", builtIn: true),
            Library("microsoft-azure", builtIn: true),
        };

        var enabled = LibraryPrecedence.Enabled(libraries, ["RELEASE-9", "Alpha", "ai-model-names", "GitHub"]);

        Assert.Equal(["ai-model-names", "github", "alpha", "release-9"], enabled.Select(l => l.Id));
        Assert.Empty(LibraryPrecedence.Enabled(libraries, []));
        Assert.Empty(LibraryPrecedence.Enabled(libraries, null));
        Assert.Empty(LibraryPrecedence.Enabled(null, ["github"]));
    }

    [Fact]
    public void Rows_that_stand_for_libraries_order_the_same_way_as_the_libraries()
    {
        var rows = new[] { ("team-terms", false), ("github", true), ("team-terms-2", false), ("ai-terminology", true) };

        var ordered = LibraryPrecedence.Order(rows, row => row.Item1, row => row.Item2).Select(row => row.Item1);

        Assert.Equal(["ai-terminology", "github", "team-terms-2", "team-terms"], ordered);
    }

    [Fact]
    public void Missing_libraries_are_dropped()
    {
        var ordered = LibraryPrecedence.Order([null, Library("github", builtIn: true), null]);

        Assert.Equal(["github"], ordered.Select(l => l.Id));
    }

    private static DictionaryLibrary Library(string id, bool builtIn, string? name = null, string category = "Custom") =>
        new(id, name ?? id, category, Description: null, builtIn, [DictionaryEntry.New("term", "Term")]);

    private static List<string> ShippedIds()
    {
        var assembly = typeof(BuiltInDictionaryLibraries).Assembly;
        return assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal) && name.EndsWith(".csv", StringComparison.Ordinal))
            .Select(name => name[ResourcePrefix.Length..^".csv".Length])
            .ToList();
    }

    private static string FixturePath()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return Path.Combine(root.FullName, "tests", "fixtures", "libraries", "built-in-precedence.json");
    }
}
