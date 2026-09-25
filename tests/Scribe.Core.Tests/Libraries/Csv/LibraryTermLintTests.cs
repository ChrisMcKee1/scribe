using Scribe.Core.Cleanup;
using Scribe.Core.Libraries;
using Scribe.Core.PostProcessing;

namespace Scribe.Core.Tests.Libraries.Csv;

/// <summary>
/// <see cref="LibraryTermLint"/> (plan 3.6, acceptance X-9): the editor's hints, which never block a Save, share their
/// word lists with <see cref="BuiltInLibraryDataTests"/> and judge the glossary with the very predicate the glossary
/// applies, so a hint can never say a term is left out that AI cleanup receives, or the other way round.
/// </summary>
public sealed class LibraryTermLintTests
{
    [Theory]
    [InlineData("il", "IL", true, TermHints.OrdinaryWord)]
    [InlineData(" Di ", "DI", true, TermHints.OrdinaryWord | TermHints.IrregularSpacing)]
    [InlineData("and", "AND", true, TermHints.None)]
    [InlineData("sol", "SOL", false, TermHints.WholeWordOff)]
    [InlineData("Distillation", "distillation", true, TermHints.ForcesLowercase)]
    [InlineData("resource group", "resource group", true, TermHints.ForcesLowercase)]
    [InlineData("c#", "c#", true, TermHints.ForcesLowercase)]
    [InlineData("NPM", "npm", true, TermHints.None)]
    [InlineData("kubectl", "kubectl", true, TermHints.None)]
    [InlineData("123", "123", true, TermHints.None)]
    [InlineData("stra\u00DFe", "stra\u00DFe", true, TermHints.ForcesLowercase)]
    [InlineData("\u00DF", "\u00DF", true, TermHints.None)]
    [InlineData("get hub", "GitHub", true, TermHints.None)]
    [InlineData("get  hub", "GitHub", true, TermHints.IrregularSpacing)]
    [InlineData(" get hub", "GitHub", true, TermHints.IrregularSpacing)]
    [InlineData("get\thub", "GitHub", true, TermHints.IrregularSpacing)]
    [InlineData("get\u00A0hub", "GitHub", true, TermHints.IrregularSpacing)]
    [InlineData("sig", "Line one\nLine two", true, TermHints.MultiLine)]
    [InlineData("sig", "Line one\u2028Line two", true, TermHints.MultiLine)]
    [InlineData("tab", "a\tb", true, TermHints.None)]
    [InlineData("removal", "", true, TermHints.None)]
    [InlineData("il", "il", false, TermHints.OrdinaryWord | TermHints.WholeWordOff | TermHints.ForcesLowercase)]
    public void Each_hint_says_what_it_names(string spoken, string written, bool wholeWord, TermHints expected)
    {
        Assert.Equal(expected, LibraryTermLint.Check(new TermValues(spoken, written, wholeWord)));
        Assert.Equal(expected, LibraryTermLint.Check(new TermValues(spoken, written, wholeWord, Enabled: false)));
    }

    [Fact]
    public void A_written_form_past_the_glossary_cap_is_long_and_one_that_is_also_multi_line_is_both()
    {
        var atCap = new string('a', CleanupPrompt.MaxGlossaryTermChars);
        var long1 = atCap + "a";
        var longMultiLine = atCap + "\n" + atCap;
        var breakLate = new string('a', 250) + "\r";
        var multiLineAtCap = new string('a', CleanupPrompt.MaxGlossaryTermChars - 1) + "\n";

        Assert.Equal(TermHints.None, LibraryTermLint.Check(new TermValues("sig", atCap)));
        Assert.Equal(TermHints.LongForGlossary, LibraryTermLint.Check(new TermValues("sig", long1)));
        Assert.Equal(TermHints.LongForGlossary | TermHints.MultiLine, LibraryTermLint.Check(new TermValues("sig", longMultiLine)));
        Assert.Equal(TermHints.LongForGlossary | TermHints.MultiLine, LibraryTermLint.Check(new TermValues("sig", breakLate)));
        Assert.Equal(TermHints.MultiLine, LibraryTermLint.Check(new TermValues("sig", multiLineAtCap)));
    }

    [Fact]
    public void The_glossary_hints_agree_with_the_glossary_for_every_written_form()
    {
        // Seeded property test against CleanupPrompt.IsVocabularyReplacement, the rule that leaves templates out of the
        // glossary: a written form that is not blank is left out exactly when it is long or spans lines.
        const string alphabet = "ab \t\r\n\u000B\u000C\u0085\u2028\u2029\u00A0.";
        var random = new Random(20261004);
        var reached = new int[2];
        for (var i = 0; i < 20_000; i++)
        {
            var length = random.Next(4) == 0 ? random.Next(90, 260) : random.Next(0, 12);
            var written = CsvTestData.Random(random, random.Next(3) == 0 ? "ab" : alphabet, length, length);
            if (string.IsNullOrWhiteSpace(written))
            {
                continue;
            }

            var hints = LibraryTermLint.Check(new TermValues("x", written));
            var leftOut = (hints & (TermHints.LongForGlossary | TermHints.MultiLine)) != 0;

            Assert.True(leftOut == !CleanupPrompt.IsVocabularyReplacement(written), $"case {i}: {written.Length} characters");
            reached[leftOut ? 1 : 0]++;
        }

        Assert.True(reached[0] > 1000 && reached[1] > 1000, $"both outcomes reached: {reached[0]}, {reached[1]}");
    }

    [Fact]
    public void Every_line_break_the_glossary_knows_makes_a_term_multi_line()
    {
        foreach (var lineBreak in "\r\n\u000B\u000C\u0085\u2028\u2029")
        {
            Assert.Equal(TermHints.MultiLine, LibraryTermLint.Check(new TermValues("sig", "a" + lineBreak + "b")));
        }

        foreach (var notABreak in "\t \u00A0\u200B")
        {
            Assert.Equal(TermHints.None, LibraryTermLint.Check(new TermValues("sig", "a" + notABreak + "b")));
        }
    }

    [Fact]
    public void The_word_lists_are_the_ones_the_shipped_data_is_held_to()
    {
        Assert.Contains("il", LibraryTermLint.CommonWords);
        Assert.Contains("DI", LibraryTermLint.CommonWords);
        Assert.Contains("we", LibraryTermLint.CommonWords);
        Assert.DoesNotContain("npm", LibraryTermLint.CommonWords);
        Assert.Contains("npm", LibraryTermLint.AlwaysLowercaseNames);
        Assert.DoesNotContain("NPM", LibraryTermLint.AlwaysLowercaseNames);
        Assert.Throws<NotSupportedException>(() => ((ICollection<string>)LibraryTermLint.CommonWords).Add("x"));
        Assert.Throws<NotSupportedException>(() => ((ICollection<string>)LibraryTermLint.AlwaysLowercaseNames).Add("x"));
    }

    [Fact]
    public void No_shipped_term_raises_a_hint()
    {
        // The shipped libraries are held to these rules by BuiltInLibraryDataTests, so a hint on one of their rows would be
        // noise on a term the user never wrote.
        var hinted = BuiltInDictionaryLibraries.All
            .SelectMany(library => library.Entries.Select(entry => (library.Id, Entry: entry, Hints: LibraryTermLint.Check(TermValues.FromEntry(entry)))))
            .Where(item => item.Hints != TermHints.None)
            .Select(item => $"{item.Id}: '{item.Entry.Pattern}' {item.Hints}")
            .ToList();

        Assert.True(hinted.Count == 0, string.Join(", ", hinted));
    }

    [Fact]
    public void Checking_needs_values()
    {
        Assert.Throws<ArgumentNullException>(() => LibraryTermLint.Check(null!));
    }
}
