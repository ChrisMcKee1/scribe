using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;

namespace Scribe.Core.Diagnostics;

public enum UserFacingErrorDestination
{
    LocalService,
    InternetService,
    File,
    Database,
    Windows,
}

public sealed record UserFacingErrorDescription(string Message, string? SupportCode = null);

public static class UserFacingError
{
    public static UserFacingErrorDescription Describe(
        string operation,
        UserFacingErrorDestination destination,
        Exception? exception = null,
        string? supportCode = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        var why = Why(destination, exception);
        var advice = Advice(destination, exception);
        var message = $"Couldn't {operation}. {why} {advice}";
        if (!string.IsNullOrWhiteSpace(supportCode))
        {
            message += $" ({supportCode})";
        }

        return new UserFacingErrorDescription(message, supportCode);
    }

    private static string Why(UserFacingErrorDestination destination, Exception? exception) => exception switch
    {
        HttpRequestException http when ExtractStatus(http) is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            "The service denied access.",
        HttpRequestException http when ExtractStatus(http) == HttpStatusCode.NotFound =>
            "The service could not find that address.",
        HttpRequestException http when ExtractStatus(http) == HttpStatusCode.TooManyRequests =>
            "The service is receiving too many requests right now.",
        HttpRequestException or SocketException =>
            destination == UserFacingErrorDestination.LocalService
                ? "Scribe could not reach the service on this PC."
                : "Scribe could not reach the service.",
        SqliteException sqlite when sqlite.SqliteErrorCode == 5 =>
            "The data file is busy.",
        SqliteException =>
            "The data file could not be read or written.",
        UnauthorizedAccessException =>
            "Windows denied access.",
        IOException =>
            "The file is open or unavailable.",
        Win32Exception =>
            "Windows could not complete the request.",
        _ => "Something went wrong.",
    };

    private static string Advice(UserFacingErrorDestination destination, Exception? exception) => destination switch
    {
        UserFacingErrorDestination.LocalService =>
            "Check that the service is running and that the address is correct.",
        UserFacingErrorDestination.InternetService =>
            "Check your internet connection and the address.",
        UserFacingErrorDestination.File =>
            exception is UnauthorizedAccessException
                ? "Choose a folder you can write to, then try again."
                : "Close other apps using the file, then try again.",
        UserFacingErrorDestination.Database =>
            "Close other Scribe windows, then try again.",
        UserFacingErrorDestination.Windows =>
            "Try again from Windows Settings.",
        _ => "Try again.",
    };

    private static HttpStatusCode? ExtractStatus(HttpRequestException exception) =>
        exception.StatusCode;
}

public enum FailureStage
{
    AudioCapture,
    VoiceActivityDetection,
    SpeechRecognition,
    DictionaryAndSnippets,
    TextInsertion,
}

public sealed record TryDictationSummaryInput(
    bool Success,
    double ProcessingSeconds = 0,
    bool AiCleanupEnabled = false,
    string? Model = null,
    string? Where = null,
    bool CleanupFailed = false,
    bool NoSpeech = false,
    bool MicrophoneProblem = false,
    FailureStage? StoppedAt = null);

public static class TryDictationSummary
{
    public const string NoSpeechMessage =
        "Scribe didn't hear any speech. Check your microphone on the Dictation page, then try again.";

    public const string MicrophoneProblemMessage =
        "Scribe couldn't record from your microphone. Check your microphone on the Dictation page, then try again.";

    public static string Describe(TryDictationSummaryInput input)
    {
        if (input.NoSpeech)
        {
            return NoSpeechMessage;
        }

        if (input.MicrophoneProblem)
        {
            return MicrophoneProblemMessage;
        }

        if (input.StoppedAt is { } stage)
        {
            return $"Stopped at {StageName(stage)}. {StageAdvice(stage)}";
        }

        if (input.CleanupFailed)
        {
            return "Done. AI cleanup didn't finish, so Scribe typed what it heard. Try again, or turn AI cleanup off.";
        }

        if (!input.Success)
        {
            return "Your result appears here after you dictate into the box.";
        }

        var seconds = input.ProcessingSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        return input.AiCleanupEnabled
            ? $"Done. Processing took {seconds} seconds. AI cleanup: on, {input.Model} {input.Where}."
            : $"Done. Processing took {seconds} seconds. AI cleanup: off.";
    }

    public static string StageName(FailureStage stage) => stage switch
    {
        FailureStage.AudioCapture => "Audio capture",
        FailureStage.VoiceActivityDetection => "Voice activity detection",
        FailureStage.SpeechRecognition => "Speech recognition",
        FailureStage.DictionaryAndSnippets => "Dictionary and snippets",
        FailureStage.TextInsertion => "Text insertion",
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, null),
    };

    private static string StageAdvice(FailureStage stage) => stage switch
    {
        FailureStage.AudioCapture => "Check your microphone on the Dictation page, then try again.",
        FailureStage.VoiceActivityDetection => "Scribe could not finish trimming silence. Try again.",
        FailureStage.SpeechRecognition => "Scribe could not turn the recording into text. Try again.",
        FailureStage.DictionaryAndSnippets => "Scribe could not apply your dictionary and snippets. Try again.",
        FailureStage.TextInsertion => "Scribe could not type into the app. Copy the recovery text from the tray.",
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, null),
    };
}
