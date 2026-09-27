using System.Text.RegularExpressions;
using Scribe.Core.PostProcessing;

namespace Scribe.Core.Tests;

public partial class DictionarySuggestionMinerSpanTests
{
    private static readonly string[] Tokens =
    [
        "OK", "ok", "Ok", "AM", "PM", "pm", "TODO", "todo", "Todo", "FYI", "ASAP", "LOL", "LOLZ", "OKAY",
        "AB", "ABCDEFGH", "ABCDEFGHI", ".NET", "..NET", ".net", "ReBAC", "GitHub", "sherpaOnnx", "iOS", "K8s", "S3", "net10",
        "GPT4", "gpt4", "9s", "e.g", "e.g.", "ABC\n", "ABC\n\n", "\nABC", "OK\n", "ABC\r\n", "A", "Ab", "aB", "\u212Aey",
        "\u00C9T\u00C9", "K8s-1", "GPT-4", "net10a", "net1O", string.Empty, " ", "A B", "ABC ",
    ];

    public static TheoryData<string> NamedTokens
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var token in Tokens)
            {
                data.Add(token);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(NamedTokens))]
    public void Named_tokens_are_judged_as_the_previous_implementation_judged_them(string token) => AssertSameAsOracle(token);

    [Fact]
    public void Every_short_token_is_judged_as_the_previous_implementation_judged_it()
    {
        // Each pattern is anchored and ASCII-classed, so every token of up to three characters from letters of both cases
        // (the Kelvin sign and accents included), digits, a dot, a hyphen, white space and a newline covers every branch.
        char[] alphabet = ['A', 'B', 'Z', 'a', 'b', 'z', 'K', 'k', '\u212A', '0', '9', '.', '-', ' ', '\n', '\u00C9', '\u00E9', '_'];
        AssertSameAsOracle(string.Empty);
        foreach (var first in alphabet)
        {
            AssertSameAsOracle($"{first}");
            foreach (var second in alphabet)
            {
                AssertSameAsOracle($"{first}{second}");
                foreach (var third in alphabet)
                {
                    AssertSameAsOracle($"{first}{second}{third}");
                }
            }
        }
    }

    [Fact]
    public void A_span_inside_a_longer_text_is_judged_like_the_same_text_on_its_own()
    {
        foreach (var token in Tokens)
        {
            var text = "Zz9" + token + "9zZ";
            Assert.Equal(Oracle.IsCandidate(token), DictionarySuggestionMiner.IsCandidate(text.AsSpan(3, token.Length)));
        }
    }

    [Fact]
    public void A_null_token_fails_as_it_did()
    {
        var expected = Assert.Throws<ArgumentNullException>(() => Oracle.IsCandidate(null!));
        var actual = Assert.Throws<ArgumentNullException>(() => DictionarySuggestionMiner.IsCandidate((string)null!));

        Assert.Equal(expected.ParamName, actual.ParamName);
        Assert.Equal(expected.Message, actual.Message);
    }

    private static void AssertSameAsOracle(string token)
    {
        var expected = Oracle.IsCandidate(token);
        Assert.Equal(expected, DictionarySuggestionMiner.IsCandidate(token));
        Assert.Equal(expected, DictionarySuggestionMiner.IsCandidate(token.AsSpan()));
    }

    // In the collection that runs alone: no other test allocates on this thread while it measures.
    [Collection(AllocationMeasurementCollection.Name)]
    public sealed class Allocations
    {
        // Tokens the stoplist, the acronym or the camel-hump shape decides. The letter-digit pattern backtracks between two
        // overlapping loops, and its generated code allocates while it runs unoptimized (tier 0, or a Debug build of
        // Scribe.Core, measured at 96 bytes a call either way, string or span), so no zero is asserted for tokens it decides.
        private static readonly string[] DecidedEarly = ["TODO", "OK", "ABC", ".NET", "OpenAI", "iOS", "ReBAC", "GitHub"];

        private static readonly string[] Mixed = ["ok", "OpenAI", "K8s", "TODO", "net10", "hello", "ReBAC", "GPT4", "ABC", ".NET", "iOS", "the"];

        [Fact]
        public void Judging_a_token_inside_its_text_allocates_nothing_the_patterns_do_not()
        {
            var (text, starts, lengths) = Layout(DecidedEarly);
            var perPass = DecidedEarly.Count(Oracle.IsCandidate);
            SpanPass(text, starts, lengths);
            _ = RuntimeWork.Now().Since(RuntimeWork.Now());

            var work = RuntimeWork.Now();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var candidates = SpanPass(text, starts, lengths);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            var during = RuntimeWork.Now().Since(work);

            Assert.Equal(1_000 * perPass, candidates);
            AllocationMeasurement.AssertZero(
                allocated, during, "1,000 passes over tokens inside one text", () => SpanPass(text, starts, lengths));
        }

        [Fact]
        public void Judging_a_span_never_allocates_more_than_judging_the_same_token_as_a_string()
        {
            // The string overload runs first: it now delegates to the span overload, so the patterns' code is at least as far
            // along when the span pass is measured, and any tiering in between only lowers the second number.
            var (text, starts, lengths) = Layout(Mixed);
            StringPass(Mixed);
            SpanPass(text, starts, lengths);

            var stringBefore = GC.GetAllocatedBytesForCurrentThread();
            var stringCandidates = StringPass(Mixed);
            var stringBytes = GC.GetAllocatedBytesForCurrentThread() - stringBefore;

            var spanBefore = GC.GetAllocatedBytesForCurrentThread();
            var spanCandidates = SpanPass(text, starts, lengths);
            var spanBytes = GC.GetAllocatedBytesForCurrentThread() - spanBefore;

            Assert.Equal(stringCandidates, spanCandidates);
            Assert.True(spanBytes <= stringBytes, $"The span overload allocated {spanBytes} bytes, the string overload {stringBytes}.");
        }

        private static (string Text, int[] Starts, int[] Lengths) Layout(string[] tokens)
        {
            var text = string.Join(' ', tokens);
            var starts = new int[tokens.Length];
            var lengths = new int[tokens.Length];
            for (int i = 0, start = 0; i < tokens.Length; start += tokens[i].Length + 1, i++)
            {
                starts[i] = start;
                lengths[i] = tokens[i].Length;
            }

            return (text, starts, lengths);
        }

        private static int SpanPass(string text, int[] starts, int[] lengths)
        {
            var candidates = 0;
            for (var round = 0; round < 1_000; round++)
            {
                for (var i = 0; i < starts.Length; i++)
                {
                    if (DictionarySuggestionMiner.IsCandidate(text.AsSpan(starts[i], lengths[i])))
                    {
                        candidates++;
                    }
                }
            }

            return candidates;
        }

        private static int StringPass(string[] tokens)
        {
            var candidates = 0;
            for (var round = 0; round < 1_000; round++)
            {
                foreach (var token in tokens)
                {
                    if (DictionarySuggestionMiner.IsCandidate(token))
                    {
                        candidates++;
                    }
                }
            }

            return candidates;
        }
    }

    // IsCandidate as it was before the span overload, verbatim apart from the class name.
    private static partial class Oracle
    {
        private static readonly HashSet<string> Stoplist = new(StringComparer.Ordinal)
        {
            "OK", "AM", "PM", "TODO", "FYI", "ASAP", "LOL",
        };

        public static bool IsCandidate(string token) => !Stoplist.Contains(token) && IsJargonShaped(token);

        private static bool IsJargonShaped(string token) =>
            Acronym().IsMatch(token) || CamelHump().IsMatch(token) || LetterDigit().IsMatch(token);

        [GeneratedRegex(@"^\.?[A-Z]{2,8}$")]
        private static partial Regex Acronym();

        [GeneratedRegex(@"^\.?[A-Za-z]*[a-z][A-Z][A-Za-z]*$")]
        private static partial Regex CamelHump();

        [GeneratedRegex(@"^[A-Za-z]+[0-9]+[A-Za-z0-9]*$")]
        private static partial Regex LetterDigit();
    }
}
