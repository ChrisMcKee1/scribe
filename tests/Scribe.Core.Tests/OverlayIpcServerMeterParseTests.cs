using System.Globalization;
using System.Text.RegularExpressions;

namespace Scribe.Core.Tests;

/// <summary>
/// The overlay's pipe server (Scribe.Overlay's <c>OverlayIpcServer</c>, not referenced from here) reads a METER line on the
/// line's own characters before its general path, which cuts a verb and an argument substring from every line with an
/// argument. These tests pin that path's statements from source, and show that those statements take exactly the lines the
/// general path reads as METER, with the same level: over a generated corpus under several cultures, and for the casing
/// rule, over every UTF-16 code unit.
/// </summary>
public sealed class OverlayIpcServerMeterParseTests
{
    [Fact]
    public void Dispatch_reads_METER_on_the_line_before_its_general_path()
    {
        var source = Source();
        var dispatch = Body(source, "private void Dispatch(string line)");

        InOrder(
            dispatch,
            "if (TryDispatchMeter(line))",
            "return;",
            "var trimmed = line.Trim();",
            "var sp = trimmed.IndexOf(' ');",
            "var cmd = sp < 0 ? trimmed : trimmed[..sp];",
            "var arg = sp < 0 ? string.Empty : trimmed[(sp + 1)..];",
            "switch (cmd.ToUpperInvariant())");
        Assert.Matches(new Regex(@"case ""METER"":\s*DispatchMeter\(arg\);\s*break;"), dispatch);

        InOrder(
            Body(source, "private bool TryDispatchMeter(string line)"),
            "var trimmed = line.AsSpan().Trim();",
            "var sp = trimmed.IndexOf(' ');",
            "var cmd = sp < 0 ? trimmed : trimmed[..sp];",
            "if (!cmd.Equals(\"METER\", StringComparison.OrdinalIgnoreCase))",
            "return false;",
            "DispatchMeter(sp < 0 ? ReadOnlySpan<char>.Empty : trimmed[(sp + 1)..]);",
            "return true;");
        InOrder(
            Body(source, "private void DispatchMeter(ReadOnlySpan<char> arg)"),
            "if (int.TryParse(arg.Trim(), out var v))",
            "_window.SetMeter(v / 1000.0);");
        Assert.Single(Regex.Matches(source, Regex.Escape("_window.SetMeter(")));
    }

    [Theory]
    [InlineData("METER 523", true, 523)]
    [InlineData("  meter   1000\t", true, 1000)]
    [InlineData("Meter -5", true, -5)]
    [InlineData("METER +7", true, 7)]
    [InlineData("METER", true, null)]
    [InlineData("METER 1 2", true, null)]
    [InlineData("METER 99999999999", true, null)]
    [InlineData("METER\t12", false, null)]
    [InlineData("METERS 1", false, null)]
    [InlineData("\uFF2D\uFF25\uFF34\uFF25\uFF32 5", false, null)]
    [InlineData("\u00A0METER 5\u2028", true, 5)]
    [InlineData("", false, null)]
    public void A_line_is_read_as_the_general_path_read_it(string line, bool meter, int? level)
    {
        InCulture(CultureInfo.InvariantCulture, () =>
        {
            Assert.Equal((meter, level), General(line));
            Assert.Equal((meter, level), OnItsCharacters(line));
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("tr-TR")]
    [InlineData("sv-SE")]
    [InlineData("ar-SA")]
    public void Every_line_of_the_corpus_is_read_as_the_general_path_reads_it(string culture)
    {
        var mismatches = new List<string>();
        int meterWithLevel = 0, meterWithoutLevel = 0, other = 0;
        InCulture(CultureInfo.GetCultureInfo(culture), () =>
        {
            foreach (var line in Corpus())
            {
                var general = General(line);
                if (general != OnItsCharacters(line))
                {
                    mismatches.Add(Escaped(line));
                }

                if (!general.Meter)
                {
                    other++;
                }
                else if (general.Level is null)
                {
                    meterWithoutLevel++;
                }
                else
                {
                    meterWithLevel++;
                }
            }
        });

        Assert.True(mismatches.Count == 0, $"{mismatches.Count} line(s) read differently: {string.Join(", ", mismatches.Take(10))}");
        Assert.True(meterWithLevel > 0 && meterWithoutLevel > 0 && other > 0, $"{meterWithLevel}, {meterWithoutLevel}, {other}");
    }

    [Fact]
    public void Exactly_the_ASCII_letters_upper_case_or_compare_ignoring_case_to_a_letter_of_METER()
    {
        foreach (var letter in "METR")
        {
            var target = letter.ToString();
            var upperCased = new List<int>();
            var ignoringCase = new List<int>();
            for (var code = 0; code <= char.MaxValue; code++)
            {
                var text = ((char)code).ToString();
                if (text.ToUpperInvariant() == target)
                {
                    upperCased.Add(code);
                }

                if (text.AsSpan().Equals(target, StringComparison.OrdinalIgnoreCase))
                {
                    ignoringCase.Add(code);
                }
            }

            int[] ascii = [letter, char.ToLowerInvariant(letter)];
            Assert.Equal(ascii, upperCased);
            Assert.Equal(ascii, ignoringCase);
        }
    }

    // What Dispatch did with every line before, and still does with every line that is not METER, down to the level.
    private static (bool Meter, int? Level) General(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0)
        {
            return (false, null);
        }

        var sp = trimmed.IndexOf(' ');
        var cmd = sp < 0 ? trimmed : trimmed[..sp];
        var arg = sp < 0 ? string.Empty : trimmed[(sp + 1)..];
        if (cmd.ToUpperInvariant() != "METER")
        {
            return (false, null);
        }

        return int.TryParse(arg.Trim(), out var v) ? (true, v) : (true, null);
    }

    // TryDispatchMeter and DispatchMeter, as pinned above.
    private static (bool Meter, int? Level) OnItsCharacters(string line)
    {
        var trimmed = line.AsSpan().Trim();
        var sp = trimmed.IndexOf(' ');
        var cmd = sp < 0 ? trimmed : trimmed[..sp];
        if (!cmd.Equals("METER", StringComparison.OrdinalIgnoreCase))
        {
            return (false, null);
        }

        var arg = sp < 0 ? ReadOnlySpan<char>.Empty : trimmed[(sp + 1)..];
        return int.TryParse(arg.Trim(), out var v) ? (true, v) : (true, null);
    }

    // Every case of METER and look-alikes, joined to levels by white space and other separators, between edges of white
    // space and characters that are not white space: about 275,000 lines.
    private static IEnumerable<string> Corpus()
    {
        string[] verbs =
        [
            .. Cases("METER"),
            "METERS", "METE", "XMETER", "MET ER", "M", string.Empty, "RECORDING", "meterx",
            "\uFF2D\uFF25\uFF34\uFF25\uFF32", // fullwidth letters
            "MET\u0415R", // Cyrillic capital IE
            "ME\u0422ER", // Cyrillic capital TE
            "\u039CETER", // Greek capital MU
            "METE\u211B", // script capital R
            "ME\u0301TER", // a combining acute accent
            "METER\u200B", // a zero width space, which is not white space
            "\u0131METER", // dotless i
            "\uD835\uDC0CETER", // mathematical bold capital M, a surrogate pair
        ];
        string[] separators = [" ", "  ", "\t", "\u00A0", "\u3000", string.Empty];
        string[] levels =
        [
            string.Empty, "0", "523", "1000", "-5", "+7", " 12 ", "1 2", "99999999999", "2147483647", "-2147483648",
            "2147483648", "\uFF15", "\u0665", "0x10", "1e3", "1.5", "1,000", "007", "-0", "\u00A012", "12\u00A0", "(5)", "5-",
            "\u22125", "- 5",
        ];
        string[] edges = [string.Empty, " ", "\t", "\u00A0", "\u2028", "\u200B"];

        foreach (var verb in verbs)
        {
            foreach (var separator in separators)
            {
                foreach (var level in levels)
                {
                    foreach (var lead in edges)
                    {
                        foreach (var trail in edges)
                        {
                            yield return lead + verb + separator + level + trail;
                        }
                    }
                }
            }
        }
    }

    private static IEnumerable<string> Cases(string word) =>
        Enumerable.Range(0, 1 << word.Length).Select(mask => string.Concat(
            word.Select((letter, i) => (mask & (1 << i)) == 0 ? letter : char.ToLowerInvariant(letter))));

    private static void InCulture(CultureInfo culture, Action action)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = culture;
        try
        {
            action();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static string Escaped(string line) =>
        string.Concat(line.Select(c => c is >= ' ' and <= '~' ? c.ToString() : $"\\u{(int)c:X4}"));

    private static void InOrder(string code, params string[] statements)
    {
        var positions = statements.Select(statement => code.IndexOf(statement, StringComparison.Ordinal)).ToArray();
        Assert.DoesNotContain(-1, positions);
        Assert.Equal(positions.Order(), positions);
    }

    // The text of a member from its signature to its matching closing brace.
    private static string Body(string code, string signature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' was not found.");
        var open = code.IndexOf('{', code.IndexOf(')', start));
        var depth = 0;
        for (var i = open; i < code.Length; i++)
        {
            depth += code[i] == '{' ? 1 : code[i] == '}' ? -1 : 0;
            if (depth == 0)
            {
                return code[start..(i + 1)];
            }
        }

        throw new InvalidOperationException($"'{signature}' has no closing brace.");
    }

    private static string Source() => Regex.Replace(
        File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Scribe.Overlay", "Ipc", "OverlayIpcServer.cs")).ReplaceLineEndings("\n"),
        @"//[^\n]*",
        string.Empty);

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return root.FullName;
    }

    [Collection(AllocationMeasurementCollection.Name)]
    public sealed class Allocations
    {
        private static readonly string[] Lines = ["METER 523", "  meter 1000  ", "METER", "METER x", "RECORDING"];

        [Fact]
        public void Reading_METER_lines_on_their_characters_allocates_nothing()
        {
            var sum = 0L;
            for (var i = 0; i < 10; i++)
            {
                sum += Rounds(OnItsCharacters) + Rounds(General);
            }

            _ = RuntimeWork.Now().Since(RuntimeWork.Now());

            var work = RuntimeWork.Now();
            var before = GC.GetAllocatedBytesForCurrentThread();
            sum += Rounds(OnItsCharacters);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            var during = RuntimeWork.Now().Since(work);

            before = GC.GetAllocatedBytesForCurrentThread();
            sum += Rounds(General);
            var general = GC.GetAllocatedBytesForCurrentThread() - before;

            AllocationMeasurement.AssertZero(allocated, during, "10,000 lines read on their characters", () => Rounds(OnItsCharacters));
            Assert.True(general >= 2_000 * 64L, $"The general path allocated {general} bytes for 10,000 lines.");
            Assert.NotEqual(0, sum);
        }

        // 2,000 rounds of the five lines: two METER lines with a level, two without, one other verb.
        private static long Rounds(Func<string, (bool Meter, int? Level)> read)
        {
            var sum = 0L;
            for (var round = 0; round < 2_000; round++)
            {
                foreach (var line in Lines)
                {
                    var (meter, level) = read(line);
                    sum += (meter ? 1 : 0) + (level ?? 0);
                }
            }

            return sum;
        }
    }
}
