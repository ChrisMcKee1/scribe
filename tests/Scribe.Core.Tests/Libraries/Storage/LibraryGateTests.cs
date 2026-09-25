using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.Persistence;

namespace Scribe.Core.Tests.Libraries.Storage;

/// <summary>
/// The admission point (contract 2.10): <see cref="ILibraryVocabularySource.TryHandOff"/> runs a hand-off under the
/// permission gate only while the published scope covers what the request was admitted with; every narrowing is published
/// under that gate at prepare, completion and a Save that did not commit; a content change narrows too (A12); publication
/// raises <c>Changed</c> through <c>ResilientEvent</c>; and neither <c>Current</c> nor <c>TryHandOff</c> waits on library I/O.
/// </summary>
public sealed class LibraryGateTests : IDisposable
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    private readonly LibraryStorageFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private (Scribe.Core.PostProcessing.DictionaryLibraryService Service, LibraryCatalog Catalog) TwoPermitted()
    {
        _fixture.Write("team.csv", LibraryStorageFixture.Csv("Team", ("kube", "Kubernetes")));
        _fixture.Write("other.csv", LibraryStorageFixture.Csv("Other", ("o", "O")));
        _fixture.SaveEnabled("team", "other");
        var service = _fixture.Service();
        var catalog = service.LoadCatalog();
        Assert.Contains("team", service.Current.AiScope.PermittedLibraryIds);
        Assert.Contains("other", service.Current.AiScope.PermittedLibraryIds);
        return (service, catalog);
    }

    [Fact]
    public void A_revocation_prepared_for_a_Save_is_in_force_before_its_commit_and_only_for_what_it_revokes()
    {
        var (service, catalog) = TwoPermitted();
        var admitted = service.Current.AiScope;
        var otherOnly = new AiVocabularyScope(catalog.Generation, admitted.PermittedContent.Where(pair => pair.Key == "other"));

        var prepared = service.PrepareSave(Changes.Of(catalog, state: Changes.With(catalog.LocalState, ai: [("team", false)])));

        Assert.False(service.TryHandOff(admitted, () => Assert.Fail("The revoked library was handed over.")));
        var ran = false;
        Assert.True(service.TryHandOff(otherOnly, () => ran = true));
        Assert.True(ran);

        _fixture.Settings.SaveBundle(_fixture.Settings.Load(), null, null, default, prepared.Save!.Payload);
        service.CompleteSave(prepared.Save);
        Assert.False(service.TryHandOff(admitted, () => { }));
        Assert.DoesNotContain("team", service.Current.AiScope.PermittedLibraryIds);
        Assert.DoesNotContain(service.Current.AiEntries, entry => entry.Replacement == "Kubernetes");
        Assert.Contains(service.Current.Entries, entry => entry.Replacement == "Kubernetes");
    }

    [Fact]
    public void A_Save_that_changes_a_permitted_librarys_content_stops_requests_admitted_for_the_old_content_once_it_completes()
    {
        // A12: the scope pairs each library with its content; the user's own edit narrows as an outside one does.
        var (service, catalog) = TwoPermitted();
        var admitted = service.Current.AiScope;
        var prepared = service.PrepareSave(Changes.Of(catalog, writes: [Changes.Edit(catalog, "team", LibraryStorageFixture.Content("team", "Team", ("kube", "K8s")))]));
        _fixture.Settings.SaveBundle(_fixture.Settings.Load(), null, null, default, prepared.Save!.Payload);

        // Committed and not yet completed: the scope while saving keeps the committed content.
        Assert.True(service.TryHandOff(admitted, () => { }));

        service.CompleteSave(prepared.Save);

        Assert.False(service.TryHandOff(admitted, () => Assert.Fail("A request for the old content was handed over.")));
        Assert.True(service.TryHandOff(service.Current.AiScope, () => { }));
    }

    [Fact]
    public void Nothing_is_handed_over_before_anything_is_published_but_a_request_carrying_no_library()
    {
        var service = _fixture.Service();
        var someScope = new AiVocabularyScope(1, [new KeyValuePair<string, LibraryContentHash?>("github", null)]);

        Assert.False(service.TryHandOff(someScope, () => Assert.Fail("Handed over with nothing published.")));
        Assert.True(service.TryHandOff(AiVocabularyScope.None, () => { }));
    }

    [Fact]
    public void Publication_raises_Changed_with_the_generation_and_one_throwing_subscriber_stops_no_other()
    {
        var (service, catalog) = TwoPermitted();
        var seen = new List<long>();
        service.Changed += _ => throw new InvalidOperationException("a subscriber that throws");
        service.Changed += seen.Add;

        Changes.Save(service, _fixture.Settings, Changes.Of(catalog, state: Changes.With(catalog.LocalState, disable: ["other"])));

        Assert.Contains(catalog.Generation + 1, seen);
        Assert.Contains(_fixture.Log.Entries, entry => entry.Message.StartsWith("A library vocabulary subscriber failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Current_and_TryHandOff_never_wait_for_the_library_lock()
    {
        TwoPermitted();
        using var inside = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var blocking = new BlockingLoads(_fixture.Settings, inside, release);
        var held = _fixture.Service(settings: blocking);
        held.LoadCatalog();
        blocking.Armed = true;

        // A load holds the library lock inside its settings read while the vocabulary is read and a request handed over.
        var load = Task.Run(held.LoadCatalog);
        Assert.True(inside.Wait(Bound));
        var handedOver = await Task.Run(() =>
        {
            _ = held.Current.Entries.Count;
            return held.TryHandOff(held.Current.AiScope, () => { });
        }).WaitAsync(Bound);

        Assert.True(handedOver);
        Assert.False(load.IsCompleted);
        release.Set();
        await load.WaitAsync(Bound);
    }

    [Fact]
    public void An_adoption_that_cannot_be_committed_never_widens_what_is_handed_over()
    {
        // The first start's grants are held in memory when the commit fails, and the published scope keeps them out.
        _fixture.Write("team.csv", LibraryStorageFixture.Csv("Team", ("kube", "Kubernetes")));
        _fixture.SaveEnabled("team", "github");
        var failing = new NoLibraryCommits(_fixture.Settings);
        var service = _fixture.Service(settings: failing);

        var catalog = service.LoadCatalog();

        Assert.True(catalog.LocalState.AiPermissions["team"]);
        Assert.DoesNotContain("team", service.Current.AiScope.PermittedLibraryIds);
        Assert.Contains("github", service.Current.AiScope.PermittedLibraryIds);
        Assert.Contains(service.Current.Entries, entry => entry.Replacement == "Kubernetes");
    }

    /// <summary>The real repository; once armed, its next load blocks until released, while the caller holds the library lock.</summary>
    private sealed class BlockingLoads(SettingsRepository inner, ManualResetEventSlim inside, ManualResetEventSlim release) : ISettingsRepository
    {
        public bool Armed { get; set; }

        public bool LastLoadFailed => inner.LastLoadFailed;

        public AppSettings Load()
        {
            if (Armed)
            {
                Armed = false;
                inside.Set();
                Assert.True(release.Wait(Bound));
            }

            return inner.Load();
        }

        public void Save(AppSettings settings) => inner.Save(settings);

        public AppSettings Update(Action<AppSettings> mutate) => inner.Update(mutate);

        public AppSettings Update(Action<AppSettings> mutate, long revision, out bool superseded) => inner.Update(mutate, revision, out superseded);

        public void SaveBundle(AppSettings settings, IReadOnlyList<DictionaryEntry>? dictionaryEntries, IReadOnlyList<Snippet>? snippets, long aiCleanupIntent = 0) =>
            inner.SaveBundle(settings, dictionaryEntries, snippets, aiCleanupIntent);

        public void CommitLibraryState(LibrarySavePayload payload) => inner.CommitLibraryState(payload);

        public string? Get(string key) => inner.Get(key);

        public void Set(string key, string value) => inner.Set(key, value);
    }

    private sealed class NoLibraryCommits(SettingsRepository inner) : ISettingsRepository
    {
        public bool LastLoadFailed => inner.LastLoadFailed;

        public AppSettings Load() => inner.Load();

        public void Save(AppSettings settings) => inner.Save(settings);

        public AppSettings Update(Action<AppSettings> mutate) => inner.Update(mutate);

        public AppSettings Update(Action<AppSettings> mutate, long revision, out bool superseded) => inner.Update(mutate, revision, out superseded);

        public void SaveBundle(AppSettings settings, IReadOnlyList<DictionaryEntry>? dictionaryEntries, IReadOnlyList<Snippet>? snippets, long aiCleanupIntent = 0) =>
            inner.SaveBundle(settings, dictionaryEntries, snippets, aiCleanupIntent);

        public void CommitLibraryState(LibrarySavePayload payload) => throw new IOException("no library commits");

        public string? Get(string key) => inner.Get(key);

        public void Set(string key, string value) => inner.Set(key, value);
    }
}
