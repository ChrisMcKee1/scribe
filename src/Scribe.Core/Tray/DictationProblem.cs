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

public sealed record DictationProblemNotice(string Title, string Body, TrayNoticeKind Kind, string? PillText);

public static class DictationProblemText
{
    public static DictationProblem? FromLegacy(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = text.Trim();
        if (value.StartsWith("that was too quick", StringComparison.OrdinalIgnoreCase)) return DictationProblem.TooQuick;
        if (value.StartsWith("no audio from", StringComparison.OrdinalIgnoreCase)) return DictationProblem.NoAudioFromDevice;
        if (value.StartsWith("no audio captured", StringComparison.OrdinalIgnoreCase)) return DictationProblem.NoAudio;
        if (value.StartsWith("no sound from", StringComparison.OrdinalIgnoreCase)) return DictationProblem.OnlySilenceFromDevice;
        if (value.StartsWith("no sound was captured", StringComparison.OrdinalIgnoreCase)) return DictationProblem.OnlySilence;
        if (value.StartsWith("microphone is muted", StringComparison.OrdinalIgnoreCase)) return DictationProblem.MicrophoneMuted;
        if (value.Equals("microphone unavailable", StringComparison.OrdinalIgnoreCase)) return DictationProblem.MicrophoneUnavailable;
        if (value.Equals("microphone disconnected", StringComparison.OrdinalIgnoreCase)) return DictationProblem.MicrophoneDisconnected;
        if (value.StartsWith("dictation hit", StringComparison.OrdinalIgnoreCase)) return DictationProblem.DurationLimit;
        if (value.StartsWith("nothing was recognised", StringComparison.OrdinalIgnoreCase)) return DictationProblem.NothingRecognized;
        if (value.StartsWith("focus changed", StringComparison.OrdinalIgnoreCase)) return DictationProblem.FocusChanged;
        if (value.StartsWith("text could not be inserted", StringComparison.OrdinalIgnoreCase)) return DictationProblem.TypingIncomplete;
        if (value.StartsWith("choose a speech model", StringComparison.OrdinalIgnoreCase)) return DictationProblem.NoSpeechModel;
        if (value.StartsWith("transcription failed", StringComparison.OrdinalIgnoreCase)) return DictationProblem.RecognitionFailed;
        if (value.StartsWith("model failed to load", StringComparison.OrdinalIgnoreCase)) return DictationProblem.ModelLoadFailed;
        return null;
    }

    public static DictationProblemNotice Describe(DictationProblem problem, HotkeyMode mode = HotkeyMode.Hold, string? shortcut = null, string? device = null, string? chosenDevice = null, string? usedDevice = null, int minutes = 10)
    {
        var key = string.IsNullOrWhiteSpace(shortcut) ? "your shortcut" : shortcut.Trim();
        var quotedDevice = Quote(Shorten(device ?? "your microphone", 60));
        return problem switch
        {
            DictationProblem.TooQuick => new("Nothing recorded", mode == HotkeyMode.Toggle ? $"That was too quick. Press {key}, speak, then press it again." : $"That was too quick. Hold {key} while you speak, then let go.", TrayNoticeKind.Warning, null),
            DictationProblem.NoAudio => new("No sound recorded", "Scribe didn't get any sound from your microphone. Check that it's connected, or choose another from the Microphone menu.", TrayNoticeKind.Warning, null),
            DictationProblem.NoAudioFromDevice => new("No sound recorded", $"Scribe didn't get any sound from {quotedDevice}. Choose another microphone from the Microphone menu.", TrayNoticeKind.Warning, null),
            DictationProblem.OnlySilence => new("Only silence recorded", "Your microphone may be muted. Unmute it and try again.", TrayNoticeKind.Warning, null),
            DictationProblem.OnlySilenceFromDevice => new("Only silence recorded", $"{quotedDevice} may be muted. Unmute it and try again.", TrayNoticeKind.Warning, null),
            DictationProblem.MicrophoneMuted => new("Your microphone is muted", "Unmute it to keep dictating. Scribe is still recording.", TrayNoticeKind.RecordingWarning, "Microphone muted"),
            DictationProblem.MicrophoneUnavailable => new("Couldn't start recording", "Scribe couldn't open your microphone. Check that it's connected, or choose another from the Microphone menu.", TrayNoticeKind.Error, null),
            DictationProblem.MicrophoneDisconnected => new("Microphone disconnected", "Your microphone stopped during the dictation. Check that it's connected, then try again.", TrayNoticeKind.Warning, null),
            DictationProblem.DurationLimit => new($"Dictation stopped at {minutes} minutes", $"Scribe stops recording after {minutes} minutes and types what it heard. You can change this in Settings, Advanced.", TrayNoticeKind.Warning, null),
            DictationProblem.NothingRecognized => new("No words recognized", "Scribe didn't catch any words. Try again, a little closer to the microphone.", TrayNoticeKind.Warning, null),
            DictationProblem.FocusChanged => new("Couldn't type your dictation", "The window changed before Scribe finished typing. Right-click the Scribe icon and choose Copy last dictation, then paste it.", TrayNoticeKind.Error, null),
            DictationProblem.TypingIncomplete => new("Couldn't type your dictation", "This app didn't accept all of the text. Right-click the Scribe icon and choose Copy last dictation, then paste it.", TrayNoticeKind.Error, null),
            DictationProblem.NoSpeechModel => new("No speech model", "Choose a speech model in Settings, Advanced, then try again.", TrayNoticeKind.Warning, null),
            DictationProblem.RecognitionFailed => new("Dictation didn't finish", "Something went wrong while Scribe turned your speech into text. Try again. If it keeps happening, save diagnostics in Settings, Diagnostics.", TrayNoticeKind.Error, null),
            DictationProblem.ModelLoadFailed => new("Speech model didn't load", "Scribe tries again when you dictate. If dictation doesn't work, save diagnostics in Settings, Diagnostics and report the problem.", TrayNoticeKind.Warning, null),
            DictationProblem.FallbackMicrophone => new("Using another microphone", $"{Quote(string.IsNullOrWhiteSpace(chosenDevice) ? "Your chosen microphone" : chosenDevice)} isn't available, so Scribe is recording from {Quote(string.IsNullOrWhiteSpace(usedDevice) ? "the Windows default microphone" : usedDevice)}, the Windows default. To choose another, right-click the Scribe icon and choose Microphone.", TrayNoticeKind.RecordingWarning, "Using default mic"),
            _ => new("Dictation didn't finish", "Try again.", TrayNoticeKind.Warning, null),
        };
    }

    private static string Quote(string value) => $"\"{value}\"";
    private static string Shorten(string value, int max) => value.Length <= max ? value : value[..max];
}
