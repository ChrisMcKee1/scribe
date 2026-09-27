using Scribe.Core.Libraries;

namespace Scribe.Core.Tests.Libraries.Csv;

/// <summary>
/// The export formula guard (plan 3.10, review finding R2): a value that a spreadsheet would run as a formula gains one
/// leading apostrophe on export, and an import of a file that declares the guard takes exactly one away, so every value
/// comes back, a literal leading apostrophe included.
/// </summary>
public sealed class LibraryFormulaGuardTests
{
    private const string Triggers = "=+-@\uFF1D\uFF0B\uFF0D\uFF20\t\r\n";

    [Fact]
    public void Every_value_round_trips_through_the_guard()
    {
        // Seeded property test. The alphabet is mostly apostrophes, spaces and triggers, so "zero or more apostrophes, then
        // spaces, then a trigger" is common, and so are its near misses.
        const string alphabet = "''''    =+-@\uFF1D\uFF0B\uFF0D\uFF20\t\r\nab#\u3000\u00A0";
        var random = new Random(20260925);
        var guarded = 0;
        for (var i = 0; i < 20_000; i++)
        {
            var value = CsvTestData.Random(random, alphabet, 8);
            var encoded = LibraryFormulaGuard.Encode(value);

            Assert.True(string.Equals(value, LibraryFormulaGuard.Decode(encoded), StringComparison.Ordinal), $"case {i}");
            Assert.True(encoded.Length == value.Length || encoded == "'" + value, $"case {i}: at most one apostrophe added");
            guarded += encoded.Length == value.Length ? 0 : 1;
        }

        Assert.InRange(guarded, 2_000, 18_000);
    }

    [Theory]
    [InlineData("=SUM(A1)", "'=SUM(A1)")]
    [InlineData("+1", "'+1")]
    [InlineData("-x", "'-x")]
    [InlineData("@user", "'@user")]
    [InlineData("\uFF1Dx", "'\uFF1Dx")]
    [InlineData("\uFF0Bx", "'\uFF0Bx")]
    [InlineData("\uFF0Dx", "'\uFF0Dx")]
    [InlineData("\uFF20x", "'\uFF20x")]
    [InlineData("\tx", "'\tx")]
    [InlineData("\rx", "'\rx")]
    [InlineData("\nx", "'\nx")]
    [InlineData("  =x", "'  =x")]
    [InlineData("'=x", "''=x")]
    [InlineData("''+x", "'''+x")]
    [InlineData("' =x", "'' =x")]
    [InlineData("a=b", "a=b")]
    [InlineData(" ' =x", " ' =x")]
    [InlineData("\u3000=x", "\u3000=x")]
    [InlineData("\u00A0=x", "\u00A0=x")]
    [InlineData("", "")]
    [InlineData("'", "'")]
    [InlineData("''", "''")]
    [InlineData("' '", "' '")]
    [InlineData("#tag", "#tag")]
    public void Only_a_value_that_starts_like_a_formula_gains_an_apostrophe(string value, string encoded)
    {
        Assert.Equal(encoded, LibraryFormulaGuard.Encode(value));
        Assert.Equal(value, LibraryFormulaGuard.Decode(encoded));
    }

    [Theory]
    [InlineData("''=x", "'=x")]
    [InlineData("'x", "'x")]
    [InlineData("'", "'")]
    [InlineData("' =x", " =x")]
    [InlineData("=x", "=x")]
    [InlineData("'''\t", "''\t")]
    public void Decoding_takes_exactly_one_apostrophe_and_only_before_a_trigger(string value, string decoded)
    {
        Assert.Equal(decoded, LibraryFormulaGuard.Decode(value));
    }

    [Fact]
    public void The_triggers_are_exactly_the_formula_starts_their_full_width_forms_and_the_control_characters()
    {
        for (var code = 0; code <= char.MaxValue; code++)
        {
            var ch = (char)code;
            Assert.True(Triggers.Contains(ch) == LibraryFormulaGuard.IsTrigger(ch), $"U+{code:X4}");
        }

        Assert.Equal(1, LibraryFormulaGuard.Version);
    }

    [Fact]
    public void The_round_1_guard_corrupts_a_literal_apostrophe_and_this_one_does_not()
    {
        // Astra's counterexample (R2): round 1 guarded only a value starting with a trigger and stripped an apostrophe from
        // any value starting with one before a trigger, so a literal "'=literal" lost its apostrophe on the way back.
        const string literal = "'=literal";

        Assert.NotEqual(literal, Round1Decode(Round1Encode(literal)));
        Assert.Equal(literal, LibraryFormulaGuard.Decode(LibraryFormulaGuard.Encode(literal)));
    }

    [Fact]
    public void A_literal_apostrophe_survives_in_a_file_where_only_another_row_needed_the_guard()
    {
        // Round 1 wrote its marker only when some row needed guarding; here "=SUM(A1)" does, and under round 1 that marker
        // then stripped the apostrophe from the other row. This guard marks every export and keeps both rows.
        TermValues[] rows = [new("sum it", "=SUM(A1)"), new("quote", "'=literal"), new("plain", "Plain")];
        var content = CsvTestData.Content("Guarded", "Custom", null, rows);

        var imported = CsvTestData.Codec.ReadImport(CsvTestData.Codec.WriteExport(content));

        Assert.Equal(rows, imported.Terms);
        Assert.Equal(1, imported.FormulaGuardVersion);
        Assert.Empty(imported.Errors);
        Assert.Equal(["=SUM(A1)", "=literal", "Plain"], rows.Select(r => Round1Decode(Round1Encode(r.Written))));
    }

    private static string Round1Encode(string value) =>
        value.TrimStart(' ').Length > 0 && Triggers.Contains(value.TrimStart(' ')[0]) ? "'" + value : value;

    private static string Round1Decode(string value) =>
        value.StartsWith('\'') && value[1..].TrimStart(' ').Length > 0 && Triggers.Contains(value[1..].TrimStart(' ')[0])
            ? value[1..]
            : value;
}
