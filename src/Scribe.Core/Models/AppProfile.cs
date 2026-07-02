namespace Scribe.Core.Models;

/// <summary>
/// A per-app dictation profile: when the focused app at the end of a capture matches one of
/// <see cref="ProcessNames"/>, the profile's overrides apply to that dictation — a different AI
/// writing style (e.g. formal for Outlook, terse for a terminal) and/or line-break handling.
/// Null/blank overrides fall back to the global settings. Mutable POCO so it serializes inside
/// <see cref="AppSettings"/> and binds to the settings editor.
/// </summary>
public sealed class AppProfile
{
    /// <summary>Display name shown in the settings list (e.g. "Email", "Chat", "Terminal").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Process names this profile applies to, without the .exe suffix (e.g. "OUTLOOK", "slack").
    /// Compared case-insensitively; a trailing .exe on an entry is tolerated and stripped.
    /// </summary>
    public List<string> ProcessNames { get; set; } = new();

    /// <summary>AI writing style for this app; null/blank keeps the global style.</summary>
    public string? WritingStyle { get; set; }

    /// <summary>Line-break handling for this app; null keeps the global setting.</summary>
    public NewlineInjectionMode? NewlineHandling { get; set; }
}

/// <summary>Resolves which profile (if any) applies to a foreground process.</summary>
public static class AppProfileMatcher
{
    /// <summary>
    /// Returns the first profile listing <paramref name="processName"/> (case-insensitive,
    /// .exe-insensitive), or null. First match wins, in the user's configured order.
    /// </summary>
    public static AppProfile? Match(IReadOnlyList<AppProfile>? profiles, string? processName)
    {
        if (profiles is null || profiles.Count == 0 || string.IsNullOrWhiteSpace(processName))
        {
            return null;
        }

        var target = Normalize(processName);
        foreach (var profile in profiles)
        {
            foreach (var candidate in profile.ProcessNames)
            {
                if (!string.IsNullOrWhiteSpace(candidate) &&
                    string.Equals(Normalize(candidate), target, StringComparison.OrdinalIgnoreCase))
                {
                    return profile;
                }
            }
        }

        return null;
    }

    private static string Normalize(string name)
    {
        var trimmed = name.Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^4]
            : trimmed;
    }
}
