using Scribe.Core.Models;

namespace Scribe.Core.PostProcessing;

/// <summary>
/// A named, categorized collection of dictionary substitutions that can be switched on as a unit and
/// layered on top of the user's base dictionary. Built-in libraries ship embedded in the app; custom
/// libraries are imported from CSV files the user supplies. Entries carry no database id (Id 0): a
/// library is composed into the effective dictionary in memory, never written into the
/// <c>dictionary</c> table, so enabling or disabling one never touches the user's own entries.
/// </summary>
public sealed record DictionaryLibrary(
    string Id,
    string Name,
    string Category,
    string? Description,
    bool BuiltIn,
    IReadOnlyList<DictionaryEntry> Entries)
{
    /// <summary>Only the entries whose <see cref="DictionaryEntry.Enabled"/> flag is set.</summary>
    public IEnumerable<DictionaryEntry> EnabledEntries => Entries.Where(e => e is { Enabled: true });

    /// <summary>Count of enabled entries, shown in the library list.</summary>
    public int EnabledEntryCount => Entries.Count(e => e is { Enabled: true });
}
