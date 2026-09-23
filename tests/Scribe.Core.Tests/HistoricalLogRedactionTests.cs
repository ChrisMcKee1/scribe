using System.Text;
using Scribe.Core.Diagnostics;

namespace Scribe.Core.Tests;

/// <summary>
/// Every line here is synthetic, built in the shape the affected builds rendered: the file logger prefix
/// (<c>HH:mm:ss.fff [Level] ShortCategory: </c>) followed by the message template as it appears in the
/// source at the versions listed in <see cref="HistoricalLogRedaction.KnownFormats"/>.
/// </summary>
public class HistoricalLogRedactionTests : IDisposable
{
    private const string Transcript = "please send the quarterly numbers to Dana before Friday";
    private const string Host = "gpu-box.contoso.internal";
    private const string Deployment = "contoso-legal-gpt";
    private const string SkipTail = " The raw transcription was used.";

    private static readonly string DecodeLine = Line("Debug", "TranscriptionService",
        $"Decoded 4520 ms of audio in 210 ms (RTF 0.05): \"{Transcript}\"");

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "scribe-redaction-test-" + Guid.NewGuid().ToString("N"));

    public HistoricalLogRedactionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static string Line(string level, string category, string message) =>
        $"14:05:10.456 [{level}] {category}: {message}";

    private static string SkipWarning(string status, string detail) =>
        Line("Warning", "DictationController",
            $"AI cleanup was skipped for this dictation: AI cleanup is enabled but {status} ({detail}).{SkipTail}");

    private static string SkipTrace(string status, string detail, string trailer = " final_chars=55 target_app=notepad outcome=injected (1234ms)") =>
        Line("Information", "Trace",
            "trace dictation.process capture_seconds=4.52 vad_enabled=True vad_kept=True recognizer_ready=True " +
            "decode_chars=55 rtf=0.05 ai_cleanup=True ai_outcome=Skipped ai_changed=False " +
            $"ai_skip_reason=AI cleanup is enabled but {status} ({detail}).{trailer}");

    private static (string Text, LogRedactionCounts Counts) Redact(string line)
    {
        var counts = default(LogRedactionCounts);
        return (HistoricalLogRedaction.RedactLine(line, ref counts), counts);
    }

    private static (string[] Lines, LogRedactionCounts Counts) RedactLines(params string[] lines)
    {
        var redactor = new LogLineRedactor();
        return (lines.Select(redactor.Redact).ToArray(), redactor.Counts);
    }

    private static LogRedactionCounts One(LogRedactionKind kind) => default(LogRedactionCounts).Plus(kind);

    // ---- Dictation text -------------------------------------------------------------------------

    [Fact]
    public void The_decode_line_loses_its_transcript_and_keeps_its_timings()
    {
        var (text, counts) = Redact(DecodeLine);

        Assert.Equal(
            Line("Debug", "TranscriptionService",
                $"Decoded 4520 ms of audio in 210 ms (RTF 0.05): \"[transcript redacted, {Transcript.Length} chars]\""),
            text);
        Assert.Equal(One(LogRedactionKind.Transcript), counts);
    }

    [Fact]
    public void Quotes_inside_the_transcript_do_not_end_the_redaction_early()
    {
        var (text, _) = Redact(Line("Debug", "TranscriptionService",
            "Decoded 900 ms of audio in 40 ms (RTF 0.04): \"she said \"no\" twice\""));

        Assert.DoesNotContain("twice", text);
        Assert.DoesNotContain("no\"", text);
    }

    [Fact]
    public void A_decode_line_cut_short_by_a_concurrent_append_is_still_redacted()
    {
        var (text, counts) = Redact(DecodeLine[..^20]);

        Assert.DoesNotContain("quarterly", text);
        Assert.Equal(1, counts.Transcripts);
    }

    [Fact]
    public void Another_cultures_time_separator_still_matches()
    {
        // The timestamp's ':' is the current culture's time separator, which is '.' in some cultures.
        var (text, counts) = Redact("14.05.10.456" + DecodeLine[12..]);

        Assert.DoesNotContain("quarterly", text);
        Assert.Equal(1, counts.Transcripts);
    }

    [Fact]
    public void An_empty_transcript_is_left_alone_and_not_counted()
    {
        var line = Line("Debug", "TranscriptionService", "Decoded 300 ms of audio in 20 ms (RTF 0.07): \"\"");

        var (text, counts) = Redact(line);

        Assert.Same(line, text);
        Assert.Equal(default, counts);
    }

    // ---- AI cleanup skip reasons, in the warning and the Trace line -----------------------------

    public static TheoryData<string, string, string, LogRedactionKind> SkipReasonDetails => new()
    {
        {
            "Initializing", $"Connecting to {Host}\u2026",
            $"Connecting to {HistoricalLogRedaction.HostPlaceholder}\u2026", LogRedactionKind.EndpointAddress
        },
        {
            "Ready", $"'llama3.1:8b' at {Host} ready.",
            $"'llama3.1:8b' at {HistoricalLogRedaction.HostPlaceholder} ready.", LogRedactionKind.EndpointAddress
        },
        {
            "Unavailable",
            $"Couldn't reach 'llama3' at {Host}. Check the endpoint URL (it usually ends in /v1), the model name, " +
            "and the API key if the server needs one.",
            $"Couldn't reach 'llama3' at {HistoricalLogRedaction.HostPlaceholder}. Check the endpoint URL (it usually " +
            "ends in /v1), the model name, and the API key if the server needs one.",
            LogRedactionKind.EndpointAddress
        },
        {
            "Initializing", $"Connecting to Azure deployment '{Deployment}'\u2026",
            $"Connecting to Azure deployment '{HistoricalLogRedaction.AzureNamePlaceholder}'\u2026",
            LogRedactionKind.AzureResourceName
        },
        {
            "Ready", $"Azure deployment '{Deployment}' ready.",
            $"Azure deployment '{HistoricalLogRedaction.AzureNamePlaceholder}' ready.", LogRedactionKind.AzureResourceName
        },
        {
            "Unavailable",
            $"Azure could not find the deployment '{Deployment}' (404). The endpoint is reachable, so check that the " +
            "deployment name matches exactly, including any suffix, and that it lives on this resource.",
            $"Azure could not find the deployment '{HistoricalLogRedaction.AzureNamePlaceholder}' (404). The endpoint " +
            "is reachable, so check that the deployment name matches exactly, including any suffix, and that it " +
            "lives on this resource.",
            LogRedactionKind.AzureResourceName
        },
        {
            // From 0.3.9 an unavailable provider's detail could carry the server's own words: all of it goes.
            "Unavailable", $"The AI endpoint rejected the request (400). model not served by {Host} (see docs). try again",
            HistoricalLogRedaction.FailureTextPlaceholder, LogRedactionKind.ProviderText
        },
    };

    [Theory]
    [MemberData(nameof(SkipReasonDetails))]
    public void The_skip_warning_loses_the_sensitive_part_of_the_detail(
        string status, string detail, string expectedDetail, LogRedactionKind kind)
    {
        var (text, counts) = Redact(SkipWarning(status, detail));

        Assert.Equal(SkipWarning(status, expectedDetail), text);
        Assert.Equal(One(kind), counts);
    }

    [Theory]
    [MemberData(nameof(SkipReasonDetails))]
    public void The_trace_line_loses_the_sensitive_part_of_the_detail_and_keeps_every_other_tag(
        string status, string detail, string expectedDetail, LogRedactionKind kind)
    {
        var (text, counts) = Redact(SkipTrace(status, detail));

        Assert.Equal(SkipTrace(status, expectedDetail), text);
        Assert.Equal(One(kind), counts);
    }

    [Theory]
    [InlineData(" (1234ms)")]
    [InlineData(" outcome=error (88ms): Only part of the text was accepted by Windows.")]
    [InlineData(" final_chars=12 target_app=Some App outcome=injected (40ms)")]
    public void The_trace_reason_ends_where_the_known_trailing_tags_begin(string trailer)
    {
        var detail = $"The AI endpoint returned 502. upstream {Host} (retry). gave up";

        var (text, counts) = Redact(SkipTrace("Unavailable", detail, trailer));

        Assert.Equal(SkipTrace("Unavailable", HistoricalLogRedaction.FailureTextPlaceholder, trailer), text);
        Assert.Equal(1, counts.ProviderText);
    }

    [Fact]
    public void An_error_span_line_at_warning_is_covered_too()
    {
        var line = SkipTrace("Initializing", $"Connecting to {Host}\u2026").Replace("[Information]", "[Warning]") +
            ": Only part of the text was accepted by Windows.";

        var (text, _) = Redact(line);

        Assert.DoesNotContain("contoso", text);
        Assert.EndsWith("(1234ms): Only part of the text was accepted by Windows.", text);
    }

    [Theory]
    [InlineData("10.0.0.12")]
    [InlineData("[::1]")]
    [InlineData("localhost")]
    [InlineData("llm")]
    public void Every_host_shape_is_redacted(string host)
    {
        var (text, counts) = Redact(SkipWarning("Initializing", $"Connecting to {host}\u2026"));

        Assert.DoesNotContain("Connecting to " + host, text);
        Assert.Equal(1, counts.EndpointAddresses);
    }

    [Theory]
    [InlineData("Initializing", "Applying new settings\u2026")]
    [InlineData("Initializing", "Connecting to the GitHub Copilot CLI\u2026")]
    [InlineData("Downloading", "Downloading qwen3-1.7b\u2026 45%")]
    [InlineData("Ready", "'llama3' at custom endpoint ready.")]
    [InlineData("Ready", "Qwen3 1.7B ready.")]
    public void Skip_reasons_without_a_sensitive_value_are_left_exactly_as_they_were(string status, string detail)
    {
        var warning = SkipWarning(status, detail);
        var trace = SkipTrace(status, detail);

        Assert.Same(warning, Redact(warning).Text);
        Assert.Same(trace, Redact(trace).Text);
    }

    // ---- DictationController --------------------------------------------------------------------

    [Fact]
    public void The_cleanup_failure_line_loses_its_whole_reason()
    {
        var line = Line("Warning", "DictationController",
            $"AI cleanup failed (Couldn't reach 'llama3' at {Host}. Check the endpoint URL); using raw transcription.");

        var (text, counts) = Redact(line);

        Assert.Equal(Line("Warning", "DictationController",
            $"AI cleanup failed ({HistoricalLogRedaction.FailureTextPlaceholder}); using raw transcription."), text);
        Assert.Equal(One(LogRedactionKind.ProviderText), counts);
    }

    [Fact]
    public void A_reason_code_in_the_cleanup_failure_line_is_kept()
    {
        // A fixed code cannot carry a sentence or a dotted host, so a later build that logs one keeps it.
        var line = Line("Warning", "DictationController", "AI cleanup failed (endpoint-unreachable); using raw transcription.");

        Assert.Same(line, Redact(line).Text);
    }

    [Fact]
    public void The_profile_line_loses_the_profile_name_and_keeps_the_app()
    {
        var (text, counts) = Redact(Line("Information", "DictationController", "Applying profile 'Contoso merger' for WINWORD."));

        Assert.Equal(Line("Information", "DictationController",
            $"Applying profile '{HistoricalLogRedaction.ProfilePlaceholder}' for WINWORD."), text);
        Assert.Equal(One(LogRedactionKind.UserContent), counts);
    }

    // ---- TextCleanupService ---------------------------------------------------------------------

    [Fact]
    public void The_endpoint_validation_warning_loses_the_host_and_the_exception_messages()
    {
        var (lines, counts) = RedactLines(
            Line("Warning", "TextCleanupService", $"OpenAI-compatible endpoint validation failed for {Host}."),
            $"System.Net.Http.HttpRequestException: No such host is known. ({Host}:443)",
            $" ---> System.Net.Sockets.SocketException (11001): No such host is known.",
            "   at System.Net.Http.HttpConnectionPool.ConnectToTcpHostAsync(String host, Int32 port)",
            "   --- End of inner exception stack trace ---",
            "   at Scribe.Core.Cleanup.TextCleanupService.ValidateAsync()");

        Assert.Equal(
            [
                Line("Warning", "TextCleanupService",
                    $"OpenAI-compatible endpoint validation failed for {HistoricalLogRedaction.HostPlaceholder}."),
                $"System.Net.Http.HttpRequestException: {HistoricalLogRedaction.MessagePlaceholder}",
                $" ---> System.Net.Sockets.SocketException (11001): {HistoricalLogRedaction.MessagePlaceholder}",
                "   at System.Net.Http.HttpConnectionPool.ConnectToTcpHostAsync(String host, Int32 port)",
                "   --- End of inner exception stack trace ---",
                "   at Scribe.Core.Cleanup.TextCleanupService.ValidateAsync()",
            ],
            lines);
        Assert.Equal(new LogRedactionCounts(0, 1, 0, 0, 1), counts);
    }

    [Fact]
    public void The_azure_validation_warning_loses_the_deployment_and_the_endpoint()
    {
        var (text, counts) = Redact(Line("Warning", "TextCleanupService",
            $"Azure deployment validation failed for {Deployment} at https://contoso-ai.openai.azure.com/ (auth=AzureCli)."));

        Assert.Equal(Line("Warning", "TextCleanupService",
            $"Azure deployment validation failed for {HistoricalLogRedaction.AzureNamePlaceholder} at " +
            $"{HistoricalLogRedaction.EndpointPlaceholder} (auth=AzureCli)."), text);
        Assert.Equal(new LogRedactionCounts(0, 1, 1, 0, 0), counts);
    }

    [Fact]
    public void The_older_azure_validation_warning_loses_the_deployment()
    {
        var (text, counts) = Redact(Line("Warning", "TextCleanupService", $"Azure deployment validation failed for {Deployment}."));

        Assert.Equal(Line("Warning", "TextCleanupService",
            $"Azure deployment validation failed for {HistoricalLogRedaction.AzureNamePlaceholder}."), text);
        Assert.Equal(One(LogRedactionKind.AzureResourceName), counts);
    }

    [Fact]
    public void A_remote_providers_probe_failure_message_is_replaced_whole()
    {
        var (text, counts) = Redact(Line("Warning", "TextCleanupService",
            $"AI cleanup initialization probe failed (OpenAiCompatible): The AI endpoint returned 502. upstream {Host}"));

        Assert.Equal(Line("Warning", "TextCleanupService",
            $"AI cleanup initialization probe failed (OpenAiCompatible): {HistoricalLogRedaction.FailureTextPlaceholder}"), text);
        Assert.Equal(One(LogRedactionKind.ProviderText), counts);
    }

    [Fact]
    public void Foundry_local_failures_keep_their_messages_because_they_are_the_evidence()
    {
        var probe = Line("Warning", "TextCleanupService",
            "AI cleanup initialization probe failed (FoundryLocal): This model variant cannot run on this GPU. " +
            "Failed to create a WebGPU compute pipeline.");
        var (lines, counts) = RedactLines(
            probe,
            Line("Warning", "TextCleanupService", "AI cleanup initialization failed (FoundryLocal)."),
            "System.InvalidOperationException: Failed to create a WebGPU compute pipeline: QuickGelu",
            "   at Microsoft.AI.Foundry.Local.Model.LoadAsync()");

        Assert.Equal(probe, lines[0]);
        Assert.Equal("System.InvalidOperationException: Failed to create a WebGPU compute pipeline: QuickGelu", lines[2]);
        Assert.Equal(default, counts);
    }

    [Fact]
    public void A_remote_providers_initialization_failure_loses_its_exception_messages()
    {
        var (lines, counts) = RedactLines(
            Line("Warning", "TextCleanupService", "AI cleanup initialization failed (AzureFoundry)."),
            "Azure.RequestFailedException: The resource https://contoso-ai.openai.azure.com/ returned 403",
            "Status: 403 (Forbidden)",
            "   at Azure.Core.HttpPipelineExtensions.ProcessMessageAsync()");

        Assert.Equal($"Azure.RequestFailedException: {HistoricalLogRedaction.MessagePlaceholder}", lines[1]);
        Assert.Equal(HistoricalLogRedaction.MessagePlaceholder, lines[2]);
        Assert.Equal("   at Azure.Core.HttpPipelineExtensions.ProcessMessageAsync()", lines[3]);
        Assert.Equal(One(LogRedactionKind.ProviderText), counts);
    }

    [Fact]
    public void Chat_completions_fallback_lines_lose_the_deployment_and_the_provider_text()
    {
        var (lines, counts) = RedactLines(
            Line("Debug", "TextCleanupService", $"Chat Completions fallback also failed for {Deployment}: The AI endpoint returned 404."),
            Line("Debug", "TextCleanupService", $"Could not build a Chat Completions agent for {Deployment}."),
            $"System.ArgumentException: Deployment '{Deployment}' is not valid.",
            Line("Information", "TextCleanupService",
                $"Azure deployment {Deployment} does not serve the Responses API; using Chat Completions."));

        Assert.Equal(
            [
                Line("Debug", "TextCleanupService",
                    $"Chat Completions fallback also failed for {HistoricalLogRedaction.AzureNamePlaceholder}: " +
                    HistoricalLogRedaction.FailureTextPlaceholder),
                Line("Debug", "TextCleanupService",
                    $"Could not build a Chat Completions agent for {HistoricalLogRedaction.AzureNamePlaceholder}."),
                $"System.ArgumentException: {HistoricalLogRedaction.MessagePlaceholder}",
                Line("Information", "TextCleanupService",
                    $"Azure deployment {HistoricalLogRedaction.AzureNamePlaceholder} does not serve the Responses API; " +
                    "using Chat Completions."),
            ],
            lines);
        Assert.Equal(new LogRedactionCounts(0, 0, 3, 0, 2), counts);
    }

    [Theory]
    [InlineData("AI cleanup failed or timed out; using raw transcription.")]
    [InlineData("AI cleanup failed or timed out for a segment; using raw text.")]
    [InlineData("AI cleanup failed for a segment; using raw text.")]
    [InlineData("Auxiliary AI completion failed; returning null.")]
    [InlineData("GitHub Copilot session could not be started.")]
    [InlineData("Text action rewrite-for-ai failed at runtime.")]
    public void Provider_call_failures_lose_the_messages_of_their_exceptions(string message)
    {
        var (lines, counts) = RedactLines(
            Line("Debug", "TextCleanupService", message),
            $"System.Net.Http.HttpRequestException: Connection refused ({Host}:11434)",
            "   at System.Net.Http.HttpClient.SendAsync()");

        Assert.Equal($"System.Net.Http.HttpRequestException: {HistoricalLogRedaction.MessagePlaceholder}", lines[1]);
        Assert.Equal("   at System.Net.Http.HttpClient.SendAsync()", lines[2]);
        Assert.Equal(One(LogRedactionKind.ProviderText), counts);
    }

    // ---- Dictionary, snippets, libraries --------------------------------------------------------

    [Fact]
    public void The_invalid_snippet_warning_loses_the_phrase_and_the_exception_messages()
    {
        var (lines, counts) = RedactLines(
            Line("Warning", "TextPostProcessor", "Skipping invalid snippet 12 ('send the merger memo')."),
            "System.ArgumentException: Invalid pattern 'send the merger memo' at offset 3.",
            "   at System.Text.RegularExpressions.Regex..ctor(String pattern)",
            Line("Information", "App", "ordinary line after the entry"));

        Assert.Equal(
            [
                Line("Warning", "TextPostProcessor", $"Skipping invalid snippet 12 ('{HistoricalLogRedaction.SnippetPlaceholder}')."),
                $"System.ArgumentException: {HistoricalLogRedaction.MessagePlaceholder}",
                "   at System.Text.RegularExpressions.Regex..ctor(String pattern)",
                Line("Information", "App", "ordinary line after the entry"),
            ],
            lines);
        Assert.Equal(new LogRedactionCounts(0, 0, 0, 2, 0), counts);
    }

    [Fact]
    public void The_invalid_dictionary_entry_warning_loses_the_pattern_and_the_exception_messages()
    {
        var (lines, counts) = RedactLines(
            Line("Warning", "TextPostProcessor", "Skipping invalid dictionary entry 7 ('(acme')."),
            "System.Text.RegularExpressions.RegexParseException: Invalid pattern '(acme' at offset 5. Not enough )'s.",
            "   at System.Text.RegularExpressions.RegexParser.ScanRegex()");

        Assert.Equal(Line("Warning", "TextPostProcessor",
            $"Skipping invalid dictionary entry 7 ('{HistoricalLogRedaction.DictionaryPlaceholder}')."), lines[0]);
        Assert.Equal($"System.Text.RegularExpressions.RegexParseException: {HistoricalLogRedaction.MessagePlaceholder}", lines[1]);
        Assert.Equal(new LogRedactionCounts(0, 0, 0, 2, 0), counts);
    }

    [Fact]
    public void Dictionary_library_lines_lose_the_name_the_id_and_the_path()
    {
        var (lines, counts) = RedactLines(
            Line("Information", "DictionaryLibraryService", "Imported dictionary library 'Contoso Legal' (42 entries) as contoso-legal."),
            Line("Information", "DictionaryLibraryService", "Removed custom dictionary library contoso-legal."),
            Line("Warning", "DictionaryLibraryService",
                @"Skipping unreadable custom dictionary library at C:\Users\someone\AppData\Local\ScribeData\libraries\contoso-legal.json."),
            @"System.IO.IOException: The process cannot access the file 'C:\Users\someone\contoso-legal.json'.",
            "   at System.IO.File.ReadAllText(String path)");

        Assert.Equal(
            [
                Line("Information", "DictionaryLibraryService",
                    $"Imported dictionary library '{HistoricalLogRedaction.LibraryNamePlaceholder}' (42 entries) as " +
                    $"{HistoricalLogRedaction.LibraryIdPlaceholder}."),
                Line("Information", "DictionaryLibraryService",
                    $"Removed custom dictionary library {HistoricalLogRedaction.LibraryIdPlaceholder}."),
                Line("Warning", "DictionaryLibraryService",
                    $"Skipping unreadable custom dictionary library at {HistoricalLogRedaction.PathPlaceholder}."),
                $"System.IO.IOException: {HistoricalLogRedaction.MessagePlaceholder}",
                "   at System.IO.File.ReadAllText(String path)",
            ],
            lines);
        Assert.Equal(new LogRedactionCounts(0, 0, 0, 5, 0), counts);
    }

    // ---- Azure discovery ------------------------------------------------------------------------

    [Theory]
    [InlineData("Could not list deployments for account {0}.")]
    [InlineData("Could not list Foundry projects for account {0}.")]
    [InlineData("Could not list Cognitive Services accounts in subscription {0}.")]
    [InlineData("Skipping non-text deployment {0} (text-embedding-3-small); capabilities: embeddings.")]
    public void Azure_discovery_lines_lose_the_resource_name(string template)
    {
        var name = "Contoso Production";
        var (text, counts) = Redact(Line("Debug", "AzureFoundryDiscovery", string.Format(template, name)));

        Assert.Equal(Line("Debug", "AzureFoundryDiscovery", string.Format(template, HistoricalLogRedaction.AzureNamePlaceholder)), text);
        Assert.Equal(One(LogRedactionKind.AzureResourceName), counts);
    }

    [Fact]
    public void Azure_discovery_exceptions_lose_their_messages_whatever_the_line()
    {
        var (lines, counts) = RedactLines(
            Line("Warning", "AzureFoundryDiscovery", "Azure sign-in probe failed for the service principal."),
            "Azure.Identity.AuthenticationFailedException: ClientSecretCredential authentication failed: AADSTS700016: " +
            "Application with identifier '11111111-2222' was not found in the directory 'Contoso'.",
            "   at Azure.Identity.CredentialDiagnosticScope.FailWrapAndThrow(Exception ex)");

        Assert.Equal(Line("Warning", "AzureFoundryDiscovery", "Azure sign-in probe failed for the service principal."), lines[0]);
        Assert.Equal($"Azure.Identity.AuthenticationFailedException: {HistoricalLogRedaction.MessagePlaceholder}", lines[1]);
        Assert.Equal(One(LogRedactionKind.ProviderText), counts);
    }

    [Fact]
    public void The_service_principal_rejection_loses_its_message()
    {
        var (text, counts) = Redact(Line("Warning", "AzureFoundryDiscovery",
            "Azure sign-in probe: the service principal was rejected. AADSTS7000215: Invalid client secret provided for 'Contoso'."));

        Assert.Equal(Line("Warning", "AzureFoundryDiscovery",
            $"Azure sign-in probe: the service principal was rejected. {HistoricalLogRedaction.FailureTextPlaceholder}"), text);
        Assert.Equal(One(LogRedactionKind.ProviderText), counts);
    }

    // ---- Overlay --------------------------------------------------------------------------------

    [Theory]
    [InlineData("OverlayWindow.ShowFailed hold=2500ms reason='{0}'")]
    [InlineData("OverlayWindow.ShowRecordingWarning hold=3000ms reason='{0}'")]
    public void Overlay_lines_lose_the_reason_text(string template)
    {
        var (text, counts) = Redact(Line("Information", "Overlay",
            string.Format(template, $"Couldn't reach 'llama3' at {Host}. Check the endpoint URL")));

        Assert.Equal(Line("Information", "Overlay", string.Format(template, HistoricalLogRedaction.ReasonPlaceholder)), text);
        Assert.Equal(One(LogRedactionKind.ProviderText), counts);
    }

    [Theory]
    [InlineData("FAILED")]
    [InlineData("WARNING")]
    public void Overlay_command_failures_lose_the_reason_text_and_keep_the_pipe_exception(string verb)
    {
        var (lines, counts) = RedactLines(
            Line("Warning", "OverlayProcessClient",
                $"Overlay command '{verb} Couldn't reach 'llama3' at {Host}.' failed; tearing down for relaunch."),
            "System.IO.IOException: Pipe is broken.");

        Assert.Equal(Line("Warning", "OverlayProcessClient",
            $"Overlay command '{verb} {HistoricalLogRedaction.ReasonPlaceholder}' failed; tearing down for relaunch."), lines[0]);
        Assert.Equal("System.IO.IOException: Pipe is broken.", lines[1]);
        Assert.Equal(One(LogRedactionKind.ProviderText), counts);
    }

    // ---- Text actions (0.3.12 to 0.3.17) ------------------------------------------------------------

    [Fact]
    public void Text_action_failures_lose_provider_details_and_keep_fixed_ones()
    {
        var fixedDetail = Line("Information", "TextActionController", "Text action rewrite-for-ai failed: EmptySelection. Select some text first.");
        var (lines, counts) = RedactLines(
            Line("Information", "TextActionController",
                $"Text action rewrite-for-ai failed: CallFailed. The AI endpoint returned a server error (502). {Host}"),
            fixedDetail);

        Assert.Equal(Line("Information", "TextActionController",
            $"Text action rewrite-for-ai failed: CallFailed. {HistoricalLogRedaction.FailureTextPlaceholder}"), lines[0]);
        Assert.Same(fixedDetail, lines[1]);
        Assert.Equal(One(LogRedactionKind.ProviderText), counts);
    }

    // ---- Session banner (0.3.11 to 0.4.2) ------------------------------------------------------------

    [Fact]
    public void The_session_banner_cleanup_line_loses_the_azure_deployment()
    {
        const string Rest = " endpoint=configured auth=AzureCli promptStyle=Frontier customPrompt=unset writingStyle=unset";
        var (text, counts) = Redact(Line("Information", "App", $"cleanup: on provider=AzureFoundry deployment={Deployment}{Rest}"));

        Assert.Equal(Line("Information", "App",
            $"cleanup: on provider=AzureFoundry deployment={HistoricalLogRedaction.AzureNamePlaceholder}{Rest}"), text);
        Assert.Equal(One(LogRedactionKind.AzureResourceName), counts);
    }

    [Theory]
    [InlineData("cleanup: on provider=AzureFoundry deployment=configured endpoint=configured auth=AzureCli promptStyle=Frontier")]
    [InlineData("cleanup: on provider=AzureFoundry deployment=unset endpoint=unset auth=AzureCli promptStyle=Frontier")]
    [InlineData("cleanup: on provider=FoundryLocal model=qwen3-1.7b promptStyle=Local customPrompt=unset writingStyle=unset")]
    [InlineData("Scribe started. Hold Right Ctrl to dictate.")]
    public void Banner_and_app_lines_without_a_name_are_left_alone(string message)
    {
        var line = Line("Information", "App", message);

        Assert.Same(line, Redact(line).Text);
    }

    // ---- Settings (0.2.4 to 0.4.2) --------------------------------------------------------------------
    // SettingsWindow.TryLog wrote _log.LogWarning(ex, message): the fixed message, then the exception as the
    // endpoint, Azure or Entra had answered.

    [Theory]
    [InlineData("Could not start Azure sign-in.")] // 0.2.4 to 0.2.8
    [InlineData("Could not list Azure subscriptions for the filter dropdown.")] // 0.2.5 to 0.2.7
    [InlineData("Could not list Azure deployments.")] // 0.2.8 on
    [InlineData("Could not list Azure deployments for an Azure CLI account scope.")] // 0.2.8 on
    [InlineData("Could not install or update Azure CLI.")] // 0.2.9 on
    [InlineData("Could not verify Azure sign-in.")] // 0.2.9 on
    [InlineData("Could not verify the Azure service principal.")] // 0.2.15 on
    [InlineData("Could not verify the Azure API key.")] // 0.3.9 on
    [InlineData("Could not reach the Azure API-key endpoint.")] // 0.3.9 on
    public void Settings_provider_warnings_lose_the_messages_of_their_exceptions(string message)
    {
        var warning = Line("Warning", "SettingsWindow", message);
        var (lines, counts) = RedactLines(
            warning,
            $"System.Net.Http.HttpRequestException: No such host is known. ({Host}:443)",
            " ---> System.Net.Sockets.SocketException (11001): No such host is known.",
            "   at System.Net.Http.HttpConnectionPool.ConnectToTcpHostAsync(String host, Int32 port)",
            "   --- End of inner exception stack trace ---",
            $"Azure.RequestFailedException: The client 'someone@contoso.com' does not have authorization over '/subscriptions/1111/resourceGroups/rg/providers/Microsoft.CognitiveServices/accounts/{Deployment}'.",
            "Status: 403 (Forbidden)",
            "",
            "   at Scribe.App.Settings.SettingsWindow.VerifyAzureApiKeyAsync()");

        Assert.Equal(
            [
                warning,
                $"System.Net.Http.HttpRequestException: {HistoricalLogRedaction.MessagePlaceholder}",
                $" ---> System.Net.Sockets.SocketException (11001): {HistoricalLogRedaction.MessagePlaceholder}",
                "   at System.Net.Http.HttpConnectionPool.ConnectToTcpHostAsync(String host, Int32 port)",
                "   --- End of inner exception stack trace ---",
                $"Azure.RequestFailedException: {HistoricalLogRedaction.MessagePlaceholder}",
                HistoricalLogRedaction.MessagePlaceholder,
                "",
                "   at Scribe.App.Settings.SettingsWindow.VerifyAzureApiKeyAsync()",
            ],
            lines);
        Assert.Same(warning, lines[0]);
        Assert.Equal(One(LogRedactionKind.ProviderText), counts);
    }

    [Fact]
    public void The_ai_report_mail_failure_loses_the_report_its_exception_quoted()
    {
        // .NET 10 puts the file name into a failed shell launch's message, and here the file name was the mailto:.
        var mailto = "mailto:support@mckeesolutions.ai?subject=Scribe%200.4.2%3A%20report%20an%20AI%20result&body=" +
            Uri.EscapeDataString("--- AI OUTPUT ---\n" + Transcript + "\n--- END AI OUTPUT ---");
        var (lines, counts) = RedactLines(
            Line("Warning", "SettingsWindow", "Could not open a mail client for the AI report."),
            $"System.ComponentModel.Win32Exception (1155): An error occurred trying to start process '{mailto}' with " +
            @"working directory 'C:\Program Files\Scribe'. No application is associated with the specified file for this operation.",
            "   at System.Diagnostics.Process.StartWithShellExecuteEx(ProcessStartInfo startInfo)");

        Assert.Equal($"System.ComponentModel.Win32Exception (1155): {HistoricalLogRedaction.MessagePlaceholder}", lines[1]);
        Assert.Equal("   at System.Diagnostics.Process.StartWithShellExecuteEx(ProcessStartInfo startInfo)", lines[2]);
        Assert.DoesNotContain(lines, l => l.Contains("quarterly", StringComparison.Ordinal));
        Assert.Equal(One(LogRedactionKind.Transcript), counts);
    }

    [Theory]
    [InlineData("Could not load the dictionary for Settings.")]
    [InlineData("Could not load dictation history for Settings.")]
    [InlineData("Could not open the folder.")]
    [InlineData("Could not reach the Azure API-key endpoint. (HttpRequestException(NameResolutionError) inner=SocketException(HostNotFound))")]
    [InlineData("Could not verify the Azure API key")]
    public void Other_settings_warnings_and_the_current_shape_keep_their_exception_text(string message)
    {
        // Precision: only the templates that carried an endpoint's, Azure's or the report's text lose anything.
        // The last two are this build's own shape (no exception is attached any more) and a near miss.
        string[] entry =
        [
            Line("Warning", "SettingsWindow", message),
            "Microsoft.Data.Sqlite.SqliteException (0x80004005): SQLite Error 11: 'database disk image is malformed'.",
            "   at Microsoft.Data.Sqlite.SqliteException.ThrowExceptionForRC(Int32 rc, sqlite3 db)",
        ];

        var (lines, counts) = RedactLines(entry);

        Assert.Equal(entry, lines);
        Assert.Equal(default, counts);
    }

    // ---- Shared behavior ------------------------------------------------------------------------

    [Fact]
    public void Redacting_twice_changes_nothing_the_second_time()
    {
        string[] lines =
        [
            DecodeLine,
            SkipWarning("Initializing", $"Connecting to {Host}\u2026"),
            SkipTrace("Ready", $"'llama3' at {Host} ready."),
            SkipWarning("Unavailable", $"Couldn't reach 'llama3' at {Host}. Check the endpoint URL (it usually ends in /v1), x."),
            SkipTrace("Unavailable", $"Azure could not find the deployment '{Deployment}' (404). The endpoint is reachable"),
            SkipWarning("Unavailable", "The AI endpoint returned 502."),
            Line("Warning", "DictationController", "AI cleanup failed (anything at all); using raw transcription."),
            Line("Warning", "TextPostProcessor", "Skipping invalid snippet 3 ('phrase')."),
            "System.ArgumentException: phrase",
            Line("Information", "DictionaryLibraryService", "Imported dictionary library 'Name' (1 entries) as name."),
            Line("Debug", "AzureFoundryDiscovery", "Could not list deployments for account contoso."),
            Line("Information", "Overlay", "OverlayWindow.ShowFailed hold=2500ms reason='x y'"),
            Line("Warning", "SettingsWindow", "Could not verify the Azure API key."),
            $"System.Net.Http.HttpRequestException: No such host is known. ({Host}:443)",
        ];

        var (once, firstCounts) = RedactLines(lines);
        var (twice, secondCounts) = RedactLines(once);

        Assert.Equal(once, twice);
        Assert.True(firstCounts.Total > 0);
        Assert.Equal(default, secondCounts);
    }

    [Fact]
    public void The_next_entry_ends_the_exception_scrub()
    {
        var (lines, _) = RedactLines(
            Line("Warning", "TextPostProcessor", "Skipping invalid snippet 3 ('phrase')."),
            "System.ArgumentException: phrase",
            Line("Warning", "Other", "unrelated warning"),
            "System.Exception: an unrelated exception message");

        Assert.Equal("System.Exception: an unrelated exception message", lines[3]);
    }

    [Theory]
    [InlineData("14:05:09.123 [Information] TranscriptionService: Recognizer warm-up decode completed in 12 ms.")]
    [InlineData("14:05:09.123 [Information] DictationController: Transcribed 55 chars in 0.21s (RTF 0.05).")]
    [InlineData("14:05:09.123 [Debug] Overlay: Decoded 4520 ms of audio in 210 ms (RTF 0.05): \"another category\"")]
    [InlineData("14:05:09.123 [Debug] TranscriptionService: Decoded 4520 ms of audio in 210 ms (RTF 0.05), 55 chars.")]
    [InlineData("Decoded 4520 ms of audio in 210 ms (RTF 0.05): \"no line prefix\"")]
    [InlineData("14:05:10.456 [Information] DictationController: AI cleanup is enabled but Initializing (Connecting to host.example\u2026).")]
    [InlineData("14:05:10.789 [Information] Trace: trace text.inject ai_skip_reason=AI cleanup is enabled but Initializing (Connecting to host.example\u2026). (3ms)")]
    [InlineData("14:05:10.789 [Warning] TextCleanupService: Loading Foundry Local model qwen3-1.7b failed.")]
    [InlineData("14:05:10.789 [Information] DictionaryLibraryService: Loaded 3 dictionary libraries.")]
    [InlineData("14:05:10.789 [Information] Overlay: OverlayWindow.ShowFailed hold=2500ms reasonLength=42")]
    [InlineData("14:05:10.789 [Warning] OverlayProcessClient: Overlay command 'HIDE' failed; tearing down for relaunch.")]
    [InlineData("    at Scribe.Core.Transcription.TranscriptionService.Transcribe(CapturedAudio audio)")]
    [InlineData("")]
    public void Ordinary_lines_are_never_touched(string line)
    {
        var (text, counts) = Redact(line);

        Assert.Same(line, text);
        Assert.Equal(default, counts);
    }

    [Fact]
    public void Every_kind_has_a_documented_format_with_a_version_range()
    {
        foreach (var kind in Enum.GetValues<LogRedactionKind>())
        {
            Assert.Contains(HistoricalLogRedaction.KnownFormats, f => f.Kind == kind);
        }

        Assert.All(HistoricalLogRedaction.KnownFormats, f => Assert.Matches(@"^0\.\d+\.\d+ to 0\.\d+\.\d+$", f.Versions));
    }

    // ---- Copying --------------------------------------------------------------------------------

    [Fact]
    public void Copying_changes_only_the_sensitive_spans_and_preserves_every_other_byte()
    {
        var ordinary1 = "14:05:08.000 [Information] DictationController: dictation #7 start trigger=Standard"u8.ToArray();
        var ordinary2 = "14:05:11.000 [Information] Overlay: pill hidden"u8.ToArray();
        var invalidUtf8 = new byte[] { 0x31, 0x32, 0xC3, 0x28, 0x33 };
        var snippet = Line("Warning", "TextPostProcessor", "Skipping invalid snippet 3 ('merger memo').");
        using var source = new MemoryStream();
        Write(source, ordinary1, "\r\n");
        Write(source, Encoding.UTF8.GetBytes(DecodeLine), "\r\n");
        Write(source, invalidUtf8, "\n");
        Write(source, Encoding.UTF8.GetBytes(snippet), "\r\n");
        Write(source, "System.ArgumentException: merger memo"u8.ToArray(), "\r\n");
        Write(source, "   at X.Y()"u8.ToArray(), "\r\n");
        Write(source, ordinary2, string.Empty);
        source.Position = 0;

        using var target = new MemoryStream();
        var counts = HistoricalLogRedaction.CopyRedacted(source, target);

        using var expected = new MemoryStream();
        Write(expected, ordinary1, "\r\n");
        Write(expected, Encoding.UTF8.GetBytes(Redact(DecodeLine).Text), "\r\n");
        Write(expected, invalidUtf8, "\n");
        Write(expected, Encoding.UTF8.GetBytes(Redact(snippet).Text), "\r\n");
        Write(expected, Encoding.UTF8.GetBytes($"System.ArgumentException: {HistoricalLogRedaction.MessagePlaceholder}"), "\r\n");
        Write(expected, "   at X.Y()"u8.ToArray(), "\r\n");
        Write(expected, ordinary2, string.Empty);

        Assert.Equal(expected.ToArray(), target.ToArray());
        Assert.Equal(new LogRedactionCounts(1, 0, 0, 2, 0), counts);
    }

    [Fact]
    public void A_line_split_across_read_chunks_is_still_found()
    {
        // The copy reads 64 KB at a time; put the decode line across that boundary.
        var filler = new string('x', 64 * 1024 - 40);
        var content = "14:05:08.000 [Information] Scribe: " + filler + "\r\n" + DecodeLine + "\r\n";
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(content));
        using var target = new MemoryStream();

        var counts = HistoricalLogRedaction.CopyRedacted(source, target);

        Assert.Equal(1, counts.Transcripts);
        Assert.DoesNotContain("quarterly", Encoding.UTF8.GetString(target.ToArray()));
    }

    // ---- In-place scrub and its ledger ----------------------------------------------------------

    [Fact]
    public void Scrubbing_rewrites_past_files_that_leak_and_leaves_everything_else_alone()
    {
        var today = new DateOnly(2026, 9, 21);
        var trace = SkipTrace("Initializing", $"Connecting to {Host}\u2026");
        var leaky = WriteLog(today.AddDays(-2), $"ordinary{Environment.NewLine}{DecodeLine}{Environment.NewLine}{trace}{Environment.NewLine}");
        var clean = WriteLog(today.AddDays(-1), $"ordinary only{Environment.NewLine}");
        var current = WriteLog(today, $"{DecodeLine}{Environment.NewLine}");
        var unrelated = Path.Combine(_dir, "notes.txt");
        File.WriteAllText(unrelated, DecodeLine);
        var stamp = new DateTime(2026, 9, 19, 23, 59, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(leaky, stamp);
        File.SetLastWriteTimeUtc(clean, stamp);
        var cleanBytes = File.ReadAllBytes(clean);

        var result = HistoricalLogRedaction.ScrubRetainedFiles(_dir, before: today);

        Assert.Equal(2, result.FilesScanned);
        Assert.Equal(1, result.FilesRewritten);
        Assert.Equal(0, result.FilesSkipped);
        Assert.Equal(new LogRedactionCounts(1, 1, 0, 0, 0), result.Redactions);

        var scrubbed = File.ReadAllText(leaky);
        Assert.DoesNotContain("quarterly", scrubbed);
        Assert.DoesNotContain("contoso", scrubbed);
        Assert.StartsWith("ordinary", scrubbed);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(leaky));

        // A clean file is not rewritten at all, and today's file is never touched: it is still being
        // appended to by both processes.
        Assert.Equal(cleanBytes, File.ReadAllBytes(clean));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(clean));
        Assert.Contains("quarterly", File.ReadAllText(current));
        Assert.Equal(DecodeLine, File.ReadAllText(unrelated));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void The_ledger_skips_files_already_done_until_they_change()
    {
        var today = new DateOnly(2026, 9, 21);
        var leaky = WriteLog(today.AddDays(-3), $"{DecodeLine}{Environment.NewLine}");
        WriteLog(today.AddDays(-2), $"ordinary{Environment.NewLine}");

        var first = HistoricalLogRedaction.ScrubRetainedFiles(_dir, before: today);
        var second = HistoricalLogRedaction.ScrubRetainedFiles(_dir, before: today);

        Assert.Equal(2, first.FilesScanned);
        Assert.Equal(0, second.FilesScanned);
        Assert.Equal(2, second.FilesAlreadyVerified);
        Assert.Equal(default, second.Redactions);

        // A file that changes afterwards is examined again.
        File.AppendAllText(leaky, DecodeLine + Environment.NewLine);
        var third = HistoricalLogRedaction.ScrubRetainedFiles(_dir, before: today);

        Assert.Equal(1, third.FilesScanned);
        Assert.Equal(1, third.FilesRewritten);
        Assert.Equal(1, third.FilesAlreadyVerified);
        Assert.DoesNotContain("quarterly", File.ReadAllText(leaky));
    }

    [Fact]
    public void The_ledger_holds_file_names_sizes_and_times_only()
    {
        var today = new DateOnly(2026, 9, 21);
        WriteLog(today.AddDays(-2), $"{DecodeLine}{Environment.NewLine}");
        WriteLog(today.AddDays(-1), $"ordinary secret-looking words{Environment.NewLine}");

        HistoricalLogRedaction.ScrubRetainedFiles(_dir, before: today);

        var ledger = File.ReadAllLines(Path.Combine(_dir, LogScrubLedger.FileName));
        Assert.Equal($"scribe-redaction-ledger rules={HistoricalLogRedaction.RulesVersion}", ledger[0]);
        Assert.Equal(2, ledger.Length - 1);
        Assert.All(ledger.Skip(1), line => Assert.Matches(@"^scribe-\d{8}\.log \d+ \d+$", line));
        Assert.DoesNotContain(ledger, line => line.Contains("quarterly") || line.Contains("secret"));
    }

    [Theory]
    [InlineData("not a ledger at all\nscribe-20260918.log 1 2\n")]
    [InlineData("scribe-redaction-ledger rules=0\nscribe-20260918.log 999 999\n")]
    public void A_corrupt_ledger_or_one_from_other_rules_is_ignored(string ledgerText)
    {
        var today = new DateOnly(2026, 9, 21);
        WriteLog(today.AddDays(-3), $"{DecodeLine}{Environment.NewLine}");
        File.WriteAllText(Path.Combine(_dir, LogScrubLedger.FileName), ledgerText);

        var result = HistoricalLogRedaction.ScrubRetainedFiles(_dir, before: today);

        Assert.Equal(1, result.FilesScanned);
        Assert.Equal(1, result.FilesRewritten);
    }

    [Fact]
    public void A_file_the_first_rules_vouched_for_is_examined_again_for_the_settings_formats()
    {
        // Rules 1 had no Settings formats, so a file its ledger recorded as clean may still hold one. The ledger
        // entry matches the file exactly, so only the rules version can send it back for another look.
        var today = new DateOnly(2026, 9, 21);
        var leaky = WriteLog(today.AddDays(-2),
            Line("Warning", "SettingsWindow", "Could not reach the Azure API-key endpoint.") + Environment.NewLine +
            $"System.Net.Http.HttpRequestException: No such host is known. ({Host}:443)" + Environment.NewLine);
        var info = new FileInfo(leaky);
        File.WriteAllText(
            Path.Combine(_dir, LogScrubLedger.FileName),
            $"scribe-redaction-ledger rules=1\n{info.Name} {info.Length} {info.LastWriteTimeUtc.Ticks}\n");

        var result = HistoricalLogRedaction.ScrubRetainedFiles(_dir, before: today);

        Assert.Equal(1, result.FilesRewritten);
        Assert.Equal(One(LogRedactionKind.ProviderText), result.Redactions);
        Assert.DoesNotContain(Host, File.ReadAllText(leaky), StringComparison.Ordinal);
    }

    [Fact]
    public void Files_that_age_out_are_dropped_from_the_ledger()
    {
        var today = new DateOnly(2026, 9, 21);
        var old = WriteLog(today.AddDays(-4), $"ordinary{Environment.NewLine}");
        WriteLog(today.AddDays(-3), $"ordinary{Environment.NewLine}");
        HistoricalLogRedaction.ScrubRetainedFiles(_dir, before: today);

        File.Delete(old);
        HistoricalLogRedaction.ScrubRetainedFiles(_dir, before: today);

        var ledger = LogScrubLedger.Load(Path.Combine(_dir, LogScrubLedger.FileName), HistoricalLogRedaction.RulesVersion);
        Assert.Equal(1, ledger.Count);
    }

    [Fact]
    public void A_file_that_cannot_be_replaced_is_left_as_it_was_and_reported()
    {
        var today = new DateOnly(2026, 9, 21);
        var leaky = WriteLog(today.AddDays(-1), $"{DecodeLine}{Environment.NewLine}");

        // A viewer holding the file without delete sharing blocks the replace.
        using (new FileStream(leaky, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var result = HistoricalLogRedaction.ScrubRetainedFiles(_dir, before: today);

            Assert.Equal(0, result.FilesRewritten);
            Assert.Equal(1, result.FilesSkipped);
        }

        Assert.Contains("quarterly", File.ReadAllText(leaky));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));

        // Not recorded as done, so the next pass tries again and succeeds.
        var retry = HistoricalLogRedaction.ScrubRetainedFiles(_dir, before: today);
        Assert.Equal(1, retry.FilesRewritten);
    }

    [Fact]
    public void An_abandoned_scratch_file_is_removed_and_nothing_else_is()
    {
        var today = new DateOnly(2026, 9, 21);
        var scratch = Path.Combine(_dir, "scribe-20260918.log.redact-1a2b3c4d.tmp");
        File.WriteAllText(scratch, "partial copy");
        var unrelatedTmp = Path.Combine(_dir, "other.tmp");
        File.WriteAllText(unrelatedTmp, "not ours");

        HistoricalLogRedaction.ScrubRetainedFiles(_dir, before: today);

        Assert.False(File.Exists(scratch));
        Assert.True(File.Exists(unrelatedTmp));
    }

    [Fact]
    public void Scrubbing_a_missing_folder_is_a_harmless_no_op()
    {
        var result = HistoricalLogRedaction.ScrubRetainedFiles(Path.Combine(_dir, "missing"), new DateOnly(2026, 9, 21));

        Assert.Equal(0, result.FilesScanned);
    }

    private string WriteLog(DateOnly day, string content)
    {
        var path = ScribeLogFiles.PathFor(_dir, day);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private static void Write(Stream stream, byte[] body, string terminator)
    {
        stream.Write(body);
        stream.Write(Encoding.ASCII.GetBytes(terminator));
    }
}
