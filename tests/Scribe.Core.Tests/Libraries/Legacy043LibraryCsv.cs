using System.Text;
using Scribe.Core.Models;

namespace Scribe.Core.Tests.Libraries;

/// <summary>
/// 0.4.3's library CSV reader and writer, copied verbatim from v0.4.3's <c>DictionaryCsv</c> and
/// <c>DictionaryLibraryCsv</c> (only the type names changed), as the oracle for what an older build reads from a file
/// this version writes: the metadata rules (<see cref="Scribe.Core.Libraries.LibraryMetadata"/>) are checked against it
/// here, and the CSV stream's managed reader (acceptance X-2) must equal it for every file without the format marker.
/// Never edit the logic: it stands for code already in the field.
/// </summary>
internal static class Legacy043LibraryCsv
{
    private const string Header = "pattern,replacement,whole_word,enabled";
    private const string NameKey = "name";
    private const string CategoryKey = "category";
    private const string DescriptionKey = "description";

    internal sealed record File(
        string? Name,
        string? Category,
        string? Description,
        IReadOnlyList<DictionaryEntry> Entries,
        IReadOnlyList<string> Errors);

    // DictionaryLibraryCsv.Parse.
    internal static File Parse(string? csv)
    {
        string? name = null, category = null, description = null;

        if (!string.IsNullOrEmpty(csv))
        {
            using var reader = new StringReader(csv);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith('#'))
                {
                    TryReadMeta(trimmed, NameKey, ref name);
                    TryReadMeta(trimmed, CategoryKey, ref category);
                    TryReadMeta(trimmed, DescriptionKey, ref description);
                    continue;
                }

                if (trimmed.Length == 0)
                {
                    continue; // tolerate blank lines before the header
                }

                break; // reached the header/data; metadata only lives at the top
            }
        }

        var (entries, errors) = ParseRows(csv);
        return new File(NullIfBlank(name), NullIfBlank(category), NullIfBlank(description), entries, errors);
    }

    // DictionaryLibraryCsv.Export.
    internal static string Export(string name, string category, string? description, IEnumerable<DictionaryEntry> entries)
    {
        var sb = new StringBuilder();
        sb.Append("# name: ").AppendLine(SingleLine(name));
        sb.Append("# category: ").AppendLine(SingleLine(category));
        if (!string.IsNullOrWhiteSpace(description))
        {
            sb.Append("# description: ").AppendLine(SingleLine(description));
        }

        sb.Append(ExportRows(entries));
        return sb.ToString();
    }

    // DictionaryCsv.Export.
    private static string ExportRows(IEnumerable<DictionaryEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Header);
        foreach (var entry in entries)
        {
            sb.Append(Quote(entry.Pattern)).Append(',')
              .Append(Quote(entry.Replacement)).Append(',')
              .Append(entry.WholeWord ? "true" : "false").Append(',')
              .Append(entry.Enabled ? "true" : "false")
              .AppendLine();
        }

        return sb.ToString();
    }

    // DictionaryCsv.Parse.
    private static (List<DictionaryEntry> Entries, List<string> Errors) ParseRows(string? csv)
    {
        var entries = new List<DictionaryEntry>();
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(csv))
        {
            return (entries, errors);
        }

        foreach (var (fields, lineNumber) in ReadRecords(csv, errors))
        {
            // Skip blank lines, comment lines, and the header row wherever it appears.
            if (fields.Count == 0 || (fields.Count == 1 && string.IsNullOrWhiteSpace(fields[0])))
            {
                continue;
            }

            if (fields[0].TrimStart().StartsWith('#'))
            {
                continue;
            }

            if (string.Equals(fields[0].Trim(), "pattern", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (fields.Count < 2)
            {
                errors.Add($"Line {lineNumber}: expected at least a pattern and a replacement.");
                continue;
            }

            var pattern = fields[0].Trim();
            var replacement = fields[1].Trim();
            if (pattern.Length == 0)
            {
                errors.Add($"Line {lineNumber}: the pattern (spoken form) is empty.");
                continue;
            }

            if (!TryParseFlag(fields.Count > 2 ? fields[2] : null, defaultValue: true, out var wholeWord))
            {
                errors.Add($"Line {lineNumber}: whole_word should be true or false, not \"{fields[2].Trim()}\".");
                continue;
            }

            if (!TryParseFlag(fields.Count > 3 ? fields[3] : null, defaultValue: true, out var enabled))
            {
                errors.Add($"Line {lineNumber}: enabled should be true or false, not \"{fields[3].Trim()}\".");
                continue;
            }

            entries.Add(new DictionaryEntry(0, pattern, replacement, wholeWord, enabled));
        }

        return (entries, errors);
    }

    private static bool TryParseFlag(string? field, bool defaultValue, out bool value)
    {
        value = defaultValue;
        if (string.IsNullOrWhiteSpace(field))
        {
            return true; // optional column
        }

        switch (field.Trim().ToLowerInvariant())
        {
            case "true" or "yes" or "1":
                value = true;
                return true;
            case "false" or "no" or "0":
                value = false;
                return true;
            default:
                return false;
        }
    }

    private static string Quote(string value)
    {
        if (value.IndexOfAny([',', '"', '\r', '\n']) < 0)
        {
            return value;
        }

        return '"' + value.Replace("\"", "\"\"") + '"';
    }

    // Character-level RFC 4180 reader: quoted fields may contain commas, doubled quotes, and even
    // line breaks (spreadsheets emit all three), so a naive Split on newline/comma is not enough.
    private static IEnumerable<(List<string> Fields, int LineNumber)> ReadRecords(
        string csv, List<string> errors)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var line = 1;
        var recordStartLine = 1;

        for (var i = 0; i < csv.Length; i++)
        {
            var ch = csv[i];

            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < csv.Length && csv[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    if (ch == '\n')
                    {
                        line++;
                    }

                    field.Append(ch);
                }

                continue;
            }

            switch (ch)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break; // handled by the following \n (or ignored for a lone \r)
                case '\n':
                    fields.Add(field.ToString());
                    field.Clear();
                    yield return (fields, recordStartLine);
                    fields = new List<string>();
                    line++;
                    recordStartLine = line;
                    break;
                default:
                    field.Append(ch);
                    break;
            }
        }

        if (inQuotes)
        {
            errors.Add($"Line {recordStartLine}: quoted field is missing its closing quote.");
            yield break;
        }

        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            yield return (fields, recordStartLine);
        }
    }

    // Reads "# key: value" (case-insensitive key, tolerant of spacing) into value; first line wins.
    private static void TryReadMeta(string commentLine, string key, ref string? value)
    {
        if (value is not null)
        {
            return;
        }

        var body = commentLine.TrimStart('#').TrimStart();
        if (body.Length <= key.Length ||
            !body.StartsWith(key, StringComparison.OrdinalIgnoreCase) ||
            body[key.Length] != ':')
        {
            return;
        }

        value = body[(key.Length + 1)..].Trim();
    }

    // Metadata is single-line: flatten any control characters so a value can't spill into extra
    // header lines or break the comment convention when re-imported.
    private static string SingleLine(string value) =>
        new(value.Select(c => char.IsControl(c) ? ' ' : c).ToArray());

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
