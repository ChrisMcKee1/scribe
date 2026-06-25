using Scribe.Core.Models;

namespace Scribe.Core.Persistence;

/// <summary>Loads and persists the user's <see cref="AppSettings"/> plus arbitrary scalar keys.</summary>
public interface ISettingsRepository
{
    /// <summary>Returns the persisted settings, or freshly-defaulted settings when none exist.</summary>
    AppSettings Load();

    /// <summary>Persists the full settings document.</summary>
    void Save(AppSettings settings);

    /// <summary>Reads a single raw value by key, or <see langword="null"/> when absent.</summary>
    string? Get(string key);

    /// <summary>Inserts or updates a single raw value by key.</summary>
    void Set(string key, string value);
}
