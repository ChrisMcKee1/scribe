using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using Scribe.Core.Diagnostics;

namespace Scribe.Core.Tests;

public sealed class UserFacingErrorTests
{
    [Fact]
    public void Local_server_advice_never_quotes_the_exception()
    {
        var error = UserFacingError.Describe(
            "test the connection",
            UserFacingErrorDestination.LocalService,
            new HttpRequestException("connection refused (localhost:11434)", new SocketException((int)SocketError.ConnectionRefused)));

        Assert.Equal(
            "Couldn't test the connection. Scribe could not reach the service on this PC. Check that the service is running and that the address is correct.",
            error.Message);
        Assert.DoesNotContain("localhost", error.Message);
    }

    [Fact]
    public void Internet_service_advice_is_different_from_local_server_advice()
    {
        var error = UserFacingError.Describe(
            "get models",
            UserFacingErrorDestination.InternetService,
            new HttpRequestException(HttpRequestError.NameResolutionError, "host leaked", new SocketException((int)SocketError.HostNotFound)));

        Assert.Equal(
            "Couldn't get models. Scribe could not reach the service. Check your internet connection and the address.",
            error.Message);
    }

    [Fact]
    public void Http_status_can_be_exposed_only_as_a_support_code()
    {
        var error = UserFacingError.Describe(
            "verify the key",
            UserFacingErrorDestination.InternetService,
            new HttpRequestException("forbidden text", null, HttpStatusCode.Forbidden),
            "AI-403");

        Assert.Equal(
            "Couldn't verify the key. The service denied access. Check your internet connection and the address. (AI-403)",
            error.Message);
        Assert.Equal("AI-403", error.SupportCode);
        Assert.DoesNotContain("forbidden text", error.Message);
    }

    [Fact]
    public void File_database_and_windows_failures_have_fixed_copy()
    {
        Assert.Equal(
            "Couldn't save diagnostics. The file is open or unavailable. Close other apps using the file, then try again.",
            UserFacingError.Describe("save diagnostics", UserFacingErrorDestination.File, new IOException("secret path")).Message);
        Assert.Equal(
            "Couldn't save changes. The data file is busy. Close other Scribe windows, then try again.",
            UserFacingError.Describe("save changes", UserFacingErrorDestination.Database, new SqliteException("database is locked", 5)).Message);
        Assert.Equal(
            "Couldn't open sound settings. Windows could not complete the request. Try again from Windows Settings.",
            UserFacingError.Describe("open sound settings", UserFacingErrorDestination.Windows, new Win32Exception(1155, "command")).Message);
    }

    [Theory]
    [InlineData(FailureStage.AudioCapture, "Recording")]
    [InlineData(FailureStage.VoiceActivityDetection, "Trimming silence")]
    [InlineData(FailureStage.SpeechRecognition, "Speech recognition")]
    [InlineData(FailureStage.AiCleanup, "AI cleanup")]
    [InlineData(FailureStage.DictionaryAndSnippets, "Dictionary and snippets")]
    [InlineData(FailureStage.TextInsertion, "Typing")]
    public void Try_dictation_stage_names_are_timing_labels(FailureStage stage, string expected)
    {
        Assert.Equal(expected, TryDictationSummary.StageName(stage));
    }

    [Theory]
    [InlineData(FailureStage.VoiceActivityDetection, "Stopped at Trimming silence. Scribe couldn't finish trimming silence. Try again.")]
    [InlineData(FailureStage.SpeechRecognition, "Stopped at Speech recognition. Scribe couldn't turn the recording into text. Try again.")]
    [InlineData(FailureStage.AiCleanup, "Stopped at AI cleanup. Scribe couldn't finish AI cleanup. Try again, or turn AI cleanup off.")]
    [InlineData(FailureStage.DictionaryAndSnippets, "Stopped at Dictionary and snippets. Scribe couldn't apply your dictionary and snippets. Try again.")]
    [InlineData(FailureStage.TextInsertion, "Stopped at Typing. Scribe couldn't type the text. Click in the box, then try again.")]
    public void Try_dictation_summary_uses_plain_stage_advice(FailureStage stage, string expected)
    {
        var text = TryDictationSummary.Describe(new TryDictationSummaryInput(
            Success: false,
            StoppedAt: stage));

        Assert.Equal(expected, text);
        Assert.DoesNotContain("raw", text);
    }

    [Fact]
    public void Try_dictation_summary_special_cases_empty_post_processing_reason()
    {
        var text = TryDictationSummary.Describe(new TryDictationSummaryInput(
            Success: false,
            StoppedAt: FailureStage.DictionaryAndSnippets,
            Reason: TryDictationSummary.EmptyTextReason));

        Assert.Equal("Stopped at Dictionary and snippets. Your dictionary or snippets removed all of the text, so there was nothing to type.", text);
    }

    [Fact]
    public void Try_dictation_success_and_special_failures_match_the_plan()
    {
        Assert.Equal(
            "Done. Processing took 1.1 seconds. AI cleanup: on, Qwen3 on this PC.",
            TryDictationSummary.Describe(new TryDictationSummaryInput(true, 1.1, true, "AI cleanup: on, Qwen3 on this PC.")));
        Assert.Equal(
            "Done. Processing took 1.1 seconds. AI cleanup: off.",
            TryDictationSummary.Describe(new TryDictationSummaryInput(true, 1.1)));
        Assert.Equal(TryDictationSummary.NoSpeechMessage, TryDictationSummary.Describe(new TryDictationSummaryInput(false, NoSpeech: true)));
        Assert.Equal(TryDictationSummary.MicrophoneProblemMessage, TryDictationSummary.Describe(new TryDictationSummaryInput(false, MicrophoneProblem: true)));
        Assert.Equal(
            "Scribe couldn't record from your microphone. Open Windows Settings > System > Sound, then try again.",
            TryDictationSummary.Describe(new TryDictationSummaryInput(false, MicrophoneProblem: true, Reason: "Open Windows Settings > System > Sound, then try again.")));
        Assert.Equal(
            "Done. AI cleanup didn't finish, so Scribe typed what it heard. Try again later.",
            TryDictationSummary.Describe(new TryDictationSummaryInput(false, CleanupFailed: true, Reason: "Try again later.")));
        Assert.Equal(
            "Done. AI cleanup wasn't ready, so Scribe typed what it heard. Open AI cleanup to check it.",
            TryDictationSummary.Describe(new TryDictationSummaryInput(false, CleanupNotReady: true)));
        Assert.Equal(
            "Done. AI cleanup wasn't ready, so Scribe typed what it heard. Setup is still running.",
            TryDictationSummary.Describe(new TryDictationSummaryInput(false, CleanupNotReady: true, Reason: "Setup is still running.")));
    }

    [Theory]
    [InlineData(true, false, false, false, false, null, TryDictationSummaryTone.Success)]
    [InlineData(false, false, true, false, false, null, TryDictationSummaryTone.Information)]
    [InlineData(false, true, false, false, false, null, TryDictationSummaryTone.Caution)]
    [InlineData(false, false, false, true, false, null, TryDictationSummaryTone.Caution)]
    [InlineData(false, false, false, false, true, null, TryDictationSummaryTone.Critical)]
    [InlineData(false, false, false, false, false, FailureStage.TextInsertion, TryDictationSummaryTone.Critical)]
    public void Try_dictation_summary_tone_matches_state(
        bool success,
        bool cleanupFailed,
        bool noSpeech,
        bool cleanupNotReady,
        bool microphone,
        FailureStage? stopped,
        TryDictationSummaryTone expected)
    {
        var input = new TryDictationSummaryInput(
            success,
            CleanupFailed: cleanupFailed,
            NoSpeech: noSpeech,
            CleanupNotReady: cleanupNotReady,
            MicrophoneProblem: microphone,
            StoppedAt: stopped);

        Assert.Equal(expected, TryDictationSummary.ToneFor(input));
    }
}
