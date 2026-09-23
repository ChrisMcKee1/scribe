using System.Text.RegularExpressions;

namespace Scribe.Core.Diagnostics;

/// <summary>
/// Redacts the formats listed in <see cref="HistoricalLogRedaction.KnownFormats"/> from log lines fed to it
/// in order. It keeps state because an exception attached to an entry continues on the lines after it, and
/// for some entries those lines must lose their messages too. Not thread-safe: one instance per stream.
/// </summary>
public sealed partial class LogLineRedactor
{
    private const string SkipReasonLead = "AI cleanup is enabled but ";
    private const string SkipReasonTag = " ai_skip_reason=";
    private const string SkipWarningTail = " The raw transcription was used.";
    private const string AtHost = "' at ";
    private const string QuoteEllipsis = "'\u2026";
    private const string FoundryLocalProvider = "FoundryLocal";
    private const char Ellipsis = '\u2026';
    private const int MaxCodeLength = 48;

    // The head of an entry puts "] " within the first few dozen characters and the category right after it.
    private const int BracketSearchLimit = 40;
    private const int CategorySearchLimit = 40;

    // The provider-call failures whose attached exception is the provider's own error, verbatim.
    private static readonly string[] ProviderCallFailures =
    [
        "AI cleanup failed or timed out; using raw transcription.",
        "AI cleanup failed or timed out for a segment; using raw text.",
        "AI cleanup failed for a segment; using raw text.",
        "AI cleanup timed out for a segment.",
        "Auxiliary AI completion failed; returning null.",
        "GitHub Copilot session could not be started.",
    ];

    // Settings warnings (0.2.4 to 0.4.2) that carried, as their attached exception, what Azure, Entra or the
    // endpoint being verified had answered: .NET 10 appends "(host:port)" to a connection failure, and Azure and
    // Entra errors quote the account, the tenant, subscriptions and resource names.
    private static readonly string[] SettingsProviderFailures =
    [
        "Could not start Azure sign-in.",
        "Could not list Azure subscriptions for the filter dropdown.",
        "Could not list Azure deployments.",
        "Could not list Azure deployments for an Azure CLI account scope.",
        "Could not install or update Azure CLI.",
        "Could not verify Azure sign-in.",
        "Could not verify the Azure service principal.",
        "Could not verify the Azure API key.",
        "Could not reach the Azure API-key endpoint.",
    ];

    // 0.3.14 to 0.4.2. A failed shell launch's message quotes the command it was given, and this one was the AI
    // report's mailto:, which carried the report: the AI output, and the dictation when the user included it.
    private const string SettingsReportMailFailure = "Could not open a mail client for the AI report.";

    private LogRedactionKind? _exceptionKind;
    private bool _exceptionCounted;

    /// <summary>What has been replaced so far.</summary>
    public LogRedactionCounts Counts { get; private set; }

    /// <summary>True while the lines that follow are exception text that must lose its messages.</summary>
    internal bool ScrubbingException => _exceptionKind is not null;

    /// <summary>
    /// Returns <paramref name="line"/> (without its line terminator) with any recognized sensitive value
    /// replaced, or the same instance when nothing was recognized.
    /// </summary>
    public string Redact(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (!TryParseHead(line, out var categoryStart, out var categoryLength, out var start))
        {
            return _exceptionKind is { } kind ? ScrubExceptionLine(line, kind) : line;
        }

        // A new entry ends whatever exception text the previous one carried.
        _exceptionKind = null;
        _exceptionCounted = false;

        var category = line.AsSpan(categoryStart, categoryLength);
        if (!IsKnownCategory(category))
        {
            return line;
        }

        // Nothing is allocated for an entry that matches no template: the edit list only exists once a
        // value is actually replaced.
        var edits = new Edits(line);
        switch (category)
        {
            case "TranscriptionService":
                RedactTranscription(ref edits, line, start);
                break;
            case "DictationController":
                RedactDictationController(ref edits, line, start);
                break;
            case "Trace":
                RedactTrace(ref edits, line, start);
                break;
            case "TextCleanupService":
                RedactTextCleanupService(ref edits, line, start);
                break;
            case "TextPostProcessor":
                RedactTextPostProcessor(ref edits, line, start);
                break;
            case "DictionaryLibraryService":
                RedactDictionaryLibraryService(ref edits, line, start);
                break;
            case "AzureFoundryDiscovery":
                RedactAzureFoundryDiscovery(ref edits, line, start);
                break;
            case "OverlayProcessClient":
                RedactOverlayProcessClient(ref edits, line, start);
                break;
            case "Overlay":
                RedactOverlay(ref edits, line, start);
                break;
            case "TextActionController":
                RedactTextActionController(ref edits, line, start);
                break;
            case "App":
                RedactApp(ref edits, line, start);
                break;
            case "SettingsWindow":
                RedactSettingsWindow(line.AsSpan(start));
                break;
        }

        var (redacted, counts) = edits.Apply();
        Counts += counts;
        return redacted;
    }

    // Finds "HH:mm:ss.fff [Level] Category: " without allocating: the regex only proves the timestamp and
    // level, and the category is read up to the ": " that follows it.
    private static bool TryParseHead(string line, out int categoryStart, out int categoryLength, out int messageStart)
    {
        categoryStart = categoryLength = messageStart = 0;
        foreach (var prefix in EntryPrefix().EnumerateMatches(line))
        {
            categoryStart = prefix.Index + prefix.Length;
            var rest = line.AsSpan(categoryStart);
            var colon = rest[..Math.Min(rest.Length, CategorySearchLimit)].IndexOf(": ".AsSpan(), StringComparison.Ordinal);
            if (colon <= 0 || rest[..colon].IndexOfAny(" \t:") >= 0)
            {
                return false;
            }

            categoryLength = colon;
            messageStart = categoryStart + colon + 2;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Whether a line must be decoded and passed to <see cref="Redact"/>: it opens an entry of a category
    /// with a recognized format, or it continues exception text that is being scrubbed. Every other line
    /// can be copied as bytes, which keeps the common case free of allocation.
    /// </summary>
    internal bool NeedsLine(ReadOnlySpan<byte> utf8Line) => ScrubbingException || OpensKnownEntry(utf8Line);

    internal static bool OpensKnownEntry(ReadOnlySpan<char> text)
    {
        var bracket = text[..Math.Min(text.Length, BracketSearchLimit)].IndexOf("] ".AsSpan(), StringComparison.Ordinal);
        if (bracket < 0)
        {
            return false;
        }

        var rest = text[(bracket + 2)..];
        var colon = rest[..Math.Min(rest.Length, CategorySearchLimit)].IndexOf(": ".AsSpan(), StringComparison.Ordinal);
        return colon > 0 && IsKnownCategory(rest[..colon]);
    }

    internal static bool OpensKnownEntry(ReadOnlySpan<byte> utf8)
    {
        var bracket = utf8[..Math.Min(utf8.Length, BracketSearchLimit)].IndexOf("] "u8);
        if (bracket < 0)
        {
            return false;
        }

        var rest = utf8[(bracket + 2)..];
        var colon = rest[..Math.Min(rest.Length, CategorySearchLimit)].IndexOf(": "u8);
        if (colon <= 0)
        {
            return false;
        }

        var category = rest[..colon];
        return category.SequenceEqual("TranscriptionService"u8) ||
            category.SequenceEqual("DictationController"u8) ||
            category.SequenceEqual("Trace"u8) ||
            category.SequenceEqual("TextCleanupService"u8) ||
            category.SequenceEqual("TextPostProcessor"u8) ||
            category.SequenceEqual("DictionaryLibraryService"u8) ||
            category.SequenceEqual("AzureFoundryDiscovery"u8) ||
            category.SequenceEqual("OverlayProcessClient"u8) ||
            category.SequenceEqual("Overlay"u8) ||
            category.SequenceEqual("TextActionController"u8) ||
            category.SequenceEqual("App"u8) ||
            category.SequenceEqual("SettingsWindow"u8);
    }

    private static bool IsKnownCategory(ReadOnlySpan<char> category) =>
        category is "TranscriptionService" or "DictationController" or "Trace" or "TextCleanupService"
            or "TextPostProcessor" or "DictionaryLibraryService" or "AzureFoundryDiscovery"
            or "OverlayProcessClient" or "Overlay" or "TextActionController" or "App" or "SettingsWindow";

    // Decoded {AudioMs} ms of audio in {DecodeMs} ms (RTF {Rtf:F2}): "{Text}"
    private static void RedactTranscription(ref Edits edits, string line, int start)
    {
        var match = DecodeLine().Match(line, start);
        if (!match.Success)
        {
            return;
        }

        // Everything after the opening quote. The closing quote is optional so a line cut short by a
        // concurrent append is still redacted, and quotes inside the transcript need no care.
        var textStart = match.Index + match.Length;
        var closing = line.Length > textStart && line[^1] == '"';
        var textLength = line.Length - textStart - (closing ? 1 : 0);
        if (textLength <= 0 || IsTranscriptPlaceholder(line.AsSpan(textStart, textLength)))
        {
            return;
        }

        edits.Replace(
            textStart, textLength, HistoricalLogRedaction.TranscriptPlaceholder(textLength), LogRedactionKind.Transcript);
    }

    private static bool IsTranscriptPlaceholder(ReadOnlySpan<char> text)
    {
        const string Prefix = "[transcript redacted, ";
        const string Suffix = " chars]";
        if (!text.StartsWith(Prefix, StringComparison.Ordinal) || !text.EndsWith(Suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var digits = text[Prefix.Length..^Suffix.Length];
        return !digits.IsEmpty && !digits.ContainsAnyExceptInRange('0', '9');
    }

    private static void RedactDictationController(ref Edits edits, string line, int start)
    {
        // AI cleanup was skipped for this dictation: {Reason} The raw transcription was used.
        var warning = SkipWarningLine().Match(line, start);
        if (warning.Success)
        {
            var reasonStart = warning.Index + warning.Length;
            var reasonEnd = line.EndsWith(SkipWarningTail, StringComparison.Ordinal) &&
                line.Length - SkipWarningTail.Length >= reasonStart
                    ? line.Length - SkipWarningTail.Length
                    : line.Length;
            RedactSkipReason(ref edits, line, reasonStart, reasonEnd);
            return;
        }

        // AI cleanup failed ({Reason}); using raw transcription.
        var failed = CleanupFailedLine().Match(line, start);
        if (failed.Success)
        {
            edits.ReplaceFreeText(failed.Groups["v"], HistoricalLogRedaction.FailureTextPlaceholder);
            return;
        }

        // Applying profile '{Profile}' for {App}.
        var profile = ProfileLine().Match(line, start);
        if (profile.Success)
        {
            edits.Replace(profile.Groups["v"], HistoricalLogRedaction.ProfilePlaceholder, LogRedactionKind.UserContent);
        }
    }

    // trace dictation.process ... ai_skip_reason={Reason} ... ({Ms}ms)
    private static void RedactTrace(ref Edits edits, string line, int start)
    {
        var head = TraceLine().Match(line, start);
        if (!head.Success)
        {
            return;
        }

        // From the head's own trailing space, so the tag is found when it is the first one.
        var tag = line.IndexOf(SkipReasonTag + SkipReasonLead, head.Index + head.Length - 1, StringComparison.Ordinal);
        if (tag < 0)
        {
            return;
        }

        var reasonStart = tag + SkipReasonTag.Length;
        RedactSkipReason(ref edits, line, reasonStart, FindTraceReasonEnd(line, reasonStart));
    }

    // The reason ends with ")." and is followed by the tags the pipeline sets after it, then the duration and
    // an error span's description. The first ")." after which the line has exactly that shape is the end; a
    // line that never does (cut short, or an unexpected tag) is treated as running to its end.
    private static int FindTraceReasonEnd(string line, int reasonStart)
    {
        var from = reasonStart;
        while (true)
        {
            var close = line.IndexOf(").", from, StringComparison.Ordinal);
            if (close < 0)
            {
                return line.Length;
            }

            var rest = line.AsSpan(close + 2);
            if ((rest.StartsWith(" final_chars=", StringComparison.Ordinal) ||
                 rest.StartsWith(" target_app=", StringComparison.Ordinal) ||
                 rest.StartsWith(" outcome=", StringComparison.Ordinal) ||
                 rest.StartsWith(" (", StringComparison.Ordinal)) &&
                TraceDuration().IsMatch(rest))
            {
                return close + 2;
            }

            from = close + 1;
        }
    }

    // "AI cleanup is enabled but {Status} ({Detail})." Every shipped build from 0.2.19 turned any status other
    // than Ready into this reason, so the detail is whatever that status carried.
    private static void RedactSkipReason(ref Edits edits, string line, int reasonStart, int reasonEnd)
    {
        if (!line.AsSpan(reasonStart).StartsWith(SkipReasonLead, StringComparison.Ordinal))
        {
            return;
        }

        var statusStart = reasonStart + SkipReasonLead.Length;
        var statusEnd = statusStart;
        while (statusEnd < reasonEnd && char.IsAsciiLetter(line[statusEnd]))
        {
            statusEnd++;
        }

        if (statusEnd == statusStart || !line.AsSpan(statusEnd, reasonEnd - statusEnd).StartsWith(" (", StringComparison.Ordinal))
        {
            return;
        }

        var detailStart = statusEnd + 2;
        var detailEnd = reasonEnd - detailStart >= 2 && line.AsSpan(reasonEnd - 2, 2).SequenceEqual(").")
            ? reasonEnd - 2
            : reasonEnd;
        if (detailEnd <= detailStart)
        {
            return;
        }

        switch (line.AsSpan(statusStart, statusEnd - statusStart))
        {
            case "Initializing":
                RedactConnectingDetail(ref edits, line, detailStart, detailEnd);
                break;
            case "Ready":
                RedactReadyDetail(ref edits, line, detailStart, detailEnd);
                break;
            case "Unavailable":
                RedactUnavailableDetail(ref edits, line, detailStart, detailEnd);
                break;
        }
    }

    // "Connecting to Azure deployment '{d}'…" or "Connecting to {host}…".
    private static void RedactConnectingDetail(ref Edits edits, string line, int detailStart, int detailEnd)
    {
        const string AzureLead = "Connecting to Azure deployment '";
        const string HostLead = "Connecting to ";
        var detail = line.AsSpan(detailStart, detailEnd - detailStart);
        if (detail.StartsWith(AzureLead, StringComparison.Ordinal))
        {
            var nameStart = detailStart + AzureLead.Length;
            var close = line.IndexOf(QuoteEllipsis, nameStart, detailEnd - nameStart, StringComparison.Ordinal);
            edits.Replace(
                nameStart, (close >= 0 ? close : detailEnd) - nameStart,
                HistoricalLogRedaction.AzureNamePlaceholder, LogRedactionKind.AzureResourceName);
            return;
        }

        if (!detail.StartsWith(HostLead, StringComparison.Ordinal))
        {
            return;
        }

        // The host is one token ending at the ellipsis, or at the end of a line cut short. A space instead
        // means one of the host-free details sharing the lead ("Connecting to the GitHub Copilot CLI…").
        var hostStart = detailStart + HostLead.Length;
        var hostEnd = hostStart;
        while (hostEnd < detailEnd && line[hostEnd] != Ellipsis && !char.IsWhiteSpace(line[hostEnd]))
        {
            hostEnd++;
        }

        if (hostEnd < detailEnd && line[hostEnd] != Ellipsis)
        {
            return;
        }

        edits.Replace(hostStart, hostEnd - hostStart, HistoricalLogRedaction.HostPlaceholder, LogRedactionKind.EndpointAddress);
    }

    // "Azure deployment '{d}' ready." or "'{model}' at {host} ready.".
    private static void RedactReadyDetail(ref Edits edits, string line, int detailStart, int detailEnd)
    {
        const string AzureLead = "Azure deployment '";
        var detail = line.AsSpan(detailStart, detailEnd - detailStart);
        if (detail.StartsWith(AzureLead, StringComparison.Ordinal))
        {
            var nameStart = detailStart + AzureLead.Length;
            var close = line.IndexOf("' ready.", nameStart, detailEnd - nameStart, StringComparison.Ordinal);
            edits.Replace(
                nameStart, (close >= 0 ? close : detailEnd) - nameStart,
                HistoricalLogRedaction.AzureNamePlaceholder, LogRedactionKind.AzureResourceName);
            return;
        }

        if (detail[0] != '\'')
        {
            return;
        }

        // The model is user-entered free text, so the host is found backwards: the token after the last
        // "' at " in the detail. "custom endpoint" is the fallback used when the URL did not parse.
        var at = line.LastIndexOf(AtHost, detailEnd - 1, detailEnd - detailStart, StringComparison.Ordinal);
        if (at < 0)
        {
            return;
        }

        var hostStart = at + AtHost.Length;
        if (line.AsSpan(hostStart, detailEnd - hostStart).StartsWith("custom endpoint ready.", StringComparison.Ordinal))
        {
            return;
        }

        var hostEnd = hostStart;
        while (hostEnd < detailEnd && !char.IsWhiteSpace(line[hostEnd]))
        {
            hostEnd++;
        }

        edits.Replace(hostStart, hostEnd - hostStart, HistoricalLogRedaction.HostPlaceholder, LogRedactionKind.EndpointAddress);
    }

    private static void RedactUnavailableDetail(ref Edits edits, string line, int detailStart, int detailEnd)
    {
        const string ReachLead = "Couldn't reach '";
        const string ReachTail = ". Check the endpoint URL (it usually ends in /v1)";
        const string NotFoundLead = "Azure could not find the deployment '";
        const string NotFoundTail = "' (404).";
        var detail = line.AsSpan(detailStart, detailEnd - detailStart);

        // "Couldn't reach '{model}' at {host}. Check the endpoint URL (it usually ends in /v1), ...".
        if (detail.StartsWith(ReachLead, StringComparison.Ordinal))
        {
            var tail = line.IndexOf(ReachTail, detailStart, detailEnd - detailStart, StringComparison.Ordinal);
            var at = tail > detailStart
                ? line.LastIndexOf(AtHost, tail - 1, tail - detailStart, StringComparison.Ordinal)
                : -1;
            if (at >= 0)
            {
                var hostStart = at + AtHost.Length;
                var host = line.AsSpan(hostStart, tail - hostStart);
                if (host.StartsWith(HistoricalLogRedaction.HostPlaceholder, StringComparison.Ordinal) ||
                    (!host.IsEmpty && !ContainsWhiteSpace(host)))
                {
                    edits.Replace(hostStart, tail - hostStart, HistoricalLogRedaction.HostPlaceholder, LogRedactionKind.EndpointAddress);
                    return;
                }
            }
        }

        // "Azure could not find the deployment '{d}' (404). The endpoint is reachable, ...".
        if (detail.StartsWith(NotFoundLead, StringComparison.Ordinal))
        {
            var nameStart = detailStart + NotFoundLead.Length;
            var close = line.IndexOf(NotFoundTail, nameStart, detailEnd - nameStart, StringComparison.Ordinal);
            if (close >= 0)
            {
                edits.Replace(nameStart, close - nameStart, HistoricalLogRedaction.AzureNamePlaceholder, LogRedactionKind.AzureResourceName);
                return;
            }
        }

        // Anything else an unavailable provider reported, including a probe failure carrying the server's
        // own words from 0.3.9 on, is free text: the whole detail goes.
        edits.ReplaceFreeText(detailStart, detailEnd - detailStart, HistoricalLogRedaction.FailureTextPlaceholder);
    }

    private void RedactTextCleanupService(ref Edits edits, string line, int start)
    {
        Match match;
        if ((match = OpenAiValidationLine().Match(line, start)).Success)
        {
            edits.Replace(match.Groups["v"], HistoricalLogRedaction.HostPlaceholder, LogRedactionKind.EndpointAddress);
            ScrubAttachedException(LogRedactionKind.ProviderText);
        }
        else if ((match = AzureValidationWithEndpointLine().Match(line, start)).Success)
        {
            edits.Replace(match.Groups["d"], HistoricalLogRedaction.AzureNamePlaceholder, LogRedactionKind.AzureResourceName);
            edits.Replace(match.Groups["e"], HistoricalLogRedaction.EndpointPlaceholder, LogRedactionKind.EndpointAddress);
            ScrubAttachedException(LogRedactionKind.ProviderText);
        }
        else if ((match = AzureValidationLine().Match(line, start)).Success)
        {
            edits.Replace(match.Groups["d"], HistoricalLogRedaction.AzureNamePlaceholder, LogRedactionKind.AzureResourceName);
            ScrubAttachedException(LogRedactionKind.ProviderText);
        }
        else if ((match = ProbeFailedWithMessageLine().Match(line, start)).Success)
        {
            // Foundry Local runs on this PC, and its messages are the evidence for its GPU and execution
            // provider failures; they carry no remote address or account.
            if (!match.Groups["p"].ValueSpan.SequenceEqual(FoundryLocalProvider))
            {
                edits.ReplaceFreeText(match.Groups["v"], HistoricalLogRedaction.FailureTextPlaceholder);
            }
        }
        else if ((match = ProviderFailureLine().Match(line, start)).Success)
        {
            if (!match.Groups["p"].ValueSpan.SequenceEqual(FoundryLocalProvider))
            {
                ScrubAttachedException(LogRedactionKind.ProviderText);
            }
        }
        else if ((match = ChatFallbackFailedLine().Match(line, start)).Success)
        {
            edits.Replace(match.Groups["d"], HistoricalLogRedaction.AzureNamePlaceholder, LogRedactionKind.AzureResourceName);
            edits.ReplaceFreeText(match.Groups["v"], HistoricalLogRedaction.FailureTextPlaceholder);
        }
        else if ((match = ChatAgentBuildFailedLine().Match(line, start)).Success)
        {
            edits.Replace(match.Groups["d"], HistoricalLogRedaction.AzureNamePlaceholder, LogRedactionKind.AzureResourceName);
            ScrubAttachedException(LogRedactionKind.ProviderText);
        }
        else if ((match = ResponsesUnsupportedLine().Match(line, start)).Success)
        {
            edits.Replace(match.Groups["d"], HistoricalLogRedaction.AzureNamePlaceholder, LogRedactionKind.AzureResourceName);
        }
        else if (IsOneOf(line.AsSpan(start), ProviderCallFailures) || TextActionRuntimeFailureLine().IsMatch(line, start))
        {
            ScrubAttachedException(LogRedactionKind.ProviderText);
        }
    }

    private static bool IsOneOf(ReadOnlySpan<char> message, string[] templates)
    {
        foreach (var template in templates)
        {
            if (message.SequenceEqual(template))
            {
                return true;
            }
        }

        return false;
    }

    // The Settings lines themselves were fixed text; only the exception attached to them carried anything.
    private void RedactSettingsWindow(ReadOnlySpan<char> message)
    {
        if (message.SequenceEqual(SettingsReportMailFailure))
        {
            ScrubAttachedException(LogRedactionKind.Transcript);
        }
        else if (IsOneOf(message, SettingsProviderFailures))
        {
            ScrubAttachedException(LogRedactionKind.ProviderText);
        }
    }

    private void RedactTextPostProcessor(ref Edits edits, string line, int start)
    {
        Match match;
        if ((match = InvalidSnippetLine().Match(line, start)).Success)
        {
            edits.Replace(match.Groups["v"], HistoricalLogRedaction.SnippetPlaceholder, LogRedactionKind.UserContent);
            ScrubAttachedException(LogRedactionKind.UserContent);
        }
        else if ((match = InvalidDictionaryEntryLine().Match(line, start)).Success)
        {
            edits.Replace(match.Groups["v"], HistoricalLogRedaction.DictionaryPlaceholder, LogRedactionKind.UserContent);
            ScrubAttachedException(LogRedactionKind.UserContent);
        }
    }

    private void RedactDictionaryLibraryService(ref Edits edits, string line, int start)
    {
        Match match;
        if ((match = LibraryImportedLine().Match(line, start)).Success)
        {
            edits.Replace(match.Groups["n"], HistoricalLogRedaction.LibraryNamePlaceholder, LogRedactionKind.UserContent);
            edits.Replace(match.Groups["id"], HistoricalLogRedaction.LibraryIdPlaceholder, LogRedactionKind.UserContent);
        }
        else if ((match = LibraryImportedCutShortLine().Match(line, start)).Success)
        {
            edits.Replace(match.Groups["n"], HistoricalLogRedaction.LibraryNamePlaceholder, LogRedactionKind.UserContent);
        }
        else if ((match = LibraryRemovedLine().Match(line, start)).Success)
        {
            edits.Replace(match.Groups["id"], HistoricalLogRedaction.LibraryIdPlaceholder, LogRedactionKind.UserContent);
        }
        else if ((match = LibraryUnreadableLine().Match(line, start)).Success)
        {
            edits.Replace(match.Groups["p"], HistoricalLogRedaction.PathPlaceholder, LogRedactionKind.UserContent);
            ScrubAttachedException(LogRedactionKind.UserContent);
        }
    }

    private void RedactAzureFoundryDiscovery(ref Edits edits, string line, int start)
    {
        Match match;
        if ((match = AzureAccountOrSubscriptionLine().Match(line, start)).Success ||
            (match = NonTextDeploymentLine().Match(line, start)).Success)
        {
            edits.Replace(match.Groups["v"], HistoricalLogRedaction.AzureNamePlaceholder, LogRedactionKind.AzureResourceName);
        }
        else if ((match = ServicePrincipalRejectedLine().Match(line, start)).Success)
        {
            edits.ReplaceFreeText(match.Groups["v"], HistoricalLogRedaction.FailureTextPlaceholder);
        }

        // Every discovery exception is an Azure Resource Manager or Entra error, and those routinely quote
        // the signed-in account, the tenant, and subscription and resource names.
        ScrubAttachedException(LogRedactionKind.ProviderText);
    }

    // Overlay command '{Cmd}' failed; tearing down for relaunch. The FAILED and WARNING commands carried the
    // text the pill was asked to show.
    private static void RedactOverlayProcessClient(ref Edits edits, string line, int start)
    {
        var match = OverlayCommandFailedLine().Match(line, start);
        if (match.Success)
        {
            edits.ReplaceFreeText(match.Groups["v"], HistoricalLogRedaction.ReasonPlaceholder);
        }
    }

    // OverlayWindow.ShowFailed hold={n}ms reason='{reason}' and the ShowRecordingWarning twin.
    private static void RedactOverlay(ref Edits edits, string line, int start)
    {
        var match = OverlayReasonLine().Match(line, start);
        if (match.Success)
        {
            edits.ReplaceFreeText(match.Groups["v"], HistoricalLogRedaction.ReasonPlaceholder);
        }
    }

    // Text action {ActionId} failed: {Failure}. {Detail}, where a NotReady detail was the cleanup status and a
    // CallFailed detail the provider's failure description.
    private static void RedactTextActionController(ref Edits edits, string line, int start)
    {
        var match = TextActionFailedLine().Match(line, start);
        if (match.Success)
        {
            edits.ReplaceFreeText(match.Groups["v"], HistoricalLogRedaction.FailureTextPlaceholder);
        }
    }

    // The session banner's cleanup line named the Azure deployment from 0.3.11 to 0.4.2.
    private static void RedactApp(ref Edits edits, string line, int start)
    {
        var match = BannerAzureCleanupLine().Match(line, start);
        if (match.Success && match.Groups["v"].ValueSpan is not ("unset" or "configured"))
        {
            edits.Replace(match.Groups["v"], HistoricalLogRedaction.AzureNamePlaceholder, LogRedactionKind.AzureResourceName);
        }
    }

    private void ScrubAttachedException(LogRedactionKind kind) => _exceptionKind = kind;

    // Exception text keeps what names code, which is the diagnostic part, and loses what carries data:
    // type names, stack frames and the separators between them stay; every message becomes a placeholder.
    private string ScrubExceptionLine(string line, LogRedactionKind kind)
    {
        if (string.IsNullOrWhiteSpace(line) || StackFrameLine().IsMatch(line) || StackSeparatorLine().IsMatch(line))
        {
            return line;
        }

        string scrubbed;
        var header = ExceptionHeaderLine().Match(line);
        if (header.Success)
        {
            var message = header.Groups["message"];
            if (!message.Success || message.ValueSpan.SequenceEqual(HistoricalLogRedaction.MessagePlaceholder))
            {
                return line;
            }

            scrubbed = string.Concat(line.AsSpan(0, message.Index), HistoricalLogRedaction.MessagePlaceholder);
        }
        else
        {
            if (line.AsSpan().Trim().SequenceEqual(HistoricalLogRedaction.MessagePlaceholder))
            {
                return line;
            }

            scrubbed = HistoricalLogRedaction.MessagePlaceholder;
        }

        if (!_exceptionCounted)
        {
            _exceptionCounted = true;
            Counts = Counts.Plus(kind);
        }

        return scrubbed;
    }

    private static bool ContainsWhiteSpace(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                return true;
            }
        }

        return false;
    }

    // A fixed code (ASCII letters, digits, hyphen, underscore) cannot carry a sentence or a dotted host, so a
    // later build that logs a reason code in one of these templates keeps it.
    private static bool IsCode(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty || value.Length > MaxCodeLength || !char.IsAsciiLetterOrDigit(value[0]))
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The replacements for one line, applied together once every handler has had its say.</summary>
    private struct Edits(string line)
    {
        private List<(int Start, int Length, string Text, LogRedactionKind Kind)>? _items;

        public void Replace(Group group, string placeholder, LogRedactionKind kind)
        {
            if (group.Success)
            {
                Replace(group.Index, group.Length, placeholder, kind);
            }
        }

        public void Replace(int start, int length, string placeholder, LogRedactionKind kind)
        {
            // Idempotent: a value that already is the placeholder stays, and is not counted again.
            if (length <= 0 || start < 0 || start + length > line.Length ||
                line.AsSpan(start).StartsWith(placeholder, StringComparison.Ordinal))
            {
                return;
            }

            (_items ??= []).Add((start, length, placeholder, kind));
        }

        public void ReplaceFreeText(Group group, string placeholder)
        {
            if (group.Success)
            {
                ReplaceFreeText(group.Index, group.Length, placeholder);
            }
        }

        public void ReplaceFreeText(int start, int length, string placeholder)
        {
            if (length <= 0)
            {
                return;
            }

            var value = line.AsSpan(start, length);
            if (value.IsWhiteSpace() || IsCode(value))
            {
                return;
            }

            Replace(start, length, placeholder, LogRedactionKind.ProviderText);
        }

        public (string Text, LogRedactionCounts Counts) Apply()
        {
            if (_items is null)
            {
                return (line, default);
            }

            _items.Sort(static (a, b) => b.Start.CompareTo(a.Start));
            var text = line;
            var counts = default(LogRedactionCounts);
            foreach (var (start, length, replacement, kind) in _items)
            {
                text = string.Concat(text.AsSpan(0, start), replacement, text.AsSpan(start + length));
                counts = counts.Plus(kind);
            }

            return (text, counts);
        }
    }

    // "HH:mm:ss.fff [Level] " ahead of the category. The time separator follows the writer's culture, so it is
    // matched loosely (one to three characters that are neither digits nor spaces). An optional byte order
    // mark covers a file some other tool rewrote.
    [GeneratedRegex(@"^\uFEFF?[0-9]{2}[^0-9 ]{1,3}[0-9]{2}[^0-9 ]{1,3}[0-9]{2}\.[0-9]{3} \[[A-Za-z]+\] ", RegexOptions.CultureInvariant)]
    private static partial Regex EntryPrefix();

    [GeneratedRegex(@"\GDecoded [0-9]+ ms of audio in [0-9]+ ms \(RTF [^)]{0,32}\): """, RegexOptions.CultureInvariant)]
    private static partial Regex DecodeLine();

    [GeneratedRegex(@"\GAI cleanup was skipped for this dictation: (?=AI cleanup is enabled but )", RegexOptions.CultureInvariant)]
    private static partial Regex SkipWarningLine();

    [GeneratedRegex(@"\GAI cleanup failed \((?<v>.*?)(?:\); using raw transcription\.)?$", RegexOptions.CultureInvariant)]
    private static partial Regex CleanupFailedLine();

    [GeneratedRegex(@"\GApplying profile '(?<v>.*?)(?:' for [^']*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex ProfileLine();

    [GeneratedRegex(@"\Gtrace dictation\.process ", RegexOptions.CultureInvariant)]
    private static partial Regex TraceLine();

    [GeneratedRegex(@" \([0-9]+ms\)", RegexOptions.CultureInvariant)]
    private static partial Regex TraceDuration();

    [GeneratedRegex(@"\GOpenAI-compatible endpoint validation failed for (?<v>.*?)\.?$", RegexOptions.CultureInvariant)]
    private static partial Regex OpenAiValidationLine();

    [GeneratedRegex(@"\GAzure deployment validation failed for (?<d>.+?) at (?<e>[A-Za-z][A-Za-z0-9+.-]*://\S*) \(auth=[A-Za-z]*\)\.?$", RegexOptions.CultureInvariant)]
    private static partial Regex AzureValidationWithEndpointLine();

    [GeneratedRegex(@"\GAzure deployment validation failed for (?<d>.*?)\.?$", RegexOptions.CultureInvariant)]
    private static partial Regex AzureValidationLine();

    [GeneratedRegex(@"\GAI cleanup initialization probe failed \((?<p>[A-Za-z]+)\): (?<v>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ProbeFailedWithMessageLine();

    [GeneratedRegex(@"\GAI cleanup initialization (?:probe )?failed \((?<p>[A-Za-z]+)\)\.$", RegexOptions.CultureInvariant)]
    private static partial Regex ProviderFailureLine();

    [GeneratedRegex(@"\GChat Completions fallback also failed for (?<d>.+?): (?<v>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex ChatFallbackFailedLine();

    [GeneratedRegex(@"\GCould not build a Chat Completions agent for (?<d>.*?)\.?$", RegexOptions.CultureInvariant)]
    private static partial Regex ChatAgentBuildFailedLine();

    [GeneratedRegex(@"\GAzure deployment (?<d>.+?) does not serve the Responses API; using Chat Completions\.$", RegexOptions.CultureInvariant)]
    private static partial Regex ResponsesUnsupportedLine();

    [GeneratedRegex(@"\GText action [a-z0-9-]+ failed at runtime\.$", RegexOptions.CultureInvariant)]
    private static partial Regex TextActionRuntimeFailureLine();

    [GeneratedRegex(@"\GSkipping invalid snippet (?<id>\S+) \('(?<v>.*?)(?:'\)\.)?$", RegexOptions.CultureInvariant)]
    private static partial Regex InvalidSnippetLine();

    [GeneratedRegex(@"\GSkipping invalid dictionary entry (?<id>\S+) \('(?<v>.*?)(?:'\)\.)?$", RegexOptions.CultureInvariant)]
    private static partial Regex InvalidDictionaryEntryLine();

    [GeneratedRegex(@"\GImported dictionary library '(?<n>.*)' \([0-9]+ entries\) as (?<id>.+?)\.?$", RegexOptions.CultureInvariant)]
    private static partial Regex LibraryImportedLine();

    [GeneratedRegex(@"\GImported dictionary library '(?<n>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex LibraryImportedCutShortLine();

    [GeneratedRegex(@"\GRemoved custom dictionary library (?<id>.+?)\.?$", RegexOptions.CultureInvariant)]
    private static partial Regex LibraryRemovedLine();

    [GeneratedRegex(@"\GSkipping unreadable custom dictionary library at (?<p>.+?)\.?$", RegexOptions.CultureInvariant)]
    private static partial Regex LibraryUnreadableLine();

    [GeneratedRegex(@"\GCould not list (?:deployments for account|Foundry projects for account|Cognitive Services accounts in subscription) (?<v>.+?)\.?$", RegexOptions.CultureInvariant)]
    private static partial Regex AzureAccountOrSubscriptionLine();

    [GeneratedRegex(@"\GSkipping non-text deployment (?<v>.+?)(?: \(.*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex NonTextDeploymentLine();

    [GeneratedRegex(@"\GAzure sign-in probe: the service principal was rejected\. (?<v>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ServicePrincipalRejectedLine();

    [GeneratedRegex(@"\GOverlay command '(?:FAILED|WARNING) (?<v>.*?)(?:' failed; tearing down for relaunch\.)?$", RegexOptions.CultureInvariant)]
    private static partial Regex OverlayCommandFailedLine();

    [GeneratedRegex(@"\GOverlayWindow\.Show(?:Failed|RecordingWarning) hold=[0-9]+ms reason='(?<v>.*?)'?$", RegexOptions.CultureInvariant)]
    private static partial Regex OverlayReasonLine();

    [GeneratedRegex(@"\GText action [a-z0-9-]+ failed: (?:NotReady|CallFailed)\. (?<v>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex TextActionFailedLine();

    [GeneratedRegex(@"\Gcleanup: on provider=AzureFoundry deployment=(?<v>.+?) endpoint=(?:configured|unset) auth=", RegexOptions.CultureInvariant)]
    private static partial Regex BannerAzureCleanupLine();

    [GeneratedRegex(@"^\s+at \S", RegexOptions.CultureInvariant)]
    private static partial Regex StackFrameLine();

    [GeneratedRegex(@"^\s*--- End of (?:inner exception stack trace|stack trace from previous location(?: where exception was thrown)?) ---\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex StackSeparatorLine();

    // "Namespace.TypeException: message", optionally led by " ---> " for an inner exception and followed by
    // an error code such as " (0x80004005)" or " (11001)", as Win32 and socket exceptions render.
    [GeneratedRegex(@"^\s*(?:---> )?(?:[A-Za-z_][A-Za-z0-9_]*\.)*[A-Za-z_][A-Za-z0-9_]*Exception(?:`[0-9]+(?:\[[^\]]*\])?)?(?: \((?:0x[0-9A-Fa-f]+|-?[0-9]+)\))?(?:: (?<message>.*))?$", RegexOptions.CultureInvariant)]
    private static partial Regex ExceptionHeaderLine();
}
