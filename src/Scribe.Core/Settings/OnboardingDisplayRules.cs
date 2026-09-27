using System.Text.RegularExpressions;

namespace Scribe.Core.Settings;

public enum SettingsLoadState
{
    Loading,
    Loaded,
    Failed,
}

public sealed record FirstRunHintState(bool Show, string Title, string Message, string ActionText);

public static class FirstRunHint
{
    public const string Title = "Ready to dictate";
    public const string ActionText = "Try it here";

    public static FirstRunHintState ShouldShow(
        SettingsLoadState loadState,
        int historyCount,
        bool dismissed,
        string shortcutInstruction)
    {
        ArgumentNullException.ThrowIfNull(shortcutInstruction);
        var show = loadState == SettingsLoadState.Loaded && historyCount == 0 && !dismissed;
        return new FirstRunHintState(show, Title, shortcutInstruction, ActionText);
    }
}

public sealed record AppProgramGroup(string Key, string DisplayName, IReadOnlyList<string> Programs)
{
    public bool Contains(string? processName)
    {
        var normalized = AppDisplayName.NormalizeProcessName(processName);
        return Programs.Any(program => string.Equals(program, normalized, StringComparison.OrdinalIgnoreCase));
    }
}

public static partial class AppDisplayName
{
    private static readonly IReadOnlyList<AppProgramGroup> Groups =
    [
        new("outlook", "Outlook", ["OUTLOOK"]),
        new("new-outlook", "New Outlook", ["olk"]),
        new("teams", "Teams", ["ms-teams", "Teams", "msteams"]),
        new("terminal", "Terminal", ["WindowsTerminal", "wt"]),
    ];

    private static readonly IReadOnlyDictionary<string, string> Known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["winword"] = "Word",
        ["word"] = "Word",
        ["excel"] = "Excel",
        ["powerpnt"] = "PowerPoint",
        ["powerpoint"] = "PowerPoint",
        ["onenote"] = "OneNote",
        ["msedge"] = "Edge",
        ["edge"] = "Edge",
        ["chrome"] = "Chrome",
        ["firefox"] = "Firefox",
        ["code"] = "VS Code",
        ["devenv"] = "Visual Studio",
        ["notepad"] = "Notepad",
        ["slack"] = "Slack",
        ["discord"] = "Discord",
        ["zoom"] = "Zoom",
    };

    public static string For(string? processName)
    {
        var normalized = NormalizeProcessName(processName);
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        var group = GroupFor(normalized);
        if (group is not null)
        {
            return group.DisplayName;
        }

        return Known.TryGetValue(normalized, out var display) ? display : normalized;
    }

    public static AppProgramGroup? GroupFor(string? processName)
    {
        var normalized = NormalizeProcessName(processName);
        return normalized.Length == 0 ? null : Groups.FirstOrDefault(group => group.Contains(normalized));
    }

    public static string GroupKeyFor(string? processName)
    {
        var normalized = NormalizeProcessName(processName);
        return GroupFor(normalized)?.Key ?? normalized;
    }

    public static IReadOnlyList<string> GroupMembersFor(string? processName)
    {
        var normalized = NormalizeProcessName(processName);
        var group = GroupFor(normalized);
        return group is null ? [normalized] : group.Programs;
    }

    public static string NormalizeProcessName(string? processName)
    {
        var value = (processName ?? string.Empty).Trim();
        if (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^4];
        }

        return MultipleWhitespace().Replace(value, " ");
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex MultipleWhitespace();
}
