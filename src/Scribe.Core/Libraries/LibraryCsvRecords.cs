using System.Buffers;
using System.Text;

namespace Scribe.Core.Libraries;

/// <summary>One field of a CSV record: its text, and whether a double quote was used anywhere in it.</summary>
/// <param name="Text">The field as 0.4.3's reader returned it: quotes removed, a doubled quote inside quotes kept as one.</param>
/// <param name="Quoted">Whether quoting was used in the field, which the strict rules read as "take the text exactly as it is".</param>
internal readonly record struct CsvField(string Text, bool Quoted);

/// <summary>One CSV record as the shared reader returns it.</summary>
internal sealed class CsvRecord
{
    internal CsvRecord(int line, IReadOnlyList<CsvField> fields, bool rawComment)
    {
        Line = line;
        Fields = fields;
        RawComment = rawComment;
    }

    /// <summary>The 1-based line the record starts on, counted as 0.4.3 counted it (line feeds only).</summary>
    public int Line { get; }

    /// <summary>The fields; a record always has at least one.</summary>
    public IReadOnlyList<CsvField> Fields { get; }

    /// <summary>
    /// Whether the record's raw text, after white space and before any double quote, starts with <c>#</c>: a comment on
    /// an unquoted raw line, the only comment the strict rules recognize. <c>"#tag"</c> is not one; <c>#tag</c> and
    /// <c>  # note, "quoted"</c> are.
    /// </summary>
    public bool RawComment { get; }
}

/// <summary>
/// The one CSV record reader and field writer behind every library and dictionary CSV (pattern P-2): the library codec's
/// managed and exchanged files, and the personal dictionary's <see cref="PostProcessing.DictionaryCsv"/> and
/// <see cref="PostProcessing.DictionaryLibraryCsv"/>.
/// </summary>
/// <remarks>
/// <para>
/// The reader is 0.4.3's (<c>DictionaryCsv.ReadRecords</c>) character for character, because a managed file without the
/// format marker must read exactly as 0.4.3 read it, and dictation must not change on upgrade: a double quote anywhere
/// outside a quoted field opens one, a doubled quote inside one is a literal quote, a carriage return outside quotes is
/// dropped, a line feed ends the record, and a quoted field still open at the end of the text is reported and its
/// record dropped. What it adds is only what the strict rules need and 0.4.3 threw away: whether each field used
/// quoting (<see cref="CsvField.Quoted"/>) and whether the record is a raw comment line (<see cref="CsvRecord.RawComment"/>).
/// </para>
/// <para>
/// The writer quotes a field when it holds a comma, a double quote, a carriage return or a line feed (0.4.3's rule), when
/// it begins or ends with white space (which a reader that trims unquoted fields would otherwise lose), and, for the
/// first field of a row, when it starts with <c>#</c> after optional white space or reads as the header
/// (<c>pattern</c>, ignoring case), so the strict reader takes it as data (plan 3.10, Astra N3).
/// </para>
/// </remarks>
internal static class LibraryCsvRecords
{
    /// <summary>The column header every library and dictionary CSV writes.</summary>
    internal const string Header = "pattern,replacement,whole_word,enabled";

    private static readonly SearchValues<char> Special = SearchValues.Create(",\"\r\n");

    /// <summary>
    /// The records of <paramref name="text"/>, lazily. A quoted field left open at the end of the text adds
    /// <see cref="LibraryCsvRowErrorKind.UnclosedQuote"/> to <paramref name="errors"/> when the enumeration reaches the end,
    /// so read the errors after enumerating, as 0.4.3's parser did.
    /// </summary>
    internal static IEnumerable<CsvRecord> Read(string text, List<LibraryCsvRowError> errors)
    {
        var fields = new List<CsvField>();
        var field = new StringBuilder();
        var inQuotes = false;
        var fieldQuoted = false;
        var firstFieldQuoteAt = -1;
        var rawComment = false;
        var line = 1;
        var recordStartLine = 1;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];

            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
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
                    if (!fieldQuoted)
                    {
                        fieldQuoted = true;
                        if (fields.Count == 0)
                        {
                            firstFieldQuoteAt = field.Length;
                        }
                    }

                    break;
                case ',':
                    rawComment |= EndField(fields, field, fieldQuoted, firstFieldQuoteAt);
                    fieldQuoted = false;
                    break;
                case '\r':
                    break; // handled by the following \n (or ignored for a lone \r), as 0.4.3 did
                case '\n':
                    rawComment |= EndField(fields, field, fieldQuoted, firstFieldQuoteAt);
                    yield return new CsvRecord(recordStartLine, fields, rawComment);
                    fields = new List<CsvField>();
                    fieldQuoted = false;
                    firstFieldQuoteAt = -1;
                    rawComment = false;
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
            errors.Add(new LibraryCsvRowError(recordStartLine, LibraryCsvRowErrorKind.UnclosedQuote));
            yield break;
        }

        if (field.Length > 0 || fields.Count > 0)
        {
            rawComment |= EndField(fields, field, fieldQuoted, firstFieldQuoteAt);
            yield return new CsvRecord(recordStartLine, fields, rawComment);
        }
    }

    /// <summary>Appends one row: the spoken and written forms through <see cref="AppendField"/>, then the two flags.</summary>
    internal static void AppendRow(StringBuilder text, string? spoken, string? written, bool wholeWord, bool enabled)
    {
        AppendField(text, spoken, firstField: true);
        text.Append(',');
        AppendField(text, written, firstField: false);
        text.Append(',').Append(wholeWord ? "true" : "false").Append(',').Append(enabled ? "true" : "false");
    }

    /// <summary>Appends <paramref name="value"/> as one field, quoted when <see cref="NeedsQuotes"/> says so; null is empty.</summary>
    internal static void AppendField(StringBuilder text, string? value, bool firstField)
    {
        value ??= string.Empty;
        if (NeedsQuotes(value, firstField))
        {
            AppendQuoted(text, value);
        }
        else
        {
            text.Append(value);
        }
    }

    /// <summary>
    /// Appends an export's metadata line (<c># name: Team terms</c>) as one field, quoted only by 0.4.3's rule (a comma,
    /// a double quote, a carriage return or a line feed), so a spreadsheet that pads or re-quotes the line cannot change
    /// the value and an unquoted line still reads as a comment to every reader.
    /// </summary>
    internal static void AppendMetadataField(StringBuilder text, string line)
    {
        if (line.AsSpan().IndexOfAny(Special) >= 0)
        {
            AppendQuoted(text, line);
        }
        else
        {
            text.Append(line);
        }
    }

    /// <summary>Whether the writer quotes <paramref name="value"/>, as the first field of a row or as a later one.</summary>
    internal static bool NeedsQuotes(string value, bool firstField)
    {
        if (value.Length == 0)
        {
            return false;
        }

        if (value.AsSpan().IndexOfAny(Special) >= 0 || char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]))
        {
            return true;
        }

        // Past the check above the value has no white space at its edges, so "after optional white space" is already true.
        return firstField && (value[0] == '#' || string.Equals(value, "pattern", StringComparison.OrdinalIgnoreCase));
    }

    private static void AppendQuoted(StringBuilder text, string value)
    {
        text.Append('"');
        foreach (var ch in value)
        {
            if (ch == '"')
            {
                text.Append('"');
            }

            text.Append(ch);
        }

        text.Append('"');
    }

    // Ends the current field and says, for the record's first field, whether it makes the record a raw comment line.
    private static bool EndField(List<CsvField> fields, StringBuilder field, bool quoted, int firstFieldQuoteAt)
    {
        var text = field.ToString();
        field.Clear();
        fields.Add(new CsvField(text, quoted));
        if (fields.Count != 1)
        {
            return false;
        }

        var unquotedPrefix = firstFieldQuoteAt < 0 ? text.AsSpan() : text.AsSpan(0, firstFieldQuoteAt);
        var trimmed = unquotedPrefix.TrimStart();
        return trimmed.Length > 0 && trimmed[0] == '#';
    }
}
