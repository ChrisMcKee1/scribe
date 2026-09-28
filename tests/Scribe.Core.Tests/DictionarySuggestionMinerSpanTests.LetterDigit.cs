using Scribe.Core.PostProcessing;

namespace Scribe.Core.Tests;

// The letter-digit shape has one digit where it had a run of them (^[A-Za-z]+[0-9][A-Za-z0-9]*$ for
// ^[A-Za-z]+[0-9]+[A-Za-z0-9]*$). A token holding a digit is never an acronym or a camel hump, and neither letter-digit
// pattern matches a token without one, so comparing IsCandidate with the Oracle decides the two patterns on every token.
public partial class DictionarySuggestionMinerSpanTests
{
    [Fact]
    public void Every_token_of_up_to_six_characters_is_judged_as_the_previous_pattern_judged_it()
    {
        // Letters of both cases, digits at both ends of the range, and what ends or breaks the shape: a newline (which $
        // accepts once, at the end), a dot, an underscore and the Kelvin sign, which is a letter but not an ASCII one.
        char[] alphabet = ['a', 'Z', '0', '9', '\n', '.', '_', '\u212A'];
        var token = new char[6];
        var judged = 0;
        for (var length = 0; length <= token.Length; length++)
        {
            var digits = new int[length];
            while (true)
            {
                for (var i = 0; i < length; i++)
                {
                    token[i] = alphabet[digits[i]];
                }

                AssertSameAsOracle(new string(token, 0, length));
                judged++;
                var place = length - 1;
                while (place >= 0 && ++digits[place] == alphabet.Length)
                {
                    digits[place--] = 0;
                }

                if (place < 0)
                {
                    break;
                }
            }
        }

        Assert.Equal(299_593, judged);
    }

    [Fact]
    public void Seeded_longer_tokens_are_judged_as_the_previous_pattern_judged_them()
    {
        // Mostly letters and digits, so the shape is often reached and often fails late, with the characters that break it.
        const string common = "abcxyzABCKXYZ0123456789";
        const string rare = "\n._-\u212A\u00E9 ";
        var random = new Random(51_052);
        var candidates = 0;
        for (var i = 0; i < 20_000; i++)
        {
            var chars = new char[random.Next(7, 41)];
            for (var j = 0; j < chars.Length; j++)
            {
                chars[j] = random.Next(40) == 0 ? rare[random.Next(rare.Length)] : common[random.Next(common.Length)];
            }

            if (random.Next(4) == 0)
            {
                // A letter run then digits then anything alphanumeric, which the old pattern's two loops fought over.
                var letters = random.Next(1, chars.Length - 1);
                for (var j = 0; j < chars.Length; j++)
                {
                    chars[j] = j < letters ? common[random.Next(13)] : common[random.Next(common.Length)];
                }

                chars[letters] = (char)('0' + random.Next(10));
                if (random.Next(5) == 0)
                {
                    chars[^1] = '\n';
                }
            }

            var text = new string(chars);
            AssertSameAsOracle(text);
            if (Oracle.IsCandidate(text))
            {
                candidates++;
            }
        }

        // The corpus reaches the shape: a quarter of it is built to match.
        Assert.True(candidates > 4_000, $"Only {candidates} candidates.");
    }
}
