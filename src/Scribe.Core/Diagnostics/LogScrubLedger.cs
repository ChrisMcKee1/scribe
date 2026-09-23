using System.Globalization;
using System.Text;

namespace Scribe.Core.Diagnostics;

/// <summary>
/// Remembers which past-day log files the historical scrub has already found clean or rewritten, so an
/// unchanged file is not read again at every start. One line per file: its name, its length and its
/// last-write time. Never any content. A file that changes in any of those, or a ledger written under
/// another <see cref="HistoricalLogRedaction.RulesVersion"/>, is simply examined again.
/// <para>
/// Every member is non-throwing. A missing, unreadable or malformed ledger reads as empty, which costs one
/// extra read of each retained file and nothing else.
/// </para>
/// </summary>
internal sealed class LogScrubLedger
{
    /// <summary>The ledger's name in the log folder. It never matches <see cref="ScribeLogFiles.SearchPattern"/>.</summary>
    public const string FileName = "redaction-ledger.txt";

    private const string HeaderPrefix = "scribe-redaction-ledger rules=";

    private readonly Dictionary<string, (long Length, long LastWriteUtcTicks)> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    private bool _dirty;

    /// <summary>Files currently recorded, for tests.</summary>
    internal int Count => _entries.Count;

    public static LogScrubLedger Load(string path, int rulesVersion)
    {
        var ledger = new LogScrubLedger();
        try
        {
            if (!File.Exists(path))
            {
                return ledger;
            }

            using var reader = new StreamReader(
                new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete),
                Encoding.UTF8);
            if (reader.ReadLine() != HeaderPrefix + rulesVersion.ToString(CultureInfo.InvariantCulture))
            {
                // Written under other rules: everything it vouches for must be looked at again.
                ledger._dirty = true;
                return ledger;
            }

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var parts = line.Split(' ');
                if (parts.Length == 3 &&
                    ScribeLogFiles.TryParseDay(parts[0], out _) &&
                    long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var length) &&
                    long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks))
                {
                    ledger._entries[parts[0]] = (length, ticks);
                }
            }
        }
        catch (Exception)
        {
            ledger._entries.Clear();
            ledger._dirty = true;
        }

        return ledger;
    }

    /// <summary>True when the file was recorded with exactly this length and last-write time.</summary>
    public bool IsVerified(string fileName, long length, long lastWriteUtcTicks) =>
        _entries.TryGetValue(fileName, out var entry) &&
        entry.Length == length &&
        entry.LastWriteUtcTicks == lastWriteUtcTicks;

    /// <summary>Records a file found clean, or just rewritten, as it now stands on disk.</summary>
    public void Record(string fileName, long length, long lastWriteUtcTicks)
    {
        if (!IsVerified(fileName, length, lastWriteUtcTicks))
        {
            _entries[fileName] = (length, lastWriteUtcTicks);
            _dirty = true;
        }
    }

    /// <summary>Forgets files that no longer exist, so the ledger stays as small as the folder.</summary>
    public void RetainOnly(IEnumerable<string> fileNames)
    {
        var keep = new HashSet<string>(fileNames, StringComparer.OrdinalIgnoreCase);
        foreach (var name in _entries.Keys.Where(name => !keep.Contains(name)).ToList())
        {
            _entries.Remove(name);
            _dirty = true;
        }
    }

    /// <summary>
    /// Writes the ledger if anything changed. Through a scratch file and a move, so a crash midway leaves the
    /// previous ledger or none at all, never a truncated one that vouches for the wrong files.
    /// </summary>
    public void Save(string path, int rulesVersion)
    {
        if (!_dirty)
        {
            return;
        }

        var scratch = path + ".tmp";
        try
        {
            var builder = new StringBuilder();
            builder.Append(HeaderPrefix).Append(rulesVersion.ToString(CultureInfo.InvariantCulture)).Append('\n');
            foreach (var (name, entry) in _entries.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
            {
                builder.Append(name).Append(' ')
                    .Append(entry.Length.ToString(CultureInfo.InvariantCulture)).Append(' ')
                    .Append(entry.LastWriteUtcTicks.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }

            File.WriteAllText(scratch, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(scratch, path, overwrite: true);
            _dirty = false;
        }
        catch (Exception)
        {
            try
            {
                File.Delete(scratch);
            }
            catch (Exception)
            {
                // Overwritten by the next save.
            }
        }
    }
}
