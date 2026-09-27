using System.Security.Cryptography;
using System.Text;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;

namespace Scribe.Core.Tests.Libraries.Composition;

/// <summary>
/// Legacy markers keep what dictation writes at the upgrade exactly as 0.4.3 wrote it (acceptance C-3, decision 3):
/// random custom libraries written to a real folder, 0.4.3's own selection over that folder and the enabled list it left
/// (<see cref="Legacy043LibrarySelection"/>), against the composition of the catalog the first start adopts, both through
/// the real matcher. The folders hold hand-placed twins of built-in ids beside custom files whose names sort on either
/// side of them (review finding A16), and spellings the matcher takes for a shipped form under another key (round 2, A3).
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
    public void Across_random_libraries_at_the_upgrade_dictation_writes_what_0_4_3_writes()
    {
        // The oracle is finished text through the real matcher (round 2, Astra A3), not winners per key: rows whose keys
        // differ can still match one text (the Kelvin sign), and rows with one key can match different texts (a final
        // sigma, which the shipped data has none of, so the deterministic cases above cover it).
        var shipped = BuiltInDictionaryLibraries.All.SelectMany(library => library.Entries).ToList();
        var random = new Random(20260925);
        for (var round = 0; round < 120; round++)
        {
            var folder = Path.Combine(_root, "round-" + round);
            Directory.CreateDirectory(folder);
            var stems = FileStems.OrderBy(_ => random.Next()).Take(random.Next(2, 7)).ToList();
            var forms = new List<string>();
            foreach (var stem in stems)
            {
                var rows = Enumerable.Range(0, random.Next(1, 9)).Select(_ => RandomRow(random, shipped, forms)).ToList();
                File.WriteAllText(Path.Combine(folder, stem + ".csv"), Legacy043LibraryCsv.Export(stem, "Custom", null, rows));
            }

            var enabled = BuiltInDictionaryLibraries.All.Select(l => l.Id).Where(_ => random.Next(2) == 0)
                .Concat(stems.Where(_ => random.Next(10) < 7))
                .OrderBy(_ => random.Next())
                .ToList();
            string[] sentences = [.. forms.Distinct(StringComparer.Ordinal).SelectMany(form => new[] { form, $"we said {form} twice, {form.ToLowerInvariant()}" })];

            var old = Dictation.Write([], Legacy043LibrarySelection.EnabledEntries(enabled, folder), sentences);
            var ours = Dictation.Write([], AdoptAndCompose(folder, enabled, rankTwinsByLogicalId: false).LibraryEntries, sentences);

            var differences = Enumerable.Range(0, sentences.Length).Where(i => old[i] != ours[i])
                .Select(i => $"\"{sentences[i]}\": 0.4.3 \"{old[i]}\", now \"{ours[i]}\"").ToList();
            Assert.True(differences.Count == 0,
                $"Round {round}: dictation writes differently.\n{string.Join("\n", differences)}\n{Describe(folder, enabled)}");
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

    // --- Markers by what the matcher can take for the same text (round 2, Astra A3) ---

    [Theory]
    [InlineData("kelvin", "\u212Aubernetes", "TeamCluster", "kubernetes", "Kubernetes")]          // one text, two keys
    [InlineData("kelvin, same written form", "\u212Aubernetes", "Kubernetes", "kubernetes", "Kubernetes")]
    [InlineData("final sigma, same written form", "bio\u03C2", "Bios", "bio\u03C3", "Bios")]      // one key, two texts
    [InlineData("final sigma", "bio\u03C2", "Custom Bios", "bio\u03C3", "Bios")]
    [InlineData("dotless i", "\u0131nfo", "Custom Info", "info", "Info")]                        // the fold alone
    [InlineData("dotless i, same written form", "\u0131nfo", "Info", "info", "Info")]
    [InlineData("case only, same written form", "Get Hub", "GitHub", "get hub", "GitHub")]
    public void A_legacy_row_the_matcher_can_take_for_a_built_ins_text_keeps_0_4_3s_finished_text(
        string scenario, string customSpoken, string customWritten, string shippedSpoken, string shippedWritten)
    {
        var builtIn = new DictionaryLibrary("github", "GitHub", "Microsoft", null, true, [DictionaryEntry.New(shippedSpoken, shippedWritten)]);
        var custom = new DictionaryLibrary("team", "Team", "Custom", null, false, [DictionaryEntry.New(customSpoken, customWritten)]);

        var (old, ours) = FinishedText([builtIn, custom], ["github", "team"], Variants(customSpoken, shippedSpoken));

        Assert.True(old.SequenceEqual(ours), $"{scenario}: 0.4.3 wrote [{string.Join(" | ", old)}], now [{string.Join(" | ", ours)}]");
    }

    [Fact]
    public void A_same_result_row_the_matcher_reads_differently_is_marked_too()
    {
        // Two custom libraries share "İnfo" (a dotted capital I), which the fold links with the built-in's "info" but the
        // matcher never takes for it. 0.4.3 applies alpha's, the first file. Marking only rows whose written form differs
        // would leave zeta's "Info" unmarked, let it take the spoken form from alpha, and change what dictation writes.
        var builtIn = new DictionaryLibrary("github", "GitHub", "Microsoft", null, true, [DictionaryEntry.New("info", "Info")]);
        var alpha = new DictionaryLibrary("alpha", "Alpha", "Custom", null, false, [DictionaryEntry.New("\u0130nfo", "Alpha")]);
        var zeta = new DictionaryLibrary("zeta", "Zeta", "Custom", null, false, [DictionaryEntry.New("\u0130nfo", "Info")]);

        var (old, ours) = FinishedText([builtIn, alpha, zeta], ["github", "alpha", "zeta"], ["\u0130nfo", "info", "an \u0130nfo desk"]);

        Assert.Equal(["Alpha", "Info", "an Alpha desk"], old);
        Assert.Equal(old, ours);
    }

    [Fact]
    public void A_same_result_row_the_expansion_guard_reads_differently_is_marked_too()
    {
        // The Kelvin-sign "Kafka" writes what the shipped "kafka" writes, and the matcher takes both for the same text, but
        // TextPostProcessor's guard against expanding a written form that already holds its spoken form compares ignoring
        // case, where the Kelvin sign is not a k. Supplied by the custom row, "kafka Streams" would become "kafka Streams
        // Streams".
        var builtIn = new DictionaryLibrary("github", "GitHub", "Microsoft", null, true, [DictionaryEntry.New("kafka", "kafka Streams")]);
        var custom = new DictionaryLibrary("team", "Team", "Custom", null, false, [DictionaryEntry.New("\u212Aafka", "kafka Streams")]);

        var (old, ours) = FinishedText([builtIn, custom], ["github", "team"], ["kafka Streams", "use kafka", "\u212Aafka"]);

        Assert.Equal("kafka Streams", old[0]);
        Assert.Equal(old, ours);
    }

    // What 0.4.3 and the first start of the library editor's version write for these sentences: 0.4.3's composition of the
    // enabled libraries (built-ins first, first wins by key) and the adopted composition, each through the real matcher.
    private static (string[] Old, string[] Ours) FinishedText(
        IReadOnlyList<DictionaryLibrary> libraries, IReadOnlyList<string> enabled, IReadOnlyList<string> sentences)
    {
        var old = DictionaryLibraryComposer.ComposeLibraries(LibraryPrecedence.Enabled(libraries, enabled));
        var catalogLibraries = libraries.Select(library => library.BuiltIn
            ? Lib.Committed(new LibraryContent(library.Id, true, library.Name, library.Category, null,
                [.. library.Entries.Select(e => Lib.Shipped(e.Pattern, e.Replacement, e.WholeWord))]))
            : new CatalogLibrary(Lib.CustomLibrary(library.Id, [.. library.Entries.Select(e => LibraryRow.Custom(TermValues.FromEntry(e)))]),
                LibraryFileState.Available, library.Id + ".csv", Lib.Hash((char)('a' + library.Id.Length % 6))))
            .ToList();
        var identities = catalogLibraries.Select(Lib.IdentityOf).ToList();
        var firstStart = new LibraryStateContext(false, false, false);
        var read = LibraryComposer.Instance.ReadLocalState(enabled, null, identities, firstStart);
        var adoption = LibraryComposer.Instance.PlanAdoption(Lib.Catalog(0, read, [.. catalogLibraries]), firstStart)!;
        var ours = LibraryComposition.Committed(Lib.Catalog(0, adoption.State, [.. catalogLibraries]), [], new GlossaryBudget(80));
        return (Dictation.Write([], old, sentences), Dictation.Write([], ours.LibraryEntries, sentences));
    }

    private static string[] Variants(params string[] forms) =>
        [.. forms.SelectMany(form => new[] { form, form.ToUpperInvariant(), form.ToLowerInvariant(), $"use {form} today" }).Distinct(StringComparer.Ordinal)];

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

    // A random legacy row, recording in `forms` the spoken forms whose texts the sentences must hold: its own, and the
    // shipped form it was drawn from, which a Kelvin-sign or dotless-i spelling of it may or may not match.
    private static DictionaryEntry RandomRow(Random random, IReadOnlyList<DictionaryEntry> shipped, List<string> forms)
    {
        var pick = random.Next(10);
        string spoken, written;
        if (pick < 6)
        {
            var source = shipped[random.Next(shipped.Count)];
            spoken = random.Next(5) switch
            {
                0 => source.Pattern.ToUpperInvariant(),
                1 => source.Pattern.Replace('k', '\u212A').Replace('K', '\u212A'),
                2 => source.Pattern.Replace('i', '\u0131'),
                _ => source.Pattern,
            };
            written = random.Next(3) switch
            {
                0 => source.Replacement,
                1 => source.Replacement + " Team",
                _ => "Custom" + random.Next(100),
            };
            forms.Add(source.Pattern);
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

        forms.Add(spoken);
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

    private static string Describe(string folder, IReadOnlyList<string> enabled)
    {
        var text = new StringBuilder();
        text.AppendLine("enabled: " + string.Join(", ", enabled));
        foreach (var path in Directory.GetFiles(folder, "*.csv").Order(StringComparer.OrdinalIgnoreCase))
        {
            text.AppendLine(Path.GetFileName(path) + ": " + File.ReadAllText(path).ReplaceLineEndings(" | "));
        }

        return text.ToString();
    }
}
