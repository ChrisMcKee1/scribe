using System.Buffers;
using System.Text;
using Scribe.Core.Cleanup;

namespace Scribe.Core.Diagnostics;

/// <summary>Builds the bounded aggregate-only payload for an explicit Usage AI request.</summary>
public static class UsageInsight
{
    public const string SystemPrompt =
        "Describe only the supplied aggregate dictation-usage data. Identify recurring technical " +
        "domains and terminology in 2 to 4 factual sentences. Do not infer personality, mood, " +
        "sentiment, productivity, intent, or time saved. Do not judge the user. Do not invent " +
        "terms or facts that are not present. Return plain text only.";

    // Every character .NET treats as a line break (string.ReplaceLineEndings), plus the vertical tab.
    private static readonly SearchValues<char> LineBreaks = SearchValues.Create("\r\n\u000B\u000C\u0085\u2028\u2029");

    /// <summary>
    /// Whether a dictionary replacement may be shared as a term label: one line of at most
    /// <see cref="CleanupPrompt.MaxGlossaryTermChars"/> characters, the glossary's own per-term cap,
    /// judged exactly as the user wrote it, before any trimming. A replacement that spans lines or
    /// runs longer is a template (a signature, an address, a footer), and a label would carry it out
    /// verbatim, where the glossary at least flattens and shortens it.
    /// </summary>
    internal static bool IsShareableReplacement(string? replacement) =>
        !string.IsNullOrWhiteSpace(replacement) &&
        replacement.Length <= CleanupPrompt.MaxGlossaryTermChars &&
        replacement.AsSpan().IndexOfAny(LineBreaks) < 0;

    /// <summary>
    /// Builds the payload sent to the user's configured AI endpoint. Guarantee: only terms with
    /// <c>Covered == true</c> (dictionary-canonical labels) whose replacements are shareable
    /// (<see cref="UsageAnalyzer.TermUsage.Shareable"/>) are ever included; novel mined tokens are
    /// verbatim words from the user's dictations and never enter the payload, and neither does a
    /// replacement that is really a template.
    /// </summary>
    public static string BuildSummary(UsageAnalyzer.Snapshot snapshot, int maxChars = 4000)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (maxChars <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.AppendLine($"Dictations: {snapshot.Dictations}");
        builder.AppendLine($"Words: {snapshot.Words}");
        builder.AppendLine($"Active days: {snapshot.ActiveDays}");
        builder.AppendLine("Recurring terms:");
        foreach (var term in snapshot.Terms)
        {
            // Uncovered terms are raw tokens mined from dictation text (surnames, project
            // codenames); only dictionary-canonical labels may leave the machine, and only short,
            // single-line ones. The label itself is checked too, so a term marked shareable by
            // mistake still cannot add lines to this payload.
            if (!term.Covered || !term.Shareable || !IsShareableReplacement(term.Text))
            {
                continue;
            }

            builder.AppendLine($"- {term.Text}: {term.Dictations} dictations");
        }

        return Truncate(builder.ToString().Trim(), maxChars);
    }

    public static string? Parse(string? response, int maxChars = 1200)
    {
        if (string.IsNullOrWhiteSpace(response) || maxChars <= 0)
        {
            return null;
        }

        var value = response.Trim();
        if (value.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLine = value.IndexOf('\n');
            var lastFence = value.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLine >= 0 && lastFence > firstLine)
            {
                value = value[(firstLine + 1)..lastFence].Trim();
            }
        }

        return Truncate(value, maxChars);
    }

    private static string Truncate(string value, int maxChars)
    {
        if (value.Length <= maxChars)
        {
            return value;
        }

        // Never cut between the halves of a surrogate pair: a trailing lone high surrogate is
        // invalid UTF-16 and can break downstream encoding of the request or the UI text.
        var cut = maxChars;
        if (char.IsHighSurrogate(value[cut - 1]) && char.IsLowSurrogate(value[cut]))
        {
            cut--;
        }

        return value[..cut].TrimEnd();
    }
}
