using Scribe.Core.Libraries;

namespace Scribe.Core.Tests.Libraries.Storage;

/// <summary>
/// Round 2, A3: consent is bound to content (A4), and that holds for a Save that changes only a library's switches. A
/// grant of AI permission, or a library turned on, covers the content the draft accepted, so a file replaced outside
/// Scribe since then is an outside edit even though the Save writes no row of it: the user reloads and decides again,
/// and the older reader never meets the replacement under the old consent, right after the database commit included.
/// </summary>
public sealed class LibraryConsentTests : IDisposable
{
    private const string Canary = "Wheatear";
    private readonly LibraryStorageFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private IReadOnlyList<Scribe.Core.Models.DictionaryEntry> OlderBuildApplies() =>
        Legacy043LibrarySelection.EnabledEntries(_fixture.StoredEnabledList(), _fixture.LibrariesDir);

    // team.csv committed at H1, on or off and kept from AI cleanup, and the catalog an editor opened on it.
    private (Scribe.Core.PostProcessing.DictionaryLibraryService Service, LibraryCatalog Catalog) KeptFromAi(bool on)
    {
        _fixture.Write("team.csv", LibraryStorageFixture.Csv("Team", ("kube", "Kubernetes")));
        _fixture.SaveEnabled("team");
        var service = _fixture.Service();
        var catalog = service.LoadCatalog();
        Changes.Save(service, _fixture.Settings, Changes.Of(
            catalog, state: Changes.With(catalog.LocalState, disable: on ? [] : ["team"], ai: [("team", false)])));
        catalog = service.LoadCatalog();
        Assert.False(catalog.LocalState.AiPermissions["team"]);
        Assert.DoesNotContain(OlderBuildApplies(), entry => entry.Pattern == "kube");
        return (service, catalog);
    }

    private void ReplaceOutsideScribe() =>
        _fixture.Write("team.csv", LibraryStorageFixture.Csv("Team", ("private codename", Canary)));

    // The shell's sequence up to the database commit, and then the process ends: completion never runs.
    private LibraryPrepareResult PrepareAndCommit(Scribe.Core.PostProcessing.DictionaryLibraryService service, LibraryChangeSet changes)
    {
        var prepared = service.PrepareSave(changes);
        if (prepared.Save is { } save)
        {
            _fixture.Settings.SaveBundle(_fixture.Settings.Load(), null, null, default, save.Payload);
        }

        return prepared;
    }

    [Fact]
    public void A_grant_of_AI_permission_on_content_replaced_since_the_editor_opened_is_an_outside_edit()
    {
        var (service, catalog) = KeptFromAi(on: true);
        ReplaceOutsideScribe();

        var prepared = PrepareAndCommit(service, Changes.Of(catalog, state: Changes.With(catalog.LocalState, ai: [("team", true)])));

        Assert.DoesNotContain(OlderBuildApplies(), entry => entry.Replacement == Canary);
        Assert.Equal(LibraryPrepareStatus.OutsideEdit, prepared.Status);
        Assert.Equal(["team"], prepared.OutsideEditIds);
        Assert.Equal(catalog.Generation, _fixture.StoredGeneration);
        Assert.Empty(Directory.GetFiles(_fixture.Paths.LibraryJournalDir, "*.manifest.json"));
    }

    [Fact]
    public void Turning_on_a_library_replaced_since_the_editor_opened_is_an_outside_edit_too()
    {
        var (service, catalog) = KeptFromAi(on: false);
        ReplaceOutsideScribe();

        var prepared = PrepareAndCommit(service, Changes.Of(catalog, state: Changes.With(catalog.LocalState, enable: ["team"], ai: [("team", true)])));

        Assert.DoesNotContain(OlderBuildApplies(), entry => entry.Replacement == Canary);
        Assert.DoesNotContain(service.Current.Entries, entry => entry.Replacement == Canary);
        Assert.Equal(LibraryPrepareStatus.OutsideEdit, prepared.Status);
        Assert.Equal(["team"], prepared.OutsideEditIds);
    }

    [Fact]
    public void After_a_reload_the_user_can_grant_the_new_content_and_the_older_build_then_uses_it()
    {
        var (service, catalog) = KeptFromAi(on: true);
        ReplaceOutsideScribe();
        Assert.Equal(LibraryPrepareStatus.OutsideEdit, service.PrepareSave(
            Changes.Of(catalog, state: Changes.With(catalog.LocalState, ai: [("team", true)]))).Status);

        var reloaded = service.LoadCatalog();
        Assert.Equal(Canary, reloaded.Find("team")!.Content.Rows[0].Values.Written);
        var saved = Changes.Save(service, _fixture.Settings, Changes.Of(
            reloaded, state: Changes.With(reloaded.LocalState, enable: ["team"], ai: [("team", true)])));

        Assert.Equal(LibrarySaveStatus.Applied, saved.Outcome!.Status);
        Assert.Contains(OlderBuildApplies(), entry => entry.Replacement == Canary);
    }

    [Fact]
    public void Revoking_consent_on_replaced_content_always_goes_through()
    {
        var (service, catalog) = KeptFromAi(on: true);
        var permitted = Changes.Save(service, _fixture.Settings, Changes.Of(catalog, state: Changes.With(catalog.LocalState, ai: [("team", true)])));
        Assert.Equal(LibrarySaveStatus.Applied, permitted.Outcome!.Status);
        _fixture.Write("notes.csv", LibraryStorageFixture.Csv("Notes", ("n", "N")));
        catalog = service.LoadCatalog();
        ReplaceOutsideScribe();

        // Turning the replaced library off, or its AI permission, narrows: it always goes through.
        var revoked = Changes.Save(service, _fixture.Settings, Changes.Of(catalog, state: Changes.With(catalog.LocalState, disable: ["team"], ai: [("team", false)])));

        Assert.Equal(LibrarySaveStatus.Applied, revoked.Outcome!.Status);
        Assert.DoesNotContain(OlderBuildApplies(), entry => entry.Replacement == Canary);
    }

    [Fact]
    public void A_grant_kept_by_a_Save_on_content_replaced_meanwhile_is_an_outside_edit()
    {
        // A permitted library replaced while the editor is open: the Save re-encodes the grant it carries, so it may not
        // project the old consent onto the new bytes either, even though it changes something else.
        var (service, catalog) = KeptFromAi(on: true);
        Changes.Save(service, _fixture.Settings, Changes.Of(catalog, state: Changes.With(catalog.LocalState, ai: [("team", true)])));
        _fixture.Write("notes.csv", LibraryStorageFixture.Csv("Notes", ("n", "N")));
        catalog = service.LoadCatalog();
        ReplaceOutsideScribe();

        var prepared = service.PrepareSave(Changes.Of(catalog, state: Changes.With(catalog.LocalState, enable: ["notes"])));

        Assert.Equal(LibraryPrepareStatus.OutsideEdit, prepared.Status);
        Assert.Contains("team", prepared.OutsideEditIds);
    }
}
