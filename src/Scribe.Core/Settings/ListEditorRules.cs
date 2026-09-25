using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Scribe.Core.Cleanup;
using Scribe.Core.Models;

namespace Scribe.Core.Settings;

public static partial class TextFilter
{
    public static bool Matches(string? query, params string?[] fields)
    {
        var normalizedQuery = Normalize(query);
        if (normalizedQuery.Length == 0)
        {
            return true;
        }

        return fields.Any(field => Normalize(field).Contains(normalizedQuery, StringComparison.Ordinal));
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var form = value.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(form.Length);
        foreach (var ch in form)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.ToUpperInvariant(ch));
            }
        }

        return SpaceRuns().Replace(builder.ToString().Normalize(NormalizationForm.FormC), " ");
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex SpaceRuns();
}

public static class ProgramNames
{
    public static IReadOnlyList<string> Normalize(IEnumerable<string?> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var name in names)
        {
            var value = (name ?? string.Empty).Trim();
            if (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                value = value[..^4];
            }

            if (value.Length > 0 && seen.Add(value))
            {
                result.Add(value);
            }
        }

        return result;
    }
}

public sealed record RecentApp(string ProcessName, string DisplayName, int DictationCount);

public static class RecentApps
{
    public static IReadOnlyList<RecentApp> From(IEnumerable<HistoryEntry> history, int max = 12)
    {
        ArgumentNullException.ThrowIfNull(history);
        return history
            .Select(entry => entry.TargetApp)
            .Where(app => !string.IsNullOrWhiteSpace(app))
            .Select(app => ProgramNames.Normalize([app]).FirstOrDefault())
            .Where(app => !string.IsNullOrWhiteSpace(app))
            .GroupBy(app => app!, StringComparer.OrdinalIgnoreCase)
            .Select(group => new RecentApp(group.Key, AppDisplayName.For(group.Key), group.Count()))
            .OrderByDescending(app => app.DictationCount)
            .ThenBy(app => app.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Take(max)
            .ToList();
    }
}

public enum UsagePeriod
{
    Last7Days,
    Last30Days,
    Last90Days,
    AllKeptHistory,
}

public sealed record UsagePeriodDescription(string StatusText, bool ShowRetry);

public static class UsagePeriodState
{
    public static string Label(UsagePeriod period) => period switch
    {
        UsagePeriod.Last7Days => "Last 7 days",
        UsagePeriod.Last30Days => "Last 30 days",
        UsagePeriod.Last90Days => "Last 90 days",
        UsagePeriod.AllKeptHistory => "All kept history",
        _ => throw new ArgumentOutOfRangeException(nameof(period), period, null),
    };

    public static UsagePeriodDescription Describe(UsagePeriod shownPeriod, UsagePeriod? loadingPeriod, bool loadFailed)
    {
        if (loadFailed)
        {
            return new UsagePeriodDescription($"Showing {Label(shownPeriod)}. Usage isn't available right now.", ShowRetry: true);
        }

        return loadingPeriod is { } next && next != shownPeriod
            ? new UsagePeriodDescription($"Showing {Label(shownPeriod)}. Loading {Label(next)}...", ShowRetry: false)
            : new UsagePeriodDescription(Label(shownPeriod), ShowRetry: false);
    }
}

public sealed record TextChangesNoticeState(bool Show, string Message, string ActionText);

public static class TextChangesNotice
{
    public const string Message = "Your dictionary and snippets are turned off, so Scribe saves these changes but doesn't use them.";
    public const string ActionText = "Turn on";

    public static TextChangesNoticeState Describe(bool applyDictionaryAndSnippets) =>
        new(!applyDictionaryAndSnippets, Message, ActionText);
}

public sealed record ProfileRulesState(bool ShowAiCleanupNotice, string? NoticeText, string? ActionText, bool ShowFirstMatchHint);

public static class ProfileRules
{
    public const string AiCleanupNotice = "Writing styles are used only when AI cleanup is on.";
    public const string AiCleanupAction = "Go to AI cleanup";
    public const string FirstMatchHint = "When an app matches more than one profile, Scribe uses the one higher in the list.";

    public static ProfileRulesState Describe(bool aiCleanupEnabled, int profileCount) =>
        new(!aiCleanupEnabled, aiCleanupEnabled ? null : AiCleanupNotice, aiCleanupEnabled ? null : AiCleanupAction, profileCount >= 2);
}

public sealed record UsageInsightAvailabilityState(bool IsVisible, bool IsEnabled, string? DisabledReason, string Description);

public static class UsageInsightAvailability
{
    public const string DisabledReason = "AI cleanup isn't ready yet.";

    public static UsageInsightAvailabilityState Describe(bool aiCleanupEnabled, bool cleanupReady, CleanupProvider provider, string? providerName = null)
    {
        var name = providerName ?? ProviderName(provider);
        var description = $"Get a short summary of your dictation habits from {name}. Scribe sends your totals and the names of dictionary words that came up, never your dictations.";
        return new UsageInsightAvailabilityState(aiCleanupEnabled, aiCleanupEnabled && cleanupReady, aiCleanupEnabled && !cleanupReady ? DisabledReason : null, description);
    }

    private static string ProviderName(CleanupProvider provider) => provider switch
    {
        CleanupProvider.FoundryLocal => "On this PC",
        CleanupProvider.AzureFoundry => "Microsoft Foundry",
        CleanupProvider.OpenAiCompatible => "Another AI service",
        CleanupProvider.GitHubCopilot => "GitHub Copilot",
        _ => "AI cleanup",
    };
}
