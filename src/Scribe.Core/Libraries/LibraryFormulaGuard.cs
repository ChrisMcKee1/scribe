namespace Scribe.Core.Libraries;

/// <summary>
/// The reversible formula guard every export applies to every value (plan 3.10, review finding R2; format 6.8). A
/// spreadsheet runs a cell that starts with <c>=</c>, <c>+</c>, <c>-</c> or <c>@</c> (or their full-width forms, or a
/// tab, carriage return or line feed) as a formula; a leading apostrophe makes it text. The guard is injective, so an
/// import of a file that declares it (<c># formula-guard: 1</c>) gets every value back exactly, a literal leading
/// apostrophe included.
/// </summary>
/// <remarks>
/// <para>
/// A value matching "zero or more apostrophes, then zero or more U+0020 spaces, then a trigger" gains one leading
/// apostrophe; a value matching "one or more apostrophes, then zero or more spaces, then a trigger" loses exactly one.
/// Every value the first rule changes matches the second, and every value it leaves alone matches neither (the second
/// pattern is a part of the first), so decoding undoes encoding for every string.
/// </para>
/// <para>
/// Round 1 guarded only a value that started with a trigger and stripped an apostrophe from any value that started with
/// one before a trigger, so a literal <c>'=x</c> came back as <c>=x</c> (Astra's counterexample). Counting the leading
/// apostrophes into the first rule is what closes it.
/// </para>
/// <para>
/// <c>#</c> is deliberately not a trigger (decision 21). A spoken form starting with <c>#</c> is quoted in an export, and
/// a spreadsheet that saves the file again drops those quotes, which turns the row into a comment; guarding <c>#</c> would
/// save that row, but at the price of an apostrophe on every exported hashtag-like written form and a change to the
/// version 1 format the macOS port shares, for spoken forms the transcriber practically never produces, and 0.4.3 skips
/// such rows anyway. The import preview's counts are where a user sees it.
/// </para>
/// </remarks>
internal static class LibraryFormulaGuard
{
    /// <summary>The version an export declares in its <c># formula-guard:</c> line.</summary>
    internal const int Version = 1;

    /// <summary>One leading apostrophe on a value matching <c>^'* *(trigger)</c>; every other value unchanged.</summary>
    internal static string Encode(string value) => StartsLikeFormula(value, 0) ? "'" + value : value;

    /// <summary>Exactly one apostrophe off a value matching <c>^'+ *(trigger)</c>; every other value unchanged.</summary>
    internal static string Decode(string value) =>
        value.Length > 1 && value[0] == '\'' && StartsLikeFormula(value, 1) ? value[1..] : value;

    /// <summary>Whether <paramref name="ch"/> makes a spreadsheet read the cell it starts as a formula.</summary>
    internal static bool IsTrigger(char ch) =>
        ch is '=' or '+' or '-' or '@' or '\uFF1D' or '\uFF0B' or '\uFF0D' or '\uFF20' or '\t' or '\r' or '\n';

    // From start: any apostrophes, then any U+0020 spaces, then a trigger.
    private static bool StartsLikeFormula(string value, int start)
    {
        var i = start;
        while (i < value.Length && value[i] == '\'')
        {
            i++;
        }

        while (i < value.Length && value[i] == ' ')
        {
            i++;
        }

        return i < value.Length && IsTrigger(value[i]);
    }
}
