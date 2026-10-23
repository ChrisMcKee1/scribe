using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scribe.Core.Transcription;

/// <summary>
/// Transcript output style for <c>MAI-Transcribe-2</c> (<c>modelOptions.transcribeStyle</c>).
/// <see cref="Clean"/> removes filler words and formats common speech patterns;
/// <see cref="Verbatim"/> preserves speech as spoken.
/// </summary>
public enum MaiTranscribeStyle
{
    Clean = 0,
    Verbatim = 1,
}

/// <summary>
/// Tolerant JSON converter for <see cref="MaiTranscribeStyle"/>: unknown or malformed values read as
/// <see cref="MaiTranscribeStyle.Clean"/> so a cosmetic setting never triggers <c>LastLoadFailed</c>.
/// </summary>
public sealed class MaiTranscribeStyleJsonConverter : JsonConverter<MaiTranscribeStyle>
{
    public override MaiTranscribeStyle Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString()?.Trim();
            if (string.Equals(value, nameof(MaiTranscribeStyle.Verbatim), StringComparison.OrdinalIgnoreCase))
            {
                return MaiTranscribeStyle.Verbatim;
            }

            return MaiTranscribeStyle.Clean;
        }

        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number))
        {
            return number == (int)MaiTranscribeStyle.Verbatim
                ? MaiTranscribeStyle.Verbatim
                : MaiTranscribeStyle.Clean;
        }

        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            reader.Skip();
        }

        return MaiTranscribeStyle.Clean;
    }

    public override void Write(Utf8JsonWriter writer, MaiTranscribeStyle value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value == MaiTranscribeStyle.Verbatim
            ? nameof(MaiTranscribeStyle.Verbatim)
            : nameof(MaiTranscribeStyle.Clean));
}
