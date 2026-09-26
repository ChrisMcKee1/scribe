using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Infrastructure;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests.Libraries.Storage;

/// <summary>
/// The tested wrappers (J-12: <c>Import</c> and <c>Remove</c> through the journal, with the same refusals as a Save), the
/// release 0.4.4 library seam over the real parts at the upgrade (J-15, as the integration commit leaves it), and the id
/// rules J takes from <c>LibraryNaming</c> (contract 3.5.4, G9).
/// </summary>
public sealed class LibraryWrapperTests : IDisposable
{
    private readonly LibraryStorageFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void Import_creates_a_library_through_the_journal_that_is_off_and_not_sent()
    {
        var service = _fixture.Service();

        var imported = service.Import(LibraryStorageFixture.Csv("My Terms", ("foo bar", "FooBar")), "fallback");

        Assert.Equal("my-terms", imported.Id);
        Assert.True(_fixture.Exists("my-terms.csv"));
        Assert.Equal(1, _fixture.StoredGeneration);
        Assert.Empty(Directory.GetFiles(_fixture.Paths.LibraryJournalDir, "*.json"));
        var catalog = service.LoadCatalog();
        Assert.DoesNotContain("my-terms", catalog.LocalState.EnabledIds);
        Assert.False(ContractComposer.IsPermitted(catalog.LocalState, "my-terms", false, catalog.Find("my-terms")!.ContentHash));
        Assert.Equal(_fixture.HashOf("my-terms.csv"), catalog.LocalState.AcceptedContent["my-terms"]);
    }

    [Fact]
    public void Remove_moves_the_library_into_Recently_deleted_and_refuses_a_built_in()
    {
        var service = _fixture.Service();
        var imported = service.Import(LibraryStorageFixture.Csv("Temp", ("a", "B")), null);
        var bytes = File.ReadAllBytes(_fixture.PathOf(imported.Id + ".csv"));

        service.Remove(imported.Id);

        Assert.False(_fixture.Exists(imported.Id + ".csv"));
        Assert.DoesNotContain(service.GetLibraries(), library => library.Id == imported.Id);
        var entry = Assert.Single(Directory.GetFiles(_fixture.Paths.LibraryDeletedDir));
        Assert.Equal(bytes, File.ReadAllBytes(entry));
        Assert.Equal(imported.Id, Assert.Single(service.LoadCatalog().RecentlyDeleted).OriginalId);
        Assert.Throws<InvalidOperationException>(() => service.Remove("microsoft-azure"));

        // Removing a library that is not there changes nothing, as it never did.
        var generation = _fixture.StoredGeneration;
        service.Remove("never-existed");
        Assert.Equal(generation, _fixture.StoredGeneration);
    }

    [Fact]
    public void The_wrappers_refuse_while_a_Save_is_live()
    {
        _fixture.Write("team.csv", LibraryStorageFixture.Csv("Team", ("kube", "Kubernetes")));
        var service = _fixture.Service();
        var catalog = service.LoadCatalog();
        var prepared = service.PrepareSave(Changes.Of(catalog, writes: [Changes.Create(LibraryStorageFixture.Content("custom-x", "X", ("x", "X")))]));
        Assert.Equal(LibraryPrepareStatus.Prepared, prepared.Status);

        Assert.Equal("A library change is still being saved.", Assert.Throws<InvalidOperationException>(() => service.Import("a,A\n", "a")).Message);
        Assert.Equal("A library change is still being saved.", Assert.Throws<InvalidOperationException>(() => service.Remove("team")).Message);
        Assert.Equal(LibraryPrepareStatus.Busy, service.PrepareSave(Changes.Of(catalog)).Status);

        service.CompleteSave(prepared.Save!);
        Assert.NotNull(service.Import("a,A\n", "a"));
    }

    [Fact]
    public void The_wrappers_refuse_on_defaults_and_with_a_newer_state()
    {
        _fixture.Write("team.csv", LibraryStorageFixture.Csv("Team", ("kube", "Kubernetes")));
        _fixture.Context = new LibraryStateContext(RunningOnDefaults: true, DatabaseRepaired: false, GenerationStored: false);
        var onDefaults = _fixture.Service();
        Assert.Equal("Save your settings first.", Assert.Throws<InvalidOperationException>(() => onDefaults.Import("a,A\n", "a")).Message);
        Assert.Equal("Save your settings first.", Assert.Throws<InvalidOperationException>(() => onDefaults.Remove("team")).Message);

        _fixture.Context = default;
        _fixture.Settings.Set(LibrarySettingKeys.State, "{\"version\":2}");
        var newer = _fixture.Service();
        Assert.Equal("Libraries were changed by a newer version of Scribe.", Assert.Throws<InvalidOperationException>(() => newer.Import("a,A\n", "a")).Message);
        Assert.Equal("Libraries were changed by a newer version of Scribe.", Assert.Throws<InvalidOperationException>(() => newer.Remove("team")).Message);
        Assert.Equal(LibraryPrepareStatus.ReadOnly, newer.PrepareSave(Changes.Of(newer.LoadCatalog(), writes: [Changes.Create(LibraryStorageFixture.Content("custom-x", "X", ("x", "X")))])).Status);
        Assert.Equal("{\"version\":2}", _fixture.Row(LibrarySettingKeys.State));
    }

    [Fact]
    public void At_the_upgrade_the_library_seam_and_the_committed_vocabulary_write_what_0_4_3_wrote_including_a_hand_placed_twin()
    {
        // J-15 with the real parts, through the public constructor: for each enabled list 0.4.3 could have stored, a first
        // start over the same folder. Release 0.4.4's seam (the ids overload, which dictation calls until the vocabulary
        // source replaces it) composes exactly 0.4.3's entries; the committed vocabulary, composed in tiers with the legacy
        // markers the first start adds, gives 0.4.3's winner for every spoken form (decision 1's upgrade guarantee).
        string[][] sets = [["team-terms", "team-terms-2", "github"], ["github"], ["TEAM-TERMS"], ["github", "ai-terminology"], []];
        foreach (var enabled in sets)
        {
            using var upgrade = new LibraryStorageFixture();
            SeedUpgradeFolder(upgrade);
            upgrade.SaveEnabled(enabled);
            var service = new DictionaryLibraryService(upgrade.Paths, upgrade.Settings, NullLogger<DictionaryLibraryService>.Instance);
            var legacy = Legacy043LibrarySelection.EnabledEntries(enabled, upgrade.LibrariesDir);

            Assert.Equal(legacy, service.GetEnabledLibraryEntries(enabled));
            Assert.Equal(Winners(legacy), Winners(service.GetEnabledLibraryEntries()));
            Assert.Equal(Winners(legacy), Winners(service.Current.Entries));

            // The first start recorded the upgrade: a state row now carries the enabled state, and the list it projects for
            // older builds is the one 0.4.3 had.
            Assert.NotNull(upgrade.Row(LibrarySettingKeys.State));
            Assert.Equal(
                enabled.ToHashSet(StringComparer.OrdinalIgnoreCase),
                upgrade.StoredEnabledList().ToHashSet(StringComparer.OrdinalIgnoreCase));
        }

        // The legacy view lists the twin under the id 0.4.3 loads it as, beside the built-in, and the empty file too.
        SeedUpgradeFolder(_fixture);
        var libraries = new DictionaryLibraryService(_fixture.Paths, _fixture.Settings, NullLogger<DictionaryLibraryService>.Instance).GetLibraries();
        Assert.Contains(libraries, library => library is { Id: "github", BuiltIn: false, FileName: "github.csv" });
        Assert.Contains(libraries, library => library is { Id: "github", BuiltIn: true });
        Assert.Contains(libraries, library => library is { Id: "empty", BuiltIn: false } && library.Entries.Count == 0);
    }

    [Fact]
    public void A_wrapper_import_records_the_library_off_and_kept_from_AI_cleanup_and_leaves_the_list_alone()
    {
        _fixture.SaveEnabled("github", "gone-library");
        var service = new DictionaryLibraryService(_fixture.Paths, _fixture.Settings, NullLogger<DictionaryLibraryService>.Instance);
        service.LoadCatalog();
        var adopted = _fixture.StoredGeneration;

        var imported = service.Import(LibraryStorageFixture.Csv("Imported", ("i", "I")), null);

        // One more generation, the state row recording the library's content, and the list older builds read unchanged:
        // an imported library starts off and kept from AI cleanup (Decision 2), so it is not in the projection.
        Assert.Equal(adopted + 1, _fixture.StoredGeneration);
        var catalog = service.LoadCatalog();
        Assert.Equal(_fixture.HashOf(imported.Id + ".csv"), catalog.LocalState.AcceptedContent[imported.Id]);
        Assert.DoesNotContain(imported.Id, catalog.LocalState.EnabledIds);
        Assert.False(AiVocabularyPolicy.IsPermitted(catalog.LocalState, imported.Id, false, catalog.Find(imported.Id)!.ContentHash));
        Assert.Equal(["github", "gone-library"], _fixture.StoredEnabledList());
        Assert.DoesNotContain(service.GetEnabledLibraryEntries(), entry => entry.Replacement == "I");
        Assert.True(_fixture.Exists("journal/state.witness"));

        // The file is a managed library of this version, written by the library CSV codec: it carries the format marker.
        Assert.Contains("# scribe-format: 2", _fixture.Read(imported.Id + ".csv"), StringComparison.Ordinal);
    }

    [Fact]
    public void An_import_takes_release_0_4_4s_ids_through_LibraryNaming_for_every_slug_vector()
    {
        // tests/fixtures/libraries/slugs.json is the deciders' file; the Import wrapper derives its id from the name by
        // LibraryNaming's slug and suffix rules, unprefixed as release 0.4.4 did (review finding G9), and each answer is
        // also checked against the rule contract 3.5.4 pastes.
        var checkedVectors = 0;
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "tests", "fixtures", "libraries", "slugs.json")));
        foreach (var vector in document.RootElement.GetProperty("slugs").EnumerateArray())
        {
            var name = vector.GetProperty("name").GetString()!;
            var slug = vector.GetProperty("slug").GetString()!;
            Assert.Equal(slug, Reference(name));
            if (name.Trim().Length == 0)
            {
                continue;
            }

            using var fresh = new LibraryStorageFixture();
            var service = fresh.Service();
            var imported = service.Import("# name: " + name + "\npattern,replacement\nkube,Kubernetes\n", null);
            Assert.Equal(slug, imported.Id);
            Assert.Equal(slug + "-2", service.Import("# name: " + name + "\npattern,replacement\nkube,K8s\n", null).Id);
            checkedVectors++;
        }

        Assert.True(checkedVectors >= 8, $"only {checkedVectors} slug vectors were imported");
    }

    [Fact]
    public void An_import_with_no_name_is_an_imported_word_pack_and_never_takes_a_built_in_id()
    {
        var service = _fixture.Service();

        var unnamed = service.Import("pattern,replacement\nkube,Kubernetes\n", "   ");
        var azure = service.Import("# name: Microsoft Azure\npattern,replacement\nx,Y\n", null);

        Assert.Equal(LibraryNaming.ImportedLibraryBaseName, unnamed.Name);
        Assert.Equal("imported-word-pack", unnamed.Id);
        Assert.Equal("microsoft-azure-2", azure.Id);
    }

    [Fact]
    public void Hand_placed_files_and_kept_versions_take_their_ids_from_LibraryNaming()
    {
        // The remap of a stem that is a built-in id, a newcomer beside a recorded id, and a kept outside version's id are
        // LibraryNaming's rules (contract 3.5.4, 6.3), each compared without case.
        var ids = CustomLibraryStore.AssignIds(["GitHub.csv", "custom-github.csv"], LibraryFileIds.Parse(null), []);
        Assert.Equal("custom-GitHub-2", ids["GitHub.csv"]);
        Assert.Equal(LibraryNaming.RemapId("GitHub", ["custom-github"]), ids["GitHub.csv"]);
        Assert.Equal("custom-github", ids["custom-github.csv"]);

        var recorded = LibraryFileIds.Parse("{\"version\":1,\"files\":{\"github.csv\":\"custom-github\"}}");
        var later = CustomLibraryStore.AssignIds(["github.csv", "custom-github.csv"], recorded, []);
        Assert.Equal("custom-github", later["github.csv"]);
        Assert.Equal(LibraryNaming.Unique("custom-github", id => id.Equals("custom-github", StringComparison.OrdinalIgnoreCase)), later["custom-github.csv"]);
        Assert.Equal("custom-github-2", later["custom-github.csv"]);

        Assert.Equal("Team terms (changed outside Scribe)", LibraryNaming.ChangedOutsideName("Team terms"));
    }

    private static void SeedUpgradeFolder(LibraryStorageFixture fixture)
    {
        fixture.Write("team-terms.csv", "# name: Team\npattern,replacement\nkube,K8s\nget hub,GitHub Enterprise\n");
        fixture.Write("team-terms-2.csv", "pattern,replacement\nkube,Kubernetes\n");
        fixture.Write("github.csv", "pattern,replacement\nprivate codename,Nightjar\nget hub,Twin GitHub\n");
        fixture.Write("empty.csv", "pattern,replacement\n");
    }

    // What dictation writes for each spoken form: the winners, by key, whatever order a composition lists them in.
    private static IReadOnlyList<string> Winners(IEnumerable<DictionaryEntry> entries) =>
        [.. entries.Select(entry => $"{LibraryTermKey.From(entry.Pattern).Value.ToUpperInvariant()} => {entry.Replacement} ({entry.WholeWord})")
            .Order(StringComparer.Ordinal)];

    // Contract 3.5.4's Slugify, as pasted there.
    private static string Reference(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length);
        var pendingDash = false;
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                if (pendingDash && sb.Length > 0)
                {
                    sb.Append('-');
                }

                sb.Append(ch);
                pendingDash = false;
            }
            else
            {
                pendingDash = true;
            }
        }

        var slug = sb.ToString();
        return slug.Length == 0 ? "library" : slug;
    }

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return root.FullName;
    }
}
