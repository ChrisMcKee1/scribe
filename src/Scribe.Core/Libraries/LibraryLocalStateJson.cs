using System.Text.Json;

namespace Scribe.Core.Libraries;

/// <summary>
/// The <see cref="LibrarySettingKeys.State"/> row, version 1 (W1b contracts 6.2): System.Text.Json, camelCase names,
/// compact. Required members are <c>version</c> (an integer), <c>aiPermissionsLost</c> (a boolean), <c>enabled</c> and
/// <c>legacyProjection</c> (arrays of strings); every other member is optional and reads as empty when missing or null.
/// </summary>
/// <remarks>
/// Reading never throws and fails closed: anything this version cannot read exactly (not a JSON object, a required member
/// missing or of the wrong type, a known optional member of the wrong type, a known member given twice, an id both
/// permitted and denied, an accepted hash that is not 64 lowercase hexadecimal digits) is
/// <see cref="LocalStateHealth.Unreadable"/>, because reading such a member as empty could re-grant a permission the user
/// took away. A version above 1 is <see cref="LocalStateHealth.Newer"/> and is not read further. Unknown members are
/// ignored, so a later version may add an optional member whose loss to this reader changes nothing without a new
/// version. Ids are kept as stored and compared without case.
/// </remarks>
internal static class LibraryLocalStateJson
{
    public const int CurrentVersion = 1;

    private const string Version = "version";
    private const string Enabled = "enabled";
    private const string LegacyProjection = "legacyProjection";
    private const string Ai = "ai";
    private const string AiOn = "on";
    private const string AiOff = "off";
    private const string AiPermissionsLost = "aiPermissionsLost";
    private const string LegacyMarkers = "legacyMarkers";
    private const string MarkerLibrary = "library";
    private const string MarkerKey = "key";
    private const string Accepted = "accepted";
    private const string UpgradeNotice = "upgradeNotice";

    private static readonly HashSet<string> KnownMembers = new(StringComparer.Ordinal)
    {
        Version, Enabled, LegacyProjection, Ai, AiPermissionsLost, LegacyMarkers, Accepted, UpgradeNotice,
    };

    /// <summary>The members of a version 1 row.</summary>
    internal sealed record Row(
        IReadOnlyList<string> Enabled,
        IReadOnlyList<string> LegacyProjection,
        IReadOnlyList<KeyValuePair<string, bool>> AiPermissions,
        bool AiPermissionsLost,
        IReadOnlyList<LegacyMarker> LegacyMarkers,
        IReadOnlyList<KeyValuePair<string, LibraryContentHash>> Accepted,
        IReadOnlyList<string> UpgradeNotice);

    /// <summary>The row's health, and its members when it is <see cref="LocalStateHealth.Ok"/>.</summary>
    public static (LocalStateHealth Health, Row? Row) Read(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        try
        {
            using var document = JsonDocument.Parse(value);
            return Read(document.RootElement);
        }
        catch (JsonException)
        {
            return (LocalStateHealth.Unreadable, null);
        }
    }

    /// <summary>The row for <paramref name="row"/>, members in a fixed order and every optional member written.</summary>
    public static string Write(Row row)
    {
        ArgumentNullException.ThrowIfNull(row);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber(Version, CurrentVersion);
            WriteStrings(writer, Enabled, row.Enabled);
            WriteStrings(writer, LegacyProjection, row.LegacyProjection);
            writer.WriteStartObject(Ai);
            WriteStrings(writer, AiOn, row.AiPermissions.Where(pair => pair.Value).Select(pair => pair.Key));
            WriteStrings(writer, AiOff, row.AiPermissions.Where(pair => !pair.Value).Select(pair => pair.Key));
            writer.WriteEndObject();
            writer.WriteBoolean(AiPermissionsLost, row.AiPermissionsLost);
            writer.WriteStartArray(LegacyMarkers);
            foreach (var marker in row.LegacyMarkers)
            {
                writer.WriteStartObject();
                writer.WriteString(MarkerLibrary, marker.LibraryId);
                writer.WriteString(MarkerKey, marker.Key.Value);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartObject(Accepted);
            foreach (var (id, hash) in row.Accepted)
            {
                writer.WriteString(id, hash.Value);
            }

            writer.WriteEndObject();
            WriteStrings(writer, UpgradeNotice, row.UpgradeNotice);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Whether <paramref name="value"/> is a content hash as stored: 64 lowercase hexadecimal digits.</summary>
    public static bool IsHash(string? value) =>
        value is { Length: 64 } && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static (LocalStateHealth Health, Row? Row) Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return (LocalStateHealth.Unreadable, null);
        }

        // A known member given twice could hide a contradiction behind whichever copy a reader happens to take.
        var members = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (KnownMembers.Contains(property.Name) && !members.TryAdd(property.Name, property.Value))
            {
                return (LocalStateHealth.Unreadable, null);
            }
        }

        if (!members.TryGetValue(Version, out var version) || version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt64(out var number))
        {
            return (LocalStateHealth.Unreadable, null);
        }

        if (number > CurrentVersion)
        {
            return (LocalStateHealth.Newer, null);
        }

        if (number < CurrentVersion ||
            !members.TryGetValue(AiPermissionsLost, out var lost) || lost.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !TryStrings(members.GetValueOrDefault(Enabled), required: true, out var enabled) ||
            !TryStrings(members.GetValueOrDefault(LegacyProjection), required: true, out var projection) ||
            !TryPermissions(members.GetValueOrDefault(Ai), out var permissions) ||
            !TryMarkers(members.GetValueOrDefault(LegacyMarkers), out var markers) ||
            !TryAccepted(members.GetValueOrDefault(Accepted), out var accepted) ||
            !TryStrings(members.GetValueOrDefault(UpgradeNotice), required: false, out var notice))
        {
            return (LocalStateHealth.Unreadable, null);
        }

        return (LocalStateHealth.Ok, new Row(
            enabled, projection, permissions, lost.GetBoolean(), markers, accepted, notice));
    }

    private static bool IsMissing(JsonElement element) =>
        element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null;

    private static bool TryStrings(JsonElement element, bool required, out IReadOnlyList<string> values)
    {
        values = [];
        if (IsMissing(element))
        {
            return !required;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var list = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            list.Add(item.GetString()!);
        }

        values = list;
        return true;
    }

    private static bool TryPermissions(JsonElement element, out IReadOnlyList<KeyValuePair<string, bool>> permissions)
    {
        permissions = [];
        if (IsMissing(element))
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        JsonElement on = default, off = default;
        foreach (var property in element.EnumerateObject())
        {
            // Inside "ai" too, a repeated list could hide a contradiction.
            if ((property.NameEquals(AiOn) && on.ValueKind != JsonValueKind.Undefined) ||
                (property.NameEquals(AiOff) && off.ValueKind != JsonValueKind.Undefined))
            {
                return false;
            }

            if (property.NameEquals(AiOn))
            {
                on = property.Value;
            }
            else if (property.NameEquals(AiOff))
            {
                off = property.Value;
            }
        }

        if (!TryStrings(on, required: false, out var permitted) || !TryStrings(off, required: false, out var denied))
        {
            return false;
        }

        // An id both permitted and denied cannot be read either way without guessing (Astra on decision 7).
        var permittedIds = new HashSet<string>(permitted.Select(id => id.Trim()), StringComparer.OrdinalIgnoreCase);
        if (denied.Any(id => permittedIds.Contains(id.Trim()) && !string.IsNullOrWhiteSpace(id)))
        {
            return false;
        }

        permissions = [.. permitted.Select(id => new KeyValuePair<string, bool>(id, true)),
            .. denied.Select(id => new KeyValuePair<string, bool>(id, false))];
        return true;
    }

    private static bool TryMarkers(JsonElement element, out IReadOnlyList<LegacyMarker> markers)
    {
        markers = [];
        if (IsMissing(element))
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var list = new List<LegacyMarker>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !TryString(item, MarkerLibrary, out var library) ||
                !TryString(item, MarkerKey, out var key))
            {
                return false;
            }

            // A marker with an empty library or key is dropped (6.2); LibraryLocalState.Create drops it.
            list.Add(new LegacyMarker(library ?? string.Empty, LibraryTermKey.From(key)));
        }

        markers = list;
        return true;
    }

    // A string member, or a missing or null one (read as empty); any other type is not readable.
    private static bool TryString(JsonElement parent, string name, out string? value)
    {
        value = null;
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString();
        return true;
    }

    private static bool TryAccepted(JsonElement element, out IReadOnlyList<KeyValuePair<string, LibraryContentHash>> accepted)
    {
        accepted = [];
        if (IsMissing(element))
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var list = new List<KeyValuePair<string, LibraryContentHash>>();
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String || !IsHash(property.Value.GetString()))
            {
                return false;
            }

            list.Add(new(property.Name, new LibraryContentHash(property.Value.GetString()!)));
        }

        accepted = list;
        return true;
    }

    private static void WriteStrings(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }
}
