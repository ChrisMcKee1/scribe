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
            builder.AppendLine($"- {term.Text}: {term.Dictations} dictations");
        }

        var value = builder.ToString().Trim();
        return value.Length <= maxChars ? value : value[..maxChars].TrimEnd();
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

        return value.Length <= maxChars ? value : value[..maxChars].TrimEnd();
    }
}