using Scribe.Core.Libraries;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests.Libraries.Deciders;

public sealed class WordPackNoticesTests
{
    public static TheoryData<LibraryPrepareResult, string, WordPackNoticeAction[]> PrepareCases => new()
    {
        { new(LibraryPrepareStatus.PreviousSaveUnfinished, null, [], LibraryIoFailure.None), "Scribe is still finishing an earlier save. Try again in a moment.", [] },
        { new(LibraryPrepareStatus.PreviousSaveUnfinished, null, [], LibraryIoFailure.SharingViolation), "A word pack file is open in another app, so Scribe can't finish saving. Close it there and try again.", [WordPackNoticeAction.Retry] },
        { new(LibraryPrepareStatus.PreviousSaveUnfinished, null, [], LibraryIoFailure.AccessDenied), "Scribe can't finish saving a word pack file because access was denied.", [WordPackNoticeAction.Retry] },
        { new(LibraryPrepareStatus.PreviousSaveUnfinished, null, [], LibraryIoFailure.DiskFull), "Scribe can't finish saving a word pack file because the disk is full.", [WordPackNoticeAction.Retry] },
        { new(LibraryPrepareStatus.ReadOnly, null, []), "Word packs were changed by a newer version of Scribe. Update Scribe to change them here.", [] },
        { new(LibraryPrepareStatus.OutsideEdit, null, ["pack"]), "A word pack changed outside Scribe.", [WordPackNoticeAction.KeepEditing, WordPackNoticeAction.ReloadSavedVersion, WordPackNoticeAction.SaveDraftAsNew] },
        { new(LibraryPrepareStatus.Stale, null, []), "Word packs changed, so the page was reloaded. Your edits are still here.", [] },
        { new(LibraryPrepareStatus.Failed, null, []), "Couldn't save your changes. Your edits are still here.", [WordPackNoticeAction.Retry, WordPackNoticeAction.SaveCopy] },
    };

    [Theory]
    [MemberData(nameof(PrepareCases))]
    public void Prepare_notices_use_contract_copy(LibraryPrepareResult result, string text, WordPackNoticeAction[] actions)
    {
        var notice = WordPackNotices.FromPrepare(result);
        Assert.Equal(text, notice.Text);
        Assert.Equal(actions, notice.Actions);
    }

    [Theory]
    [InlineData(LibrarySaveStatus.AppliedAwaitingRelease, "Saved. Close the word pack file in the other app so Scribe can finish.")]
    [InlineData(LibrarySaveStatus.CommitUnknown, "Scribe couldn't confirm that your word pack changes were saved. It will finish saving them, and your edits stay here until it has.")]
    [InlineData(LibrarySaveStatus.NotCommitted, "Couldn't save your changes. Your edits are still here.")]
    [InlineData(LibrarySaveStatus.Superseded, "Word packs changed, so the page was reloaded. Your edits are still here.")]
    public void Save_status_notices_use_contract_copy(LibrarySaveStatus status, string text)
    {
        Assert.Equal(text, WordPackNotices.FromSaveStatus(status).Text);
    }

    [Fact]
    public void Load_notices_name_pack_when_contract_requires_it()
    {
        Assert.Equal("GitHub edits couldn't be read. The original file has not been changed.", WordPackNotices.FromLoad(WordPackLoadState.PausedBuiltIn, "GitHub").Text);
        Assert.Equal("Scribe couldn't read which word packs AI cleanup may use.", WordPackNotices.FromLoad(WordPackLoadState.AiPermissionsLost).Text);
        Assert.Equal("A built-in spelling is in use, as before this update.", WordPackNotices.FromLoad(WordPackLoadState.LegacyMarker).Text);
    }
}
