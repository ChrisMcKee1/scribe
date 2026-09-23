using System.Buffers;
using System.Globalization;
using System.Text;

namespace Scribe.Core.Diagnostics;

/// <summary>The kinds of sensitive value that <see cref="HistoricalLogRedaction"/> replaces.</summary>
public enum LogRedactionKind
{
    /// <summary>The recognized text of a dictation.</summary>
    Transcript,

    /// <summary>A custom AI endpoint's host name or address.</summary>
    EndpointAddress,

    /// <summary>An Azure deployment, account or subscription name.</summary>
    AzureResourceName,

    /// <summary>Dictionary, snippet, dictionary library or per-app profile text the user wrote.</summary>
    UserContent,

    /// <summary>
    /// Failure or status text that came from, or could echo, an AI provider or Azure: cleanup failure
    /// reasons, sign-in messages, overlay failure text, and the messages of exceptions attached to
    /// recognized lines.
    /// </summary>
    ProviderText,
}

/// <summary>How many sensitive values a redaction pass replaced, by kind. Counts only, never content.</summary>
public readonly record struct LogRedactionCounts(
    int Transcripts,
    int EndpointAddresses,
    int AzureResourceNames,
    int UserContent,
    int ProviderText)
{
    /// <summary>Every value replaced, of any kind.</summary>
    public int Total => Transcripts + EndpointAddresses + AzureResourceNames + UserContent + ProviderText;

    /// <summary>The count for one kind.</summary>
    public int Of(LogRedactionKind kind) => kind switch
    {
        LogRedactionKind.Transcript => Transcripts,
        LogRedactionKind.EndpointAddress => EndpointAddresses,
        LogRedactionKind.AzureResourceName => AzureResourceNames,
        LogRedactionKind.UserContent => UserContent,
        _ => ProviderText,
    };

    /// <summary>These counts with one more of <paramref name="kind"/>.</summary>
    public LogRedactionCounts Plus(LogRedactionKind kind) => kind switch
    {
        LogRedactionKind.Transcript => this with { Transcripts = Transcripts + 1 },
        LogRedactionKind.EndpointAddress => this with { EndpointAddresses = EndpointAddresses + 1 },
        LogRedactionKind.AzureResourceName => this with { AzureResourceNames = AzureResourceNames + 1 },
        LogRedactionKind.UserContent => this with { UserContent = UserContent + 1 },
        _ => this with { ProviderText = ProviderText + 1 },
    };

    public static LogRedactionCounts operator +(LogRedactionCounts left, LogRedactionCounts right) =>
        new(
            left.Transcripts + right.Transcripts,
            left.EndpointAddresses + right.EndpointAddresses,
            left.AzureResourceNames + right.AzureResourceNames,
            left.UserContent + right.UserContent,
            left.ProviderText + right.ProviderText);
}

/// <summary>One historical line format the redaction recognizes, worded for a person reading report.txt.</summary>
/// <param name="Kind">What the format carried.</param>
/// <param name="Lines">Which log lines, in plain words.</param>
/// <param name="Versions">The shipped versions whose logs can contain them, as "first to last".</param>
public sealed record HistoricalLogFormat(LogRedactionKind Kind, string Lines, string Versions);

/// <summary>Outcome of scrubbing retained log files in place.</summary>
/// <param name="FilesScanned">Past-day log files read in this pass.</param>
/// <param name="FilesRewritten">Files that held a known format and were rewritten without it.</param>
/// <param name="FilesSkipped">Files that could not be examined or rewritten this time; retried later.</param>
/// <param name="FilesAlreadyVerified">Files the ledger recorded as done and that have not changed since.</param>
/// <param name="Redactions">What the rewritten files had replaced.</param>
public sealed record LogScrubResult(
    int FilesScanned, int FilesRewritten, int FilesSkipped, int FilesAlreadyVerified, LogRedactionCounts Redactions);

/// <summary>
/// Replaces sensitive values that shipped builds wrote into the shared daily log in known line formats,
/// and nothing else. Each format is anchored to its full line shape (<see cref="LogLineFormat"/>: the
/// timestamp, the level and the logger category) and to its message template, so only the placeholder
/// that carried the value changes; timings, counts, statuses and identifiers around it survive for
/// diagnosis. <see cref="KnownFormats"/> lists every format with the versions that wrote it, and the
/// diagnostics bundle repeats that list so nobody reads the redaction as more than it is.
/// <para>
/// Deliberately not a general URL, host or name scrubber: free text cannot be redacted reliably by
/// pattern. Where a recognized template carried free text that came from a provider (a failure reason,
/// a sign-in message, the message of an attached exception), the whole value is replaced instead.
/// Redaction is idempotent. Three callers share it: the diagnostics bundle copy, the one-time in-place
/// scrub of past days' files (<see cref="ScrubRetainedFiles"/>), and the live file sink as defense in depth.
/// </para>
/// </summary>
public static class HistoricalLogRedaction
{
    /// <summary>Replaces a custom endpoint's host name.</summary>
    public const string HostPlaceholder = "[endpoint host redacted]";

    /// <summary>Replaces a full endpoint address.</summary>
    public const string EndpointPlaceholder = "[endpoint redacted]";

    /// <summary>Replaces an Azure deployment, account or subscription name.</summary>
    public const string AzureNamePlaceholder = "[Azure name redacted]";

    /// <summary>Replaces a dictionary entry's text.</summary>
    public const string DictionaryPlaceholder = "[dictionary text redacted]";

    /// <summary>Replaces a snippet's spoken phrase.</summary>
    public const string SnippetPlaceholder = "[snippet text redacted]";

    /// <summary>Replaces a dictionary library's name.</summary>
    public const string LibraryNamePlaceholder = "[library name redacted]";

    /// <summary>Replaces a dictionary library's id, which is derived from its name.</summary>
    public const string LibraryIdPlaceholder = "[library id redacted]";

    /// <summary>Replaces a file path that named a dictionary library.</summary>
    public const string PathPlaceholder = "[path redacted]";

    /// <summary>Replaces a per-app profile's name.</summary>
    public const string ProfilePlaceholder = "[profile name redacted]";

    /// <summary>Replaces failure or status text that came from a provider.</summary>
    public const string FailureTextPlaceholder = "[failure text redacted]";

    /// <summary>Replaces the reason text the overlay was asked to show.</summary>
    public const string ReasonPlaceholder = "[reason redacted]";

    /// <summary>Replaces the message of an exception attached to a recognized line.</summary>
    public const string MessagePlaceholder = "[message redacted]";

    /// <summary>
    /// Version of the rule set. Bump it whenever a format is added or changed, so files the scrub ledger
    /// recorded as done under older rules are examined again. 2 added the Settings warnings.
    /// </summary>
    public const int RulesVersion = 2;

    private const string ScratchInfix = ".redact-";
    private const string ScratchExtension = ".tmp";
    private const int ChunkBytes = 64 * 1024;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Every format this redaction recognizes, grouped by what it carried. Ranges are the shipped versions
    /// whose logs can hold the line: Debug lines only reached the file from 0.3.11.
    /// </summary>
    public static IReadOnlyList<HistoricalLogFormat> KnownFormats { get; } =
    [
        new(LogRedactionKind.Transcript,
            "speech decoding lines from TranscriptionService (Debug)", "0.3.11 to 0.4.2"),
        new(LogRedactionKind.Transcript,
            "Settings warnings when no mail client could open an AI result report (the message of the attached " +
            "exception, which quoted the report)", "0.3.14 to 0.4.2"),

        new(LogRedactionKind.EndpointAddress,
            "AI cleanup skip reasons naming a custom endpoint while connecting, when ready, or when unreachable " +
            "(DictationController warnings and Trace lines)", "0.2.19 to 0.4.2"),
        new(LogRedactionKind.EndpointAddress,
            "OpenAI-compatible endpoint validation warnings", "0.1.7 to 0.3.8"),
        new(LogRedactionKind.EndpointAddress,
            "Azure deployment validation warnings (the endpoint address)", "0.2.19 to 0.3.8"),

        new(LogRedactionKind.AzureResourceName,
            "AI cleanup skip reasons naming an Azure deployment while connecting, when ready, or when not found",
            "0.2.19 to 0.4.2"),
        new(LogRedactionKind.AzureResourceName,
            "Azure deployment validation warnings (the deployment name)", "0.1.0 to 0.3.8"),
        new(LogRedactionKind.AzureResourceName,
            "Chat Completions fallback lines naming an Azure deployment", "0.4.0 to 0.4.2"),
        new(LogRedactionKind.AzureResourceName,
            "Azure discovery lines naming an account, a subscription or a deployment (Debug)", "0.3.11 to 0.4.2"),
        new(LogRedactionKind.AzureResourceName,
            "session banner cleanup lines naming the Azure deployment", "0.3.11 to 0.4.2"),

        new(LogRedactionKind.UserContent,
            "invalid dictionary entry warnings, with the attached exception", "0.1.0 to 0.4.2"),
        new(LogRedactionKind.UserContent,
            "invalid snippet warnings, with the attached exception", "0.1.7 to 0.4.2"),
        new(LogRedactionKind.UserContent,
            "custom dictionary library import, removal and read-failure lines, with the attached exception",
            "0.1.16 to 0.4.2"),
        new(LogRedactionKind.UserContent,
            "per-app profile lines naming the profile", "0.1.7 to 0.4.2"),

        new(LogRedactionKind.ProviderText,
            "AI cleanup failure lines (the whole reason)", "0.1.4 to 0.4.2"),
        new(LogRedactionKind.ProviderText,
            "AI cleanup skip reasons for an unavailable provider, other than the endpoint and deployment forms " +
            "above (the whole detail)", "0.2.19 to 0.4.2"),
        new(LogRedactionKind.ProviderText,
            "AI cleanup initialization probe warnings for providers other than Foundry Local", "0.3.9 to 0.4.2"),
        new(LogRedactionKind.ProviderText,
            "overlay failure and warning text, in overlay lines and overlay command failures", "0.1.5 to 0.4.2"),
        new(LogRedactionKind.ProviderText,
            "text action failure details", "0.3.12 to 0.3.17"),
        new(LogRedactionKind.ProviderText,
            "Azure sign-in rejection messages", "0.2.15 to 0.4.2"),
        new(LogRedactionKind.ProviderText,
            "messages of exceptions attached to AI cleanup provider failures other than Foundry Local, to " +
            "endpoint and deployment validation warnings, and to Azure discovery lines", "0.1.0 to 0.4.2"),
        new(LogRedactionKind.ProviderText,
            "messages of exceptions attached to Settings warnings about Azure sign-in, listing subscriptions and " +
            "deployments, installing Azure CLI, and verifying a service principal or an API key and its endpoint",
            "0.2.4 to 0.4.2"),
    ];

    /// <summary>
    /// Cheap test for whether an entry opens with a logger category that has a recognized format, so ordinary
    /// entries never pay for parsing.
    /// </summary>
    public static bool MayContainKnownFormat(string? text) =>
        !string.IsNullOrEmpty(text) && LogLineRedactor.OpensKnownEntry(text);

    /// <summary>
    /// Returns <paramref name="line"/> with any recognized sensitive value replaced, adding to
    /// <paramref name="counts"/>, or the same instance when nothing was recognized. One line on its own:
    /// exception text on the lines after it needs a <see cref="LogLineRedactor"/> that sees them in order.
    /// </summary>
    public static string RedactLine(string line, ref LogRedactionCounts counts)
    {
        ArgumentNullException.ThrowIfNull(line);
        var redactor = new LogLineRedactor();
        var redacted = redactor.Redact(line);
        counts += redactor.Counts;
        return redacted;
    }

    /// <summary>
    /// Redacts one entry on its way to the log. An entry can span several lines when an exception is
    /// attached; its line breaks are kept exactly.
    /// </summary>
    public static string RedactEntry(string text)
    {
        if (!MayContainKnownFormat(text))
        {
            return text;
        }

        var redactor = new LogLineRedactor();
        if (text.IndexOf('\n') < 0)
        {
            return redactor.Redact(text);
        }

        var builder = new StringBuilder(text.Length);
        var changed = false;
        var position = 0;
        while (true)
        {
            var newline = text.IndexOf('\n', position);
            var end = newline < 0 ? text.Length : newline;
            var bodyEnd = end > position && text[end - 1] == '\r' ? end - 1 : end;
            var body = text.Substring(position, bodyEnd - position);
            var redacted = redactor.Redact(body);
            changed |= !ReferenceEquals(redacted, body);
            builder.Append(redacted).Append(text, bodyEnd, end - bodyEnd);
            if (newline < 0)
            {
                break;
            }

            builder.Append('\n');
            position = newline + 1;
        }

        return changed ? builder.ToString() : text;
    }

    /// <summary>
    /// Copies a log file from <paramref name="source"/> to <paramref name="destination"/>, redacting the
    /// recognized formats line by line. Every other byte is copied unchanged, line endings included, and a
    /// final line with no terminator (a file being appended to) is still examined.
    /// </summary>
    public static LogRedactionCounts CopyRedacted(Stream source, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        var redactor = new LogLineRedactor();
        var chunk = ArrayPool<byte>.Shared.Rent(ChunkBytes);
        var carry = new ArrayBufferWriter<byte>(256);
        try
        {
            int read;
            while ((read = source.Read(chunk, 0, ChunkBytes)) > 0)
            {
                var span = chunk.AsSpan(0, read);
                int newline;
                while ((newline = span.IndexOf((byte)'\n')) >= 0)
                {
                    var segment = span[..(newline + 1)];
                    if (carry.WrittenCount == 0)
                    {
                        EmitLine(segment, destination, redactor);
                    }
                    else
                    {
                        carry.Write(segment);
                        EmitLine(carry.WrittenSpan, destination, redactor);
                        carry.ResetWrittenCount();
                    }

                    span = span[(newline + 1)..];
                }

                if (!span.IsEmpty)
                {
                    carry.Write(span);
                }
            }

            if (carry.WrittenCount > 0)
            {
                EmitLine(carry.WrittenSpan, destination, redactor);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        return redactor.Counts;
    }

    /// <summary>
    /// Rewrites, in place, every retained log file dated before <paramref name="before"/> that still holds a
    /// recognized format. A file without one is not touched at all. Each rewrite goes through a scratch file
    /// in the same folder, flushed to disk and then moved over the original in one step with its last-write
    /// time kept, so a failure at any point leaves the original as it was. A small ledger in the folder
    /// records files already found clean or rewritten (name, length and last-write time only, never content)
    /// so an unchanged file is not read again. Never throws.
    /// </summary>
    /// <param name="logsDirectory">The log folder.</param>
    /// <param name="before">
    /// First day NOT to touch (see <see cref="LogDayChange.ScrubBefore"/>): a file that may still be
    /// appended to must never be replaced underneath its writers.
    /// </param>
    public static LogScrubResult ScrubRetainedFiles(string logsDirectory, DateOnly before)
    {
        var scanned = 0;
        var rewritten = 0;
        var skipped = 0;
        var verified = 0;
        var total = default(LogRedactionCounts);
        try
        {
            if (string.IsNullOrWhiteSpace(logsDirectory) || !Directory.Exists(logsDirectory))
            {
                return new LogScrubResult(0, 0, 0, 0, total);
            }

            DeleteAbandonedScratchFiles(logsDirectory);

            var ledgerPath = Path.Combine(logsDirectory, LogScrubLedger.FileName);
            var ledger = LogScrubLedger.Load(ledgerPath, RulesVersion);
            var files = ScribeLogFiles.Enumerate(logsDirectory);
            foreach (var file in files.Where(f => f.Day < before).OrderBy(f => f.Day))
            {
                var name = Path.GetFileName(file.Path);
                if (!TryStat(file.Path, out var length, out var lastWriteUtcTicks))
                {
                    skipped++;
                    continue;
                }

                if (ledger.IsVerified(name, length, lastWriteUtcTicks))
                {
                    verified++;
                    continue;
                }

                scanned++;
                switch (TryScrubFile(file.Path, out var counts))
                {
                    case ScrubOutcome.Clean:
                        ledger.Record(name, length, lastWriteUtcTicks);
                        break;
                    case ScrubOutcome.Rewritten:
                        rewritten++;
                        total += counts;
                        if (TryStat(file.Path, out var newLength, out var newLastWriteUtcTicks))
                        {
                            ledger.Record(name, newLength, newLastWriteUtcTicks);
                        }

                        break;
                    default:
                        skipped++;
                        break;
                }
            }

            ledger.RetainOnly(files.Select(f => Path.GetFileName(f.Path)));
            ledger.Save(ledgerPath, RulesVersion);
        }
        catch (Exception)
        {
            // Housekeeping; the next start or day change tries again.
        }

        return new LogScrubResult(scanned, rewritten, skipped, verified, total);
    }

    private enum ScrubOutcome
    {
        Clean,
        Rewritten,
        Failed,
    }

    private static bool TryStat(string path, out long length, out long lastWriteUtcTicks)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists)
            {
                length = info.Length;
                lastWriteUtcTicks = info.LastWriteTimeUtc.Ticks;
                return true;
            }
        }
        catch (Exception)
        {
            // Treated as unreadable this time.
        }

        length = 0;
        lastWriteUtcTicks = 0;
        return false;
    }

    private static ScrubOutcome TryScrubFile(string path, out LogRedactionCounts counts)
    {
        counts = default;
        string? scratch = null;
        try
        {
            DateTime lastWriteUtc;
            using (var source = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                // A read-only pass first, so a clean file is never rewritten, not even byte for byte.
                if (CopyRedacted(source, Stream.Null).Total == 0)
                {
                    return ScrubOutcome.Clean;
                }

                lastWriteUtc = File.GetLastWriteTimeUtc(path);
                source.Position = 0;
                scratch = path + ScratchInfix + Guid.NewGuid().ToString("N")[..8] + ScratchExtension;
                using var target = new FileStream(scratch, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                counts = CopyRedacted(source, target);

                // On disk before the rename, so a power loss straight after it cannot leave a renamed but
                // empty file where the original used to be.
                target.Flush(flushToDisk: true);
            }

            File.SetLastWriteTimeUtc(scratch, lastWriteUtc);

            // Fails, and leaves the original untouched, while another process holds the file open without
            // delete sharing (a viewer, or the diagnostics export mid-read). The next pass retries.
            File.Move(scratch, path, overwrite: true);
            scratch = null;
            return ScrubOutcome.Rewritten;
        }
        catch (Exception)
        {
            counts = default;
            return ScrubOutcome.Failed;
        }
        finally
        {
            if (scratch is not null)
            {
                TryDelete(scratch);
            }
        }
    }

    // A scratch file only survives a crash between creating it and the move. Its name never matches
    // ScribeLogFiles.SearchPattern, so the retention sweep would otherwise keep it forever.
    private static void DeleteAbandonedScratchFiles(string logsDirectory)
    {
        foreach (var path in Directory.EnumerateFiles(logsDirectory, "scribe-*" + ScratchInfix + "*" + ScratchExtension))
        {
            var name = Path.GetFileName(path);
            var infix = name.IndexOf(ScribeLogFiles.FileExtension + ScratchInfix, StringComparison.OrdinalIgnoreCase);
            if (infix > 0 && ScribeLogFiles.TryParseDay(name[..(infix + ScribeLogFiles.FileExtension.Length)], out _))
            {
                TryDelete(path);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // Left for the next pass.
        }
    }

    private static void EmitLine(ReadOnlySpan<byte> line, Stream destination, LogLineRedactor redactor)
    {
        var terminator = line.EndsWith("\r\n"u8) ? 2 : line.EndsWith("\n"u8) ? 1 : 0;
        var body = line[..^terminator];
        if (!redactor.NeedsLine(body))
        {
            destination.Write(line);
            return;
        }

        var text = Utf8.GetString(body);
        var redacted = redactor.Redact(text);
        if (ReferenceEquals(redacted, text))
        {
            destination.Write(line);
            return;
        }

        destination.Write(Utf8.GetBytes(redacted));
        destination.Write(line[^terminator..]);
    }

    internal static string TranscriptPlaceholder(int length) =>
        string.Create(CultureInfo.InvariantCulture, $"[transcript redacted, {length} chars]");
}
