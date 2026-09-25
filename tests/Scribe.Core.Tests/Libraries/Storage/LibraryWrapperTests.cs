using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Infrastructure;
using Scribe.Core.Libraries;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;

namespace Scribe.Core.Tests.Libraries.Storage;

/// <summary>
/// The tested wrappers (J-12: <c>Import</c> and <c>Remove</c> through the journal, with the same refusals as a Save), the
/// interim parts that keep this branch running exactly as release 0.4.4 (J-15), and the id rules J copies from
/// <c>LibraryNaming</c> until the integration commit (contract 3.5.4, G9).
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
    public void With_the_interim_parts_the_library_seam_selects_exactly_as_0_4_3_including_a_hand_placed_twin()
    {
        // J-15: the public constructor, the interim parts, and 0.4.3's oracle over the same folder.
        var paths = new AppPaths(_fixture.Root);
        _fixture.Write("team-terms.csv", "# name: Team\npattern,replacement\nkube,K8s\nget hub,GitHub Enterprise\n");
        _fixture.Write("team-terms-2.csv", "pattern,replacement\nkube,Kubernetes\n");
        _fixture.Write("github.csv", "pattern,replacement\nprivate codename,Nightjar\nget hub,Twin GitHub\n");
        _fixture.Write("empty.csv", "pattern,replacement\n");
        string[][] sets = [["team-terms", "team-terms-2", "github"], ["github"], ["TEAM-TERMS"], ["github", "ai-terminology"], []];
        foreach (var enabled in sets)
        {
            _fixture.SaveEnabled(enabled);
            var service = new DictionaryLibraryService(paths, _fixture.Settings, NullLogger<DictionaryLibraryService>.Instance);

            Assert.Equal(Legacy043LibrarySelection.EnabledEntries(enabled, _fixture.LibrariesDir), service.GetEnabledLibraryEntries());
            Assert.Equal(Legacy043LibrarySelection.EnabledEntries(enabled, _fixture.LibrariesDir), service.GetEnabledLibraryEntries(enabled));
        }

        // The legacy view lists the twin under the id 0.4.3 loads it as, beside the built-in, and the empty file too.
        var libraries = new DictionaryLibraryService(paths, _fixture.Settings, NullLogger<DictionaryLibraryService>.Instance).GetLibraries();
        Assert.Contains(libraries, library => library is { Id: "github", BuiltIn: false, FileName: "github.csv" });
        Assert.Contains(libraries, library => library is { Id: "github", BuiltIn: true });
        Assert.Contains(libraries, library => library is { Id: "empty", BuiltIn: false } && library.Entries.Count == 0);

        // The interim composer writes no state row, so nothing reads the enabled list any other way yet.
        Assert.Null(_fixture.Row(LibrarySettingKeys.State));
    }

    [Fact]
    public void With_the_interim_parts_a_wrapper_commit_writes_the_generation_and_leaves_the_state_row_and_list_alone()
    {
        var paths = new AppPaths(_fixture.Root);
        _fixture.SaveEnabled("github", "gone-library");
        var service = new DictionaryLibraryService(paths, _fixture.Settings, NullLogger<DictionaryLibraryService>.Instance);

        var imported = service.Import(LibraryStorageFixture.Csv("Imported", ("i", "I")), null);

        Assert.Equal(1, _fixture.StoredGeneration);
        Assert.Null(_fixture.Row(LibrarySettingKeys.State));
        Assert.Equal(["github", "gone-library"], _fixture.StoredEnabledList());
        Assert.DoesNotContain(service.GetEnabledLibraryEntries(), entry => entry.Replacement == "I");
        Assert.True(_fixture.Exists("journal/state.witness"));
        _ = imported;
    }

    [Theory]
    [InlineData("Team terms", "team-terms")]
    [InlineData("  Team   Terms  ", "team-terms")]
    [InlineData("C# & .NET", "c-net")]
    [InlineData("Zulu Notes 2", "zulu-notes-2")]
    [InlineData("Crème brûlée", "crème-brûlée")]
    [InlineData("---", "library")]
    [InlineData("", "library")]
    [InlineData("İstanbul", "İstanbul")]
    [InlineData("Team terms (changed outside Scribe)", "team-terms-changed-outside-scribe")]
    public void The_slug_rule_is_release_0_4_4s_Slugify(string name, string slug)
    {
        // Contract 3.5.4's vectors, which D's fixture holds too; each answer is also checked against the rule itself.
        Assert.Equal(slug, InterimLibraryNaming.Slug(name));
        Assert.Equal(slug, Reference(name));
    }

    [Fact]
    public void The_slug_fixture_agrees_when_it_is_present()
    {
        // tests/fixtures/libraries/slugs.json is the deciders' file; once it lands, J's copy of the rule reads it too.
        var path = Path.Combine(RepositoryRoot(), "tests", "fixtures", "libraries", "slugs.json");
        if (!File.Exists(path))
        {
            return;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var vector in document.RootElement.EnumerateArray())
        {
            if (vector.TryGetProperty("name", out var name) && vector.TryGetProperty("slug", out var slug))
            {
                Assert.Equal(slug.GetString(), InterimLibraryNaming.Slug(name.GetString()!));
            }
        }
    }

    [Fact]
    public void New_and_remapped_ids_take_the_next_free_suffix_without_case()
    {
        var taken = new HashSet<string>(["custom-team", "CUSTOM-TEAM-2"], StringComparer.OrdinalIgnoreCase);

        Assert.Equal("custom-team-3", InterimLibraryNaming.NewCustomId("Team", taken.Contains));
        Assert.Equal("custom-github", CustomLibraryStore.RemapId("github", new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
        Assert.Equal("custom-GitHub-2", CustomLibraryStore.RemapId("GitHub", new HashSet<string>(["custom-github"], StringComparer.OrdinalIgnoreCase)));
        Assert.Equal("Team terms (changed outside Scribe)", InterimLibraryNaming.ChangedOutsideName("Team terms"));
    }

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
