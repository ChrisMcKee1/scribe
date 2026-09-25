using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.Persistence;

namespace Scribe.Core.Tests.Libraries.Storage;

/// <summary>
/// Round 2, A8: when completion cannot read the stored generation, whether the settings transaction committed is not
/// known, and completion says so (<see cref="LibrarySaveStatus.CommitUnknown"/>) instead of calling a durable Save unsaved.
/// The manifest stays pending and is never discarded on that evidence; new Saves are fenced (PreviousSaveUnfinished, no
/// failure) and the wrappers refuse; the vocabulary scope stays at the narrowed intersection it took at prepare, through
/// any load meanwhile; and a later read of the generation settles it by the journal's own rules.
/// </summary>
public sealed class LibraryCommitUnknownTests : IDisposable
{
    private readonly LibraryStorageFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private LibraryContent Edited => LibraryStorageFixture.Content("team", "Team", ("kube", "K8s"));

    private (Scribe.Core.PostProcessing.DictionaryLibraryService Service, UnreadableGeneration Settings, LibraryCatalog Start, AiVocabularyScope Admitted) Permitted()
    {
        _fixture.Write("team.csv", LibraryStorageFixture.Csv("Team", ("kube", "Kubernetes")));
        _fixture.Write("other.csv", LibraryStorageFixture.Csv("Other", ("o", "O")));
        _fixture.SaveEnabled("team", "other");
        var settings = new UnreadableGeneration(_fixture.Settings);
        var service = _fixture.Service(settings: settings);
        var start = service.LoadCatalog();
        var admitted = service.Current.AiScope;
        Assert.Contains("team", admitted.PermittedLibraryIds);
        return (service, settings, start, admitted);
    }

    private LibraryChangeSet RevokeAndEdit(LibraryCatalog start) => Changes.Of(
        start, writes: [Changes.Edit(start, "team", Edited)], state: Changes.With(start.LocalState, ai: [("team", false)]));

    private void AssertFenced(Scribe.Core.PostProcessing.DictionaryLibraryService service, LibraryCatalog start, AiVocabularyScope admitted)
    {
        Assert.Single(Directory.GetFiles(_fixture.Paths.LibraryJournalDir, "*.manifest.json"));
        var refused = service.PrepareSave(Changes.Of(start, state: Changes.With(start.LocalState, disable: ["other"])));
        Assert.Equal(LibraryPrepareStatus.PreviousSaveUnfinished, refused.Status);
        Assert.Equal(LibraryIoFailure.None, refused.Failure);
        Assert.Throws<InvalidOperationException>(() => service.Import("pattern,replacement\nn,N\n", "Notes"));
        Assert.False(service.TryHandOff(admitted, () => Assert.Fail("A scope the Save narrowed was handed over.")));
        var other = new AiVocabularyScope(admitted.Generation, admitted.PermittedContent.Where(pair => pair.Key == "other"));
        Assert.True(service.TryHandOff(other, () => { }));
    }

    [Fact]
    public void A_committed_Save_whose_generation_cannot_be_read_is_reported_unknown_kept_fenced_and_installed_once_it_can()
    {
        var (service, settings, start, admitted) = Permitted();
        var prepared = service.PrepareSave(RevokeAndEdit(start));
        _fixture.Settings.SaveBundle(_fixture.Settings.Load(), null, null, default, prepared.Save!.Payload);
        settings.Failing = true;

        var outcome = service.CompleteSave(prepared.Save);

        Assert.NotEqual(LibrarySaveStatus.NotCommitted, outcome.Status);
        Assert.Equal("CommitUnknown", outcome.Status.ToString());
        Assert.Equal(start.Generation, outcome.CommittedGeneration);
        Assert.Equal(LibraryIoFailure.None, outcome.Failure);
        AssertFenced(service, start, admitted);
        Assert.ThrowsAny<Exception>(() => service.LoadCatalog());
        AssertFenced(service, start, admitted);

        // A later read settles it: this Save's generation is the one stored, so it installs, and the revocation stands.
        settings.Failing = false;
        var settled = service.LoadCatalog();
        Assert.Equal(start.Generation + 1, settled.Generation);
        Assert.Equal("K8s", settled.Find("team")!.Content.Rows[0].Values.Written);
        Assert.Equal(LibraryStorageFixture.Managed(Edited), File.ReadAllBytes(_fixture.PathOf("team.csv")));
        Assert.Empty(Directory.GetFiles(_fixture.Paths.LibraryJournalDir, "*.manifest.json"));
        Assert.False(service.TryHandOff(admitted, () => { }));
        Assert.Equal(LibraryPrepareStatus.Prepared, service.PrepareSave(Changes.Of(settled, state: Changes.With(settled.LocalState, disable: ["other"]))).Status);
    }

    [Fact]
    public void An_uncommitted_Save_whose_generation_cannot_be_read_is_discarded_once_it_can_and_the_committed_scope_is_back()
    {
        var (service, settings, start, admitted) = Permitted();
        var prepared = service.PrepareSave(RevokeAndEdit(start));
        settings.Failing = true;

        // SaveBundle failed before its commit, and the read after it fails too: the outcome is as unknown as before.
        var outcome = service.CompleteSave(prepared.Save!);

        Assert.Equal("CommitUnknown", outcome.Status.ToString());
        AssertFenced(service, start, admitted);

        settings.Failing = false;
        var settled = service.LoadCatalog();
        Assert.Equal(start.Generation, settled.Generation);
        Assert.Equal("Kubernetes", settled.Find("team")!.Content.Rows[0].Values.Written);
        Assert.Empty(Directory.GetFiles(_fixture.Paths.LibraryJournalDir, "*.manifest.json"));
        Assert.True(service.TryHandOff(admitted, () => { }));
    }

    [Fact]
    public void Completing_the_same_Save_again_once_the_generation_reads_reports_where_it_stands()
    {
        var (service, settings, start, _) = Permitted();
        var prepared = service.PrepareSave(RevokeAndEdit(start));
        _fixture.Settings.SaveBundle(_fixture.Settings.Load(), null, null, default, prepared.Save!.Payload);
        settings.Failing = true;
        Assert.Equal("CommitUnknown", service.CompleteSave(prepared.Save).Status.ToString());

        settings.Failing = false;
        var again = service.CompleteSave(prepared.Save);

        Assert.Equal(LibrarySaveStatus.Applied, again.Status);
        Assert.Equal(start.Generation + 1, again.CommittedGeneration);
    }

    /// <summary>The real repository, except that reading the library generation fails while <see cref="Failing"/> is set.</summary>
    private sealed class UnreadableGeneration(SettingsRepository inner) : ISettingsRepository
    {
        public bool Failing { get; set; }

        public bool LastLoadFailed => inner.LastLoadFailed;

        public AppSettings Load() => inner.Load();

        public void Save(AppSettings settings) => inner.Save(settings);

        public AppSettings Update(Action<AppSettings> mutate) => inner.Update(mutate);

        public AppSettings Update(Action<AppSettings> mutate, long revision, out bool superseded) => inner.Update(mutate, revision, out superseded);

        public void SaveBundle(AppSettings settings, IReadOnlyList<DictionaryEntry>? dictionaryEntries, IReadOnlyList<Snippet>? snippets, long aiCleanupIntent = 0) =>
            inner.SaveBundle(settings, dictionaryEntries, snippets, aiCleanupIntent);

        public void CommitLibraryState(LibrarySavePayload payload) => inner.CommitLibraryState(payload);

        public string? Get(string key) =>
            Failing && key == LibrarySettingKeys.Generation ? throw new IOException("injected: the settings store cannot be read") : inner.Get(key);

        public void Set(string key, string value) => inner.Set(key, value);
    }
}
