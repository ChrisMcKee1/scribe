using Scribe.Core.Models;

namespace Scribe.Core.Settings;

/// <summary>
/// Pure builder for the per-app profile list edited in the settings window. Turns editor rows
/// (name, comma-separated process names, writing style, newline mode) into <see cref="AppProfile"/>
/// instances, skipping empty placeholder rows, trimming, and splitting the process list.
/// </summary>
public static class ProfileBuilder
{
    /// <summary>One editor row: raw name, the comma-separated process list, style, and newline mode.</summary>
    public readonly record struct Row(
        string? Name, string? Processes, string? WritingStyle, NewlineInjectionMode? NewlineHandling);

    /// <summary>
    /// Builds the profiles to persist, skipping rows that have neither a name nor any process names.
    /// </summary>
    public static List<AppProfile> Build(IReadOnlyList<Row> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var profiles = new List<AppProfile>();
        foreach (var row in rows)
        {
            var processes = (row.Processes ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(p => p.Length > 0)
                .ToList();

            if (string.IsNullOrWhiteSpace(row.Name) && processes.Count == 0)
            {
                continue; // an empty placeholder row
            }

            profiles.Add(new AppProfile
            {
                Name = string.IsNullOrWhiteSpace(row.Name) ? "Unnamed profile" : row.Name.Trim(),
                ProcessNames = processes,
                WritingStyle = string.IsNullOrWhiteSpace(row.WritingStyle) ? null : row.WritingStyle.Trim(),
                NewlineHandling = row.NewlineHandling,
            });
        }

        return profiles;
    }
}
