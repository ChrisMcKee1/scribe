using Scribe.Core.Libraries;

namespace Scribe.Core.Settings;

public sealed record WordPackNotice(
    WordPackNoticeSeverity Severity,
    string Text,
    IReadOnlyList<WordPackNoticeAction> Actions);

public enum WordPackNoticeSeverity
{
    Info,
    Warning,
    Error,
}

public enum WordPackNoticeAction
{
    Retry,
    SaveCopy,
    KeepEditing,
    ReloadSavedVersion,
    SaveDraftAsNew,
    RestorePreviousCopy,
    BackUpAndReset,
    UseTheseChoices,
    UseMySpelling,
}

public enum WordPackSettlement
{
    Saved,
    NotSaved,
}

public enum WordPackLoadState
{
    AwaitingRelease,
    KeptVersion,
    KeptOutsideVersion,
    KeptNewId,
    PausedBuiltIn,
    PartlyReadable,
    AiPermissionsLost,
    ContentReplaced,
    LegacyMarker,
    Upgrade,
}

public static class WordPackNotices
{
    public static WordPackNotice FromPrepare(LibraryPrepareResult? result) =>
        result?.Status switch
        {
            LibraryPrepareStatus.PreviousSaveUnfinished when result.Failure == LibraryIoFailure.SharingViolation =>
                Warning("A word pack file is open in another app, so Scribe can't finish saving. Close it there and try again.", WordPackNoticeAction.Retry),
            LibraryPrepareStatus.PreviousSaveUnfinished when result.Failure is LibraryIoFailure.AccessDenied or LibraryIoFailure.DiskFull =>
                Warning(result.Failure == LibraryIoFailure.AccessDenied
                    ? "Scribe can't finish saving a word pack file because access was denied."
                    : "Scribe can't finish saving a word pack file because the disk is full.", WordPackNoticeAction.Retry),
            LibraryPrepareStatus.PreviousSaveUnfinished =>
                Warning("Scribe is still finishing an earlier save. Try again in a moment."),
            LibraryPrepareStatus.ReadOnly =>
                Error("Word packs were changed by a newer version of Scribe. Update Scribe to change them here."),
            LibraryPrepareStatus.OutsideEdit =>
                Warning("A word pack changed outside Scribe.", WordPackNoticeAction.KeepEditing, WordPackNoticeAction.ReloadSavedVersion, WordPackNoticeAction.SaveDraftAsNew),
            LibraryPrepareStatus.Stale =>
                Warning("Word packs changed, so the page was reloaded. Your edits are still here."),
            _ => Error("Couldn't save your changes. Your edits are still here.", WordPackNoticeAction.Retry, WordPackNoticeAction.SaveCopy),
        };

    public static WordPackNotice FromSaveStatus(LibrarySaveStatus status, bool duringDictation = false) =>
        status switch
        {
            LibrarySaveStatus.Applied when duringDictation =>
                Info("Saved. Applies from the next dictation"),
            LibrarySaveStatus.AppliedAwaitingRelease =>
                Warning("Saved. Close the word pack file in the other app so Scribe can finish.", WordPackNoticeAction.Retry),
            LibrarySaveStatus.CommitUnknown =>
                Warning("Scribe couldn't confirm that your word pack changes were saved. It will finish saving them, and your edits stay here until it has."),
            LibrarySaveStatus.Superseded =>
                Warning("Word packs changed, so the page was reloaded. Your edits are still here."),
            LibrarySaveStatus.NotCommitted =>
                Error("Couldn't save your changes. Your edits are still here.", WordPackNoticeAction.Retry, WordPackNoticeAction.SaveCopy),
            _ => Info("Settings saved."),
        };

    public static WordPackNotice FromSettlement(WordPackSettlement settlement) =>
        settlement == WordPackSettlement.Saved
            ? Info("Word pack changes were saved.")
            : Warning("Your word pack changes weren't saved. Your edits are still here.");

    public static WordPackNotice FromLoad(WordPackLoadState state, string? pack = null) =>
        state switch
        {
            WordPackLoadState.AwaitingRelease =>
                Warning("A word pack file is open in another app. Close it there and try again.", WordPackNoticeAction.Retry),
            WordPackLoadState.KeptVersion =>
                Info("Scribe kept the newer word pack file."),
            WordPackLoadState.KeptOutsideVersion =>
                Info("Scribe kept the word pack file changed outside Scribe."),
            WordPackLoadState.KeptNewId =>
                Info("Scribe saved the word pack under a new id."),
            WordPackLoadState.PausedBuiltIn =>
                Warning($"{Name(pack)} edits couldn't be read. The original file has not been changed.", WordPackNoticeAction.RestorePreviousCopy, WordPackNoticeAction.BackUpAndReset),
            WordPackLoadState.PartlyReadable =>
                Warning($"{Name(pack)} has rows Scribe couldn't read. Import the file again to see them."),
            WordPackLoadState.AiPermissionsLost =>
                Warning("Scribe couldn't read which word packs AI cleanup may use.", WordPackNoticeAction.UseTheseChoices),
            WordPackLoadState.ContentReplaced =>
                Warning($"{Name(pack)} was replaced outside Scribe. Review it before saving."),
            WordPackLoadState.LegacyMarker =>
                Info("A built-in spelling is in use, as before this update.", WordPackNoticeAction.UseMySpelling),
            WordPackLoadState.Upgrade =>
                Info("Some built-in word pack words were updated. Review the marked words."),
            _ => Info("Word packs loaded."),
        };

    private static WordPackNotice Info(string text, params WordPackNoticeAction[] actions) =>
        new(WordPackNoticeSeverity.Info, text, actions);

    private static WordPackNotice Warning(string text, params WordPackNoticeAction[] actions) =>
        new(WordPackNoticeSeverity.Warning, text, actions);

    private static WordPackNotice Error(string text, params WordPackNoticeAction[] actions) =>
        new(WordPackNoticeSeverity.Error, text, actions);

    private static string Name(string? pack) => string.IsNullOrWhiteSpace(pack) ? "This word pack" : pack;
}
