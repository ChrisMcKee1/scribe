namespace Scribe.Core.Models;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Where Settings gets its accent colour.</summary>
public enum AccentSource
{
    /// <summary>Use Scribe blue.</summary>
    Scribe = 0,

    /// <summary>Use the user's Windows accent colour.</summary>
    Windows = 1,
}

/// <summary>
/// Reads unknown cosmetic accent values as Scribe blue, so a later build's value never makes the whole settings
/// document unreadable.
/// </summary>
internal sealed class AccentSourceJsonConverter : JsonConverter<AccentSource>
{
    public override AccentSource Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            if (string.Equals(value, nameof(AccentSource.Scribe), StringComparison.OrdinalIgnoreCase))
            {
                return AccentSource.Scribe;
            }

            if (string.Equals(value, nameof(AccentSource.Windows), StringComparison.OrdinalIgnoreCase))
            {
                return AccentSource.Windows;
            }
        }

        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            using var _ = JsonDocument.ParseValue(ref reader);
        }

        return AccentSource.Scribe;
    }

    public override void Write(Utf8JsonWriter writer, AccentSource value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Enum.IsDefined(value) ? value.ToString() : AccentSource.Scribe.ToString());
}
