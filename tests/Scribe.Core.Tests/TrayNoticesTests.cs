using System.Reflection;
using Scribe.Core.Tray;

namespace Scribe.Core.Tests;

public sealed class TrayNoticesTests
{
    public static IEnumerable<object[]> NoticeRows()
    {
        yield return Row(TrayNotices.NothingToCopy(), "Nothing to copy", "There's no dictation to copy yet.", TrayNoticeKind.Info, true, TrayNoticeAction.None);
        yield return Row(TrayNotices.CopiedLastDictation(), "Copied", "Your last dictation is on the clipboard. Press Ctrl+V to paste it.", TrayNoticeKind.Info, true, TrayNoticeAction.None);
        yield return Row(TrayNotices.CopiedRecentDictation(), "Copied", "That dictation is on the clipboard. Press Ctrl+V to paste it.", TrayNoticeKind.Info, true, TrayNoticeAction.None);
        yield return Row(TrayNotices.ClipboardBusy(), "Couldn't copy", "Another app may be using the clipboard. Try again in a moment.", TrayNoticeKind.Error, false, TrayNoticeAction.RetryCopy);
        yield return Row(TrayNotices.QuickAddOpenFailed(), "Couldn't open Add to dictionary", "Try again, or add the word in Settings, Dictionary.", TrayNoticeKind.Error, false, TrayNoticeAction.OpenSettingsDictionary);
        yield return Row(TrayNotices.QuickAddSavedAndClosed(), "Saved to your dictionary", "Scribe uses it from your next dictation.", TrayNoticeKind.Info, true, TrayNoticeAction.None);
        yield return Row(TrayNotices.QuickAddSavedButNotReloaded(), "Saved, but not in use yet", "Scribe saved your word but couldn't start using it. Quit and reopen Scribe to use it.", TrayNoticeKind.Warning, false, TrayNoticeAction.None);
        yield return Row(TrayNotices.TypingFailed(incomplete: false), "Couldn't type your dictation", "The window changed before Scribe finished typing. Right-click the Scribe icon and choose Copy last dictation, then paste it.", TrayNoticeKind.Error, false, TrayNoticeAction.CopyLastDictation);
        yield return Row(TrayNotices.TypingFailed(incomplete: true), "Couldn't type your dictation", "This app didn't accept all of the text. Right-click the Scribe icon and choose Copy last dictation, then paste it.", TrayNoticeKind.Error, false, TrayNoticeAction.CopyLastDictation);
        yield return Row(TrayNotices.SoundSettingsFailed(), "Couldn't open sound settings", "Open Windows Settings > System > Sound.", TrayNoticeKind.Error, false, TrayNoticeAction.None);
        yield return Row(TrayNotices.AiCleanupChangeFailed(), "Couldn't change AI cleanup", "Try again, or change it in Settings, AI cleanup.", TrayNoticeKind.Error, false, TrayNoticeAction.OpenSettingsAiCleanup);
        yield return Row(TrayNotices.MicrophoneChangeFailed(), "Couldn't change the microphone", "Try again, or choose one in Settings, Dictation.", TrayNoticeKind.Error, false, TrayNoticeAction.OpenSettings);
        yield return Row(TrayNotices.NoSpeechModel(), "No speech model", "Choose a speech model in Settings, Advanced, to start dictating.", TrayNoticeKind.Warning, false, TrayNoticeAction.OpenSettings);
        yield return Row(TrayNotices.SpeechModelFailed(), "Speech model didn't load", "Scribe tries again when you dictate. If dictation doesn't work, save diagnostics in Settings, Diagnostics and report the problem.", TrayNoticeKind.Warning, false, TrayNoticeAction.OpenSettingsDiagnostics);
        yield return Row(TrayNotices.UpdateReady("0.4.5"), "Update ready", "Scribe 0.4.5 is downloaded. Right-click the Scribe icon and choose Restart to update, or it installs the next time you quit Scribe.", TrayNoticeKind.Info, true, TrayNoticeAction.None);
        yield return Row(TrayNotices.RestartFailed(), "Couldn't restart to update", "Scribe finishes the update the next time you quit it.", TrayNoticeKind.Error, false, TrayNoticeAction.None);
        yield return Row(TrayNotices.AiCleanupEpisodeFailed(), "AI cleanup isn't working", "Scribe types what it hears until it's fixed. Open Settings, AI cleanup to see why.", TrayNoticeKind.Warning, false, TrayNoticeAction.OpenSettingsAiCleanup);
        yield return Row(TrayNotices.SavedSettingsStartup(), "Using default settings", "Scribe couldn't use your saved settings, so it's using defaults for now. Open Settings, review them and choose Save to keep them.", TrayNoticeKind.Warning, false, TrayNoticeAction.OpenSettings);
        yield return Row(TrayNotices.SavedSettingsTray("AI cleanup"), "Your settings need a review", "Scribe couldn't use your saved settings, so it's using defaults. Open Settings, review them and choose Save. Then you can change AI cleanup here.", TrayNoticeKind.Warning, false, TrayNoticeAction.OpenSettings);
    }

    [Theory]
    [MemberData(nameof(NoticeRows))]
    public void Factories_pin_exact_titles_bodies_kinds_silent_flags_and_actions(TrayNotice notice, string title, string body, TrayNoticeKind kind, bool silent, TrayNoticeAction action)
    {
        Assert.Equal(title, notice.Title);
        Assert.Equal(body, notice.Body);
        Assert.Equal(kind, notice.Kind);
        Assert.Equal(silent, TrayNoticeDelivery.For(kind).Silent);
        Assert.Equal(action, notice.Action);
    }

    [Theory]
    [MemberData(nameof(NoticeRows))]
    public void Titles_fit_63_and_bodies_fit_255(TrayNotice notice, string title, string body, TrayNoticeKind kind, bool silent, TrayNoticeAction action)
    {
        _ = title; _ = body; _ = kind; _ = silent; _ = action;
        Assert.True(notice.Title.Length <= 63, notice.Title);
        Assert.True(notice.Body.Length <= 255, notice.Body);
    }

    [Theory]
    [MemberData(nameof(NoticeRows))]
    public void Shell_text_has_no_banned_words_or_dashes(TrayNotice notice, string title, string body, TrayNoticeKind kind, bool silent, TrayNoticeAction action)
    {
        _ = title; _ = body; _ = kind; _ = silent; _ = action;
        var text = notice.Title + " " + notice.Body;
        Assert.DoesNotContain("could not", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rule", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("quick add", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("transcribed", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("recognised", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain('\u2013', text);
        Assert.DoesNotContain('\u2014', text);
    }

    [Fact]
    public void Factories_take_no_exception_and_no_transcript_parameter()
    {
        var methods = typeof(TrayNotices).GetMethods(BindingFlags.Public | BindingFlags.Static);
        foreach (var parameter in methods.SelectMany(method => method.GetParameters()))
        {
            Assert.False(typeof(Exception).IsAssignableFrom(parameter.ParameterType), parameter.Name);
            Assert.DoesNotContain("transcript", parameter.Name ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("dictation", parameter.Name ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static object[] Row(TrayNotice notice, string title, string body, TrayNoticeKind kind, bool silent, TrayNoticeAction action) => [notice, title, body, kind, silent, action];
}
