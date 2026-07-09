using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Scribe.Core.Models;

namespace Scribe.Core.Persistence;

/// <inheritdoc cref="ISettingsRepository"/>
public sealed class SettingsRepository : ISettingsRepository
{
    private const string SettingsKey = "app_settings";
    private const string RecoveryKey = "app_settings_recovery";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false,
    };

    private readonly ScribeDatabase _database;

    public SettingsRepository(ScribeDatabase database) => _database = database;

    public bool LastLoadFailed { get; private set; }

    public AppSettings Load()
    {
        var json = Get(SettingsKey);
        if (string.IsNullOrWhiteSpace(json))
        {
            LastLoadFailed = false;
            return AppSettings.CreateDefault();
        }

        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (settings is null)
            {
                throw new JsonException("The settings document contained null.");
            }

            LastLoadFailed = false;
            return Normalize(settings);
        }
        catch (JsonException)
        {
            LastLoadFailed = true;
            PreserveRecoveryCopy(json);
            return AppSettings.CreateDefault();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        Set(SettingsKey, json);
    }

    public void SaveBundle(
        AppSettings settings,
        IReadOnlyList<DictionaryEntry>? dictionaryEntries,
        IReadOnlyList<Snippet>? snippets)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var json = JsonSerializer.Serialize(settings, JsonOptions);

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        if (dictionaryEntries is not null)
        {
            DictionaryRepository.SaveAll(connection, transaction, dictionaryEntries);
        }

        if (snippets is not null)
        {
            SnippetRepository.SaveAll(connection, transaction, snippets);
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO settings (key, value) VALUES ($key, $value)
            ON CONFLICT (key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", SettingsKey);
        command.Parameters.AddWithValue("$value", json);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public string? Get(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    public void Set(string key, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO settings (key, value) VALUES ($key, $value)
            ON CONFLICT (key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private void PreserveRecoveryCopy(string json)
    {
        try
        {
            if (Get(RecoveryKey) is null)
            {
                Set(RecoveryKey, json);
            }
        }
        catch
        {
            // Recovery metadata must never turn a settings fallback into a startup failure.
        }
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        settings.Hotkey ??= HotkeyBinding.Default;
        settings.EnabledDictionaryLibraryIds ??= [];
        settings.Profiles ??= [];
        settings.Profiles = settings.Profiles
            .Where(profile => profile is not null)
            .Select(profile => new AppProfile
            {
                Name = profile.Name ?? string.Empty,
                ProcessNames = profile.ProcessNames ?? [],
                WritingStyle = profile.WritingStyle,
                NewlineHandling = profile.NewlineHandling,
            })
            .ToList();
        settings.AiCleanupModel ??= Cleanup.CleanupModelCatalog.DefaultAlias;
        settings.AiCleanupWritingStyle ??= string.Empty;
        settings.AiCleanupFrontierPrompt ??= string.Empty;
        settings.AiCleanupLocalPrompt ??= string.Empty;
        settings.DecodeThreads = Math.Clamp(settings.DecodeThreads, 0, 16);
        return settings;
    }
}
