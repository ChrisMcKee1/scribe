using System.Collections.Frozen;
using System.Globalization;
using System.Text;

namespace Scribe.Core.Diagnostics;

/// <summary>
/// Decides which dictation span tags may leave the process, and how they are shown, for both the file
/// log bridge and an optional OTLP exporter.
/// <para>
/// An allowlist rather than a blocklist, because the blocklist approach already failed once: the
/// <c>ai_skip_reason</c> tag carried free text that, for a custom OpenAI-compatible endpoint, named the
/// endpoint's host, and the bridge rendered every tag value it was handed. Now each known tag has an
/// expected value shape. A value that does not fit its shape is shown as <see cref="OmittedValue"/>, so
/// the tag's presence still reads as a signal, and a tag that is not on the list is counted and left
/// out entirely, key and value, however harmless it may look.
/// </para>
/// <para>
/// A new tag in <see cref="ScribeTelemetry"/> must be added here too; a test fails until it is.
/// </para>
/// </summary>
public static class TraceTagPolicy
{
    /// <summary>Shown in place of a known tag's value when the value does not have the expected shape.</summary>
    public const string OmittedValue = "(omitted)";

    /// <summary>How many tags an exported span lost to the allowlist.</summary>
    public const string OmittedTagCountTag = "scribe.omitted_tags";

    private const string ScribeTagPrefix = "scribe.";
    private const string OmittedTagCountDisplayKey = "omitted_tags";
    private const int MaxCodeLength = 48;
    private const int MaxAppNameLength = 64;

    private enum ValueShape
    {
        /// <summary>A numeric primitive, shown in the invariant culture.</summary>
        Number,

        /// <summary>A boolean.</summary>
        Flag,

        /// <summary>
        /// A fixed identifier such as an outcome or an enum name: ASCII letters, digits, hyphen and
        /// underscore only. No spaces and no dots, so neither a sentence nor a host name can pass.
        /// </summary>
        Code,

        /// <summary>A process name, which PRIVACY.md lists among what diagnostics may record.</summary>
        AppName,
    }

    private static readonly FrozenDictionary<string, ValueShape> Allowed = new Dictionary<string, ValueShape>
    {
        [ScribeTelemetry.TagOutcome] = ValueShape.Code,
        [ScribeTelemetry.TagCaptureSeconds] = ValueShape.Number,
        [ScribeTelemetry.TagVadEnabled] = ValueShape.Flag,
        [ScribeTelemetry.TagVadKept] = ValueShape.Flag,
        [ScribeTelemetry.TagRecognizerReady] = ValueShape.Flag,
        [ScribeTelemetry.TagDecodeChars] = ValueShape.Number,
        [ScribeTelemetry.TagRealTimeFactor] = ValueShape.Number,
        [ScribeTelemetry.TagAiCleanup] = ValueShape.Flag,
        [ScribeTelemetry.TagAiChanged] = ValueShape.Flag,
        [ScribeTelemetry.TagAiOutcome] = ValueShape.Code,

        // Historically a sentence (and the one that leaked a host). The controller now sets a fixed
        // code (AiSkipReason), which is shown as is; a sentence would still read "(omitted)".
        [ScribeTelemetry.TagAiSkipReason] = ValueShape.Code,
        [ScribeTelemetry.TagFinalChars] = ValueShape.Number,
        [ScribeTelemetry.TagTargetApp] = ValueShape.AppName,
        [ScribeTelemetry.TagInjectMethod] = ValueShape.Code,
        [ScribeTelemetry.TagInjectChars] = ValueShape.Number,
        [ScribeTelemetry.TagInjectSent] = ValueShape.Number,
        [ScribeTelemetry.TagInjectTotal] = ValueShape.Number,
        [ScribeTelemetry.TagInjectComplete] = ValueShape.Flag,
        [ScribeTelemetry.TagInjectFallback] = ValueShape.Flag,
        [ScribeTelemetry.TagPasteDelivery] = ValueShape.Code,
        [ScribeTelemetry.TagClipboardRestore] = ValueShape.Code,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>True when <paramref name="key"/> is on the allowlist.</summary>
    public static bool IsAllowed(string key) => key is not null && Allowed.ContainsKey(key);

    /// <summary>
    /// The text to show for a tag. False for a tag that is not on the allowlist, which must then not be
    /// shown at all. For an allowed tag whose value has the wrong shape the text is <see cref="OmittedValue"/>.
    /// </summary>
    public static bool TryFormatValue(string key, object? value, out string formatted) =>
        Classify(key, value, out formatted) != TagVerdict.NotAllowed;

    /// <summary>
    /// One span as the file log shows it: the operation, the allowed tags as <c>key=value</c> without the
    /// <c>scribe.</c> prefix, a count of omitted tags when there were any, and the duration.
    /// </summary>
    public static string FormatSpan(
        string operationName, IEnumerable<KeyValuePair<string, object?>> tags, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(tags);

        var builder = new StringBuilder(operationName);
        var omitted = 0;
        foreach (var tag in tags)
        {
            if (!TryFormatValue(tag.Key, tag.Value, out var formatted))
            {
                omitted++;
                continue;
            }

            builder.Append(' ').Append(DisplayKey(tag.Key)).Append('=').Append(formatted);
        }

        if (omitted > 0)
        {
            builder.Append(' ').Append(OmittedTagCountDisplayKey).Append('=')
                .Append(omitted.ToString(CultureInfo.InvariantCulture));
        }

        builder.Append(" (")
            .Append(((long)duration.TotalMilliseconds).ToString(CultureInfo.InvariantCulture))
            .Append("ms)");
        return builder.ToString();
    }

    /// <summary>
    /// The suffix for an error span's status description, kept on one line so line-oriented tools
    /// still read the log correctly. Empty when there is no description.
    /// </summary>
    public static string FormatStatusDetail(string? description) =>
        string.IsNullOrWhiteSpace(description) ? string.Empty : ": " + description.ReplaceLineEndings(" ");

    /// <summary>
    /// The tag changes that make a finished span safe to export: each tag not on the allowlist is
    /// removed (a null value), each allowed tag with a wrongly shaped value becomes
    /// <see cref="OmittedValue"/>, and <see cref="OmittedTagCountTag"/> records how many were removed.
    /// Materialized, so the caller can apply it while no enumeration of the tags is in progress.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, object?>> PlanExportScrub(
        IEnumerable<KeyValuePair<string, object?>> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);

        List<KeyValuePair<string, object?>>? changes = null;
        var removed = 0;
        foreach (var tag in tags)
        {
            if (string.Equals(tag.Key, OmittedTagCountTag, StringComparison.Ordinal))
            {
                continue;
            }

            switch (Classify(tag.Key, tag.Value, out _))
            {
                case TagVerdict.NotAllowed:
                    (changes ??= []).Add(new KeyValuePair<string, object?>(tag.Key, null));
                    removed++;
                    break;
                case TagVerdict.Omitted:
                    (changes ??= []).Add(new KeyValuePair<string, object?>(tag.Key, OmittedValue));
                    break;
            }
        }

        if (changes is null)
        {
            return Array.Empty<KeyValuePair<string, object?>>();
        }

        if (removed > 0)
        {
            changes.Add(new KeyValuePair<string, object?>(OmittedTagCountTag, removed));
        }

        return changes;
    }

    private enum TagVerdict
    {
        NotAllowed,
        Shown,
        Omitted,
    }

    private static TagVerdict Classify(string key, object? value, out string formatted)
    {
        if (key is null || !Allowed.TryGetValue(key, out var shape))
        {
            formatted = string.Empty;
            return TagVerdict.NotAllowed;
        }

        if (Render(shape, value) is { } rendered)
        {
            formatted = rendered;
            return TagVerdict.Shown;
        }

        formatted = OmittedValue;
        return TagVerdict.Omitted;
    }

    private static string DisplayKey(string key) =>
        key.StartsWith(ScribeTagPrefix, StringComparison.Ordinal) ? key[ScribeTagPrefix.Length..] : key;

    private static string? Render(ValueShape shape, object? value) => shape switch
    {
        ValueShape.Number => value is sbyte or byte or short or ushort or int or uint or long or ulong
            or float or double or decimal
            ? Convert.ToString(value, CultureInfo.InvariantCulture)
            : null,
        ValueShape.Flag => value is bool flag ? (flag ? "True" : "False") : null,
        ValueShape.Code => (value is Enum ? value.ToString() : value as string) is { } code && IsCode(code) ? code : null,
        ValueShape.AppName => value is string name && IsAppName(name) ? name : null,
        _ => null,
    };

    private static bool IsCode(string value)
    {
        if (value.Length is 0 or > MaxCodeLength || !char.IsAsciiLetterOrDigit(value[0]))
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAppName(string value)
    {
        if (value.Length is 0 or > MaxAppNameLength || value[0] == ' ' || value[^1] == ' ')
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsLetterOrDigit(c) && c is not (' ' or '.' or '_' or '-' or '(' or ')' or '+'))
            {
                return false;
            }
        }

        return true;
    }
}
