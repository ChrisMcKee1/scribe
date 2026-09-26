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
                FromOutsideEdit(result.OutsideEditIds.FirstOrDefault()),
            LibraryPrepareStatus.Stale =>
                FromStaleOrSuperseded(),
            _ => Error("Couldn't save your changes. Your edits are still here.", WordPackNoticeAction.Retry, WordPackNoticeAction.SaveCopy),
        };

    public static WordPackNotice FromSaveStatus(LibrarySaveStatus status, bool duringDictation = false) =>
        status switch
        {
            LibrarySaveStatus.Applied when duringDictation =>
                Info("Saved. Applies from the next dictation."),
            LibrarySaveStatus.AppliedAwaitingRelease =>
                Warning("Saved. A word pack file is open in another app, so Scribe will finish saving it after you close it there."),
            LibrarySaveStatus.CommitUnknown =>
                Warning("Scribe couldn't confirm that your word pack changes were saved. It will finish saving them, and your edits stay here until it has."),
            LibrarySaveStatus.Superseded =>
                FromStaleOrSuperseded(),
            LibrarySaveStatus.NotCommitted =>
                Error("Couldn't save your changes. Your edits are still here.", WordPackNoticeAction.Retry, WordPackNoticeAction.SaveCopy),
            _ => Info("Settings saved."),
        };

    public static WordPackNotice FromSettlement(WordPackSettlement settlement) =>
        settlement == WordPackSettlement.Saved
            ? Info("Your word pack changes were saved.")
            : Warning("Your word pack changes weren't saved. Your edits are still here.");

    public static WordPackNotice FromLoad(WordPackLoadState state, string? pack = null) =>
        state switch
        {
            WordPackLoadState.AwaitingRelease =>
                Warning($"{QuoteOrThis(pack)} is open in another app. Scribe keeps using its saved words; close it there to make changes."),
            WordPackLoadState.KeptVersion =>
                FromKeptVersion(LibraryKeptVersionKind.OutsideVersion, pack),
            WordPackLoadState.KeptOutsideVersion =>
                FromKeptVersion(LibraryKeptVersionKind.OutsideVersion, pack),
            WordPackLoadState.KeptNewId =>
                FromKeptVersion(LibraryKeptVersionKind.SavedUnderNewId, pack),
            WordPackLoadState.PausedBuiltIn =>
                Warning($"Your edits to {QuoteOrThis(pack)} couldn't be read. The file with them hasn't been changed.", WordPackNoticeAction.RestorePreviousCopy, WordPackNoticeAction.BackUpAndReset),
            WordPackLoadState.PartlyReadable =>
                Warning($"Some rows of {QuoteOrThis(pack)} couldn't be read, so it can't be edited here. Import the file again to see them."),
            WordPackLoadState.AiPermissionsLost =>
                Warning("Scribe couldn't recover which word packs AI cleanup may use, so none is sent until you choose.", WordPackNoticeAction.UseTheseChoices),
            WordPackLoadState.ContentReplaced =>
                Warning($"{QuoteOrThis(pack)} changed outside Scribe, so it's off and no longer sent to AI cleanup. Turn it back on to use it."),
            WordPackLoadState.LegacyMarker =>
                Info("A built-in spelling is in use, as before this update.", WordPackNoticeAction.UseMySpelling),
            WordPackLoadState.Upgrade =>
                Info("Your own word packs are still used in AI cleanup, as before this update. Clear any you don't want sent, then save."),
            _ => Info("Word packs loaded."),
        };

    public static WordPackNotice FromOutsideEdit(string? pack) =>
        string.IsNullOrWhiteSpace(pack)
            ? Warning("A word pack changed outside Scribe while you were editing it.", WordPackNoticeAction.KeepEditing, WordPackNoticeAction.ReloadSavedVersion, WordPackNoticeAction.SaveDraftAsNew)
            : Warning($"{Quote(pack)} changed outside Scribe while you were editing it.", WordPackNoticeAction.KeepEditing, WordPackNoticeAction.ReloadSavedVersion, WordPackNoticeAction.SaveDraftAsNew);

    public static WordPackNotice FromStaleOrSuperseded() =>
        Info("Word packs changed outside this window, so Scribe reloaded them. Your edits are still here.");

    public static WordPackNotice FromKeptVersion(LibraryKeptVersionKind kind, string? pack) =>
        kind switch
        {
            LibraryKeptVersionKind.SavedUnderNewId =>
                Info($"Another app created a file where Scribe was saving {QuoteOrThis(pack)}. Both are kept: yours is saved as a separate word pack."),
            LibraryKeptVersionKind.EditsSetAside =>
                Info($"Edits to {QuoteOrThis(pack)} changed outside Scribe. Your saved edits are in use, and the other version was set aside."),
            _ =>
                Info($"{QuoteOrThis(pack)} changed outside Scribe after you started saving. Your version is saved, and the other one is kept as a new word pack, turned off."),
        };

    public static string Label(WordPackNoticeAction action) =>
        action switch
        {
            WordPackNoticeAction.Retry => "Try again",
            WordPackNoticeAction.SaveCopy => "Save a copy of this word pack...",
            WordPackNoticeAction.KeepEditing => "Keep editing",
            WordPackNoticeAction.ReloadSavedVersion => "Reload saved version",
            WordPackNoticeAction.SaveDraftAsNew => "Save my draft as a new word pack",
            WordPackNoticeAction.RestorePreviousCopy => "Restore the previous copy",
            WordPackNoticeAction.BackUpAndReset => "Back up and reset",
            WordPackNoticeAction.UseTheseChoices => "Use these choices",
            WordPackNoticeAction.UseMySpelling => "Use my spelling",
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
        };

    private static WordPackNotice Info(string text, params WordPackNoticeAction[] actions) =>
        new(WordPackNoticeSeverity.Info, text, actions);

    private static WordPackNotice Warning(string text, params WordPackNoticeAction[] actions) =>
        new(WordPackNoticeSeverity.Warning, text, actions);

    private static WordPackNotice Error(string text, params WordPackNoticeAction[] actions) =>
        new(WordPackNoticeSeverity.Error, text, actions);

    private static string QuoteOrThis(string? pack) => string.IsNullOrWhiteSpace(pack) ? "this word pack" : Quote(pack);

    private static string Quote(string pack) => $"\"{pack}\"";
}
