using Scribe.Core.Models;

namespace Scribe.Core.Tray;

public enum DictationProblem
{
    TooQuick,
    NoAudio,
    NoAudioFromDevice,
    OnlySilence,
    OnlySilenceFromDevice,
    MicrophoneMuted,
    MicrophoneUnavailable,
    MicrophoneDisconnected,
    DurationLimit,
    NothingRecognized,
    FocusChanged,
    TypingIncomplete,
    NoSpeechModel,
    RecognitionFailed,
    ModelLoadFailed,
    FallbackMicrophone,
}

public sealed record DictationProblemReport(
    DictationProblem Problem,
    string? Device = null,
    string? ChosenDevice = null,
    string? UsedDevice = null,
    int Minutes = 10,
    long RecordingRevision = 0);

public sealed record DictationProblemNotice(string Title, string Body, TrayNoticeKind Kind, TrayNoticeAction Action = TrayNoticeAction.None);

public enum DictationProblemSurface
{
    None,
    PillOutcome,
    RecordingPill,
    Notice,
    PillAndNotice,
}

public static class DictationProblemRouting
{
    public static DictationProblemSurface Decide(DictationProblem problem, bool recordingIndicatorOn) => problem switch
    {
        DictationProblem.NoSpeechModel or DictationProblem.FocusChanged or DictationProblem.TypingIncomplete => DictationProblemSurface.PillAndNotice,
        DictationProblem.MicrophoneDisconnected or DictationProblem.DurationLimit => DictationProblemSurface.Notice,
        DictationProblem.MicrophoneMuted or DictationProblem.FallbackMicrophone =>
            recordingIndicatorOn ? DictationProblemSurface.RecordingPill : DictationProblemSurface.Notice,
        _ => recordingIndicatorOn ? DictationProblemSurface.PillOutcome : DictationProblemSurface.Notice,
    };
}

public static class DictationProblemText
{
    public static DictationProblemNotice Describe(
        DictationProblem problem,
        HotkeyMode mode = HotkeyMode.Hold,
        string? shortcut = null,
        string? device = null,
        string? chosenDevice = null,
        string? usedDevice = null,
        int minutes = 10)
    {
        var key = string.IsNullOrWhiteSpace(shortcut) ? "your shortcut" : shortcut.Trim();
        var quotedDevice = Quote(Shorten(device ?? "your microphone", 60));
        return problem switch
        {
            DictationProblem.TooQuick => new("Nothing recorded", mode == HotkeyMode.Toggle ? $"That was too quick. Press {key}, speak, then press it again." : $"That was too quick. Hold {key} while you speak, then let go.", TrayNoticeKind.Warning),
            DictationProblem.NoAudio => new("No sound recorded", "Scribe didn't get any sound from your microphone. Check that it's connected, or choose another from the Microphone menu.", TrayNoticeKind.Warning),
            DictationProblem.NoAudioFromDevice => new("No sound recorded", $"Scribe didn't get any sound from {quotedDevice}. Choose another microphone from the Microphone menu.", TrayNoticeKind.Warning),
            DictationProblem.OnlySilence => new("Only silence recorded", "Your microphone may be muted. Unmute it and try again.", TrayNoticeKind.Warning),
            DictationProblem.OnlySilenceFromDevice => new("Only silence recorded", $"{quotedDevice} may be muted. Unmute it and try again.", TrayNoticeKind.Warning),
            DictationProblem.MicrophoneMuted => new("Your microphone is muted", "Unmute it to keep dictating. Scribe is still recording.", TrayNoticeKind.RecordingWarning),
            DictationProblem.MicrophoneUnavailable => new("Couldn't start recording", "Scribe couldn't open your microphone. Check that it's connected, or choose another from the Microphone menu.", TrayNoticeKind.Error),
            DictationProblem.MicrophoneDisconnected => new("Microphone disconnected", "Your microphone stopped during the dictation. Check that it's connected, then try again.", TrayNoticeKind.Warning),
            DictationProblem.DurationLimit => new($"Dictation stopped at {minutes} minutes", $"Scribe stops recording after {minutes} minutes and types what it heard. You can change this in Settings, Advanced.", TrayNoticeKind.Warning),
            DictationProblem.NothingRecognized => new("No words recognized", "Scribe didn't catch any words. Try again, a little closer to the microphone.", TrayNoticeKind.Warning),
            DictationProblem.FocusChanged => new("Couldn't type your dictation", "The window changed before Scribe finished typing. Right-click the Scribe icon and choose Copy last dictation, then paste it.", TrayNoticeKind.Error, TrayNoticeAction.CopyLastDictation),
            DictationProblem.TypingIncomplete => new("Couldn't type your dictation", "This app didn't accept all of the text. Right-click the Scribe icon and choose Copy last dictation, then paste it.", TrayNoticeKind.Error, TrayNoticeAction.CopyLastDictation),
            DictationProblem.NoSpeechModel => new("No speech model", "Choose a speech model in Settings, Advanced, then try again.", TrayNoticeKind.Warning, TrayNoticeAction.OpenSettings),
            DictationProblem.RecognitionFailed => new("Dictation didn't finish", "Something went wrong while Scribe turned your speech into text. Try again. If it keeps happening, save diagnostics in Settings, Diagnostics.", TrayNoticeKind.Error, TrayNoticeAction.OpenSettingsDiagnostics),
            DictationProblem.ModelLoadFailed => new("Speech model didn't load", "Scribe tries again when you dictate. If dictation doesn't work, save diagnostics in Settings, Diagnostics and report the problem.", TrayNoticeKind.Warning, TrayNoticeAction.OpenSettingsDiagnostics),
            DictationProblem.FallbackMicrophone => new("Using another microphone", FallbackMicrophoneBody(chosenDevice, usedDevice), TrayNoticeKind.RecordingWarning),
            _ => new("Dictation didn't finish", "Try again.", TrayNoticeKind.Warning),
        };
    }

    public static DictationProblemNotice Describe(DictationProblemReport report, HotkeyMode mode = HotkeyMode.Hold, string? shortcut = null) =>
        Describe(report.Problem, mode, shortcut, report.Device, report.ChosenDevice, report.UsedDevice, report.Minutes);

    public static string? PillLine(DictationProblemReport report, HotkeyMode mode = HotkeyMode.Hold) =>
        PillLine(report.Problem, mode, report.Minutes);

    public static string? PillLine(DictationProblem problem, HotkeyMode mode = HotkeyMode.Hold, int minutes = 10) => problem switch
    {
        DictationProblem.TooQuick => mode == HotkeyMode.Toggle
            ? "Press, speak, press again"
            : "Hold the shortcut to speak",
        DictationProblem.NoAudio => "Check your microphone",
        DictationProblem.NoAudioFromDevice => "Try another microphone",
        DictationProblem.OnlySilence or DictationProblem.OnlySilenceFromDevice => "Microphone may be muted",
        DictationProblem.MicrophoneMuted => "Microphone muted",
        DictationProblem.MicrophoneUnavailable => "Microphone unavailable",
        DictationProblem.DurationLimit => $"Stopped at {minutes} minutes",
        DictationProblem.NothingRecognized => "No words heard, try again",
        DictationProblem.FocusChanged or DictationProblem.TypingIncomplete => "Copy it from the tray menu",
        DictationProblem.NoSpeechModel => "No speech model",
        DictationProblem.RecognitionFailed => "Something went wrong",
        DictationProblem.ModelLoadFailed => "Speech model didn't load",
        DictationProblem.FallbackMicrophone => "Using the default mic",
        DictationProblem.MicrophoneDisconnected => null,
        _ => null,
    };

    private static string FallbackMicrophoneBody(string? chosenDevice, string? usedDevice)
    {
        if (string.IsNullOrWhiteSpace(chosenDevice) || string.IsNullOrWhiteSpace(usedDevice))
        {
            return "Your chosen microphone isn't available, so Scribe is recording from the Windows default microphone. To choose another, right-click the Scribe icon and choose Microphone.";
        }

        return $"{Quote(Shorten(chosenDevice, 40))} isn't available, so Scribe is recording from {Quote(Shorten(usedDevice, 40))}, the Windows default. To choose another, right-click the Scribe icon and choose Microphone.";
    }

    private static string Quote(string value) => $"\"{value}\"";
    private static string Shorten(string value, int max) => value.Length <= max ? value : value[..max];
}
