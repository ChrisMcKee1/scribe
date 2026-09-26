using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Infrastructure;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;

namespace Scribe.Core.Tests.Libraries;

/// <summary>
/// The round 3 contract rules the shared surface carries itself: a request scope bound to content (review finding A12),
/// a library's separate logical, precedence and legacy names (A15, A16), and precedence by physical file name, checked
/// against what 0.4.3 applies (<see cref="Legacy043LibrarySelection"/>).
/// </summary>
public sealed class LibraryScopeAndIdentityTests : IDisposable
{
    private static readonly LibraryContentHash H1 = new(new string('1', 64));
    private static readonly LibraryContentHash H2 = new(new string('2', 64));

    private readonly string _root = Path.Combine(Path.GetTempPath(), "scribe-lib-identity-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void A_request_admitted_for_old_content_is_not_covered_once_the_new_content_is_permitted()
    {
        // A12: "team" admitted at H1; replaced outside Scribe by H2, which revokes it; then the user permits H2.
        var admitted = Scope(("team", H1), ("github", null));
        var afterReplacement = Scope(("github", null));
        var afterPermittingH2 = Scope(("team", H2), ("github", null));

        Assert.True(admitted.Covers(admitted));
        Assert.False(afterReplacement.Covers(admitted));
        Assert.False(afterPermittingH2.Covers(admitted));
        Assert.True(afterPermittingH2.Covers(Scope(("TEAM", H2))));
    }

    [Fact]
    public void Covering_ignores_what_the_current_scope_adds_and_compares_ids_without_case()
    {
        var admitted = Scope(("Team", H1));

        Assert.True(Scope(("team", H1), ("github", null), ("other", H2)).Covers(admitted));
        Assert.True(admitted.Covers(AiVocabularyScope.None));
        Assert.True(AiVocabularyScope.None.Covers(AiVocabularyScope.None));
        Assert.False(AiVocabularyScope.None.Covers(admitted));
    }

    [Fact]
    public void A_built_in_is_bound_to_its_edits_document_or_to_having_none()
    {
        var shippedOnly = Scope(("github", null));
        var edited = Scope(("github", H1));

        Assert.True(Scope(("github", null)).Covers(shippedOnly));
        Assert.False(edited.Covers(shippedOnly));
        Assert.False(shippedOnly.Covers(edited));
        Assert.Equal(H1, edited.PermittedContent["GITHUB"]);
        Assert.Null(shippedOnly.PermittedContent["github"]);
    }

    [Fact]
    public void A_scope_keeps_its_own_copy_and_drops_blank_ids()
    {
        var permitted = new List<KeyValuePair<string, LibraryContentHash?>> { new(" team ", H1), new(" ", H2), new("TEAM", H2) };
        var scope = new AiVocabularyScope(7, permitted);

        permitted.Clear();

        Assert.Equal(7, scope.Generation);
        Assert.Single(scope.PermittedContent);
        Assert.Equal(H2, scope.PermittedContent["team"]);
        Assert.Contains("Team", scope.PermittedLibraryIds);
    }

    [Fact]
    public void A_remapped_file_keeps_its_file_name_and_the_id_older_builds_load_it_as()
    {
        var twin = new LibraryIdentity("custom-github", BuiltIn: false, "github.csv");
        var builtIn = new LibraryIdentity("github", BuiltIn: true, FileName: null);
        var created = LibraryIdentity.NewCustom("custom-release-notes");

        Assert.Equal("github", twin.LegacyId);
        Assert.Equal("github.csv", twin.PrecedenceName);
        Assert.Equal("github", builtIn.LegacyId);
        Assert.Null(builtIn.PrecedenceName);
        Assert.Equal(twin.LegacyId, builtIn.LegacyId, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("custom-release-notes.csv", created.FileName);
        Assert.Equal("custom-release-notes", created.LegacyId);
        Assert.Equal("Zulu Notes", new LibraryIdentity("Zulu Notes", false, "Zulu Notes.csv").LegacyId);
    }

    [Fact]
    public void A_remapped_file_ranks_by_its_physical_name_where_0_4_3_ranked_it()
    {
        // A16: 0.4.3 loads epsilon.csv before github.csv, so epsilon supplies "project token". Ranking the hand-placed
        // github.csv by its logical id, custom-github.csv, would put it first and hand the spoken form to the twin.
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "epsilon.csv"), "pattern,replacement\nproject token,Epsilon\n");
        File.WriteAllText(Path.Combine(_root, "github.csv"), "pattern,replacement\nproject token,Twin\nzeta only,Zeta\n");
        var old = Legacy043LibrarySelection.EnabledEntries(["epsilon", "github"], _root);

        var epsilon = new DictionaryLibrary("epsilon", "Epsilon", "Custom", null, false, [new(0, "project token", "Epsilon")]);
        var twin = new DictionaryLibrary("custom-github", "Twin", "Custom", null, false,
            [new(0, "project token", "Twin"), new(0, "zeta only", "Zeta")]) { FileName = "github.csv" };
        var composed = DictionaryLibraryComposer.ComposeLibraries([twin, epsilon]);
        var byLogicalId = DictionaryLibraryComposer.ComposeLibraries([twin with { FileName = null }, epsilon]);

        Assert.Equal("Epsilon", Winner(old, "project token"));
        Assert.Equal(Winner(old, "project token"), Winner(composed, "project token"));
        Assert.Equal(Winner(old, "zeta only"), Winner(composed, "zeta only"));
        Assert.Equal("Twin", Winner(byLogicalId, "project token"));
        Assert.Equal(new[] { "epsilon", "custom-github" }, LibraryPrecedence.Order([twin, epsilon]).Select(l => l.Id));
        Assert.Equal(
            new[] { "epsilon.csv", "github.csv" },
            LibraryPrecedence.Order(
                new[] { (File: "github.csv", Id: "custom-github"), (File: "epsilon.csv", Id: "epsilon") },
                row => row.Id, _ => false, row => row.File).Select(row => row.File));
    }

    [Fact]
    public void Without_a_file_name_every_library_ranks_exactly_as_before()
    {
        string[] ids = ["team-terms", "team-terms-2", "Zulu Notes", "alpha", "a.b", "a-b", "a b"];
        var libraries = ids.Select(id => new DictionaryLibrary(id, id, "Custom", null, false, [])).ToList();

        var withDefault = LibraryPrecedence.Order(libraries).Select(l => l.Id).ToList();
        var withExplicit = LibraryPrecedence.Order(libraries.Select(l => l with { FileName = l.Id + ".csv" })).Select(l => l.Id).ToList();
        var fourArguments = ids.OrderBy(id => id, Comparer<string>.Create((a, b) => LibraryPrecedence.Compare(a, false, b, false))).ToList();

        Assert.Equal(withDefault, withExplicit);
        Assert.Equal(withDefault, fourArguments);
    }

    [Fact]
    public void A_draft_library_carries_its_file_name()
    {
        var content = new LibraryContent("custom-github", false, "Twin", "Custom", null, []);

        Assert.Null(new DraftLibrary(content, LibraryOrigin.Existing, LibraryFileState.Available).FileName);
        Assert.Equal("github.csv", new DraftLibrary(content, LibraryOrigin.Existing, LibraryFileState.Available, FileName: "github.csv").FileName);
    }

    [Fact]
    public void The_0_4_3_oracle_loads_a_twin_under_the_built_in_id_and_one_flag_turns_both_on()
    {
        // The shape A15 guards against: 0.4.3 has one enabled flag per id, so "github" turns on the built-in and a
        // hand-placed github.csv together, and "custom-github" means nothing to it.
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "github.csv"), "pattern,replacement\nprivate codename,Nightjar\n");

        Assert.Contains(Legacy043LibrarySelection.Libraries(_root), library => library is { Id: "github", BuiltIn: false });
        Assert.Contains(Legacy043LibrarySelection.EnabledEntries(["github"], _root), entry => entry.Replacement == "Nightjar");
        Assert.DoesNotContain(Legacy043LibrarySelection.EnabledEntries(["custom-github"], _root), entry => entry.Replacement == "Nightjar");
    }

    [Fact]
    public void The_0_4_3_oracle_agrees_with_the_service_for_folders_without_a_remap()
    {
        // The service's release 0.4.4 seam still selects libraries exactly as 0.4.3 did (W1a's golden); on a folder this
        // version has not remapped, the oracle must say the same, or it is not an oracle. (The committed vocabulary composes
        // the same winners in tiers, authored rows first, which LibraryWrapperTests' upgrade case checks by winner.)
        var paths = new AppPaths(_root);
        Directory.CreateDirectory(paths.LibrariesDir);
        File.WriteAllText(Path.Combine(paths.LibrariesDir, "team-terms.csv"), "# name: Team\npattern,replacement\nkube,K8s\nget hub,GitHub Enterprise\n");
        File.WriteAllText(Path.Combine(paths.LibrariesDir, "team-terms-2.csv"), "pattern,replacement\nkube,Kubernetes\n");
        File.WriteAllText(Path.Combine(paths.LibrariesDir, "empty.csv"), "pattern,replacement\n");

        string[][] enabledSets = [["team-terms", "team-terms-2", "github"], ["TEAM-TERMS"], ["github", "ai-terminology"], []];
        foreach (var enabled in enabledSets)
        {
            var settings = new EnabledListSettings(enabled);
            var service = new DictionaryLibraryService(paths, settings, NullLogger<DictionaryLibraryService>.Instance);

            Assert.Equal(Legacy043LibrarySelection.EnabledEntries(enabled, paths.LibrariesDir), service.GetEnabledLibraryEntries(enabled));
        }
    }

    private static AiVocabularyScope Scope(params (string Id, LibraryContentHash? Content)[] permitted) =>
        new(3, permitted.Select(pair => new KeyValuePair<string, LibraryContentHash?>(pair.Id, pair.Content)));

    private static string? Winner(IReadOnlyList<DictionaryEntry> entries, string spoken) =>
        entries.FirstOrDefault(entry => string.Equals(entry.Pattern.Trim(), spoken, StringComparison.OrdinalIgnoreCase))?.Replacement;

    private sealed class EnabledListSettings(IReadOnlyList<string> enabled) : ISettingsRepository
    {
        public bool LastLoadFailed => false;

        public AppSettings Load()
        {
            var settings = AppSettings.CreateDefault();
            settings.EnabledDictionaryLibraryIds.Clear();
            settings.EnabledDictionaryLibraryIds.AddRange(enabled);
            return settings;
        }

        public void Save(AppSettings settings) => throw new NotSupportedException();

        public AppSettings Update(Action<AppSettings> mutate) => throw new NotSupportedException();

        public AppSettings Update(Action<AppSettings> mutate, long revision, out bool superseded) => throw new NotSupportedException();

        public void SaveBundle(AppSettings settings, IReadOnlyList<DictionaryEntry>? dictionaryEntries, IReadOnlyList<Snippet>? snippets, long aiCleanupIntent = 0) =>
            throw new NotSupportedException();

        public string? Get(string key) => null;

        public void Set(string key, string value) => throw new NotSupportedException();
    }
}
