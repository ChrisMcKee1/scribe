using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Scribe.Core.Models;

namespace Scribe.Core.Persistence;

/// <inheritdoc cref="ISettingsRepository"/>
public sealed class SettingsRepository : ISettingsRepository
{
    private const string SettingsKey = "app_settings";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false,
    };

    private readonly ScribeDatabase _database;

    public SettingsRepository(ScribeDatabase database) => _database = database;

    public AppSettings Load()
    {
        var json = Get(SettingsKey);
        if (string.IsNullOrWhiteSpace(json)) return AppSettings.CreateDefault();

        try
        {
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? AppSettings.CreateDefault();
        }
        catch (JsonException)
        {
            // A corrupt or schema-incompatible document should never brick startup.
            return AppSettings.CreateDefault();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        Set(SettingsKey, json);
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
}
