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


public sealed record SnippetListItemText(string Primary, string? Secondary);

public static class SnippetListText
{
    public static SnippetListItemText Describe(string? phrase, bool enabled)
    {
        var primary = string.IsNullOrWhiteSpace(phrase) ? "New snippet" : phrase.Trim();
        return new SnippetListItemText(primary, enabled ? null : "Off");
    }
}

public sealed record ProfileListItemText(string Primary, string Secondary);

public static class ProfileListText
{
    public const string EmptyApps = "No apps";

    public static ProfileListItemText Describe(string? name, string? apps)
    {
        var primary = string.IsNullOrWhiteSpace(name) ? "New profile" : name.Trim();
        var secondary = FriendlyApps(apps);
        return new ProfileListItemText(primary, secondary.Length == 0 ? EmptyApps : secondary);
    }

    public static string FriendlyApps(string? apps) =>
        string.Join(", ", ProfileAppChips.FromProgramNames(apps).Select(chip => chip.DisplayName));

    private static IEnumerable<string> SplitApps(string? apps) =>
        (apps ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public sealed record ProfileAppChip(string GroupKey, string DisplayName, IReadOnlyList<string> ProgramNames, string RemoveName)

{

    public static ProfileAppChip FromProgramNames(IReadOnlyList<string> programNames)

    {

        ArgumentNullException.ThrowIfNull(programNames);

        if (programNames.Count == 0)

        {

            throw new ArgumentException("At least one program name is required.", nameof(programNames));

        }



        var first = programNames[0];

        var display = AppDisplayName.For(first);

        return new ProfileAppChip(AppDisplayName.GroupKeyFor(first), display, programNames, $"Remove {display}");

    }

}



public static class ProfileAppChips

{

    public static IReadOnlyList<ProfileAppChip> FromProgramNames(string? apps)

    {

        var normalized = ProgramNames.Normalize((apps ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return normalized

            .GroupBy(AppDisplayName.GroupKeyFor, StringComparer.OrdinalIgnoreCase)

            .Select(group => ProfileAppChip.FromProgramNames(group.ToList()))

            .ToList();

    }



    public static string RemoveGroup(string? apps, string groupKey)

    {

        ArgumentException.ThrowIfNullOrWhiteSpace(groupKey);

        var filtered = ProgramNames.Normalize((apps ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))

            .Where(app => !string.Equals(AppDisplayName.GroupKeyFor(app), groupKey, StringComparison.OrdinalIgnoreCase));

        return string.Join(", ", filtered);

    }



    public static string ToProgramNames(IEnumerable<ProfileAppChip> chips) =>

        string.Join(", ", chips.SelectMany(chip => chip.ProgramNames));

}



public sealed record AppPickerCandidate(string ProcessName, string DisplayName, bool IsRunning, int RecentDictations = 0);

public sealed record AppPickerOption(string ProcessName, string DisplayName, bool IsRunning, int RecentDictations)
{
    public string Label => $"{DisplayName} ({ProcessName})";
}

public static class AppPickerOptions
{
    public static IReadOnlyList<AppPickerOption> Build(
        IEnumerable<AppPickerCandidate> runningApps,
        IEnumerable<RecentApp> recentApps,
        IEnumerable<string?> selectedApps)
    {
        ArgumentNullException.ThrowIfNull(runningApps);
        ArgumentNullException.ThrowIfNull(recentApps);
        ArgumentNullException.ThrowIfNull(selectedApps);

        var selected = new HashSet<string>(ProgramNames.Normalize(selectedApps), StringComparer.OrdinalIgnoreCase);
        var options = new Dictionary<string, AppPickerOption>(StringComparer.OrdinalIgnoreCase);

        foreach (var app in runningApps
            .Select(app => new AppPickerCandidate(
                ProgramNames.Normalize([app.ProcessName]).FirstOrDefault() ?? string.Empty,
                string.IsNullOrWhiteSpace(app.DisplayName) ? AppDisplayName.For(app.ProcessName) : app.DisplayName.Trim(),
                IsRunning: true,
                app.RecentDictations))
            .Where(app => app.ProcessName.Length > 0 && !selected.Contains(app.ProcessName))
            .OrderBy(app => app.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(app => app.ProcessName, StringComparer.OrdinalIgnoreCase))
        {
            options.TryAdd(app.ProcessName, new AppPickerOption(app.ProcessName, app.DisplayName, IsRunning: true, app.RecentDictations));
        }

        foreach (var app in recentApps.Where(app => !selected.Contains(app.ProcessName)))
        {
            if (options.TryGetValue(app.ProcessName, out var existing))
            {
                options[app.ProcessName] = existing with { RecentDictations = app.DictationCount };
                continue;
            }

            options.Add(app.ProcessName, new AppPickerOption(app.ProcessName, app.DisplayName, IsRunning: false, app.DictationCount));
        }

        return options.Values.ToList();
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
    public const string AiCleanupOffMessage = "Your dictionary and snippets are turned off, so Scribe saves these changes but doesn't use them.";
    public const string AiCleanupOnMessage = "Your dictionary and snippets are turned off, so Scribe doesn't replace any words with them. AI cleanup still receives your vocabulary when this is off.";
    public const string ActionText = "Turn on";

    public static TextChangesNoticeState Describe(bool applyDictionaryAndSnippets) =>
        Describe(applyDictionaryAndSnippets, aiCleanupEnabled: false);

    public static TextChangesNoticeState Describe(bool applyDictionaryAndSnippets, bool aiCleanupEnabled) =>
        new(
            !applyDictionaryAndSnippets,
            aiCleanupEnabled ? AiCleanupOnMessage : AiCleanupOffMessage,
            ActionText);
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
