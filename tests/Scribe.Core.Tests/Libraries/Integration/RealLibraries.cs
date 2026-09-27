using Scribe.Core.Libraries;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;
using Scribe.Core.Tests.Libraries.Storage;

namespace Scribe.Core.Tests.Libraries.Integration;

/// <summary>
/// The five sub-streams wired as the app wires them since the integration commit, over a real data folder and settings
/// database: J's service and journal with X's codec, O's overlay and C's composer, and D's workspace in front, driven the
/// way W2's Save will drive them (capture, prepare, the settings commit, completion from a finally, then MarkSaved only for
/// a Save that stands).
/// </summary>
internal sealed class RealLibraries : IDisposable
{
    public LibraryStorageFixture Fixture { get; } = new() { RealParts = true };

    public string LibrariesDir => Fixture.LibrariesDir;

    public DictionaryLibraryService Service(
        ILibraryFileSystem? files = null, IBuiltInLibraryOverlay? overlay = null, ISettingsRepository? settings = null) =>
        Fixture.Service(files, overlay: overlay, settings: settings);

    /// <summary>A new process over the same folder and database.</summary>
    public DictionaryLibraryService Restart(ILibraryFileSystem? files = null, IBuiltInLibraryOverlay? overlay = null)
    {
        Fixture.Restart();
        return Service(files, overlay);
    }

    public static LibraryWorkspace Workspace(LibraryCatalog catalog) =>
        new(catalog, BuiltInLibraryOverlay.Instance, LibraryDecisions.DefaultAiPermission);

    /// <summary>
    /// The rule W2 copies (contract 9.3 and 9.7, W2-3): only a Save that stands is marked saved. <c>NotCommitted</c> and
    /// <c>Superseded</c> did not go through, and <c>CommitUnknown</c> may or may not have, so the draft stays unsaved until a
    /// later load settles it.
    /// </summary>
    public static bool MarksSaved(LibrarySaveStatus status) =>
        status is LibrarySaveStatus.Applied or LibrarySaveStatus.AppliedAwaitingRelease;

    /// <summary>
    /// The shell's Save: capture, prepare, the settings commit (through <paramref name="settings"/>, the fixture's store by
    /// default), completion from a finally whether the commit returned or threw, and MarkSaved only when the Save stands
    /// (<see cref="MarksSaved"/>). A commit that threw is reported, as the shell reports it, not rethrown.
    /// </summary>
    public SaveResult Save(
        DictionaryLibraryService service, LibraryWorkspace workspace, ISettingsRepository? settings = null, Action? beforeCommit = null)
    {
        var capture = workspace.CaptureChangeSet();
        Assert.Empty(capture.Issues);
        var changes = capture.ChangeSet!;
        Assert.False(changes.IsEmpty, "the workspace captured nothing to save");
        var prepared = service.PrepareSave(changes);
        Assert.Equal(LibraryPrepareStatus.Prepared, prepared.Status);
        var store = settings ?? Fixture.Settings;
        Exception? commitFailure = null;
        LibrarySaveOutcome outcome;
        try
        {
            beforeCommit?.Invoke();
            store.SaveBundle(store.Load(), null, null, default, prepared.Save!.Payload);
        }
        catch (Exception ex)
        {
            commitFailure = ex;
        }
        finally
        {
            outcome = service.CompleteSave(prepared.Save!);
        }

        if (!MarksSaved(outcome.Status))
        {
            return new SaveResult(changes, outcome, null, commitFailure, MarkedSaved: false);
        }

        var committed = service.LoadCatalog();
        workspace.MarkSaved(changes, committed);
        return new SaveResult(changes, outcome, committed, commitFailure, MarkedSaved: true);
    }

    public static DraftTermRow Row(LibraryWorkspace workspace, string libraryId, string key) =>
        workspace.RowsOf(libraryId).Single(row => row.Row.Key == LibraryTermKey.From(key));

    public void Dispose() => Fixture.Dispose();

    /// <summary>What one Save did. <see cref="Committed"/> is the catalog read after a Save that stands.</summary>
    internal sealed record SaveResult(
        LibraryChangeSet Changes, LibrarySaveOutcome Outcome, LibraryCatalog? CommittedOrNull, Exception? CommitFailure, bool MarkedSaved)
    {
        public LibraryCatalog Committed =>
            CommittedOrNull ?? throw new InvalidOperationException($"The Save did not stand ({Outcome.Status}), so no committed catalog was read.");
    }
}
