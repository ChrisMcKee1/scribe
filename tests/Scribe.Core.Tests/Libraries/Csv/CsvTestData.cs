using System.Text;
using Scribe.Core.Libraries;
using Scribe.Core.Models;

namespace Scribe.Core.Tests.Libraries.Csv;

/// <summary>
/// What the CSV codec's tests share: a codec whose ANSI fallback is Windows-1252 whatever the machine uses, content
/// builders, seeded value generators that lean on the characters the CSV, header and formula guard rules care about,
/// 0.4.3's own decoding of a file, a spreadsheet's save of a CSV, and the paths of the shared fixtures.
/// </summary>
internal static class CsvTestData
{
    /// <summary>Characters that matter to the record reader, the quoting rules, the header and comment rules and the guard.</summary>
    public const string Adversarial = "ab ,\"'#=+-@\t\r\n\u00A0\u00E9\uFF1D\uFF0B\uFF0D\uFF20;:";

    /// <summary>A codec whose ANSI fallback is Windows-1252, so a test reads the same bytes the same way on any machine.</summary>
    public static LibraryCsvCodec Codec { get; } = new(1252);

    public static LibraryContent Content(
        string name, string category, string? description, IEnumerable<TermValues> rows, string? basedOn = null) =>
        new("custom-test", BuiltIn: false, name, category, description, rows.Select(LibraryRow.Custom).ToList(), basedOn);

    public static byte[] Utf8(string text) => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);

    public static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    /// <summary>A random string of up to <paramref name="maxLength"/> characters from <paramref name="alphabet"/>.</summary>
    public static string Random(Random random, string alphabet, int maxLength, int minLength = 0) =>
        new(Enumerable.Range(0, random.Next(minLength, maxLength + 1)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());

    /// <summary>A spoken form an import or the editor can hold: never blank, anything else goes.</summary>
    public static string Spoken(Random random, string alphabet)
    {
        return random.Next(12) switch
        {
            0 => "pattern",
            1 => "#tag",
            2 => " Pattern ",
            3 => "=SUM(A1)",
            4 => "'=literal",
            _ => NonBlank(random, alphabet),
        };
    }

    public static string NonBlank(Random random, string alphabet)
    {
        var value = Random(random, alphabet, 10, 1);
        return string.IsNullOrWhiteSpace(value) ? "x" + value : value;
    }

    public static TermValues Term(Random random, string alphabet) =>
        new(Spoken(random, alphabet), Random(random, alphabet, 10), random.Next(2) == 0, random.Next(3) != 0);

    /// <summary>
    /// What 0.4.3 read from a managed file: <c>File.ReadAllText</c> of its bytes, the call 0.4.3's loader made. A real
    /// file, because the decoding is part of what must not change.
    /// </summary>
    public static string ReadAllTextOf(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), "scribe-csv-" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            File.WriteAllBytes(path, bytes);
            return File.ReadAllText(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>0.4.3's row error message, as the line and kind the codec reports.</summary>
    public static (int Line, LibraryCsvRowErrorKind Kind) LegacyError(string message)
    {
        var colon = message.IndexOf(':', StringComparison.Ordinal);
        var line = int.Parse(message["Line ".Length..colon], System.Globalization.CultureInfo.InvariantCulture);
        var text = message[(colon + 1)..];
        var kind =
            text.Contains("expected at least a pattern", StringComparison.Ordinal) ? LibraryCsvRowErrorKind.MissingFields :
            text.Contains("pattern (spoken form) is empty", StringComparison.Ordinal) ? LibraryCsvRowErrorKind.EmptySpoken :
            text.Contains("whole_word should be", StringComparison.Ordinal) ? LibraryCsvRowErrorKind.InvalidWholeWord :
            text.Contains("enabled should be", StringComparison.Ordinal) ? LibraryCsvRowErrorKind.InvalidEnabled :
            text.Contains("closing quote", StringComparison.Ordinal) ? LibraryCsvRowErrorKind.UnclosedQuote :
            throw new InvalidOperationException("Not a 0.4.3 CSV message: " + message);
        return (line, kind);
    }

    /// <summary>Asserts that a managed read gave exactly what 0.4.3 read from the same file, metadata, rows and errors.</summary>
    public static void AssertReadsAs043(string text043, LibraryCsvDocument actual, string because)
    {
        var expected = Legacy043LibraryCsv.Parse(text043);

        Assert.True(expected.Name == actual.Name, $"{because}: name");
        Assert.True(expected.Category == actual.Category, $"{because}: category");
        Assert.True(expected.Description == actual.Description, $"{because}: description");
        Assert.True(
            expected.Entries.Select(TermValues.FromEntry).SequenceEqual(actual.Terms),
            $"{because}: rows ({expected.Entries.Count} in 0.4.3, {actual.Terms.Count} here)");
        Assert.True(
            expected.Errors.Select(LegacyError).SequenceEqual(actual.Errors.Select(e => (e.Line, e.Kind))),
            $"{because}: errors ({string.Join("; ", expected.Errors)} in 0.4.3, " +
            $"{string.Join("; ", actual.Errors.Select(e => $"{e.Line} {e.Kind}"))} here)");
    }

    /// <summary>
    /// A spreadsheet's open and save of a CSV, as Excel and LibreOffice do it by default: each record is read with RFC 4180
    /// (a double quote opens a field only at its start), every record is padded to <paramref name="columns"/> fields, and a
    /// field is written quoted only when it holds a comma, a double quote or a line break, so quotes the file had for any
    /// other reason are gone. Line breaks become CRLF. <paramref name="quoteAll"/> is LibreOffice's "Quote all text cells".
    /// </summary>
    public static string SpreadsheetSave(string csv, int columns = 4, bool quoteAll = false)
    {
        var output = new StringBuilder();
        foreach (var record in SpreadsheetRecords(csv))
        {
            var cells = record.ToList();
            while (cells.Count < columns)
            {
                cells.Add(string.Empty);
            }

            output.Append(string.Join(",", cells.Select(cell =>
                (quoteAll && cell.Length > 0) || cell.AsSpan().IndexOfAny(",\"\r\n") >= 0
                    ? "\"" + cell.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
                    : cell)));
            output.Append("\r\n");
        }

        return output.ToString();
    }

    private static IEnumerable<List<string>> SpreadsheetRecords(string csv)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var i = 0;
        var atFieldStart = true;
        while (i < csv.Length)
        {
            var ch = csv[i];
            if (atFieldStart && ch == '"')
            {
                i++;
                while (i < csv.Length)
                {
                    if (csv[i] == '"')
                    {
                        if (i + 1 < csv.Length && csv[i + 1] == '"')
                        {
                            field.Append('"');
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    field.Append(csv[i++]);
                }

                atFieldStart = false;
                continue;
            }

            atFieldStart = false;
            if (ch == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
                atFieldStart = true;
            }
            else if (ch == '\r' || ch == '\n')
            {
                if (ch == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n')
                {
                    i++;
                }

                fields.Add(field.ToString());
                field.Clear();
                yield return fields;
                fields = new List<string>();
                atFieldStart = true;
            }
            else
            {
                field.Append(ch);
            }

            i++;
        }

        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            yield return fields;
        }
    }

    public static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return root.FullName;
    }

    /// <summary>A path under the shared fixtures, <c>tests/fixtures/libraries/csv</c>.</summary>
    public static string FixturePath(params string[] parts) =>
        Path.Combine([RepositoryRoot(), "tests", "fixtures", "libraries", "csv", .. parts]);

    /// <summary>The built-in library CSVs exactly as they ship, as bytes.</summary>
    public static IEnumerable<(string Resource, byte[] Bytes)> BuiltInCsvs()
    {
        var assembly = typeof(PostProcessing.BuiltInDictionaryLibraries).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames()
                     .Where(name => name.Contains(".PostProcessing.Libraries.", StringComparison.Ordinal) &&
                                    name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                     .Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            yield return (resource, copy.ToArray());
        }
    }

    public static DictionaryEntry Entry(TermValues values) => values.ToEntry();

    /// <summary>Rows as escaped text, for a failure message.</summary>
    public static string Show(IEnumerable<TermValues> terms) =>
        string.Join(" | ", terms.Select(t => $"[{Escape(t.Spoken)}] -> [{Escape(t.Written)}] {(t.WholeWord ? "w" : "-")}{(t.Enabled ? "e" : "-")}"));

    public static string Escape(string value) =>
        string.Concat(value.Select(c => c is < ' ' or > '~' ? $"\\u{(int)c:X4}" : c.ToString()));
}
