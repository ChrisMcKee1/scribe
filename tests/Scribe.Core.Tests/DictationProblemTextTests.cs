using System.Reflection;
using Scribe.Core.Models;
using Scribe.Core.Tray;

namespace Scribe.Core.Tests;

public sealed class DictationProblemTextTests
{
    [Fact]
    public void The_pill_outcome_carries_every_controller_error_but_a_disconnect()
    {
        // Overlay stream OV: every RaiseError site ends in an outcome the pill shows, except OnCaptureFaulted, which is
        // raised mid-recording while processing goes on to describe the insertion (coordinator's per-path list at 885175f).
        foreach (var problem in Enum.GetValues<DictationProblem>())
        {
            Assert.Equal(problem != DictationProblem.MicrophoneDisconnected, DictationProblemText.CarriedByPillOutcome(problem));
        }
    }

    public static IEnumerable<object?[]> ProblemRows()
    {
        yield return Row(DictationProblem.MicrophoneMuted, null, null, null, 10, HotkeyMode.Hold, "Page Down", "Your microphone is muted", "Unmute it to keep dictating. Scribe is still recording.", TrayNoticeKind.RecordingWarning, true, "Microphone muted", TrayNoticeAction.None);
        yield return Row(DictationProblem.MicrophoneUnavailable, null, null, null, 10, HotkeyMode.Hold, "Page Down", "Couldn't start recording", "Scribe couldn't open your microphone. Check that it's connected, or choose another from the Microphone menu.", TrayNoticeKind.Error, false, null, TrayNoticeAction.None);
        yield return Row(DictationProblem.MicrophoneDisconnected, null, null, null, 10, HotkeyMode.Hold, "Page Down", "Microphone disconnected", "Your microphone stopped during the dictation. Check that it's connected, then try again.", TrayNoticeKind.Warning, false, null, TrayNoticeAction.None);
        yield return Row(DictationProblem.DurationLimit, null, null, null, 3, HotkeyMode.Hold, "Page Down", "Dictation stopped at 3 minutes", "Scribe stops recording after 3 minutes and types what it heard. You can change this in Settings, Advanced.", TrayNoticeKind.Warning, false, null, TrayNoticeAction.None);
        yield return Row(DictationProblem.TooQuick, null, null, null, 10, HotkeyMode.Hold, "Page Down", "Nothing recorded", "That was too quick. Hold Page Down while you speak, then let go.", TrayNoticeKind.Warning, false, null, TrayNoticeAction.None);
        yield return Row(DictationProblem.TooQuick, null, null, null, 10, HotkeyMode.Toggle, "Page Down", "Nothing recorded", "That was too quick. Press Page Down, speak, then press it again.", TrayNoticeKind.Warning, false, null, TrayNoticeAction.None);
        yield return Row(DictationProblem.NoAudio, null, null, null, 10, HotkeyMode.Hold, "Page Down", "No sound recorded", "Scribe didn't get any sound from your microphone. Check that it's connected, or choose another from the Microphone menu.", TrayNoticeKind.Warning, false, null, TrayNoticeAction.None);
        yield return Row(DictationProblem.NoAudioFromDevice, "Jabra", null, null, 10, HotkeyMode.Hold, "Page Down", "No sound recorded", "Scribe didn't get any sound from \"Jabra\". Choose another microphone from the Microphone menu.", TrayNoticeKind.Warning, false, null, TrayNoticeAction.None);
        yield return Row(DictationProblem.NothingRecognized, null, null, null, 10, HotkeyMode.Hold, "Page Down", "No words recognized", "Scribe didn't catch any words. Try again, a little closer to the microphone.", TrayNoticeKind.Warning, false, null, TrayNoticeAction.None);
        yield return Row(DictationProblem.FocusChanged, null, null, null, 10, HotkeyMode.Hold, "Page Down", "Couldn't type your dictation", "The window changed before Scribe finished typing. Right-click the Scribe icon and choose Copy last dictation, then paste it.", TrayNoticeKind.Error, false, null, TrayNoticeAction.CopyLastDictation);
        yield return Row(DictationProblem.TypingIncomplete, null, null, null, 10, HotkeyMode.Hold, "Page Down", "Couldn't type your dictation", "This app didn't accept all of the text. Right-click the Scribe icon and choose Copy last dictation, then paste it.", TrayNoticeKind.Error, false, null, TrayNoticeAction.CopyLastDictation);
        yield return Row(DictationProblem.NoSpeechModel, null, null, null, 10, HotkeyMode.Hold, "Page Down", "No speech model", "Choose a speech model in Settings, Advanced, then try again.", TrayNoticeKind.Warning, false, null, TrayNoticeAction.OpenSettings);
        yield return Row(DictationProblem.RecognitionFailed, null, null, null, 10, HotkeyMode.Hold, "Page Down", "Dictation didn't finish", "Something went wrong while Scribe turned your speech into text. Try again. If it keeps happening, save diagnostics in Settings, Diagnostics.", TrayNoticeKind.Error, false, null, TrayNoticeAction.OpenSettingsDiagnostics);
        yield return Row(DictationProblem.ModelLoadFailed, null, null, null, 10, HotkeyMode.Hold, "Page Down", "Speech model didn't load", "Scribe tries again when you dictate. If dictation doesn't work, save diagnostics in Settings, Diagnostics and report the problem.", TrayNoticeKind.Warning, false, null, TrayNoticeAction.OpenSettingsDiagnostics);
        yield return Row(DictationProblem.OnlySilence, null, null, null, 10, HotkeyMode.Hold, "Page Down", "Only silence recorded", "Your microphone may be muted. Unmute it and try again.", TrayNoticeKind.Warning, false, null, TrayNoticeAction.None);
        yield return Row(DictationProblem.OnlySilenceFromDevice, "Jabra", null, null, 10, HotkeyMode.Hold, "Page Down", "Only silence recorded", "\"Jabra\" may be muted. Unmute it and try again.", TrayNoticeKind.Warning, false, null, TrayNoticeAction.None);
        yield return Row(DictationProblem.FallbackMicrophone, null, "Laptop", "Jabra", 10, HotkeyMode.Hold, "Page Down", "Using another microphone", "\"Laptop\" isn't available, so Scribe is recording from \"Jabra\", the Windows default. To choose another, right-click the Scribe icon and choose Microphone.", TrayNoticeKind.RecordingWarning, true, "Using default mic", TrayNoticeAction.None);
        yield return Row(DictationProblem.FallbackMicrophone, null, null, null, 10, HotkeyMode.Hold, "Page Down", "Using another microphone", "Your chosen microphone isn't available, so Scribe is recording from the Windows default microphone. To choose another, right-click the Scribe icon and choose Microphone.", TrayNoticeKind.RecordingWarning, true, "Using default mic", TrayNoticeAction.None);
    }

    [Theory]
    [MemberData(nameof(ProblemRows))]
    public void Every_problem_has_exact_notice_kind_silent_flag_pill_and_action(DictationProblem problem, string? device, string? chosen, string? used, int minutes, HotkeyMode mode, string shortcut, string title, string body, TrayNoticeKind kind, bool silent, string? pill, TrayNoticeAction action)
    {
        var notice = DictationProblemText.Describe(problem, mode, shortcut, device, chosen, used, minutes);

        Assert.Equal(title, notice.Title);
        Assert.Equal(body, notice.Body);
        Assert.Equal(kind, notice.Kind);
        Assert.Equal(silent, TrayNoticeDelivery.For(kind).Silent);
        Assert.Equal(pill, notice.PillText);
        Assert.Equal(action, notice.Action);
    }

    [Fact]
    public void Too_quick_follows_hold_press_and_mouse_button_shortcut_modes()
    {
        Assert.Equal("That was too quick. Hold Page Down while you speak, then let go.", DictationProblemText.Describe(DictationProblem.TooQuick, HotkeyMode.Hold, "Page Down").Body);
        Assert.Equal("That was too quick. Press Page Down, speak, then press it again.", DictationProblemText.Describe(DictationProblem.TooQuick, HotkeyMode.Toggle, "Page Down").Body);
        Assert.Equal("That was too quick. Hold the middle mouse button while you speak, then let go.", DictationProblemText.Describe(DictationProblem.TooQuick, HotkeyMode.Hold, "the middle mouse button").Body);
    }

    [Fact]
    public void FromLegacy_maps_every_current_dictation_controller_string()
    {
        Assert.Equal(DictationProblem.TooQuick, DictationProblemText.FromLegacy("that was too quick, hold the key while you speak"));
        Assert.Equal(DictationProblem.NoAudio, DictationProblemText.FromLegacy("no audio captured. Check your microphone in Settings"));
        Assert.Equal(DictationProblem.NoAudioFromDevice, DictationProblemText.FromLegacy("no audio from 'Jabra'. Pick a different microphone in Settings"));
        Assert.Equal(DictationProblem.OnlySilence, DictationProblemText.FromLegacy("no sound was captured, your microphone may be muted"));
        Assert.Equal(DictationProblem.OnlySilenceFromDevice, DictationProblemText.FromLegacy("no sound from 'Jabra', it may be muted"));
        Assert.Equal(DictationProblem.MicrophoneUnavailable, DictationProblemText.FromLegacy("microphone unavailable"));
        Assert.Equal(DictationProblem.MicrophoneDisconnected, DictationProblemText.FromLegacy("microphone disconnected"));
        Assert.Equal(DictationProblem.NothingRecognized, DictationProblemText.FromLegacy("nothing was recognised, try again"));
        Assert.Equal(DictationProblem.FocusChanged, DictationProblemText.FromLegacy("focus changed, so the dictation was not inserted"));
        Assert.Equal(DictationProblem.TypingIncomplete, DictationProblemText.FromLegacy("text could not be inserted completely"));
        Assert.Equal(DictationProblem.NoSpeechModel, DictationProblemText.FromLegacy("choose a speech model in Settings"));
        Assert.Equal(DictationProblem.RecognitionFailed, DictationProblemText.FromLegacy("transcription failed"));
        Assert.Equal(DictationProblem.ModelLoadFailed, DictationProblemText.FromLegacy("model failed to load, see logs"));
        Assert.Equal(DictationProblem.MicrophoneMuted, DictationProblemText.FromLegacy("microphone is muted, unmute it to dictate"));
        Assert.Equal(DictationProblem.DurationLimit, DictationProblemText.FromLegacy("dictation hit the 10 minute limit and was transcribed"));
        Assert.Equal(DictationProblem.FallbackMicrophone, DictationProblemText.FromLegacy("\"Laptop\" isn't available, so Scribe is using the Windows default microphone."));
    }

    [Fact]
    public void Controller_fallback_microphone_text_stays_mapped()
    {
        var controller = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Scribe.App", "Dictation", "DictationController.cs"));
        Assert.Contains("isn't available, so Scribe is using the Windows default microphone", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void Titles_fit_63_and_bodies_fit_255_with_long_device_names()
    {
        var longName = new string('m', 120);
        foreach (var problem in Enum.GetValues<DictationProblem>())
        {
            var notice = DictationProblemText.Describe(problem, device: longName, chosenDevice: longName, usedDevice: longName);
            Assert.True(notice.Title.Length <= 63, notice.Title);
            Assert.True(notice.Body.Length <= 255, notice.Body);
        }
    }

    [Fact]
    public void Text_has_no_banned_words_or_dashes()
    {
        foreach (var row in ProblemRows())
        {
            var notice = DictationProblemText.Describe((DictationProblem)row[0]!, (HotkeyMode)row[5]!, (string)row[6]!, (string?)row[1], (string?)row[2], (string?)row[3], (int)row[4]!);
            var text = notice.Title + " " + notice.Body;
            Assert.DoesNotContain("could not", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("rule", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("quick add", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("transcribed", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("recognised", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain('\u2013', text);
            Assert.DoesNotContain('\u2014', text);
        }
    }

    [Fact]
    public void Describe_takes_no_exception_and_no_transcript_parameter()
    {
        foreach (var parameter in typeof(DictationProblemText).GetMethods(BindingFlags.Public | BindingFlags.Static).SelectMany(method => method.GetParameters()))
        {
            Assert.False(typeof(Exception).IsAssignableFrom(parameter.ParameterType), parameter.Name);
            Assert.DoesNotContain("transcript", parameter.Name ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static object?[] Row(DictationProblem problem, string? device, string? chosen, string? used, int minutes, HotkeyMode mode, string shortcut, string title, string body, TrayNoticeKind kind, bool silent, string? pill, TrayNoticeAction action) =>
        [problem, device, chosen, used, minutes, mode, shortcut, title, body, kind, silent, pill, action];

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return root.FullName;
    }
}
