using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.Persistence;

namespace Scribe.Core.Tests.Libraries.Storage;

/// <summary>
/// Round 2, A6: the settings a load reads (the document and whether it can be used, the generation, the state row and
/// the file-id map) come from one committed generation. A Save committing between those reads must leave the reader
/// with the whole old snapshot or the whole new one, never the old document's list beside the new state row, which the
/// composer would rightly read as an older build turning a library back on: a generation that never existed.
/// </summary>
public sealed class LibrarySnapshotTests : IDisposable
{
    private readonly LibraryStorageFixture _fixture = new(fileDatabase: true);

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void A_commit_between_the_reads_of_a_load_yields_the_whole_old_or_the_whole_new_catalog()
    {
        _fixture.Write("team.csv", LibraryStorageFixture.Csv("Team", ("kube", "Kubernetes")));
        _fixture.Write("other.csv", LibraryStorageFixture.Csv("Other", ("o", "O")));
        _fixture.SaveEnabled("team", "other");
        var writer = _fixture.Service();
        var start = writer.LoadCatalog();
        Assert.Contains("team", start.LocalState.EnabledIds);
        var prepared = writer.PrepareSave(Changes.Of(start, state: Changes.With(start.LocalState, disable: ["team"])));
        Assert.Equal(LibraryPrepareStatus.Prepared, prepared.Status);

        // Another process loads; the Save commits right after that load has read the settings document.
        var gated = new CommitsAfterTheDocumentIsRead(
            _fixture.Settings, () => _fixture.Settings.SaveBundle(_fixture.Settings.Load(), null, null, default, prepared.Save!.Payload));
        var reader = _fixture.Service(settings: gated);
        gated.Armed = true;

        var seen = reader.LoadCatalog();

        Assert.True(gated.Fired);
        var old = seen.Generation == start.Generation && seen.LocalState.EnabledIds.Contains("team");
        var @new = seen.Generation == start.Generation + 1 && !seen.LocalState.EnabledIds.Contains("team");
        Assert.True(old || @new, $"generation {seen.Generation} with team {(seen.LocalState.EnabledIds.Contains("team") ? "on" : "off")}");
        Assert.Equal(seen.LocalState.EnabledIds.Contains("team"), reader.Current.Entries.Any(entry => entry.Replacement == "Kubernetes"));
        writer.CompleteSave(prepared.Save!);
        Assert.DoesNotContain("team", _fixture.Service().LoadCatalog().LocalState.EnabledIds);
    }

    [Fact]
    public void The_repository_reads_every_library_setting_in_one_transaction_whatever_commits_between_its_reads()
    {
        // The gate sits inside the repository's read, between the document and the library rows: the Save commits there,
        // on another connection, and the read still returns the generation it began in, whole.
        _fixture.Write("team.csv", LibraryStorageFixture.Csv("Team", ("kube", "Kubernetes")));
        _fixture.SaveEnabled("team");
        var service = _fixture.Service();
        var start = service.LoadCatalog();
        var prepared = service.PrepareSave(Changes.Of(start, state: Changes.With(start.LocalState, disable: ["team"])));
        var armed = true;
        _fixture.Settings.ReadStep = step =>
        {
            if (armed && step == "document read")
            {
                armed = false;
                _fixture.Settings.SaveBundle(_fixture.Settings.Load(), null, null, default, prepared.Save!.Payload);
            }
        };

        var during = service.LoadCatalog();
        _fixture.Settings.ReadStep = null;

        Assert.False(armed);
        Assert.Equal(start.Generation, during.Generation);
        Assert.Contains("team", during.LocalState.EnabledIds);
        var after = service.LoadCatalog();
        Assert.Equal(start.Generation + 1, after.Generation);
        Assert.DoesNotContain("team", after.LocalState.EnabledIds);
        Assert.Equal(LibrarySaveStatus.Applied, service.CompleteSave(prepared.Save!).Status);
    }

    /// <summary>The real repository; once armed, its next <see cref="Load"/> commits another Save before it returns.</summary>
    private sealed class CommitsAfterTheDocumentIsRead(SettingsRepository inner, Action commit) : ISettingsRepository
    {
        public bool Armed { get; set; }

        public bool Fired { get; private set; }

        public bool LastLoadFailed => inner.LastLoadFailed;

        public AppSettings Load()
        {
            var document = inner.Load();
            if (Armed)
            {
                Armed = false;
                Fired = true;
                commit();
            }

            return document;
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
}
