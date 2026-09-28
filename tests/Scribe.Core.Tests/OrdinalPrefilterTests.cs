using System.Text;
using System.Text.RegularExpressions;
using Scribe.Core.PostProcessing;

namespace Scribe.Core.Tests;

/// <summary>
/// The guard behind <see cref="OrdinalPrefilter"/> (combined.md rows 6 and 8, E.6): the characters that make an ordinal
/// ignore-case search unsound for an ASCII pattern, read from the regex engine with the matcher's options.
/// </summary>
public sealed class OrdinalPrefilterTests
{
    [Fact]
    public void The_guard_is_what_the_engine_says_under_every_culture_and_is_the_kelvin_sign_on_this_runtime()
    {
        // Recomputed here from scratch, so a runtime whose case table changes shows up as a failure, not as a missed match.
        var computed = OrdinalPrefilter.ComputeUnsafeForAscii();
        Assert.Equal(computed, OrdinalPrefilter.UnsafeForAsciiList);
        foreach (var culture in UsageEquivalenceCorpus.Cultures)
        {
            UsageEquivalenceCorpus.InCulture(culture, () => Assert.Equal(computed, OrdinalPrefilter.ComputeUnsafeForAscii()));
        }

        // .NET 10.0.12: only U+212A KELVIN SIGN matches an ASCII letter (k) that OrdinalIgnoreCase does not equate with it.
        // If a runtime adds one, this line names it; the prefilter stays sound either way, since it reads the set.
        Assert.Equal(['\u212A'], computed);
    }

    [Fact]
    public void A_failed_search_never_drops_a_regex_match_in_a_sound_text()
    {
        // The proof, checked by brute force: every ASCII pattern of one or two characters, in every text of two characters
        // drawn from a spread of code units (the guard's own excluded), is either found by the search or unmatched by the regex.
        var units = new List<char>();
        for (var c = 0; c < 0x250; c++)
        {
            units.Add((char)c);
        }

        units.AddRange(['\u0130', '\u0131', '\u017F', '\u1E9E', '\u2126', '\u212B', '\u212A', '\uFF21', '\uFF41', '\uD800', '\uDC00']);
        var ascii = Enumerable.Range(0x20, 0x5F).Select(c => (char)c).ToArray();
        var checkedPairs = 0;
        foreach (var p in ascii)
        {
            foreach (var pattern in new[] { p.ToString(), p + "k", "s" + p })
            {
                var regex = new Regex(Regex.Escape(pattern), TextPostProcessor.DictionaryMatchOptions);
                foreach (var a in units)
                {
                    var text = new StringBuilder().Append(a).Append(pattern.Length == 1 ? 'x' : pattern[^1]).ToString();
                    if (!OrdinalPrefilter.IsSound(text))
                    {
                        continue;
                    }

                    if (regex.IsMatch(text))
                    {
                        Assert.True(OrdinalPrefilter.MayMatch(text, pattern), $"pattern '{pattern}' in U+{(int)a:X4}");
                    }

                    checkedPairs++;
                }
            }
        }

        Assert.True(checkedPairs > 100_000);
    }

    [Fact]
    public void A_text_with_the_kelvin_sign_is_not_sound()
    {
        Assert.True(OrdinalPrefilter.IsSound("kube k8s KUBE"));
        Assert.False(OrdinalPrefilter.IsSound("\u212Aube"));
        Assert.Matches(new Regex("kube", TextPostProcessor.DictionaryMatchOptions), "\u212Aube");
        Assert.False(OrdinalPrefilter.MayMatch("\u212Aube", "kube"));
    }
}
