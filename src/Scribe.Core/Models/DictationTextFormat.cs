using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scribe.Core.Models;

/// <summary>
/// How a dictation's text reaches the target app. Every format keeps the characters the pipeline produced: none asks AI
/// cleanup for Markdown or generates syntax. <see cref="MarkdownWithFormatting"/> pastes that same Markdown text and adds an
/// HTML rendering of it beside it on the clipboard, for apps that read HTML but leave pasted Markdown as typed.
/// </summary>
/// <remarks>
/// Saved as its name. A build that does not know a name reads it as <see cref="Plain"/> (see <see cref="FormattingJson"/>),
/// so an older build given <see cref="MarkdownWithFormatting"/> pastes plain text rather than refusing the document.
/// </remarks>
public enum DictationTextFormat
{
    Plain,
    MarkdownSource,
    MarkdownWithFormatting,
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
        if (reader.TokenType == JsonTokenType.String && reader.GetString() is { } name)
        {
            if (string.Equals(name, nameof(DictationTextFormat.MarkdownSource), StringComparison.OrdinalIgnoreCase))
            {
                return DictationTextFormat.MarkdownSource;
            }

            if (string.Equals(name, nameof(DictationTextFormat.MarkdownWithFormatting), StringComparison.OrdinalIgnoreCase))
            {
                return DictationTextFormat.MarkdownWithFormatting;
            }
        }

        SkipValue(ref reader);
        return DictationTextFormat.Plain;
    }

    // Only a known token is written. An undefined value is saved as Plain, the value an unknown token reads back as.
    internal static void WriteFormat(Utf8JsonWriter writer, DictationTextFormat value) =>
        writer.WriteStringValue(value switch
        {
            DictationTextFormat.MarkdownSource => nameof(DictationTextFormat.MarkdownSource),
            DictationTextFormat.MarkdownWithFormatting => nameof(DictationTextFormat.MarkdownWithFormatting),
            _ => nameof(DictationTextFormat.Plain),
        });
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

// Only a JSON false turns it off; a loosely parsed string or number never decides it either way.
internal sealed class AppAwareFormattingJsonConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // On is the default (AppSettings.AppAwareFormattingEnabled), so only an explicit false turns it off.
        var enabled = reader.TokenType != JsonTokenType.False;
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
