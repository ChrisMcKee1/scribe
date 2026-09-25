namespace Scribe.Core.Tray;

public enum TrayNoticeKind
{
    Info,
    Warning,
    RecordingWarning,
    Error,
}

public enum TrayNoticeAction
{
    None,
    OpenSettings,
    OpenSettingsAiCleanup,
    OpenSettingsDictionary,
    OpenSettingsDiagnostics,
    CopyLastDictation,
    CopyFixedDictation,
    OpenSoundSettings,

    // Repeats the copy that failed; the text stays with the caller, never in the notice.
    RetryCopy,
}

public enum TrayNotificationIcon
{
    Info,
    Warning,
    Error,
}

public sealed record TrayNotice(string Title, string Body, TrayNoticeKind Kind, TrayNoticeAction Action = TrayNoticeAction.None);

public sealed record TrayNoticeDelivery(TrayNotificationIcon Icon, bool Silent, bool RespectQuietTime, bool Realtime)
{
    public static TrayNoticeDelivery For(TrayNoticeKind kind) => kind switch
    {
        TrayNoticeKind.Info => new(TrayNotificationIcon.Info, Silent: true, RespectQuietTime: true, Realtime: false),
        TrayNoticeKind.Warning => new(TrayNotificationIcon.Warning, Silent: false, RespectQuietTime: true, Realtime: false),
        TrayNoticeKind.RecordingWarning => new(TrayNotificationIcon.Warning, Silent: true, RespectQuietTime: false, Realtime: true),
        TrayNoticeKind.Error => new(TrayNotificationIcon.Error, Silent: false, RespectQuietTime: true, Realtime: false),
        _ => new(TrayNotificationIcon.Info, Silent: true, RespectQuietTime: true, Realtime: false),
    };
}

public static class TrayNotices
{
    public static TrayNotice NothingToCopy() => new("Nothing to copy", "There's no dictation to copy yet.", TrayNoticeKind.Info);
    public static TrayNotice CopiedLastDictation() => new("Copied", "Your last dictation is on the clipboard. Press Ctrl+V to paste it.", TrayNoticeKind.Info);
    public static TrayNotice CopiedRecentDictation() => new("Copied", "That dictation is on the clipboard. Press Ctrl+V to paste it.", TrayNoticeKind.Info);
    public static TrayNotice ClipboardBusy() => new("Couldn't copy", "Another app may be using the clipboard. Try again in a moment.", TrayNoticeKind.Error, TrayNoticeAction.RetryCopy);
    public static TrayNotice QuickAddOpenFailed() => new("Couldn't open Add to dictionary", "Try again, or add the word in Settings, Dictionary.", TrayNoticeKind.Error, TrayNoticeAction.OpenSettingsDictionary);
    public static TrayNotice QuickAddSavedAndClosed() => new("Saved to your dictionary", "Scribe uses it from your next dictation.", TrayNoticeKind.Info);
    public static TrayNotice QuickAddSavedButNotReloaded() => new("Saved, but not in use yet", "Scribe saved your word but couldn't start using it. Quit and reopen Scribe to use it.", TrayNoticeKind.Warning);
    public static TrayNotice TypingFailed(bool incomplete) => new("Couldn't type your dictation", incomplete ? "This app didn't accept all of the text. Right-click the Scribe icon and choose Copy last dictation, then paste it." : "The window changed before Scribe finished typing. Right-click the Scribe icon and choose Copy last dictation, then paste it.", TrayNoticeKind.Error, TrayNoticeAction.CopyLastDictation);
    public static TrayNotice SoundSettingsFailed() => new("Couldn't open sound settings", "Open Windows Settings > System > Sound.", TrayNoticeKind.Error);
    public static TrayNotice AiCleanupChangeFailed() => new("Couldn't change AI cleanup", "Try again, or change it in Settings, AI cleanup.", TrayNoticeKind.Error, TrayNoticeAction.OpenSettingsAiCleanup);
    public static TrayNotice MicrophoneChangeFailed() => new("Couldn't change the microphone", "Try again, or choose one in Settings, Dictation.", TrayNoticeKind.Error, TrayNoticeAction.OpenSettings);
    public static TrayNotice NoSpeechModel() => new("No speech model", "Choose a speech model in Settings, Advanced, to start dictating.", TrayNoticeKind.Warning, TrayNoticeAction.OpenSettings);
    public static TrayNotice SpeechModelFailed() => new("Speech model didn't load", "Scribe tries again when you dictate. If dictation doesn't work, save diagnostics in Settings, Diagnostics and report the problem.", TrayNoticeKind.Warning, TrayNoticeAction.OpenSettingsDiagnostics);
    public static TrayNotice UpdateReady(string version) => new("Update ready", $"Scribe {version} is downloaded. Right-click the Scribe icon and choose Restart to update, or it installs the next time you quit Scribe.", TrayNoticeKind.Info);
    public static TrayNotice RestartFailed() => new("Couldn't restart to update", "Scribe finishes the update the next time you quit it.", TrayNoticeKind.Error);
    public static TrayNotice AiCleanupEpisodeFailed() => new("AI cleanup isn't working", "Scribe types what it hears until it's fixed. Open Settings, AI cleanup to see why.", TrayNoticeKind.Warning, TrayNoticeAction.OpenSettingsAiCleanup);
    public static TrayNotice SavedSettingsStartup() => new("Using default settings", "Scribe couldn't use your saved settings, so it's using defaults for now. Open Settings, review them and choose Save to keep them.", TrayNoticeKind.Warning, TrayNoticeAction.OpenSettings);
    public static TrayNotice SavedSettingsTray(string change) => new("Your settings need a review", $"Scribe couldn't use your saved settings, so it's using defaults. Open Settings, review them and choose Save. Then you can change {change} here.", TrayNoticeKind.Warning, TrayNoticeAction.OpenSettings);
}
