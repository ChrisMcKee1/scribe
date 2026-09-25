using System.Text.RegularExpressions;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;

namespace Scribe.Core.Tests;

/// <summary>
/// <see cref="SpokenFormFold"/> must be broader than every comparison dictation makes, on every character, because the
/// dictionary cleanup relies on it to prove two spoken forms can never meet the same text. These tests check it against
/// the matcher, the comparer and invariant case mapping directly, character by character.
/// </summary>
public sealed class SpokenFormFoldTests
{
    private const RegexOptions MatcherOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    [Fact]
    public void Every_character_the_matcher_takes_for_another_folds_the_same_and_one_for_one()
    {
        // The matcher's regex for each character, run over every character: whatever it matches must fold the same, must
        // be one character long (the matcher never folds one character into two), and must match back, which the fold's
        // search relies on to reach the same set from any member.
        var text = EveryCharacter();
        var equivalents = new Dictionary<char, HashSet<char>>();
        var failures = new List<string>();
        for (var i = 0; i <= char.MaxValue; i++)
        {
            var c = (char)i;
            if (char.IsSurrogate(c))
            {
                continue;
            }

            var found = new HashSet<char>();
            for (var match = new Regex(Regex.Escape(c.ToString()), MatcherOptions).Match(text); match.Success; match = match.NextMatch())
            {
                var other = text[match.Index];
                found.Add(other);
                if (match.Length != 1)
                {
                    failures.Add($"U+{i:X4} matched {match.Length} characters at U+{match.Index:X4}");
                }
                else if (SpokenFormFold.Fold(other) != SpokenFormFold.Fold(c))
                {
                    failures.Add($"U+{i:X4} matches U+{(int)other:X4} but they fold apart");
                }
            }

            equivalents[c] = found;
        }

        foreach (var (c, found) in equivalents)
        {
            failures.AddRange(found.Where(other => !equivalents[other].Contains(c)).Select(other => $"U+{(int)c:X4} matches U+{(int)other:X4} but not back"));
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(40)));
        Assert.True(equivalents.Count(e => e.Value.Count > 1) > 2000, "the matcher should fold thousands of characters");
    }

    [Fact]
    public void The_matcher_is_the_regex_the_fold_reads()
    {
        // Through TextPostProcessor itself, for every character that has another the regex takes it for, and for a sample of
        // the rest: a rule replacing that one character rewrites exactly the characters the plain regex matches. The text
        // leaves out whitespace, which the matcher's normalization would move, and the marker the rule writes.
        const char marker = '\uFFFF';
        var kept = Enumerable.Range(0, char.MaxValue + 1).Select(i => (char)i)
            .Where(c => !char.IsSurrogate(c) && !char.IsWhiteSpace(c) && c != marker)
            .ToArray();
        var text = new string(kept);

        var checkedCount = 0;
        foreach (var c in kept)
        {
            var regex = new Regex(Regex.Escape(c.ToString()), MatcherOptions);
            var expected = regex.Matches(text).Select(m => m.Index).ToList();
            if (expected.Count < 2 && c % 97 != 0)
            {
                continue;
            }

            var written = TextPostProcessor.ApplyRule(text, new DictionaryEntry(0, c.ToString(), marker.ToString(), WholeWord: false, Enabled: true));
            Assert.Equal(text.Length, written.Length);
            var rewritten = Enumerable.Range(0, text.Length).Where(i => written[i] != text[i]).ToList();
            Assert.True(expected.SequenceEqual(rewritten), $"U+{(int)c:X4}: the matcher rewrote other characters than the regex matches");
            checkedCount++;
        }

        Assert.True(checkedCount > 2500, $"checked {checkedCount} characters");
    }

    [Fact]
    public void OrdinalIgnoreCase_and_invariant_case_mapping_never_split_a_fold()
    {
        var failures = new List<string>();
        var byUpper = new Dictionary<string, List<char>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i <= char.MaxValue; i++)
        {
            var c = (char)i;
            if (char.IsSurrogate(c))
            {
                continue;
            }

            var folded = SpokenFormFold.Fold(c);
            if (SpokenFormFold.Fold(char.ToUpperInvariant(c)) != folded || SpokenFormFold.Fold(char.ToLowerInvariant(c)) != folded)
            {
                failures.Add($"U+{i:X4} folds apart from its invariant upper or lower case");
            }

            // The comparer's own grouping: a dictionary keyed by it puts every character it calls equal under one key.
            if (!byUpper.TryGetValue(c.ToString(), out var group))
            {
                byUpper[c.ToString()] = group = [];
            }

            group.Add(c);
        }

        failures.AddRange(byUpper.Values
            .Where(group => group.Select(SpokenFormFold.Fold).Distinct().Count() > 1)
            .Select(group => "OrdinalIgnoreCase calls these equal but they fold apart: " + string.Join(" ", group.Select(c => $"U+{(int)c:X4}"))));

        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(40)));
    }

    [Theory]
    [InlineData("k", "K")]
    [InlineData("k", "\u212A")]
    [InlineData("ς", "σ")]
    [InlineData("ς", "Σ")]
    [InlineData("ß", "\u1E9E")]
    [InlineData("i", "\u0130")]
    [InlineData("i", "\u0131")]
    [InlineData("I", "\u0131")]
    [InlineData("s", "\u017F")]
    [InlineData("å", "\u212B")]
    [InlineData("ω", "\u2126")]
    [InlineData("\u0264", "\uA7CB")]
    public void Letters_any_comparison_takes_for_the_same_fold_the_same(string one, string other)
    {
        Assert.Equal(SpokenFormFold.Fold(one), SpokenFormFold.Fold(other));
    }

    [Fact]
    public void A_character_outside_the_basic_plane_folds_like_any_other()
    {
        // OrdinalIgnoreCase folds Deseret capital and small letters as whole code points, which a character-by-character
        // fold cannot see; so every surrogate folds the same, and such a character overlaps any other in its place.
        Assert.True(StringComparer.OrdinalIgnoreCase.Equals("\U00010400", "\U00010428"));
        Assert.Equal(SpokenFormFold.Fold("\U00010400"), SpokenFormFold.Fold("\U00010428"));
        Assert.Equal(SpokenFormFold.Fold("a\U0001F600b"), SpokenFormFold.Fold("A\U0001F601B"));
        Assert.NotEqual(SpokenFormFold.Fold("a"), SpokenFormFold.Fold("b"));
    }

    [Fact]
    public void A_spoken_form_is_matched_as_the_literal_text_it_is()
    {
        // The fold compares spoken forms as text, which holds because the matcher escapes every spoken form.
        Assert.Equal("axb", TextPostProcessor.ApplyRule("axb", DictionaryEntry.New("a.b", "X", wholeWord: false)));
        Assert.Equal("X", TextPostProcessor.ApplyRule("a.b", DictionaryEntry.New("a.b", "X", wholeWord: false)));
        Assert.Equal("use cc", TextPostProcessor.ApplyRule("use cc", DictionaryEntry.New("c+", "X")));
        Assert.Equal("use X", TextPostProcessor.ApplyRule("use c+", DictionaryEntry.New("c+", "X", wholeWord: false)));
        Assert.Equal("a b", TextPostProcessor.ApplyRule("a b", DictionaryEntry.New("[ab]", "X", wholeWord: false)));
        Assert.Equal("X", TextPostProcessor.ApplyRule("[ab]", DictionaryEntry.New("[ab]", "X", wholeWord: false)));
    }

    [Fact]
    public void A_dictionary_rule_compiles_to_the_literal_or_the_literal_between_the_word_lookarounds()
    {
        // LibrarySwitchOffCopy decides which rules can meet from these two constructs and treats any other as meeting every
        // rule; a change here must be deliberate, and must come with its model.
        Assert.Equal(@"c\#-code", TextPostProcessor.DictionaryPattern(DictionaryEntry.New("c#-code", "X", wholeWord: false)));
        Assert.Equal(@"(?<!\w)get\ hub(?!\w)", TextPostProcessor.DictionaryPattern(DictionaryEntry.New("get hub", "GitHub")));
        Assert.True(TextPostProcessor.GuardsWrittenForm("york", "New York"));
        Assert.False(TextPostProcessor.GuardsWrittenForm("azure", "Azure"));
        Assert.False(TextPostProcessor.GuardsWrittenForm("um", ""));
    }

    [Fact]
    public void Word_characters_are_the_regex_engines_own_and_a_fold_can_be_a_boundary_only_if_a_member_can()
    {
        var word = new Regex(@"\w", MatcherOptions);
        foreach (var c in "aZ7_\u00DF\u212A\u0301\u03C2 -#.,'\u00A0")
        {
            Assert.Equal(word.IsMatch(c.ToString()), SpokenFormFold.IsWordCharacter(c));
        }

        // A letter, a digit, the underscore, a combining mark and the Kelvin sign are word characters, and so is every
        // character they fold with; a space, a hyphen, a hash and a full stop are not, and nor is a lone surrogate.
        foreach (var c in "aZ7_\u00DF\u212A\u0301\u03C2")
        {
            Assert.False(SpokenFormFold.CanBeNonWord(c), $"U+{(int)c:X4}");
        }

        foreach (var c in " -#.,'\u00A0\uD800\uDC00")
        {
            Assert.True(SpokenFormFold.CanBeNonWord(c), $"U+{(int)c:X4}");
        }
    }

    private static string EveryCharacter() =>
        string.Create(char.MaxValue + 1, 0, static (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = (char)i;
            }
        });
}
