using Microsoft.Extensions.Logging;

namespace Scribe.Core.Cleanup;

/// <summary>
/// The logger Scribe hands the Foundry Local SDK. The SDK writes its own failures through it with
/// the raw exception text (<c>FoundryLocalException</c> logs its message, and its inner exception,
/// through the logger it was given), and those name paths under the user profile, which include the
/// user name. This folds the profile directory to <c>%USERPROFILE%</c> in the message and in the
/// exception text and passes everything else through unchanged, so the SDK's hardware and
/// execution-provider diagnostics still reach the log.
/// </summary>
/// <remarks>
/// Deliberately a prefix rule rather than a scrubber: Microsoft's download hosts and the loopback
/// port the SDK's web service listens on are not user data, and they are what makes a download or
/// startup failure diagnosable. Renders the exception the way the file sink would (message, then
/// the exception text on the next line) and passes no exception object on, so nothing unredacted
/// can reach a sink through it. Never throws.
/// </remarks>
internal sealed class FoundrySdkLogger : ILogger
{
    internal const string ProfileToken = "%USERPROFILE%";

    // A bound on one rendered exception, so a pathological chain cannot become a giant log line.
    private const int MaxExceptionText = 32 * 1024;

    private readonly ILogger _inner;
    private readonly string[] _profileSpellings;

    public FoundrySdkLogger(ILogger inner, string? userProfile)
    {
        _inner = inner;
        _profileSpellings = SpellingsOf(userProfile);
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        try
        {
            if (!_inner.IsEnabled(logLevel))
            {
                return;
            }

            var message = Redact(formatter(state, exception));
            if (exception is not null)
            {
                // Redacted before it is bounded, so a cut can never leave half a user name behind.
                var rendered = Redact(Render(exception));
                message += Environment.NewLine +
                    (rendered.Length <= MaxExceptionText ? rendered : rendered[..MaxExceptionText] + " (truncated)");
            }

            _inner.Log(logLevel, eventId, message, null, static (text, _) => text);
        }
        catch (Exception)
        {
            // A logging failure must never reach the SDK call that was only reporting one.
        }
    }

    /// <summary>The text with every spelling of the user profile directory folded to <see cref="ProfileToken"/>.</summary>
    internal string Redact(string? text) => RedactProfile(text, _profileSpellings);

    internal static string RedactProfile(string? text, IReadOnlyList<string> spellings)
    {
        if (string.IsNullOrEmpty(text) || spellings.Count == 0)
        {
            return text ?? string.Empty;
        }

        foreach (var spelling in spellings)
        {
            text = ReplaceAtBoundary(text, spelling);
        }

        return text;
    }

    // The profile as it appears in text: native separators, forward slashes (file URIs, some native
    // messages), and JSON-escaped backslashes (the SDK's native core reports errors as JSON).
    internal static string[] SpellingsOf(string? userProfile)
    {
        if (string.IsNullOrWhiteSpace(userProfile) || !Path.IsPathFullyQualified(userProfile))
        {
            return [];
        }

        var profile = Path.TrimEndingDirectorySeparator(userProfile.Trim());

        // A root like "C:\" would fold every path on the drive; that is not a profile directory.
        if (Path.GetPathRoot(profile) is { } root && string.Equals(Path.TrimEndingDirectorySeparator(root), profile, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        // Longest first, so the escaped form is folded before its unescaped prefix could split it.
        return new[] { profile.Replace(@"\", @"\\"), profile, profile.Replace('\\', '/') }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(spelling => spelling.Length)
            .ToArray();
    }

    // Case-insensitive, and only where the profile ends at a path boundary: C:\Users\chris must not
    // fold the start of another profile, C:\Users\christine.
    private static string ReplaceAtBoundary(string text, string spelling)
    {
        var start = 0;
        while (start < text.Length)
        {
            var index = text.IndexOf(spelling, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                break;
            }

            var end = index + spelling.Length;
            if (end < text.Length && IsNameCharacter(text[end]))
            {
                start = index + 1;
                continue;
            }

            text = string.Concat(text.AsSpan(0, index), ProfileToken, text.AsSpan(end));
            start = index + ProfileToken.Length;
        }

        return text;
    }

    private static bool IsNameCharacter(char c) => char.IsLetterOrDigit(c) || c is '_' or '-' or '$' or '~';

    private static string Render(Exception exception)
    {
        try
        {
            return exception.ToString();
        }
        catch (Exception)
        {
            // An exception whose own text throws still leaves its type.
            return exception.GetType().FullName ?? "Exception";
        }
    }
}
