using System.Globalization;

namespace Scribe.Core.Libraries;

/// <summary>What a journal file name names.</summary>
internal enum LibraryJournalNameKind
{
    /// <summary><c>journal\g&lt;G&gt;-&lt;id&gt;.manifest.json</c>: a pending manifest.</summary>
    Manifest,

    /// <summary><c>journal\g&lt;G&gt;-&lt;id&gt;.manifest.json.tmp</c>: a manifest being written, renamed into place when whole.</summary>
    ManifestTemp,

    /// <summary><c>journal\g&lt;G&gt;-&lt;id&gt;.set-aside.&lt;stamp&gt;.json</c>: a quarantined manifest.</summary>
    SetAsideManifest,

    /// <summary><c>journal\g&lt;G&gt;-&lt;id&gt;</c>: a manifest's folder of redo images.</summary>
    RedoFolder,

    /// <summary><c>~g&lt;G&gt;-&lt;id&gt;-&lt;n&gt;.scribe-staged</c> beside a target: an operation's install copy.</summary>
    InstallCopy,

    /// <summary><c>~g&lt;G&gt;-&lt;id&gt;-&lt;n&gt;[-&lt;k&gt;].scribe-backup</c> beside a target: one of an operation's backups.</summary>
    Backup,
}

/// <summary>
/// A journal file name parsed whole: the generation and manifest id it belongs to, and for an install copy or a backup the
/// operation number and the backup's place in its series (1 for the unsuffixed name).
/// </summary>
internal readonly record struct LibraryJournalName(
    LibraryJournalNameKind Kind,
    long Generation,
    string ManifestId,
    int Operation = 0,
    int BackupIndex = 0,
    DateTimeOffset Stamp = default)
{
    /// <summary>Whether this file belongs to the manifest of <paramref name="generation"/> and <paramref name="manifestId"/>.</summary>
    public bool BelongsTo(long generation, string manifestId) =>
        Generation == generation && string.Equals(ManifestId, manifestId, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether this file belongs to that manifest's operation <paramref name="operation"/>, compared as integers.</summary>
    public bool BelongsTo(long generation, string manifestId, int operation) =>
        BelongsTo(generation, manifestId) && Operation == operation;
}

/// <summary>
/// The one grammar of every journal name (contract 6.6.1, review finding A19), and the only way the journal decides
/// whether a file is its own and which manifest and operation it belongs to. A name is recognized only when it matches
/// whole: G a decimal integer of at least 1 without leading zeros, the manifest id exactly 32 hexadecimal digits, an
/// operation number a decimal integer without leading zeros (0 allowed), a backup's retry index a decimal integer of at
/// least 2 without leading zeros, and a stamp <c>yyyyMMdd'T'HHmmss'Z'</c>.
/// </summary>
/// <remarks>
/// <para>
/// Letters compare without case the way Windows compares these names, and only ASCII letters fold: a name that spells a
/// letter of the grammar with another character never parses, so the journal never mistakes another app's file for one
/// of its own. A glob or a prefix never decides ownership (<c>~g43-&lt;id&gt;-1*.scribe-backup</c> also matches operation
/// 10's and 100's backups); a pattern handed to <see cref="ILibraryFileSystem.EnumerateFiles"/> only narrows a listing.
/// A name that does not parse whole is not a journal file, and nothing in the journal moves or deletes it.
/// </para>
/// </remarks>
internal static class LibraryJournalNames
{
    public const string ManifestSuffix = ".manifest.json";
    public const string ManifestTempSuffix = ".manifest.json.tmp";
    public const string StagedSuffix = ".scribe-staged";
    public const string BackupSuffix = ".scribe-backup";
    public const string RedoSuffix = ".redo";
    public const string StampFormat = "yyyyMMdd'T'HHmmss'Z'";
    public const int ManifestIdLength = 32;

    private const string SetAsideInfix = ".set-aside.";
    private const string JsonSuffix = ".json";
    private const int StampLength = 16;

    public static string Manifest(long generation, string manifestId) =>
        string.Create(CultureInfo.InvariantCulture, $"g{generation}-{manifestId}{ManifestSuffix}");

    public static string ManifestTemp(long generation, string manifestId) =>
        string.Create(CultureInfo.InvariantCulture, $"g{generation}-{manifestId}{ManifestTempSuffix}");

    public static string SetAside(long generation, string manifestId, DateTimeOffset stamp) =>
        string.Create(CultureInfo.InvariantCulture, $"g{generation}-{manifestId}{SetAsideInfix}{FormatStamp(stamp)}{JsonSuffix}");

    public static string RedoFolder(long generation, string manifestId) =>
        string.Create(CultureInfo.InvariantCulture, $"g{generation}-{manifestId}");

    public static string RedoImage(int operation) =>
        string.Create(CultureInfo.InvariantCulture, $"{operation}{RedoSuffix}");

    public static string InstallCopy(long generation, string manifestId, int operation) =>
        string.Create(CultureInfo.InvariantCulture, $"~g{generation}-{manifestId}-{operation}{StagedSuffix}");

    /// <summary>The <paramref name="index"/>-th name of an operation's backup series: unsuffixed for 1, then <c>-2</c>, <c>-3</c>.</summary>
    public static string Backup(long generation, string manifestId, int operation, int index)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(index, 1);
        return index == 1
            ? string.Create(CultureInfo.InvariantCulture, $"~g{generation}-{manifestId}-{operation}{BackupSuffix}")
            : string.Create(CultureInfo.InvariantCulture, $"~g{generation}-{manifestId}-{operation}-{index}{BackupSuffix}");
    }

    /// <summary>A UTC time as the journal stamps names with it.</summary>
    public static string FormatStamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString(StampFormat, CultureInfo.InvariantCulture);

    /// <summary>A stamp exactly as <see cref="FormatStamp"/> writes it (the two letters in either case), as UTC.</summary>
    public static bool TryParseStamp(ReadOnlySpan<char> text, out DateTimeOffset value)
    {
        value = default;
        if (text.Length != StampLength || !IsLetter(text[8], 'T') || !IsLetter(text[15], 'Z'))
        {
            return false;
        }

        for (var i = 0; i < StampLength; i++)
        {
            if (i is not (8 or 15) && !char.IsAsciiDigit(text[i]))
            {
                return false;
            }
        }

        Span<char> canonical = stackalloc char[StampLength];
        text.CopyTo(canonical);
        canonical[8] = 'T';
        canonical[15] = 'Z';
        if (!DateTime.TryParseExact(
                canonical, StampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return false;
        }

        value = new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Utc));
        return true;
    }

    /// <summary>A manifest id: exactly 32 hexadecimal digits.</summary>
    public static bool IsManifestId(string? value)
    {
        if (value is not { Length: ManifestIdLength })
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A redo image's name, <c>&lt;n&gt;.redo</c>, parsed whole.</summary>
    public static bool TryParseRedoImage(string? name, out int operation)
    {
        operation = 0;
        if (name is null)
        {
            return false;
        }

        var s = name.AsSpan();
        var i = 0;
        if (!ReadNumber(s, ref i, allowZero: true, out var n) || n > int.MaxValue ||
            !ReadLiteral(s, ref i, RedoSuffix) || i != s.Length)
        {
            return false;
        }

        operation = (int)n;
        return true;
    }

    /// <summary>Any journal file or folder name, parsed whole; false for everything else.</summary>
    public static bool TryParse(string? name, out LibraryJournalName result)
    {
        result = default;
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        var s = name.AsSpan();
        var i = 0;
        var besideTarget = s[0] == '~';
        if (besideTarget)
        {
            i = 1;
        }

        if (!ReadLiteral(s, ref i, "g") || !ReadNumber(s, ref i, allowZero: false, out var generation) ||
            !ReadLiteral(s, ref i, "-") || !ReadManifestId(s, ref i, out var manifestId))
        {
            return false;
        }

        if (besideTarget)
        {
            return TryParseBesideTarget(s, i, generation, manifestId, out result);
        }

        var rest = s[i..];
        if (rest.Length == 0)
        {
            result = new LibraryJournalName(LibraryJournalNameKind.RedoFolder, generation, manifestId);
            return true;
        }

        var j = i;
        if (ReadLiteral(s, ref j, ManifestTempSuffix) && j == s.Length)
        {
            result = new LibraryJournalName(LibraryJournalNameKind.ManifestTemp, generation, manifestId);
            return true;
        }

        j = i;
        if (ReadLiteral(s, ref j, ManifestSuffix) && j == s.Length)
        {
            result = new LibraryJournalName(LibraryJournalNameKind.Manifest, generation, manifestId);
            return true;
        }

        j = i;
        if (ReadLiteral(s, ref j, SetAsideInfix) && s.Length - j == StampLength + JsonSuffix.Length &&
            TryParseStamp(s.Slice(j, StampLength), out var stamp))
        {
            j += StampLength;
            if (ReadLiteral(s, ref j, JsonSuffix) && j == s.Length)
            {
                result = new LibraryJournalName(LibraryJournalNameKind.SetAsideManifest, generation, manifestId, Stamp: stamp);
                return true;
            }
        }

        return false;
    }

    private static bool TryParseBesideTarget(
        ReadOnlySpan<char> s, int i, long generation, string manifestId, out LibraryJournalName result)
    {
        result = default;
        if (!ReadLiteral(s, ref i, "-") || !ReadNumber(s, ref i, allowZero: true, out var operation) || operation > int.MaxValue)
        {
            return false;
        }

        var j = i;
        if (ReadLiteral(s, ref j, StagedSuffix) && j == s.Length)
        {
            result = new LibraryJournalName(LibraryJournalNameKind.InstallCopy, generation, manifestId, (int)operation);
            return true;
        }

        j = i;
        if (ReadLiteral(s, ref j, BackupSuffix) && j == s.Length)
        {
            result = new LibraryJournalName(LibraryJournalNameKind.Backup, generation, manifestId, (int)operation, BackupIndex: 1);
            return true;
        }

        j = i;
        if (ReadLiteral(s, ref j, "-") && ReadNumber(s, ref j, allowZero: false, out var index) && index is >= 2 and <= int.MaxValue &&
            ReadLiteral(s, ref j, BackupSuffix) && j == s.Length)
        {
            result = new LibraryJournalName(LibraryJournalNameKind.Backup, generation, manifestId, (int)operation, (int)index);
            return true;
        }

        return false;
    }

    // ASCII letters fold, nothing else does: see the remarks on the class.
    private static bool ReadLiteral(ReadOnlySpan<char> s, ref int i, string literal)
    {
        if (s.Length - i < literal.Length)
        {
            return false;
        }

        for (var k = 0; k < literal.Length; k++)
        {
            var expected = literal[k];
            var actual = s[i + k];
            if (actual != expected && !(char.IsAsciiLetter(expected) && IsLetter(actual, expected)))
            {
                return false;
            }
        }

        i += literal.Length;
        return true;
    }

    private static bool IsLetter(char actual, char letter) =>
        char.IsAsciiLetter(actual) && (actual | 0x20) == (letter | 0x20);

    // Decimal digits without a leading zero; "0" alone only when allowZero. Stops at the first non-digit.
    private static bool ReadNumber(ReadOnlySpan<char> s, ref int i, bool allowZero, out long value)
    {
        value = 0;
        var start = i;
        while (i < s.Length && char.IsAsciiDigit(s[i]))
        {
            if (value > (long.MaxValue - 9) / 10)
            {
                return false;
            }

            value = (value * 10) + (s[i] - '0');
            i++;
        }

        var digits = i - start;
        if (digits == 0 || (digits > 1 && s[start] == '0'))
        {
            return false;
        }

        return allowZero || value >= 1;
    }

    private static bool ReadManifestId(ReadOnlySpan<char> s, ref int i, out string manifestId)
    {
        manifestId = string.Empty;
        if (s.Length - i < ManifestIdLength)
        {
            return false;
        }

        var candidate = s.Slice(i, ManifestIdLength);
        foreach (var c in candidate)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        manifestId = candidate.ToString();
        i += ManifestIdLength;
        return true;
    }
}
