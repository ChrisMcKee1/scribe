using System.Text;
using Scribe.Core.Libraries;

namespace Scribe.Core.Tests.Libraries.Storage;

/// <summary>
/// A library's three names and what older builds do with what this version stores (contract 2.15 and 6.3): a hand-placed
/// file remapped away from a built-in id (J-11, A16, A17), content replaced outside Scribe (J-16, A4), and the
/// downgrade-safe projection an older build reads, through <see cref="Legacy043LibrarySelection"/> over the real folder,
/// after a Save, after an adoption and while a Save's files are still being installed (J-20, J-20b; A15, A18).
/// </summary>
public sealed class LibraryIdentityStorageTests : IDisposable
{
    private const string Canary = "Nightjar";
    private readonly LibraryStorageFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private IReadOnlyList<Scribe.Core.Models.DictionaryEntry> OlderBuildApplies() =>
        Legacy043LibrarySelection.EnabledEntries(_fixture.StoredEnabledList(), _fixture.LibrariesDir);

    private void SeedTwin() => _fixture.Write("github.csv", LibraryStorageFixture.Csv("Team GitHub", ("private codename", Canary)));

    [Fact]
    public void A_hand_placed_file_with_a_built_in_stem_is_remapped_and_keeps_its_id_while_other_files_come_and_go()
    {
        // J-11: custom-github is taken by a file of its own, so the twin is custom-github-2, recorded at the first commit.
        SeedTwin();
        _fixture.Write("custom-github.csv", LibraryStorageFixture.Csv("Other", ("o", "O")));
        _fixture.SaveEnabled("github");
        var service = _fixture.Service();

        var first = service.LoadCatalog();

        var twin = first.Find("custom-github-2")!;
        Assert.False(twin.Content.BuiltIn);
        Assert.Equal("github.csv", twin.FileName);
        Assert.Equal("custom-github.csv", first.Find("custom-github")!.FileName);
        Assert.True(first.Find("github")!.Content.BuiltIn);
        Assert.Contains("\"github.csv\":\"custom-github-2\"", _fixture.Row(LibrarySettingKeys.FileIds)!, StringComparison.Ordinal);

        // At the first start the twin takes the enabled state github had, and AI permission as an existing library.
        Assert.Contains("custom-github-2", first.LocalState.EnabledIds);
        Assert.True(ContractComposer.IsPermitted(first.LocalState, "custom-github-2", false, twin.ContentHash));

        File.Delete(_fixture.PathOf("custom-github.csv"));
        _fixture.Restart();
        Assert.NotNull(_fixture.Service().LoadCatalog().Find("custom-github-2"));
    }

    [Fact]
    public void A_recorded_id_stays_with_its_file_when_a_later_file_has_that_id_for_a_stem()
    {
        // Round 2, A10: github.csv is recorded as custom-github at the first commit; a hand-placed custom-github.csv appears
        // later. The recorded mapping of a file that still exists wins, and the newcomer takes the next free suffix.
        SeedTwin();
        _fixture.SaveEnabled("github");
        var first = _fixture.Service().LoadCatalog();
        Assert.Equal("github.csv", first.Find("custom-github")!.FileName);
        Assert.Contains("custom-github", first.LocalState.EnabledIds);
        Assert.True(ContractComposer.IsPermitted(first.LocalState, "custom-github", false, first.Find("custom-github")!.ContentHash));
        _fixture.Write("custom-github.csv", LibraryStorageFixture.Csv("Newcomer", ("newcomer term", "Newcomer")));

        _fixture.Restart();
        var later = _fixture.Service().LoadCatalog();

        Assert.Equal("github.csv", later.Find("custom-github")!.FileName);
        Assert.Equal("custom-github.csv", later.Find("custom-github-2")!.FileName);
        Assert.Contains("custom-github", later.LocalState.EnabledIds);
        Assert.True(ContractComposer.IsPermitted(later.LocalState, "custom-github", false, later.Find("custom-github")!.ContentHash));
        Assert.DoesNotContain("custom-github-2", later.LocalState.EnabledIds);
        Assert.False(ContractComposer.IsPermitted(later.LocalState, "custom-github-2", false, later.Find("custom-github-2")!.ContentHash));
        Assert.Contains("\"custom-github.csv\":\"custom-github-2\"", _fixture.Row(LibrarySettingKeys.FileIds)!, StringComparison.Ordinal);
        Assert.Contains("\"github.csv\":\"custom-github\"", _fixture.Row(LibrarySettingKeys.FileIds)!, StringComparison.Ordinal);

        // The older build loads github.csv as github and custom-github.csv as custom-github: the list keeps both twins on,
        // as before this version, and never names the newcomer, which starts off.
        Assert.Contains(OlderBuildApplies(), entry => entry.Replacement == Canary);
        Assert.DoesNotContain(OlderBuildApplies(), entry => entry.Replacement == "Newcomer");
        Assert.DoesNotContain(_fixture.Service().Current.Entries, entry => entry.Replacement == "Newcomer");

        // Both stay put across a Save and a restart; turned on and permitted, the newcomer reaches the older build too.
        var service = _fixture.Service();
        var catalog = service.LoadCatalog();
        Changes.Save(service, _fixture.Settings, Changes.Of(catalog, state: Changes.With(catalog.LocalState, enable: ["custom-github-2"], ai: [("custom-github-2", true)])));
        _fixture.Restart();
        var reloaded = _fixture.Service().LoadCatalog();
        Assert.Equal("github.csv", reloaded.Find("custom-github")!.FileName);
        Assert.Equal("custom-github.csv", reloaded.Find("custom-github-2")!.FileName);
        Assert.Contains(OlderBuildApplies(), entry => entry.Replacement == "Newcomer");
        Assert.Contains(OlderBuildApplies(), entry => entry.Replacement == Canary);
    }

    [Fact]
    public void A_list_entry_no_file_answers_to_selects_nothing_even_when_a_remapped_file_took_it_as_its_logical_id()
    {
        // Round 2, A1: the list keeps custom-github from a custom-github.csv that was removed; a hand-placed github.csv is
        // then remapped to the logical id custom-github. 0.4.3 selects neither, and neither does this build's legacy seam.
        _fixture.SaveEnabled("custom-github");
        SeedTwin();

        // The interim parts first: they adopt nothing, so the stored list still names custom-github when they read it.
        foreach (var service in (Scribe.Core.PostProcessing.DictionaryLibraryService[])
                 [new(_fixture.Paths, _fixture.Settings, _fixture.Log), _fixture.Service()])
        {
            Assert.Equal(["custom-github"], _fixture.StoredEnabledList());
            var catalog = service.LoadCatalog();
            Assert.Equal("github.csv", catalog.Find("custom-github")!.FileName);
            Assert.DoesNotContain(Legacy043LibrarySelection.EnabledEntries(["custom-github"], _fixture.LibrariesDir), entry => entry.Replacement == Canary);
            Assert.DoesNotContain(service.GetEnabledLibraryEntries(["custom-github"]), entry => entry.Replacement == Canary);
            Assert.DoesNotContain(service.GetEnabledLibraryEntries(), entry => entry.Replacement == Canary);
            Assert.DoesNotContain(service.Current.Entries, entry => entry.Replacement == Canary);
            Assert.DoesNotContain(service.Current.AiEntries, entry => entry.Replacement == Canary);
        }

        // The legacy id still selects it, as 0.4.3 does.
        Assert.Contains(_fixture.Service().GetEnabledLibraryEntries(["github"]), entry => entry.Replacement == Canary);
        Assert.Contains(Legacy043LibrarySelection.EnabledEntries(["github"], _fixture.LibrariesDir), entry => entry.Replacement == Canary);
    }

    [Fact]
    public void A_twin_placed_after_the_first_start_starts_off_and_is_not_sent()
    {
        // J-11 (decision 5 as Grok reviewed it): a file remapped at a later start is a new file.
        _fixture.SaveEnabled("data-and-ai");
        _fixture.Service().LoadCatalog();
        _fixture.Write("data-and-ai.csv", LibraryStorageFixture.Csv("Placed later", ("later", "Later")));

        var catalog = _fixture.Service().LoadCatalog();

        Assert.NotNull(catalog.Find("custom-data-and-ai"));
        Assert.DoesNotContain("custom-data-and-ai", catalog.LocalState.EnabledIds);
        Assert.Contains("data-and-ai", catalog.LocalState.EnabledIds);
        Assert.False(catalog.LocalState.AiPermissions["custom-data-and-ai"]);
    }

    [Fact]
    public void A_remapped_file_ranks_by_its_physical_name_so_dictation_writes_what_0_4_3_writes_and_its_save_writes_that_file()
    {
        // J-11 (A16): epsilon.csv and github.csv both supply "project token"; 0.4.3 writes Epsilon.
        _fixture.Write("epsilon.csv", LibraryStorageFixture.Csv("Epsilon", ("project token", "Epsilon")));
        _fixture.Write("github.csv", LibraryStorageFixture.Csv("Twin", ("project token", "Twin"), ("zeta only", "Zeta")));
        _fixture.SaveEnabled("epsilon", "github");
        var service = _fixture.Service();
        var catalog = service.LoadCatalog();

        Assert.Equal("Epsilon", Winner(Legacy043LibrarySelection.EnabledEntries(["epsilon", "github"], _fixture.LibrariesDir), "project token"));
        Assert.Equal("Epsilon", Winner(service.Current.Entries, "project token"));
        Assert.Equal("Epsilon", Winner(service.GetEnabledLibraryEntries(["epsilon", "github"]), "project token"));
        Assert.Equal(
            ["epsilon", "custom-github"],
            catalog.Libraries.Where(library => !library.Content.BuiltIn).Select(library => library.Content.Id));

        var edited = LibraryStorageFixture.Content("custom-github", "Twin", ("project token", "Twin 2"));
        var saved = Changes.Save(service, _fixture.Settings, Changes.Of(catalog, writes: [Changes.Edit(catalog, "custom-github", edited)]));

        Assert.Equal(LibrarySaveStatus.Applied, saved.Outcome!.Status);
        Assert.Equal(LibraryStorageFixture.Managed(edited), File.ReadAllBytes(_fixture.PathOf("github.csv")));
        Assert.False(_fixture.Exists("custom-github.csv"));
    }

    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, true, true)]
    [InlineData(true, true, true, false)]
    public void After_a_restart_each_twin_keeps_its_own_enabled_state_and_AI_choice(bool builtInOn, bool twinOn, bool builtInAi, bool twinAi)
    {
        // J-11 (A17), both on and both permitted included.
        SeedTwin();
        _fixture.SaveEnabled("github");
        var service = _fixture.Service();
        var catalog = service.LoadCatalog();
        var state = Changes.With(
            catalog.LocalState,
            enable: [.. (builtInOn ? ["github"] : Array.Empty<string>()), .. (twinOn ? ["custom-github"] : Array.Empty<string>())],
            disable: [.. (builtInOn ? Array.Empty<string>() : ["github"]), .. (twinOn ? Array.Empty<string>() : ["custom-github"])],
            ai: [("github", builtInAi), ("custom-github", twinAi)]);
        Changes.Save(service, _fixture.Settings, Changes.Of(catalog, state: state));

        _fixture.Restart();
        var reloaded = _fixture.Service().LoadCatalog();

        Assert.Equal(builtInOn, reloaded.LocalState.EnabledIds.Contains("github"));
        Assert.Equal(twinOn, reloaded.LocalState.EnabledIds.Contains("custom-github"));
        Assert.Equal(builtInAi, reloaded.LocalState.AiPermissions["github"]);
        Assert.Equal(twinAi, reloaded.LocalState.AiPermissions["custom-github"]);
    }

    [Fact]
    public void A_library_replaced_outside_Scribe_under_its_old_name_is_off_and_not_sent_and_Scribes_own_saves_never_read_as_replaced()
    {
        // J-16 (A4): an older build removed the accepted library and imported different content under the same slug.
        _fixture.Write("team.csv", LibraryStorageFixture.Csv("Team", ("kube", "Kubernetes")));
        _fixture.SaveEnabled("team");
        var service = _fixture.Service();
        var first = service.LoadCatalog();
        Assert.Contains("team", service.Current.AiScope.PermittedLibraryIds);

        File.Delete(_fixture.PathOf("team.csv"));
        _fixture.Write("team.csv", LibraryStorageFixture.Csv("Someone else's team", ("secret", "PrivateCanary")));
        _fixture.Restart();
        service = _fixture.Service();
        var replaced = service.LoadCatalog();

        Assert.DoesNotContain("team", replaced.LocalState.EnabledIds);
        Assert.False(replaced.LocalState.AiPermissions["team"]);
        Assert.DoesNotContain(service.Current.Entries, entry => entry.Replacement == "PrivateCanary");
        Assert.DoesNotContain("team", service.Current.AiScope.PermittedLibraryIds);
        Assert.Equal(_fixture.HashOf("team.csv"), replaced.LocalState.AcceptedContent["team"]);

        // This version's own Save of the library is accepted in the same commit, so nothing reads as replaced after it.
        var turnedOn = Changes.With(replaced.LocalState, enable: ["team"], ai: [("team", true)]);
        Changes.Save(service, _fixture.Settings, Changes.Of(
            replaced, writes: [Changes.Edit(replaced, "team", LibraryStorageFixture.Content("team", "Team", ("kube", "K8s")))], state: turnedOn));
        var mark = _fixture.Log.Entries.Count;
        _fixture.Restart();
        var saved = _fixture.Service().LoadCatalog();
        Assert.Contains("team", saved.LocalState.EnabledIds);
        Assert.True(ContractComposer.IsPermitted(saved.LocalState, "team", false, saved.Find("team")!.ContentHash));
        Assert.DoesNotContain(_fixture.Log.Entries.Skip(mark), entry => entry.Message.StartsWith("Adopted ", StringComparison.Ordinal));
        _ = first;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_older_build_never_applies_a_twin_this_version_has_off_or_kept_from_AI_after_a_Save_or_an_adoption(bool twinOnButKept)
    {
        // J-20 (A15).
        SeedTwin();
        _fixture.SaveEnabled("github");
        var service = _fixture.Service();
        var catalog = service.LoadCatalog();
        Assert.Contains(OlderBuildApplies(), entry => entry.Replacement == Canary);

        var state = twinOnButKept
            ? Changes.With(catalog.LocalState, ai: [("custom-github", false)])
            : Changes.With(catalog.LocalState, disable: ["custom-github"]);
        Changes.Save(service, _fixture.Settings, Changes.Of(catalog, state: state));

        Assert.DoesNotContain("github", _fixture.StoredEnabledList(), StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(OlderBuildApplies(), entry => entry.Replacement == Canary);

        // An adoption re-encodes the projection the same way.
        _fixture.Write("found.csv", LibraryStorageFixture.Csv("Found", ("f", "F")));
        _fixture.Restart();
        _fixture.Service().LoadCatalog();
        Assert.DoesNotContain("github", _fixture.StoredEnabledList(), StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(OlderBuildApplies(), entry => entry.Replacement == Canary);
    }

    [Fact]
    public void With_both_twins_on_and_permitted_the_older_build_applies_both_as_it_did_before()
    {
        // J-20.
        SeedTwin();
        _fixture.SaveEnabled("github");
        var service = _fixture.Service();
        var catalog = service.LoadCatalog();

        Changes.Save(service, _fixture.Settings, Changes.Of(catalog, state: Changes.With(catalog.LocalState, ai: [("custom-github", true), ("github", true)])));

        Assert.Contains("github", _fixture.StoredEnabledList());
        Assert.Contains(OlderBuildApplies(), entry => entry.Replacement == Canary);
        Assert.Contains(OlderBuildApplies(), entry => entry.Replacement == "GitHub");
    }

    [Fact]
    public void Deleting_a_twin_kept_from_AI_never_lets_the_older_build_apply_it_while_its_move_is_deferred()
    {
        // J-20b (A18): grouped over the libraries before and after the commit, so the built-in stays out until the file goes.
        SeedTwin();
        _fixture.SaveEnabled("github");
        var service = _fixture.Service();
        var start = service.LoadCatalog();
        Changes.Save(service, _fixture.Settings, Changes.Of(start, state: Changes.With(start.LocalState, ai: [("custom-github", false)])));
        var catalog = service.LoadCatalog();
        var prepared = service.PrepareSave(Changes.Of(catalog, deletions: [Changes.Delete(catalog, "custom-github")]));
        _fixture.Settings.SaveBundle(_fixture.Settings.Load(), null, null, default, prepared.Save!.Payload);

        // Right after the database commit, and while the move stays deferred: the twin's file is still on disk.
        Assert.DoesNotContain("github", _fixture.StoredEnabledList(), StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(OlderBuildApplies(), entry => entry.Replacement == Canary);
        using (new FileStream(_fixture.PathOf("github.csv"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(LibrarySaveStatus.AppliedAwaitingRelease, service.CompleteSave(prepared.Save).Status);
        }

        Assert.True(_fixture.Exists("github.csv"));
        Assert.DoesNotContain(OlderBuildApplies(), entry => entry.Replacement == Canary);

        // Once the deletion completes, the next commit brings the built-in back into the list.
        var done = service.LoadCatalog();
        Assert.False(_fixture.Exists("github.csv"));
        Changes.Save(service, _fixture.Settings, Changes.Of(done, state: Changes.With(done.LocalState, enable: ["microsoft-azure"])));
        Assert.Contains("github", _fixture.StoredEnabledList());
    }

    [Fact]
    public void A_rewrite_under_a_permission_granted_in_the_same_Save_keeps_the_old_bytes_from_the_older_build_until_the_next_commit()
    {
        // J-20b, the same shape for bytes.
        _fixture.Write("team.csv", LibraryStorageFixture.Csv("Team", ("kube", "Kubernetes"), ("secret", "PrivateCanary")));
        _fixture.SaveEnabled("team");
        var service = _fixture.Service();
        var start = service.LoadCatalog();
        Changes.Save(service, _fixture.Settings, Changes.Of(start, state: Changes.With(start.LocalState, ai: [("team", false)])));
        var catalog = service.LoadCatalog();
        Assert.DoesNotContain("team", _fixture.StoredEnabledList());

        var withoutSecret = LibraryStorageFixture.Content("team", "Team", ("kube", "Kubernetes"));
        var prepared = service.PrepareSave(Changes.Of(
            catalog, writes: [Changes.Edit(catalog, "team", withoutSecret)], state: Changes.With(catalog.LocalState, ai: [("team", true)])));
        _fixture.Settings.SaveBundle(_fixture.Settings.Load(), null, null, default, prepared.Save!.Payload);

        Assert.DoesNotContain("team", _fixture.StoredEnabledList());
        using (new FileStream(_fixture.PathOf("team.csv"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(LibrarySaveStatus.AppliedAwaitingRelease, service.CompleteSave(prepared.Save).Status);
        }

        Assert.DoesNotContain(OlderBuildApplies(), entry => entry.Replacement == "PrivateCanary");

        var installed = service.LoadCatalog();
        Changes.Save(service, _fixture.Settings, Changes.Of(installed, state: Changes.With(installed.LocalState, enable: ["microsoft-azure"])));
        Assert.Contains("team", _fixture.StoredEnabledList());
        Assert.DoesNotContain(OlderBuildApplies(), entry => entry.Replacement == "PrivateCanary");
    }

    [Fact]
    public void An_edit_of_an_already_permitted_library_keeps_it_in_the_older_builds_list_throughout()
    {
        // J-20b: ordinary edits cost older builds nothing.
        _fixture.Write("team.csv", LibraryStorageFixture.Csv("Team", ("kube", "Kubernetes")));
        _fixture.SaveEnabled("team");
        var service = _fixture.Service();
        var catalog = service.LoadCatalog();
        Assert.Contains("team", _fixture.StoredEnabledList());

        var prepared = service.PrepareSave(Changes.Of(catalog, writes: [Changes.Edit(catalog, "team", LibraryStorageFixture.Content("team", "Team", ("kube", "K8s")))]));
        _fixture.Settings.SaveBundle(_fixture.Settings.Load(), null, null, default, prepared.Save!.Payload);

        Assert.Contains("team", _fixture.StoredEnabledList());
        Assert.Equal(LibrarySaveStatus.Applied, service.CompleteSave(prepared.Save).Status);
        Assert.Contains("team", _fixture.StoredEnabledList());
    }

    [Fact]
    public void A_kept_outside_version_never_lands_on_an_id_the_documents_list_keeps()
    {
        // J-20b, found while fixing A18: the list keeps an id no library has, and the keepAs series avoids it.
        const string Listed = "custom-team-changed-outside-scribe";
        _fixture.Write("team.csv", LibraryStorageFixture.Csv("Team", ("kube", "Kubernetes")));
        _fixture.SaveEnabled("team", Listed);
        var service = _fixture.Service();
        var catalog = service.LoadCatalog();
        var outside = Encoding.UTF8.GetBytes(LibraryStorageFixture.Csv("Team", ("kube", "OutsideCanary")));

        var saved = Changes.Save(service, _fixture.Settings, Changes.Of(catalog, writes: [Changes.Edit(catalog, "team", LibraryStorageFixture.Content("team", "Team", ("kube", "K8s")))]),
            beforeCommit: () => _fixture.WriteBytes("team.csv", outside));

        var kept = Assert.Single(saved.Outcome!.KeptVersions);
        Assert.NotEqual(Listed, kept.KeptAsId, StringComparer.OrdinalIgnoreCase);
        Assert.False(_fixture.Exists(Listed + ".csv"));
        Assert.Contains(Listed, _fixture.StoredEnabledList());
        Assert.DoesNotContain(OlderBuildApplies(), entry => entry.Replacement == "OutsideCanary");
    }

    private static string? Winner(IReadOnlyList<Scribe.Core.Models.DictionaryEntry> entries, string spoken) =>
        entries.FirstOrDefault(entry => string.Equals(entry.Pattern.Trim(), spoken, StringComparison.OrdinalIgnoreCase))?.Replacement;
}
