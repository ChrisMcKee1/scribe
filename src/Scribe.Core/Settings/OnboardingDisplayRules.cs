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

public static partial class AppDisplayName
{
    private static readonly IReadOnlyDictionary<string, string> Known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["outlook"] = "Outlook",
        ["olk"] = "Outlook",
        ["ms-teams"] = "Teams",
        ["teams"] = "Teams",
        ["msteams"] = "Teams",
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
        ["windowsterminal"] = "Terminal",
        ["wt"] = "Terminal",
        ["slack"] = "Slack",
        ["discord"] = "Discord",
        ["zoom"] = "Zoom",
    };

    public static string For(string? processName)
    {
        var normalized = NormalizeProcessName(processName);
        return normalized.Length == 0
            ? string.Empty
            : Known.TryGetValue(normalized, out var display) ? display : normalized;
    }

    private static string NormalizeProcessName(string? processName)
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
