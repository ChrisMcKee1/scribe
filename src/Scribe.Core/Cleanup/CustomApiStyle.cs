using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scribe.Core.Cleanup;

/// <summary>
/// Which OpenAI API another AI service is reached through: Chat Completions, which nearly every OpenAI-compatible server
/// takes and every release before 0.5.3 used, or Responses. An address that ends in an API's own path names it, and that
/// wins over this choice (<see cref="CustomServiceAddress"/>); Ollama and LM Studio at their own addresses always take Chat
/// Completions, the API Scribe manages them through.
/// </summary>
public enum CustomApiStyle
{
    /// <summary>POST <c>/chat/completions</c>. Scribe sends no <c>store</c> field: a chat completion is stored only when asked.</summary>
    ChatCompletions = 0,

    /// <summary>POST <c>/responses</c>, which OpenAI stores unless told not to, so every request says <c>store: false</c>.</summary>
    Responses = 1,
}

/// <summary>
/// Reads a value this build does not know as Chat Completions, so a later build's API never makes the whole settings
/// document unreadable (as <see cref="Models.AccentSource"/> is read).
/// </summary>
internal sealed class CustomApiStyleJsonConverter : JsonConverter<CustomApiStyle>
{
    public override CustomApiStyle Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String &&
            string.Equals(reader.GetString(), nameof(CustomApiStyle.Responses), StringComparison.OrdinalIgnoreCase))
        {
            return CustomApiStyle.Responses;
        }

        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            using var _ = JsonDocument.ParseValue(ref reader);
        }

        return CustomApiStyle.ChatCompletions;
    }

    public override void Write(Utf8JsonWriter writer, CustomApiStyle value, JsonSerializerOptions options) =>
        writer.WriteStringValue(
            value == CustomApiStyle.Responses ? nameof(CustomApiStyle.Responses) : nameof(CustomApiStyle.ChatCompletions));
}
