using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.Tests.Libraries.Storage;

namespace Scribe.Core.Tests.Libraries.Integration;

/// <summary>
/// Every outcome a library Save can have, through the real J, X, O and C parts and D's workspace, with the rule the shell
/// follows (contract 9.3 and 9.7, W2-3; Grok's G3 on the integration): the draft is marked saved after <c>Applied</c> and
/// <c>AppliedAwaitingRelease</c> only, and stays unsaved, its edits intact, after <c>NotCommitted</c>, <c>Superseded</c> and
/// <c>CommitUnknown</c>.
/// </summary>
public sealed class LibrarySaveOutcomeTests
{
    public static TheoryData<LibrarySaveStatus> Outcomes() =>
    [
        LibrarySaveStatus.Applied,
        LibrarySaveStatus.AppliedAwaitingRelease,
        LibrarySaveStatus.NotCommitted,
        LibrarySaveStatus.Superseded,
        LibrarySaveStatus.CommitUnknown,
    ];

    [Theory]
    [MemberData(nameof(Outcomes))]
    public void Only_a_Save_that_stands_is_marked_saved(LibrarySaveStatus expected)
    {
        using var libraries = new RealLibraries();
        var fixture = libraries.Fixture;
        fixture.Write("team-terms.csv", "# name: Team terms\npattern,replacement\nkube,Kubernetes\n");
        fixture.SaveEnabled("team-terms");
        var settings = new GenerationUnreadableAfterCommit(fixture.Settings);
        var service = libraries.Service(settings: settings);
        var start = service.LoadCatalog();
        var workspace = RealLibraries.Workspace(start);
        Assert.True(workspace.EditTerm("team-terms", RealLibraries.Row(workspace, "team-terms", "kube").RowId, new TermValues("kube", "K8s")).Applied);
        Assert.True(workspace.HasUnsavedChanges);

        FileStream? held = null;
        Action? beforeCommit = null;
        switch (expected)
        {
            case LibrarySaveStatus.AppliedAwaitingRelease:
                // Another app reads the file with the handle open: the install waits for it.
                held = new FileStream(fixture.PathOf("team-terms.csv"), FileMode.Open, FileAccess.Read, FileShare.Read);
                break;
            case LibrarySaveStatus.NotCommitted:
                fixture.Settings.WriteStep = (step, _, _) =>
                {
                    if (step == "save committing")
                    {
                        throw FaultingFileSystem.Injected();
                    }
                };
                break;
            case LibrarySaveStatus.Superseded:
                // Stands for a commit this process did not make: the stored generation moved past the prepared one.
                beforeCommit = () => fixture.Settings.Set(
                    LibrarySettingKeys.Generation, (start.Generation + 2).ToString(System.Globalization.CultureInfo.InvariantCulture));
                break;
            case LibrarySaveStatus.CommitUnknown:
                settings.FailAfterNextCommit = true;
                break;
        }

        RealLibraries.SaveResult saved;
        try
        {
            saved = libraries.Save(service, workspace, settings, beforeCommit);
        }
        finally
        {
            held?.Dispose();
            fixture.Settings.WriteStep = null;
        }

        Assert.Equal(expected, saved.Outcome.Status);
        var stands = expected is LibrarySaveStatus.Applied or LibrarySaveStatus.AppliedAwaitingRelease;
        Assert.Equal(stands, RealLibraries.MarksSaved(saved.Outcome.Status));
        Assert.Equal(stands, saved.MarkedSaved);
        Assert.Equal(!stands, workspace.HasUnsavedChanges);
        Assert.Equal(expected is LibrarySaveStatus.NotCommitted or LibrarySaveStatus.Superseded, saved.CommitFailure is not null);

        // The edit is what the draft shows either way: saved and committed, or still waiting for a Save that goes through.
        Assert.Equal("K8s", RealLibraries.Row(workspace, "team-terms", "kube").Row.Values.Written);
        settings.Failing = false;
        var reloaded = service.LoadCatalog();
        var written = reloaded.Find("team-terms")!.Content.Rows.Single().Values.Written;
        Assert.Equal(expected is LibrarySaveStatus.NotCommitted or LibrarySaveStatus.Superseded ? "Kubernetes" : "K8s", written);
        if (stands)
        {
            Assert.False(RealLibraries.Workspace(reloaded).HasUnsavedChanges);
        }
    }

    /// <summary>
    /// The real settings store, except that with <see cref="FailAfterNextCommit"/> set, the reads of the stored library
    /// generation fail from right after the next library commit (which commits), until <see cref="Failing"/> is cleared: what
    /// makes a completion unable to tell whether its Save committed (J's A8).
    /// </summary>
    private sealed class GenerationUnreadableAfterCommit(SettingsRepository inner) : ISettingsRepository
    {
        public bool FailAfterNextCommit { get; set; }

        public bool Failing { get; set; }

        public bool LastLoadFailed => inner.LastLoadFailed;

        public AppSettings Load() => inner.Load();

        public void Save(AppSettings settings) => inner.Save(settings);

        public AppSettings Update(Action<AppSettings> mutate) => inner.Update(mutate);

        public AppSettings Update(Action<AppSettings> mutate, long revision, out bool superseded) => inner.Update(mutate, revision, out superseded);

        public void SaveBundle(AppSettings settings, IReadOnlyList<DictionaryEntry>? dictionaryEntries, IReadOnlyList<Snippet>? snippets, long aiCleanupIntent = 0) =>
            inner.SaveBundle(settings, dictionaryEntries, snippets, aiCleanupIntent);

        public void SaveBundle(
            AppSettings settings, IReadOnlyList<DictionaryEntry>? dictionaryEntries, IReadOnlyList<Snippet>? snippets,
            ExternalIntents intents, LibrarySavePayload? libraries)
        {
            inner.SaveBundle(settings, dictionaryEntries, snippets, intents, libraries);
            if (libraries is not null && FailAfterNextCommit)
            {
                FailAfterNextCommit = false;
                Failing = true;
            }
        }

        public void CommitLibraryState(LibrarySavePayload payload) => inner.CommitLibraryState(payload);

        public string? Get(string key) =>
            Failing && key == LibrarySettingKeys.Generation ? throw new IOException("injected: the settings store cannot be read") : inner.Get(key);

        public void Set(string key, string value) => inner.Set(key, value);
    }
}
