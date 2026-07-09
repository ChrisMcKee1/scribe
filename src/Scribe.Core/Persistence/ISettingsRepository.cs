using Scribe.Core.Models;

namespace Scribe.Core.Persistence;

/// <summary>Loads and persists the user's <see cref="AppSettings"/> plus arbitrary scalar keys.</summary>
public interface ISettingsRepository
{
    /// <summary>True when the most recent load fell back because the persisted document was invalid.</summary>
    bool LastLoadFailed { get; }

    /// <summary>Returns the persisted settings, or freshly-defaulted settings when none exist.</summary>
    AppSettings Load();

    /// <summary>Persists the full settings document.</summary>
    void Save(AppSettings settings);

    /// <summary>
    /// Atomically persists settings plus any changed dictionary and snippet collections.
    /// A null collection leaves that section untouched.
    /// </summary>
    void SaveBundle(
        AppSettings settings,
        IReadOnlyList<DictionaryEntry>? dictionaryEntries,
        IReadOnlyList<Snippet>? snippets);

    /// <summary>Reads a single raw value by key, or <see langword="null"/> when absent.</summary>
    string? Get(string key);

    /// <summary>Inserts or updates a single raw value by key.</summary>
    void Set(string key, string value);
}
