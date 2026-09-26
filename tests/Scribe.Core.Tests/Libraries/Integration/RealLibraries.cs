using Scribe.Core.Libraries;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;
using Scribe.Core.Tests.Libraries.Storage;

namespace Scribe.Core.Tests.Libraries.Integration;

/// <summary>
/// The five sub-streams wired as the app wires them since the integration commit, over a real data folder and settings
/// database: J's service and journal with X's codec, O's overlay and C's composer, and D's workspace in front, driven the
/// way W2's Save will drive them (capture, prepare, the settings commit, completion from a finally, then MarkSaved).
/// </summary>
internal sealed class RealLibraries : IDisposable
{
    public LibraryStorageFixture Fixture { get; } = new() { RealParts = true };

    public string LibrariesDir => Fixture.LibrariesDir;

    public DictionaryLibraryService Service(ILibraryFileSystem? files = null, IBuiltInLibraryOverlay? overlay = null) =>
        Fixture.Service(files, overlay: overlay);

    /// <summary>A new process over the same folder and database.</summary>
    public DictionaryLibraryService Restart(ILibraryFileSystem? files = null, IBuiltInLibraryOverlay? overlay = null)
    {
        Fixture.Restart();
        return Service(files, overlay);
    }

    public static LibraryWorkspace Workspace(LibraryCatalog catalog) =>
        new(catalog, BuiltInLibraryOverlay.Instance, LibraryDecisions.DefaultAiPermission);

    /// <summary>The shell's Save: capture, prepare, the settings commit, completion from a finally, and MarkSaved when it stands.</summary>
    public SaveResult Save(DictionaryLibraryService service, LibraryWorkspace workspace)
    {
        var capture = workspace.CaptureChangeSet();
        Assert.Empty(capture.Issues);
        var changes = capture.ChangeSet!;
        Assert.False(changes.IsEmpty, "the workspace captured nothing to save");
        var prepared = service.PrepareSave(changes);
        Assert.Equal(LibraryPrepareStatus.Prepared, prepared.Status);
        LibrarySaveOutcome outcome;
        try
        {
            Fixture.Settings.SaveBundle(Fixture.Settings.Load(), null, null, default, prepared.Save!.Payload);
        }
        finally
        {
            outcome = service.CompleteSave(prepared.Save!);
        }

        var committed = service.LoadCatalog();
        if (outcome.Status is LibrarySaveStatus.Applied or LibrarySaveStatus.AppliedAwaitingRelease)
        {
            workspace.MarkSaved(changes, committed);
        }

        return new SaveResult(changes, outcome, committed);
    }

    public static DraftTermRow Row(LibraryWorkspace workspace, string libraryId, string key) =>
        workspace.RowsOf(libraryId).Single(row => row.Row.Key == LibraryTermKey.From(key));

    public void Dispose() => Fixture.Dispose();

    internal sealed record SaveResult(LibraryChangeSet Changes, LibrarySaveOutcome Outcome, LibraryCatalog Committed);
}
