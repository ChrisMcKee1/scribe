using System.Text;
using Scribe.Core.Models;

namespace Scribe.Core.PostProcessing;

/// <summary>
/// CSV round-tripping for the user dictionary, so vocabularies can be built in a spreadsheet and
/// shared between people instead of typed row by row in Settings. Format is RFC 4180-style:
/// <c>pattern,replacement,whole_word,enabled</c> with a header row, quoted fields when needed,
/// and <c>#</c> comment lines (which double as instructions in the downloadable template).
/// </summary>
public static class DictionaryCsv
{
    public const string Header = "pattern,replacement,whole_word,enabled";

    /// <summary>
    /// The starter file behind the "Get template" button. Comment lines explain the columns so the
    /// file is self-documenting when it opens in a spreadsheet or editor.
    /// </summary>
    public const string Template =
        """
        # Scribe dictionary template
        #
        # One row per substitution: what the transcriber usually hears, and what you
        # want written instead. Fill it in, then use Import in Scribe's Dictionary
        # settings. Lines starting with # are ignored.
        #
        # pattern     - the spoken word or phrase as it gets transcribed (required)
        # replacement - what to write instead (required)
        # whole_word  - true to match on word boundaries only, false for phrase
        #               replacement anywhere (optional, default true)
        # enabled     - false to keep the row but switch it off (optional, default true)
        #
        pattern,replacement,whole_word,enabled
        azure,Azure,true,true
        cube flow,Kubeflow,true,true
        kay eight ess,K8s,true,true
        """;

    /// <summary>Renders entries as a CSV document (header included), ready to save or share.</summary>
    public static string Export(IEnumerable<DictionaryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

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

    /// <summary>
    /// Parses a dictionary CSV. Never throws on content: rows that can't be understood are
    /// reported in <see cref="DictionaryCsvResult.Errors"/> with their line number while the good
    /// rows still import, so one typo in a shared 300-term file doesn't reject the other 299.
    /// </summary>
    public static DictionaryCsvResult Parse(string? csv)
    {
        var entries = new List<DictionaryEntry>();
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(csv))
        {
            return new DictionaryCsvResult(entries, errors);
        }

        foreach (var (fields, lineNumber) in ReadRecords(csv))
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

        return new DictionaryCsvResult(entries, errors);
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
    private static IEnumerable<(List<string> Fields, int LineNumber)> ReadRecords(string csv)
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

        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            yield return (fields, recordStartLine);
        }
    }
}

/// <summary>Outcome of parsing a dictionary CSV: the usable entries plus per-line errors.</summary>
public sealed record DictionaryCsvResult(
    IReadOnlyList<DictionaryEntry> Entries,
    IReadOnlyList<string> Errors);
