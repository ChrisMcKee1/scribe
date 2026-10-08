using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scribe.Core.Models;

/// <summary>A preference for literal text, not a request to generate or convert Markdown.</summary>
public enum DictationTextFormat
{
    Plain,
    MarkdownSource,
}

internal static class FormattingJson
{
    internal static void SkipValue(ref Utf8JsonReader reader)
    {
        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            reader.Skip();
        }
    }

    internal static DictationTextFormat ReadFormat(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.String &&
            string.Equals(reader.GetString(), nameof(DictationTextFormat.MarkdownSource), StringComparison.OrdinalIgnoreCase))
        {
            return DictationTextFormat.MarkdownSource;
        }

        SkipValue(ref reader);
        return DictationTextFormat.Plain;
    }

    // Only a known token is written. An undefined value is saved as Plain, the value an unknown token reads back as.
    internal static void WriteFormat(Utf8JsonWriter writer, DictationTextFormat value) =>
        writer.WriteStringValue(value == DictationTextFormat.MarkdownSource
            ? nameof(DictationTextFormat.MarkdownSource)
            : nameof(DictationTextFormat.Plain));
}

internal sealed class DictationTextFormatJsonConverter : JsonConverter<DictationTextFormat>
{
    public override DictationTextFormat Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        FormattingJson.ReadFormat(ref reader);

    public override void Write(Utf8JsonWriter writer, DictationTextFormat value, JsonSerializerOptions options) =>
        FormattingJson.WriteFormat(writer, value);
}

internal sealed class ProfileTextFormatJsonConverter : JsonConverter<DictationTextFormat?>
{
    public override bool HandleNull => true;

    public override DictationTextFormat? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : FormattingJson.ReadFormat(ref reader);

    public override void Write(Utf8JsonWriter writer, DictationTextFormat? value, JsonSerializerOptions options)
    {
        if (value is { } format)
        {
            FormattingJson.WriteFormat(writer, format);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}

// Opt-in requires a JSON true, never a loosely parsed string or number.
internal sealed class AppAwareFormattingJsonConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var enabled = reader.TokenType == JsonTokenType.True;
        FormattingJson.SkipValue(ref reader);
        return enabled;
    }

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) => writer.WriteBooleanValue(value);
}

internal sealed class ProfileInjectionMethodJsonConverter : JsonConverter<InjectionMethod?>
{
    public override bool HandleNull => true;

    public override InjectionMethod? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            if (string.Equals(value, nameof(InjectionMethod.UnicodeType), StringComparison.OrdinalIgnoreCase))
            {
                return InjectionMethod.UnicodeType;
            }

            if (string.Equals(value, nameof(InjectionMethod.ClipboardPaste), StringComparison.OrdinalIgnoreCase))
            {
                return InjectionMethod.ClipboardPaste;
            }
        }

        FormattingJson.SkipValue(ref reader);
        return null;
    }

    public override void Write(Utf8JsonWriter writer, InjectionMethod? value, JsonSerializerOptions options)
    {
        if (value is { } method && Enum.IsDefined(method))
        {
            writer.WriteStringValue(method.ToString());
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}

internal sealed class ProfileShiftEnterJsonConverter : JsonConverter<bool?>
{
    public override bool HandleNull => true;

    public override bool? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType is JsonTokenType.True or JsonTokenType.False)
        {
            return reader.GetBoolean();
        }

        FormattingJson.SkipValue(ref reader);
        return null;
    }

    public override void Write(Utf8JsonWriter writer, bool? value, JsonSerializerOptions options)
    {
        if (value is { } enabled)
        {
            writer.WriteBooleanValue(enabled);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
