using System.Reflection;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Scribe.Core.Overlay;
using Scribe.Core.Models;
using Scribe.Core.Tray;

namespace Scribe.Core.Tests;

public sealed class DictationProblemTextTests
{
    public static IEnumerable<object?[]> ProblemRows()
    {
        yield return Row(DictationProblem.MicrophoneMuted, null, null, null, 10, HotkeyMode.Hold, "Page Down", "Your microphone is muted", "Unmute it to keep dictating. Scribe is still recording.", TrayNoticeKind.RecordingWarning, true, TrayNoticeAction.None);
        yield return Row(DictationProblem.MicrophoneUnavailable, null, null, null, 10, HotkeyMode.Hold, "Page Down", "Couldn't start recording", "Scribe couldn't open your microphone. Check that it's connected, or choose another from the Microphone menu.", TrayNoticeKind.Error, false, TrayNoticeAction.None);
        yield return Row(DictationProblem.MicrophoneDisconnected, null, null, null, 10, HotkeyMode.Hold, "Page Down", "Microphone disconnected", "Your microphone stopped during the dictation. Check that it's connected, then try again.", TrayNoticeKind.Warning, false, TrayNoticeAction.None);
        yield return Row(DictationProblem.DurationLimit, null, null, null, 3, HotkeyMode.Hold, "Page Down", "Dictation stopped at 3 minutes", "Scribe stops recording after 3 minutes and types what it heard. You can change this in Settings, Advanced.", TrayNoticeKind.Warning, false, TrayNoticeAction.None);
        yield return Row(DictationProblem.TooQuick, null, null, null, 10, HotkeyMode.Hold, "Page Down", "Nothing recorded", "That was too quick. Hold Page Down while you speak, then let go.", TrayNoticeKind.Warning, false, TrayNoticeAction.None);
        yield return Row(DictationProblem.TooQuick, null, null, null, 10, HotkeyMode.Toggle, "Page Down", "Nothing recorded", "That was too quick. Press Page Down, speak, then press it again.", TrayNoticeKind.Warning, false, TrayNoticeAction.None);
        yield return Row(DictationProblem.NoAudio, null, null, null, 10, HotkeyMode.Hold, "Page Down", "No sound recorded", "Scribe didn't get any sound from your microphone. Check that it's connected, or choose another from the Microphone menu.", TrayNoticeKind.Warning, false, TrayNoticeAction.None);
        yield return Row(DictationProblem.NoAudioFromDevice, "Jabra", null, null, 10, HotkeyMode.Hold, "Page Down", "No sound recorded", "Scribe didn't get any sound from \"Jabra\". Choose another microphone from the Microphone menu.", TrayNoticeKind.Warning, false, TrayNoticeAction.None);
        yield return Row(DictationProblem.NothingRecognized, null, null, null, 10, HotkeyMode.Hold, "Page Down", "No words recognized", "Scribe didn't catch any words. Try again, a little closer to the microphone.", TrayNoticeKind.Warning, false, TrayNoticeAction.None);
        yield return Row(DictationProblem.FocusChanged, null, null, null, 10, HotkeyMode.Hold, "Page Down", "Couldn't type your dictation", "The window changed before Scribe finished typing. Right-click the Scribe icon and choose Copy last dictation, then paste it.", TrayNoticeKind.Error, false, TrayNoticeAction.CopyLastDictation);
        yield return Row(DictationProblem.TypingIncomplete, null, null, null, 10, HotkeyMode.Hold, "Page Down", "Couldn't type your dictation", "This app didn't accept all of the text. Right-click the Scribe icon and choose Copy last dictation, then paste it.", TrayNoticeKind.Error, false, TrayNoticeAction.CopyLastDictation);
        yield return Row(DictationProblem.NoSpeechModel, null, null, null, 10, HotkeyMode.Hold, "Page Down", "No speech model", "Choose a speech model in Settings, Advanced, then try again.", TrayNoticeKind.Warning, false, TrayNoticeAction.OpenSettings);
        yield return Row(DictationProblem.RecognitionFailed, null, null, null, 10, HotkeyMode.Hold, "Page Down", "Dictation didn't finish", "Something went wrong while Scribe turned your speech into text. Try again. If it keeps happening, save diagnostics in Settings, Diagnostics.", TrayNoticeKind.Error, false, TrayNoticeAction.OpenSettingsDiagnostics);
        yield return Row(DictationProblem.ModelLoadFailed, null, null, null, 10, HotkeyMode.Hold, "Page Down", "Speech model didn't load", "Scribe tries again when you dictate. If dictation doesn't work, save diagnostics in Settings, Diagnostics and report the problem.", TrayNoticeKind.Warning, false, TrayNoticeAction.OpenSettingsDiagnostics);
        yield return Row(DictationProblem.OnlySilence, null, null, null, 10, HotkeyMode.Hold, "Page Down", "Only silence recorded", "Your microphone may be muted. Unmute it and try again.", TrayNoticeKind.Warning, false, TrayNoticeAction.None);
        yield return Row(DictationProblem.OnlySilenceFromDevice, "Jabra", null, null, 10, HotkeyMode.Hold, "Page Down", "Only silence recorded", "\"Jabra\" may be muted. Unmute it and try again.", TrayNoticeKind.Warning, false, TrayNoticeAction.None);
        yield return Row(DictationProblem.FallbackMicrophone, null, "Laptop", "Jabra", 10, HotkeyMode.Hold, "Page Down", "Using another microphone", "\"Laptop\" isn't available, so Scribe is recording from \"Jabra\", the Windows default. To choose another, right-click the Scribe icon and choose Microphone.", TrayNoticeKind.RecordingWarning, true, TrayNoticeAction.None);
        yield return Row(DictationProblem.FallbackMicrophone, null, null, null, 10, HotkeyMode.Hold, "Page Down", "Using another microphone", "Your chosen microphone isn't available, so Scribe is recording from the Windows default microphone. To choose another, right-click the Scribe icon and choose Microphone.", TrayNoticeKind.RecordingWarning, true, TrayNoticeAction.None);
    }

    public static IEnumerable<object?[]> PillRows()
    {
        yield return Pill(DictationProblem.TooQuick, HotkeyMode.Hold, 10, "Hold the shortcut to speak");
        yield return Pill(DictationProblem.TooQuick, HotkeyMode.Toggle, 10, "Press, speak, press again");
        yield return Pill(DictationProblem.NoAudio, HotkeyMode.Hold, 10, "Check your microphone");
        yield return Pill(DictationProblem.NoAudioFromDevice, HotkeyMode.Hold, 10, "Try another microphone");
        yield return Pill(DictationProblem.OnlySilence, HotkeyMode.Hold, 10, "Microphone may be muted");
        yield return Pill(DictationProblem.OnlySilenceFromDevice, HotkeyMode.Hold, 10, "Microphone may be muted");
        yield return Pill(DictationProblem.MicrophoneMuted, HotkeyMode.Hold, 10, "Microphone muted");
        yield return Pill(DictationProblem.MicrophoneUnavailable, HotkeyMode.Hold, 10, "Microphone unavailable");
        yield return Pill(DictationProblem.DurationLimit, HotkeyMode.Hold, 10, "Stopped at 10 minutes");
        yield return Pill(DictationProblem.NothingRecognized, HotkeyMode.Hold, 10, "No words heard, try again");
        yield return Pill(DictationProblem.FocusChanged, HotkeyMode.Hold, 10, "Copy it from the tray menu");
        yield return Pill(DictationProblem.TypingIncomplete, HotkeyMode.Hold, 10, "Copy it from the tray menu");
        yield return Pill(DictationProblem.NoSpeechModel, HotkeyMode.Hold, 10, "No speech model");
        yield return Pill(DictationProblem.RecognitionFailed, HotkeyMode.Hold, 10, "Something went wrong");
        yield return Pill(DictationProblem.ModelLoadFailed, HotkeyMode.Hold, 10, "Speech model didn't load");
        yield return Pill(DictationProblem.FallbackMicrophone, HotkeyMode.Hold, 10, "Using the default mic");
        yield return Pill(DictationProblem.MicrophoneDisconnected, HotkeyMode.Hold, 10, null);
    }

    [Theory]
    [MemberData(nameof(ProblemRows))]
    public void Every_problem_has_exact_notice_kind_silent_flag_and_action(DictationProblem problem, string? device, string? chosen, string? used, int minutes, HotkeyMode mode, string shortcut, string title, string body, TrayNoticeKind kind, bool silent, TrayNoticeAction action)
    {
        var notice = DictationProblemText.Describe(problem, mode, shortcut, device, chosen, used, minutes);

        Assert.Equal(title, notice.Title);
        Assert.Equal(body, notice.Body);
        Assert.Equal(kind, notice.Kind);
        Assert.Equal(silent, TrayNoticeDelivery.For(kind).Silent);
        Assert.Equal(action, notice.Action);
    }

    [Theory]
    [MemberData(nameof(PillRows))]
    public void Every_problem_has_the_catalog_pill_line_or_is_notice_only(DictationProblem problem, HotkeyMode mode, int minutes, string? expected)
    {
        Assert.Equal(expected, DictationProblemText.PillLine(problem, mode, minutes));
    }

    [Fact]
    public void Pill_lines_fit_have_no_dashes_and_use_US_spelling()
    {
        foreach (var mode in new[] { HotkeyMode.Hold, HotkeyMode.Toggle })
        {
            foreach (var minutes in new[] { 1, 10, 1440 })
            {
                foreach (var problem in Enum.GetValues<DictationProblem>())
                {
                    var line = DictationProblemText.PillLine(problem, mode, minutes);
                    if (line is null) continue;
                    var budget = problem is DictationProblem.MicrophoneMuted or DictationProblem.FallbackMicrophone
                        ? 134
                        : 150;
                    var weight = problem is DictationProblem.MicrophoneMuted or DictationProblem.FallbackMicrophone
                        ? FontWeights.SemiBold
                        : FontWeights.Normal;
                    var width = Measure(line, weight);
                    Assert.True(width <= budget, $"{problem} {mode} {minutes}: {width:F1} DIP > {budget} DIP: {line}");
                    Assert.DoesNotContain('\u2013', line);
                    Assert.DoesNotContain('\u2014', line);
                    Assert.DoesNotContain("recognised", line, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("key", line, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("button", line, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("Settings", line, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
    }

    [Fact]
    public void Outcome_AI_cleanup_line_and_status_lines_fit_the_measured_pill_budgets()
    {
        Assert.Equal("See Settings, AI cleanup", PillOutcome.CleanupDidNotRun);
        Assert.Equal("Microphone muted", DictationProblemText.PillLine(DictationProblem.MicrophoneMuted));
        Assert.Equal("Using the default mic", DictationProblemText.PillLine(DictationProblem.FallbackMicrophone));

        Assert.True(Measure(PillOutcome.CleanupDidNotRun, FontWeights.Normal) <= 150);
        Assert.True(Measure("Microphone muted", FontWeights.SemiBold) <= 134);
        Assert.True(Measure("Using the default mic", FontWeights.SemiBold) <= 134);
    }

    [Fact]
    public void Too_quick_follows_hold_press_and_mouse_button_shortcut_modes()
    {
        Assert.Equal("That was too quick. Hold Page Down while you speak, then let go.", DictationProblemText.Describe(DictationProblem.TooQuick, HotkeyMode.Hold, "Page Down").Body);
        Assert.Equal("That was too quick. Press Page Down, speak, then press it again.", DictationProblemText.Describe(DictationProblem.TooQuick, HotkeyMode.Toggle, "Page Down").Body);
        Assert.Equal("That was too quick. Hold the middle mouse button while you speak, then let go.", DictationProblemText.Describe(DictationProblem.TooQuick, HotkeyMode.Hold, "the middle mouse button").Body);
    }

    [Fact]
    public void Routing_covers_every_problem_with_indicator_on_and_off()
    {
        foreach (var problem in Enum.GetValues<DictationProblem>())
        {
            var on = DictationProblemRouting.Decide(problem, recordingIndicatorOn: true);
            var off = DictationProblemRouting.Decide(problem, recordingIndicatorOn: false);

            Assert.Equal(Expected(problem, indicatorOn: true), on);
            Assert.Equal(Expected(problem, indicatorOn: false), off);
        }

        static DictationProblemSurface Expected(DictationProblem problem, bool indicatorOn) => problem switch
        {
            DictationProblem.NoSpeechModel or DictationProblem.FocusChanged or DictationProblem.TypingIncomplete => DictationProblemSurface.PillAndNotice,
            DictationProblem.MicrophoneDisconnected or DictationProblem.DurationLimit => DictationProblemSurface.Notice,
            DictationProblem.MicrophoneMuted or DictationProblem.FallbackMicrophone =>
                indicatorOn ? DictationProblemSurface.RecordingPill : DictationProblemSurface.Notice,
            _ => indicatorOn ? DictationProblemSurface.PillOutcome : DictationProblemSurface.Notice,
        };
    }

    [Fact]
    public void Controller_passes_only_typed_problem_reports()
    {
        var controller = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Scribe.App", "Dictation", "DictationController.cs"));

        Assert.Contains("public event Action<DictationProblemReport>? Error", controller, StringComparison.Ordinal);
        Assert.Contains("public event Action<DictationProblemReport>? Warning", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("RaiseError(\"", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("RaiseError($\"", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("RaiseWarning(\"", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("RaiseWarning($\"", controller, StringComparison.Ordinal);
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

    private static object?[] Row(DictationProblem problem, string? device, string? chosen, string? used, int minutes, HotkeyMode mode, string shortcut, string title, string body, TrayNoticeKind kind, bool silent, TrayNoticeAction action) =>
        [problem, device, chosen, used, minutes, mode, shortcut, title, body, kind, silent, action];

    private static object?[] Pill(DictationProblem problem, HotkeyMode mode, int minutes, string? line) =>
        [problem, mode, minutes, line];

    private static double Measure(string text, FontWeight weight) =>
        new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal),
            12,
            Brushes.Black,
            pixelsPerDip: 1).WidthIncludingTrailingWhitespace;

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
