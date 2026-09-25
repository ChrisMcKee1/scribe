using System.Security.Cryptography;
using System.Text;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;

namespace Scribe.Core.Tests.Libraries.Composition;

/// <summary>
/// Legacy markers reproduce 0.4.3's result, written form and word boundaries, for every spoken form at the upgrade
/// (acceptance C-3, decision 3): random custom libraries written to a real folder, 0.4.3's own selection over that folder
/// and the enabled list it left (<see cref="Legacy043LibrarySelection"/>), against the composition of the catalog the
/// first start adopts. The folders hold hand-placed twins of built-in ids beside custom files whose names sort on either
/// side of them (review finding A16), so a twin ranked by its logical id instead of its file name moves winners.
/// </summary>
public sealed class LegacyMarkerUpgradeTests : IDisposable
{
    private static readonly string[] FileStems =
    [
        "github", "gi", "githubz", "h-notes", "ai", "ai-terminology", "ai-terminologyz", "data-and-ai", "b-team", "zeta",
        "team-terms", "team-terms-2", "Zulu Notes", "microsoft-azure", "microsoft-azurf",
    ];

    private static readonly string[] SharedCustomForms = ["kube", "north star", "project token", "sprint", "tailspin"];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "scribe-lib-markers-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a leftover temp folder is harmless.
        }
    }

    [Fact]
    public void Across_random_libraries_every_winner_at_the_upgrade_gives_0_4_3s_result()
    {
        var shipped = BuiltInDictionaryLibraries.All.SelectMany(library => library.Entries).ToList();
        var random = new Random(20260925);
        for (var round = 0; round < 120; round++)
        {
            var folder = Path.Combine(_root, "round-" + round);
            Directory.CreateDirectory(folder);
            var stems = FileStems.OrderBy(_ => random.Next()).Take(random.Next(2, 7)).ToList();
            foreach (var stem in stems)
            {
                var rows = Enumerable.Range(0, random.Next(1, 9)).Select(_ => RandomRow(random, shipped)).ToList();
                File.WriteAllText(Path.Combine(folder, stem + ".csv"), Legacy043LibraryCsv.Export(stem, "Custom", null, rows));
            }

            var enabled = BuiltInDictionaryLibraries.All.Select(l => l.Id).Where(_ => random.Next(2) == 0)
                .Concat(stems.Where(_ => random.Next(10) < 7))
                .OrderBy(_ => random.Next())
                .ToList();

            var old = Results(Legacy043LibrarySelection.EnabledEntries(enabled, folder));
            var composition = AdoptAndCompose(folder, enabled, rankTwinsByLogicalId: false);
            var ours = Results(composition.LibraryEntries);

            Assert.True(
                old.Count == ours.Count && old.All(pair => ours.TryGetValue(pair.Key, out var result) && result == pair.Value),
                $"Round {round}: the winners differ from 0.4.3's.\n{Describe(folder, enabled, old, ours)}");
        }
    }

    [Fact]
    public void Ranking_a_twin_by_its_logical_id_moves_a_winner_away_from_0_4_3s()
    {
        // The property above catches this; here it is on purpose, so the case the property guards stays visible.
        var folder = Path.Combine(_root, "twin");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "epsilon.csv"), "pattern,replacement\nproject token,Epsilon\n");
        File.WriteAllText(Path.Combine(folder, "github.csv"), "pattern,replacement\nproject token,Twin\n");
        string[] enabled = ["epsilon", "github"];

        var old = Results(Legacy043LibrarySelection.EnabledEntries(enabled, folder));
        var ours = Results(AdoptAndCompose(folder, enabled, rankTwinsByLogicalId: false).LibraryEntries);
        var byLogicalId = Results(AdoptAndCompose(folder, enabled, rankTwinsByLogicalId: true).LibraryEntries);

        Assert.Equal(("Epsilon", true), old[LibraryTermKey.From("project token")]);
        Assert.Equal(old[LibraryTermKey.From("project token")], ours[LibraryTermKey.From("project token")]);
        Assert.Equal(("Twin", true), byLogicalId[LibraryTermKey.From("project token")]);
    }

    [Fact]
    public void A_twin_takes_the_enabled_state_its_stem_had_and_its_rows_keep_the_built_ins_result()
    {
        var folder = Path.Combine(_root, "stem");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "github.csv"), "pattern,replacement\nget hub,Twin Hub\nnightjar,Nightjar\n");

        var composition = AdoptAndCompose(folder, ["github"], rankTwinsByLogicalId: false);

        Assert.Equal("GitHub", Lib.Winner(composition, "get hub"));
        Assert.Equal("Nightjar", Lib.Winner(composition, "nightjar"));
        Assert.Equal("custom-github", Lib.WinnerLibrary(composition, "nightjar"));
        Assert.Contains(composition.EnabledLibraries, library => library.Id == "custom-github");
        Assert.Contains(composition.EnabledLibraries, library => library.Id == "github");
    }

    private static DictionaryEntry RandomRow(Random random, IReadOnlyList<DictionaryEntry> shipped)
    {
        var pick = random.Next(10);
        string spoken, written;
        if (pick < 6)
        {
            var source = shipped[random.Next(shipped.Count)];
            spoken = random.Next(4) == 0 ? source.Pattern.ToUpperInvariant() : source.Pattern;
            written = random.Next(3) switch
            {
                0 => source.Replacement,
                1 => source.Replacement + " Team",
                _ => "Custom" + random.Next(100),
            };
        }
        else if (pick < 9)
        {
            spoken = SharedCustomForms[random.Next(SharedCustomForms.Length)];
            written = "Written" + random.Next(4);
        }
        else
        {
            spoken = "unique" + random.Next(1000);
            written = "Unique";
        }

        return new DictionaryEntry(0, spoken, written, WholeWord: random.Next(7) != 0, Enabled: random.Next(10) != 0);
    }

    // The first start of the library editor's version over this folder: 0.4.3's enabled list read with no library state
    // row, the adoption's legacy markers, and the committed composition. A file whose stem is a built-in id is the
    // hand-placed twin custom-<stem>, still stored as <stem>.csv.
    private static LibraryComposition AdoptAndCompose(string folder, IReadOnlyList<string> enabled, bool rankTwinsByLogicalId)
    {
        var builtInIds = BuiltInDictionaryLibraries.All.Select(l => l.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var libraries = BuiltInDictionaryLibraries.All
            .Select(library => Lib.Committed(new LibraryContent(library.Id, true, library.Name, library.Category, library.Description,
                [.. library.Entries.Select(entry => Lib.Shipped(entry.Pattern, entry.Replacement, entry.WholeWord))])))
            .ToList();
        foreach (var path in Directory.GetFiles(folder, "*.csv"))
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            var bytes = File.ReadAllBytes(path);
            var rows = Legacy043LibraryCsv.Parse(Encoding.UTF8.GetString(bytes)).Entries.Select(e => LibraryRow.Custom(TermValues.FromEntry(e)));
            var id = builtInIds.Contains(stem) ? "custom-" + stem : stem;
            var fileName = rankTwinsByLogicalId && builtInIds.Contains(stem) ? id + ".csv" : stem + ".csv";
            libraries.Add(new CatalogLibrary(
                Lib.CustomLibrary(id, [.. rows]), LibraryFileState.Available, fileName,
                new LibraryContentHash(Convert.ToHexStringLower(SHA256.HashData(bytes)))));
        }

        // Older builds load a twin under its stem, so its identity keeps the stem as the legacy id whatever it ranks by.
        var identities = libraries.Select(library => new LibraryIdentity(
            library.Content.Id, library.Content.BuiltIn,
            library.Content.Id.StartsWith("custom-", StringComparison.Ordinal) && builtInIds.Contains(library.Content.Id["custom-".Length..])
                ? library.Content.Id["custom-".Length..] + ".csv"
                : library.FileName)).ToList();
        var firstStart = new LibraryStateContext(false, false, false);
        var read = LibraryComposer.Instance.ReadLocalState(enabled, null, identities, firstStart);
        var adoption = LibraryComposer.Instance.PlanAdoption(Lib.Catalog(0, read, [.. libraries]), firstStart);
        Assert.NotNull(adoption);
        Assert.Equal(LibraryAdoptionReasons.FirstStart, adoption.Reasons);
        return LibraryComposition.Committed(Lib.Catalog(0, adoption.State, [.. libraries]), [], new GlossaryBudget(80));
    }

    private static Dictionary<LibraryTermKey, (string Written, bool WholeWord)> Results(IEnumerable<DictionaryEntry> entries)
    {
        var results = new Dictionary<LibraryTermKey, (string, bool)>();
        foreach (var entry in entries)
        {
            results.TryAdd(LibraryTermKey.From(entry.Pattern), (entry.Replacement, entry.WholeWord));
        }

        return results;
    }

    private static string Describe(
        string folder,
        IReadOnlyList<string> enabled,
        IReadOnlyDictionary<LibraryTermKey, (string Written, bool WholeWord)> old,
        IReadOnlyDictionary<LibraryTermKey, (string Written, bool WholeWord)> ours)
    {
        var text = new StringBuilder();
        text.AppendLine("enabled: " + string.Join(", ", enabled));
        foreach (var path in Directory.GetFiles(folder, "*.csv").Order(StringComparer.OrdinalIgnoreCase))
        {
            text.AppendLine(Path.GetFileName(path) + ": " + File.ReadAllText(path).ReplaceLineEndings(" | "));
        }

        foreach (var key in old.Keys.Union(ours.Keys).Where(key => !old.TryGetValue(key, out var a) || !ours.TryGetValue(key, out var b) || a != b))
        {
            text.AppendLine($"{key.Value}: 0.4.3 {Show(old, key)}, now {Show(ours, key)}");
        }

        return text.ToString();

        static string Show(IReadOnlyDictionary<LibraryTermKey, (string Written, bool WholeWord)> results, LibraryTermKey key) =>
            results.TryGetValue(key, out var result) ? $"\"{result.Written}\"{(result.WholeWord ? string.Empty : " (substring)")}" : "none";
    }
}
