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
    public bool Contains(string? processName) => ContainsNormalized(AppDisplayName.NormalizeProcessName(processName));

    internal bool ContainsNormalized(string normalized)
    {
        for (var i = 0; i < Programs.Count; i++)
        {
            if (string.Equals(Programs[i], normalized, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

public static partial class AppDisplayName
{
    private static readonly AppProgramGroup[] Groups =
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

        var group = FirstGroupContaining(NormalizeAgain(normalized));
        if (group is not null)
        {
            return group.DisplayName;
        }

        return Known.TryGetValue(normalized, out var display) ? display : normalized;
    }

    public static AppProgramGroup? GroupFor(string? processName) => FirstGroupContaining(NormalizeProcessName(processName));

    public static string GroupKeyFor(string? processName)
    {
        var normalized = NormalizeProcessName(processName);
        return FirstGroupContaining(NormalizeAgain(normalized))?.Key ?? normalized;
    }

    public static IReadOnlyList<string> GroupMembersFor(string? processName)
    {
        var normalized = NormalizeProcessName(processName);
        var group = FirstGroupContaining(NormalizeAgain(normalized));
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

    // GroupFor for the name its NormalizeProcessName call returned. AppProgramGroup.Contains normalizes its argument again,
    // for every group alike, so it is normalized once here.
    private static AppProgramGroup? FirstGroupContaining(string normalized)
    {
        if (normalized.Length == 0)
        {
            return null;
        }

        var member = NormalizeAgain(normalized);
        foreach (var group in Groups)
        {
            if (group.ContainsNormalized(member))
            {
                return group;
            }
        }

        return null;
    }

    // Normalizing is not idempotent: "x.exe.exe" loses one ".exe" per pass, and stripping ".exe" from "x .exe" exposes a space the
    // next pass trims. The group lookups have always normalized a name two or three times, so a name that is not yet a fixed
    // point still gets each pass; one that is (the usual process name) skips them, since they would return the same text.
    private static string NormalizeAgain(string normalized) =>
        IsFixedPoint(normalized) ? normalized : NormalizeProcessName(normalized);

    // True only when NormalizeProcessName would return this text unchanged: no white space to trim at either end, no ".exe" to
    // strip, and no white-space run the collapse would rewrite (every one a single U+0020). The pattern's \s is char.IsWhiteSpace.
    private static bool IsFixedPoint(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsWhiteSpace(value[i]) &&
                (i == 0 || i == value.Length - 1 || value[i] != ' ' || char.IsWhiteSpace(value[i + 1])))
            {
                return false;
            }
        }

        return !value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex MultipleWhitespace();
}
