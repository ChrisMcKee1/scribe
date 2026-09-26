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
    [InlineData(FailureStage.AudioCapture, "Audio capture")]
    [InlineData(FailureStage.VoiceActivityDetection, "Voice activity detection")]
    [InlineData(FailureStage.SpeechRecognition, "Speech recognition")]
    [InlineData(FailureStage.DictionaryAndSnippets, "Dictionary and snippets")]
    [InlineData(FailureStage.TextInsertion, "Text insertion")]
    public void Try_dictation_stage_names_are_fixed(FailureStage stage, string expected)
    {
        Assert.Equal(expected, TryDictationSummary.StageName(stage));
    }

    [Fact]
    public void Try_dictation_summary_uses_fixed_failure_copy_not_raw_reason()
    {
        var text = TryDictationSummary.Describe(new TryDictationSummaryInput(
            Success: false,
            StoppedAt: FailureStage.TextInsertion));

        Assert.Equal("Stopped at Text insertion. Scribe could not type into the app. Copy the recovery text from the tray.", text);
        Assert.DoesNotContain("raw", text);
    }

    [Fact]
    public void Try_dictation_success_and_special_failures_match_the_plan()
    {
        Assert.Equal(
            "Done. Processing took 1.1 seconds. AI cleanup: on, Qwen3 on this PC.",
            TryDictationSummary.Describe(new TryDictationSummaryInput(true, 1.1, true, "Qwen3", "on this PC")));
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
    }
}
