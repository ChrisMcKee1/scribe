using System.Text;

namespace Scribe.Core.Diagnostics;

/// <summary>Builds the bounded aggregate-only payload for an explicit Usage AI request.</summary>
public static class UsageInsight
{
    public const string SystemPrompt =
        "Describe only the supplied aggregate dictation-usage data. Identify recurring technical " +
        "domains and terminology in 2 to 4 factual sentences. Do not infer personality, mood, " +
        "sentiment, productivity, intent, or time saved. Do not judge the user. Do not invent " +
        "terms or facts that are not present. Return plain text only.";

    /// <summary>
    /// Builds the payload sent to the user's configured AI endpoint. Guarantee: only terms with
    /// <c>Covered == true</c> (dictionary-canonical labels) are ever included; novel mined
    /// tokens are verbatim words from the user's dictations and never enter the payload.
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
            // codenames); only dictionary-canonical labels may leave the machine.
            if (!term.Covered)
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
