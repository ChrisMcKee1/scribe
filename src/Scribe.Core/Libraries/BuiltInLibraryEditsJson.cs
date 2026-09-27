using System.Buffers;
using System.Text.Json;

namespace Scribe.Core.Libraries;

/// <summary>
/// The version 1 JSON form of a built-in's edits document (<c>LibrariesDir\edits\&lt;id&gt;.json</c>): UTF-8 without a
/// byte order mark, camelCase member names, indented, read by <see cref="Read"/> and written by <see cref="Write"/> for
/// <see cref="BuiltInLibraryOverlay"/>, which owns every rule about what a document means.
/// </summary>
/// <remarks>
/// <para>
/// Reading never throws on content. It classifies the bytes: a version 1 document whose every entry this build can use
/// is <see cref="LibraryFileState.Available"/>; a version above 1, or an entry whose intent this build does not know, is
/// <see cref="LibraryFileState.Newer"/>; anything else is <see cref="LibraryFileState.Unreadable"/>, and the document
/// comes back as nothing at all, never as the entries that could be read, because applying part of a document could
/// turn a term the user turned off back on (review finding R3).
/// </para>
/// <para>
/// A document that holds both an intent this build does not know and a broken entry is <see cref="LibraryFileState.Newer"/>:
/// a newer version may write its other entries by rules this build does not know either, and both states pause the
/// library and leave its file alone. A member repeated in any object is <see cref="LibraryFileState.Unreadable"/>,
/// because two readers could take different copies of it. A leading UTF-8 byte order mark is skipped (RFC 8259 lets a
/// parser ignore one), so a document someone saved from a text editor that adds one does not pause the library.
/// </para>
/// </remarks>
internal static class BuiltInLibraryEditsJson
{
    private const string VersionMember = "version";
    private const string LibraryMember = "library";
    private const string TermsMember = "terms";
    private const string KeyMember = "key";
    private const string IntentMember = "intent";
    private const string BaseMember = "base";
    private const string ValueMember = "value";
    private const string AcknowledgedMember = "acknowledged";
    private const string SpokenMember = "spoken";
    private const string WrittenMember = "written";
    private const string WholeWordMember = "wholeWord";
    private const string EnabledMember = "enabled";

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        AllowDuplicateProperties = false,
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
    };

    // The default encoder keeps the file pure ASCII (every other character escaped), so no tool can misread its encoding.
    // A fixed "\n" keeps the bytes, and so the content hash AI permission is bound to, the same on every machine; the
    // writer's default follows Environment.NewLine.
    private static readonly JsonWriterOptions WriteOptions = new()
    {
        Indented = true,
        NewLine = "\n",
    };

    private enum EntryState
    {
        Read,
        Broken,
        Newer,
    }

    private static ReadOnlySpan<byte> Utf8ByteOrderMark => [0xEF, 0xBB, 0xBF];

    /// <summary>Classifies and parses the document of <paramref name="libraryId"/>; never throws on content.</summary>
    internal static BuiltInEditsReadResult Read(string libraryId, ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(Utf8ByteOrderMark))
        {
            bytes = bytes[Utf8ByteOrderMark.Length..];
        }

        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray(), ReadOptions);
            return Read(libraryId, document.RootElement);
        }
        catch (JsonException)
        {
            return Unreadable(version: null);
        }
        catch (InvalidOperationException)
        {
            // Text System.Text.Json refuses to decode (an escaped unpaired surrogate, say), wherever it sits.
            return Unreadable(version: null);
        }
    }

    /// <summary>
    /// The version 1 document for <paramref name="edits"/>, which the overlay has already checked is one
    /// <see cref="Read"/> takes back unchanged: in particular every string is text, so the writer never has an unpaired
    /// surrogate to replace (round 2, A2).
    /// </summary>
    internal static byte[] Write(BuiltInLibraryEdits edits)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriteOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber(VersionMember, BuiltInLibraryEdits.CurrentVersion);
            writer.WriteString(LibraryMember, edits.LibraryId);
            writer.WriteStartArray(TermsMember);
            foreach (var term in edits.Terms)
            {
                writer.WriteStartObject();
                writer.WriteString(KeyMember, term.Key.Value);
                writer.WriteString(IntentMember, IntentName(term.Intent));
                WriteValues(writer, BaseMember, term.Base);
                WriteValues(writer, ValueMember, term.Value);
                WriteValues(writer, AcknowledgedMember, term.Acknowledged);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>The name an intent has in the document.</summary>
    internal static string IntentName(BuiltInTermIntent intent) => intent switch
    {
        BuiltInTermIntent.Edited => "edited",
        BuiltInTermIntent.Added => "added",
        BuiltInTermIntent.Pinned => "pinned",
        BuiltInTermIntent.Off => "off",
        _ => throw new ArgumentOutOfRangeException(nameof(intent), intent, "Not an intent a version 1 document can hold."),
    };

    private static BuiltInEditsReadResult Read(string libraryId, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(VersionMember, out var versionElement))
        {
            return Unreadable(version: null);
        }

        if (!TryReadVersion(versionElement, out var version, out var declared))
        {
            return Unreadable(version: null);
        }

        // The version decides first: a newer document may be shaped by rules this build has never seen.
        if (version > BuiltInLibraryEdits.CurrentVersion)
        {
            return new BuiltInEditsReadResult(LibraryFileState.Newer, null, declared);
        }

        if (version != BuiltInLibraryEdits.CurrentVersion)
        {
            return Unreadable(declared);
        }

        if (!root.TryGetProperty(LibraryMember, out var libraryElement) ||
            !TryGetString(libraryElement, out var library) ||
            !string.Equals(library, libraryId, StringComparison.OrdinalIgnoreCase) ||
            !root.TryGetProperty(TermsMember, out var termsElement) ||
            termsElement.ValueKind != JsonValueKind.Array)
        {
            return Unreadable(declared);
        }

        var terms = new List<BuiltInTermEdit>();
        var keys = new HashSet<LibraryTermKey>();
        var newer = false;
        var broken = false;
        foreach (var item in termsElement.EnumerateArray())
        {
            switch (ReadEntry(item, out var entry))
            {
                case EntryState.Newer:
                    newer = true;
                    break;
                case EntryState.Broken:
                    broken = true;
                    break;
                default:
                    if (keys.Add(entry!.Key))
                    {
                        terms.Add(entry);
                    }
                    else
                    {
                        broken = true;
                    }

                    break;
            }
        }

        if (newer)
        {
            return new BuiltInEditsReadResult(LibraryFileState.Newer, null, declared);
        }

        if (broken)
        {
            return Unreadable(declared);
        }

        IReadOnlyList<BuiltInTermEdit> read = [.. terms];
        return new BuiltInEditsReadResult(LibraryFileState.Available, new BuiltInLibraryEdits(library, read), declared);
    }

    // An integer literal. One too large for 64 bits is still a version above 1 (Newer), with no number to report.
    private static bool TryReadVersion(JsonElement element, out long version, out int? declared)
    {
        version = 0;
        declared = null;
        if (element.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        if (element.TryGetInt64(out version))
        {
            declared = version is >= int.MinValue and <= int.MaxValue ? (int)version : null;
            return true;
        }

        var raw = element.GetRawText();
        if (raw.Length > 0 && raw.All(char.IsAsciiDigit))
        {
            version = long.MaxValue;
            return true;
        }

        return false;
    }

    private static EntryState ReadEntry(JsonElement item, out BuiltInTermEdit? entry)
    {
        entry = null;
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty(IntentMember, out var intentElement) ||
            !TryGetString(intentElement, out var intentName))
        {
            return EntryState.Broken;
        }

        // An intent this build does not know means a newer version wrote the entry, by rules this build cannot check.
        if (!TryParseIntent(intentName, out var intent))
        {
            return EntryState.Newer;
        }

        if (!item.TryGetProperty(KeyMember, out var keyElement) || !TryGetString(keyElement, out var keyText))
        {
            return EntryState.Broken;
        }

        var key = LibraryTermKey.From(keyText);
        if (key.IsEmpty ||
            !TryReadOptionalValues(item, BaseMember, out var @base) ||
            !TryReadOptionalValues(item, ValueMember, out var value) ||
            !TryReadOptionalValues(item, AcknowledgedMember, out var acknowledged) ||
            !BuiltInLibraryOverlay.HasTheValuesItsIntentNeeds(intent, @base, value))
        {
            return EntryState.Broken;
        }

        entry = new BuiltInTermEdit(key, intent, @base, value, acknowledged);
        return EntryState.Read;
    }

    private static bool TryParseIntent(string name, out BuiltInTermIntent intent)
    {
        // Exactly as written: the names are part of the format, and the macOS port reads the same fixtures.
        switch (name)
        {
            case "edited":
                intent = BuiltInTermIntent.Edited;
                return true;
            case "added":
                intent = BuiltInTermIntent.Added;
                return true;
            case "pinned":
                intent = BuiltInTermIntent.Pinned;
                return true;
            case "off":
                intent = BuiltInTermIntent.Off;
                return true;
            default:
                intent = default;
                return false;
        }
    }

    // An absent member and an explicit null are the same: no values. Present values need all four members, typed.
    private static bool TryReadOptionalValues(JsonElement owner, string member, out TermValues? values)
    {
        values = null;
        if (!owner.TryGetProperty(member, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(SpokenMember, out var spokenElement) || !TryGetString(spokenElement, out var spoken) ||
            !element.TryGetProperty(WrittenMember, out var writtenElement) || !TryGetString(writtenElement, out var written) ||
            !element.TryGetProperty(WholeWordMember, out var wholeWordElement) || !TryGetBoolean(wholeWordElement, out var wholeWord) ||
            !element.TryGetProperty(EnabledMember, out var enabledElement) || !TryGetBoolean(enabledElement, out var enabled))
        {
            return false;
        }

        values = new TermValues(spoken, written, wholeWord, enabled);
        return true;
    }

    private static bool TryGetString(JsonElement element, out string value)
    {
        value = string.Empty;
        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        try
        {
            value = element.GetString() ?? string.Empty;
            return true;
        }
        catch (InvalidOperationException)
        {
            // An escaped unpaired surrogate: valid JSON syntax, but not text a value can hold.
            return false;
        }
    }

    private static bool TryGetBoolean(JsonElement element, out bool value)
    {
        value = element.ValueKind == JsonValueKind.True;
        return element.ValueKind is JsonValueKind.True or JsonValueKind.False;
    }

    private static void WriteValues(Utf8JsonWriter writer, string member, TermValues? values)
    {
        if (values is null)
        {
            return;
        }

        writer.WriteStartObject(member);
        writer.WriteString(SpokenMember, values.Spoken);
        writer.WriteString(WrittenMember, values.Written);
        writer.WriteBoolean(WholeWordMember, values.WholeWord);
        writer.WriteBoolean(EnabledMember, values.Enabled);
        writer.WriteEndObject();
    }

    private static BuiltInEditsReadResult Unreadable(int? version) => new(LibraryFileState.Unreadable, null, version);
}
