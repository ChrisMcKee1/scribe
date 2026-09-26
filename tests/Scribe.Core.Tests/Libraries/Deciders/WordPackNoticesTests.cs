using Scribe.Core.Libraries;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests.Libraries.Deciders;

public sealed class WordPackNoticesTests
{
    public static TheoryData<LibraryPrepareResult, WordPackNoticeSeverity, string, WordPackNoticeAction[]> PrepareCases => new()
    {
        { new(LibraryPrepareStatus.PreviousSaveUnfinished, null, [], LibraryIoFailure.None), WordPackNoticeSeverity.Warning, "Scribe is still finishing an earlier save. Try again in a moment.", [] },
        { new(LibraryPrepareStatus.PreviousSaveUnfinished, null, [], LibraryIoFailure.SharingViolation), WordPackNoticeSeverity.Warning, "A word pack file is open in another app, so Scribe can't finish saving. Close it there and try again.", [WordPackNoticeAction.Retry] },
        { new(LibraryPrepareStatus.PreviousSaveUnfinished, null, [], LibraryIoFailure.AccessDenied), WordPackNoticeSeverity.Warning, "Scribe can't finish saving a word pack file because access was denied.", [WordPackNoticeAction.Retry] },
        { new(LibraryPrepareStatus.PreviousSaveUnfinished, null, [], LibraryIoFailure.DiskFull), WordPackNoticeSeverity.Warning, "Scribe can't finish saving a word pack file because the disk is full.", [WordPackNoticeAction.Retry] },
        { new(LibraryPrepareStatus.ReadOnly, null, []), WordPackNoticeSeverity.Error, "Word packs were changed by a newer version of Scribe. Update Scribe to change them here.", [] },
        { new(LibraryPrepareStatus.OutsideEdit, null, ["GitHub"]), WordPackNoticeSeverity.Warning, "\"GitHub\" changed outside Scribe while you were editing it.", [WordPackNoticeAction.KeepEditing, WordPackNoticeAction.ReloadSavedVersion, WordPackNoticeAction.SaveDraftAsNew] },
        { new(LibraryPrepareStatus.Stale, null, []), WordPackNoticeSeverity.Info, "Word packs changed outside this window, so Scribe reloaded them. Your edits are still here.", [] },
        { new(LibraryPrepareStatus.Failed, null, []), WordPackNoticeSeverity.Error, "Couldn't save your changes. Your edits are still here.", [WordPackNoticeAction.Retry, WordPackNoticeAction.SaveCopy] },
    };

    [Theory]
    [MemberData(nameof(PrepareCases))]
    public void Prepare_notices_use_contract_copy(LibraryPrepareResult result, WordPackNoticeSeverity severity, string text, WordPackNoticeAction[] actions)
    {
        var notice = WordPackNotices.FromPrepare(result);
        Assert.Equal(severity, notice.Severity);
        Assert.Equal(text, notice.Text);
        Assert.Equal(actions, notice.Actions);
    }

    [Theory]
    [InlineData(LibrarySaveStatus.AppliedAwaitingRelease, WordPackNoticeSeverity.Warning, "Saved. A word pack file is open in another app, so Scribe will finish saving it after you close it there.")]
    [InlineData(LibrarySaveStatus.CommitUnknown, WordPackNoticeSeverity.Warning, "Scribe couldn't confirm that your word pack changes were saved. It will finish saving them, and your edits stay here until it has.")]
    [InlineData(LibrarySaveStatus.NotCommitted, WordPackNoticeSeverity.Error, "Couldn't save your changes. Your edits are still here.")]
    [InlineData(LibrarySaveStatus.Superseded, WordPackNoticeSeverity.Info, "Word packs changed outside this window, so Scribe reloaded them. Your edits are still here.")]
    public void Save_status_notices_use_contract_copy(LibrarySaveStatus status, WordPackNoticeSeverity severity, string text)
    {
        var notice = WordPackNotices.FromSaveStatus(status);
        Assert.Equal(severity, notice.Severity);
        Assert.Equal(text, notice.Text);
    }

    [Fact]
    public void Save_applied_during_a_dictation_uses_contract_copy()
    {
        var notice = WordPackNotices.FromSaveStatus(LibrarySaveStatus.Applied, duringDictation: true);

        Assert.Equal(WordPackNoticeSeverity.Info, notice.Severity);
        Assert.Equal("Saved. Applies from the next dictation.", notice.Text);
        Assert.Empty(notice.Actions);
    }

    public static TheoryData<WordPackNotice, WordPackNoticeSeverity, string, WordPackNoticeAction[]> OtherRows => new()
    {
        { WordPackNotices.FromSettlement(WordPackSettlement.Saved), WordPackNoticeSeverity.Info, "Your word pack changes were saved.", [] },
        { WordPackNotices.FromSettlement(WordPackSettlement.NotSaved), WordPackNoticeSeverity.Warning, "Your word pack changes weren't saved. Your edits are still here.", [] },
        { WordPackNotices.FromOutsideEdit(null), WordPackNoticeSeverity.Warning, "A word pack changed outside Scribe while you were editing it.", [WordPackNoticeAction.KeepEditing, WordPackNoticeAction.ReloadSavedVersion, WordPackNoticeAction.SaveDraftAsNew] },
        { WordPackNotices.FromLoad(WordPackLoadState.AwaitingRelease, "GitHub"), WordPackNoticeSeverity.Warning, "\"GitHub\" is open in another app. Scribe keeps using its saved words; close it there to make changes.", [] },
        { WordPackNotices.FromKeptVersion(LibraryKeptVersionKind.OutsideVersion, "GitHub"), WordPackNoticeSeverity.Info, "\"GitHub\" changed outside Scribe after you started saving. Your version is saved, and the other one is kept as a new word pack, turned off.", [] },
        { WordPackNotices.FromKeptVersion(LibraryKeptVersionKind.SavedUnderNewId, "Release notes"), WordPackNoticeSeverity.Info, "Another app created a file where Scribe was saving \"Release notes\". Both are kept: yours is saved as a separate word pack.", [] },
        { WordPackNotices.FromKeptVersion(LibraryKeptVersionKind.EditsSetAside, "GitHub"), WordPackNoticeSeverity.Info, "Edits to \"GitHub\" changed outside Scribe. Your saved edits are in use, and the other version was set aside.", [] },
        { WordPackNotices.FromLoad(WordPackLoadState.PausedBuiltIn, "GitHub"), WordPackNoticeSeverity.Warning, "Your edits to \"GitHub\" couldn't be read. The file with them hasn't been changed.", [WordPackNoticeAction.RestorePreviousCopy, WordPackNoticeAction.BackUpAndReset] },
        { WordPackNotices.FromLoad(WordPackLoadState.PartlyReadable, "Team terms"), WordPackNoticeSeverity.Warning, "Some rows of \"Team terms\" couldn't be read, so it can't be edited here. Import the file again to see them.", [] },
        { WordPackNotices.FromLoad(WordPackLoadState.AiPermissionsLost), WordPackNoticeSeverity.Warning, "Scribe couldn't recover which word packs AI cleanup may use, so none is sent until you choose.", [WordPackNoticeAction.UseTheseChoices] },
        { WordPackNotices.FromLoad(WordPackLoadState.ContentReplaced, "Team terms"), WordPackNoticeSeverity.Warning, "\"Team terms\" changed outside Scribe, so it's off and no longer sent to AI cleanup. Turn it back on to use it.", [] },
        { WordPackNotices.FromLoad(WordPackLoadState.LegacyMarker), WordPackNoticeSeverity.Info, "A built-in spelling is in use, as before this update.", [WordPackNoticeAction.UseMySpelling] },
        { WordPackNotices.FromLoad(WordPackLoadState.Upgrade), WordPackNoticeSeverity.Info, "Your own word packs are still used in AI cleanup, as before this update. Clear any you don't want sent, then save.", [] },
    };

    [Theory]
    [MemberData(nameof(OtherRows))]
    public void Every_remaining_notice_row_uses_contract_copy(
        WordPackNotice notice,
        WordPackNoticeSeverity severity,
        string text,
        WordPackNoticeAction[] actions)
    {
        Assert.Equal(severity, notice.Severity);
        Assert.Equal(text, notice.Text);
        Assert.Equal(actions, notice.Actions);
    }

    [Theory]
    [InlineData(WordPackNoticeAction.Retry, "Try again")]
    [InlineData(WordPackNoticeAction.SaveCopy, "Save a copy of this word pack...")]
    [InlineData(WordPackNoticeAction.KeepEditing, "Keep editing")]
    [InlineData(WordPackNoticeAction.ReloadSavedVersion, "Reload saved version")]
    [InlineData(WordPackNoticeAction.SaveDraftAsNew, "Save my draft as a new word pack")]
    [InlineData(WordPackNoticeAction.RestorePreviousCopy, "Restore the previous copy")]
    [InlineData(WordPackNoticeAction.BackUpAndReset, "Back up and reset")]
    [InlineData(WordPackNoticeAction.UseTheseChoices, "Use these choices")]
    [InlineData(WordPackNoticeAction.UseMySpelling, "Use my spelling")]
    public void Action_labels_are_the_catalog_labels(WordPackNoticeAction action, string label)
    {
        Assert.Equal(label, WordPackNotices.Label(action));
    }
}
