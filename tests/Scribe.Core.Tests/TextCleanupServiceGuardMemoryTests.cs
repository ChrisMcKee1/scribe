using System.Text;
using System.Text.RegularExpressions;
using Scribe.Core.Cleanup;
using Xunit;
using Xunit.Abstractions;

namespace Scribe.Core.Tests;

/// <summary>
/// The reply guard and the output-token estimate decide exactly as before without building every word of the answer:
/// the terse-reply signal made a set of every lowercased word of every cleaned answer only to ask whether it had at most
/// three, and the estimate split the whole text into strings to count them. Copies of the old code are the reference.
/// </summary>
public sealed class TextCleanupServiceGuardMemoryTests
{
    private static readonly string[] Words =
    [
        "yes", "Yes", "YES", "yeah", "sure", "Sure", "no", "okay", "ok", "will", "do", "got", "it", "of", "course", "I", "i",
        "can", "help", "happy", "to", "assist", "what", "is", "how", "you", "the", "meeting", "starts", "at", "nine", "950",
        "$950", "3:30", "don't", "it's", "rock'n'roll", "\u0130stanbul", "istanbul", "\u03A3\u0399\u03A3\u03A5\u03A6\u039F\u03A3",
        "stra\u00DFe", "STRASSE", "\u01C5", "caf\u00E9", "CAF\u00C9", "x", "a", "hello", "there", "here", "am", "anything", "else",
    ];

    private static readonly string[] Marks = [string.Empty, string.Empty, string.Empty, ".", ",", "?", "!", "'", "\"", " -", ":"];

    [Fact]
    public void Counting_words_gives_the_count_splitting_on_white_space_gave_for_every_character()
    {
        Assert.Equal(0, TextCleanupService.CountWords(string.Empty));
        for (var i = 0; i <= char.MaxValue; i++)
        {
            var c = (char)i;
            foreach (var text in new[] { c.ToString(), $"a{c}b", $"{c}{c}x{c}", $" {c} y  z{c}" })
            {
                Assert.Equal(SplitCount(text), TextCleanupService.CountWords(text));
            }
        }
    }

    [Fact]
    public void Counting_words_gives_the_count_splitting_on_white_space_gave_for_generated_text()
    {
        var spaces = Enumerable.Range(0, char.MaxValue + 1).Select(i => (char)i).Where(char.IsWhiteSpace).ToArray();
        var random = new Random(20_260_927);
        for (var i = 0; i < 2_000; i++)
        {
            var builder = new StringBuilder();
            for (var j = random.Next(0, 30); j > 0; j--)
            {
                builder.Append(random.Next(3) switch
                {
                    0 => spaces[random.Next(spaces.Length)].ToString(),
                    1 => "\uD83D\uDE00",
                    _ => Words[random.Next(Words.Length)],
                });
            }

            var text = builder.ToString();
            Assert.Equal(SplitCount(text), TextCleanupService.CountWords(text));
        }
    }

    [Fact]
    public void The_distinct_words_are_the_old_word_set_or_null_past_the_limit()
    {
        var random = new Random(20_260_928);
        for (var i = 0; i < 3_000; i++)
        {
            var text = Sentence(random, random.Next(0, 9));
            var expected = OldWordSet(text);
            foreach (var limit in new[] { 0, 1, 2, 3, 4, 6, int.MaxValue })
            {
                var actual = TextCleanupService.DistinctWords(text, limit);
                if (expected.Count > limit)
                {
                    Assert.Null(actual);
                }
                else
                {
                    Assert.NotNull(actual);
                    Assert.True(expected.SetEquals(actual), $"'{text}' limit {limit}: [{string.Join(", ", actual)}].");
                    Assert.Equal(expected.Count, actual.Count);
                }
            }
        }
    }

    [Theory]
    [InlineData("Yes yes YES", 1)]
    [InlineData("It's it's IT'S its", 2)]
    [InlineData("", 0)]
    [InlineData("  ?! ", 0)]
    [InlineData("one two three four", 4)]
    public void Words_that_differ_only_in_case_count_once(string text, int distinct)
    {
        Assert.Equal(distinct, TextCleanupService.DistinctWords(text, int.MaxValue)!.Count);
        Assert.Equal(distinct <= 3 ? (int?)distinct : null, TextCleanupService.DistinctWords(text, 3)?.Count);
    }

    [Fact]
    public void The_reply_guard_decides_as_the_old_guard_did()
    {
        var random = new Random(20_260_929);
        var rejected = 0;
        for (var i = 0; i < 20_000; i++)
        {
            var candidate = random.Next(10) == 0 ? "  " : Sentence(random, random.Next(0, 3) == 0 ? random.Next(4, 12) : random.Next(0, 4));
            var original = Sentence(random, random.Next(0, 12));
            var expected = Old.LooksLikeInventedReply(candidate, original);
            Assert.True(
                expected == TextCleanupService.LooksLikeInventedReply(candidate, original),
                $"Candidate '{candidate}', original '{original}': the old guard said {expected}.");
            rejected += expected ? 1 : 0;
        }

        // Both outcomes are well represented, so the comparison is not of one answer only.
        Assert.InRange(rejected, 2_000, 18_000);
    }

    private static int SplitCount(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static string Sentence(Random random, int words)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < words; i++)
        {
            if (i > 0)
            {
                builder.Append(' ');
            }

            builder.Append(Words[random.Next(Words.Length)]).Append(Marks[random.Next(Marks.Length)]);
        }

        if (random.Next(4) == 0)
        {
            builder.Append('?');
        }

        return builder.ToString();
    }

    private static HashSet<string> OldWordSet(string text) => Old.WordSet(text);

    // In the collection that runs alone: no other test runs while it measures.
    [Collection(AllocationMeasurementCollection.Name)]
    public sealed class Allocations(ITestOutputHelper output)
    {
        private const string Original =
            "so i pushed the dot net api changes to github and the azure devops pipeline ran the tests before the blazor front " +
            "end deployed and then the kubernetes cluster pulled the new image";

        private const string Answer =
            "So I pushed the .NET API changes to GitHub, and the Azure DevOps pipeline ran the tests before the Blazor front " +
            "end deployed, and then the Kubernetes cluster pulled the new image.";

        [Fact]
        public void The_guard_on_a_cleaned_answer_lowers_at_most_four_words()
        {
            for (var i = 0; i < 20; i++)
            {
                _ = TextCleanupService.LooksLikeInventedReply(Answer, Original);
                _ = Old.LooksLikeInventedReply(Answer, Original);
            }

            var before = Allocated(() => Old.LooksLikeInventedReply(Answer, Original));
            var after = Allocated(() => TextCleanupService.LooksLikeInventedReply(Answer, Original));
            output.WriteLine($"Reply guard on a {TextCleanupService.CountWords(Answer)}-word answer: old {before:N0} bytes, now {after:N0} bytes.");

            // Four lowered words and the set that holds them, whatever the answer's length; the old guard kept every word.
            Assert.False(TextCleanupService.LooksLikeInventedReply(Answer, Original));
            Assert.True(after < 1_024, $"The guard allocated {after:N0} bytes on a {Answer.Length}-character answer.");
        }

        [Fact]
        public void Counting_words_allocates_nothing()
        {
            for (var i = 0; i < 20; i++)
            {
                _ = TextCleanupService.CountWords(Answer);
            }

            _ = RuntimeWork.Now().Since(RuntimeWork.Now());

            var split = Allocated(() => SplitCount(Answer) > 0);
            var work = RuntimeWork.Now();
            var start = GC.GetAllocatedBytesForCurrentThread();
            var words = TextCleanupService.CountWords(Answer);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - start;
            var during = RuntimeWork.Now().Since(work);
            output.WriteLine($"Counting {words} words: splitting allocated {split:N0} bytes, counting {allocated:N0}.");

            Assert.Equal(SplitCount(Answer), words);
            AllocationMeasurement.AssertZero(allocated, during, "Counting the words of a cleaned answer", () => TextCleanupService.CountWords(Answer));
        }

        private static long Allocated(Func<bool> run)
        {
            var start = GC.GetAllocatedBytesForCurrentThread();
            var result = run();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - start;
            GC.KeepAlive(result);
            return allocated;
        }
    }

    // The guard as it was, with the same patterns and options as TextCleanupService's generated expressions (which match
    // case-insensitively in the invariant culture), and a word set of every word.
    private static class Old
    {
        private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

        private static readonly Regex ReplyOpener = new(
            @"^\s*[""']?\s*(?:yes|yeah|yep|yup|sure\s+thing|sure|absolutely|definitely|certainly|of\s+course|no\s+problem|nope|nah|no|okay|ok|alright|all\s+right|indeed|agreed|understood|got\s+it|sounds\s+good|will\s+do|affirmative|you\s+bet|my\s+pleasure)\b",
            Options);

        private static readonly Regex AffirmationAnywhere = new(
            @"\b(?:yes|yeah|yep|yup|sure|absolutely|definitely|certainly|of\s+course|no\s+problem|nope|nah|no|okay|ok|alright|all\s+right|indeed|agreed|understood|got\s+it|sounds\s+good|will\s+do|affirmative|you\s+bet)\b",
            Options);

        private static readonly Regex ReplyOffer = new(
            @"\b(?:i\s+can\s+(?:help|assist)|i(?:'d|\s+would)\s+be\s+(?:happy|glad)\s+to|(?:happy|glad)\s+to\s+(?:help|assist)|how\s+(?:can|may)\s+i\s+(?:help|assist)|let\s+me\s+(?:help|assist)|i(?:'m|\s+am)\s+here\s+to\s+(?:help|assist)|is\s+there\s+anything\s+else\s+i)\b",
            Options);

        private static readonly Regex QuestionOpener = new(
            @"^\s*(?:who|what|what'?s|when|where|why|how|how'?s|which|whose|whom|do|does|did|is|are|am|was|were|can|could|will|would|should|shall|may|might|have|has|had|must)\b",
            Options);

        private static readonly Regex WordToken = new(@"[\p{L}\p{Nd}]+(?:'[\p{L}\p{Nd}]+)*");

        public static bool LooksLikeInventedReply(string? candidate, string original)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return false;
            }

            if (ReplyOffer.IsMatch(candidate) && !ReplyOffer.IsMatch(original))
            {
                return true;
            }

            if (ReplyOpener.IsMatch(candidate) && !AffirmationAnywhere.IsMatch(original))
            {
                return true;
            }

            var candidateWords = WordSet(candidate);
            if (candidateWords.Count is > 0 and <= 3 && !candidate.TrimEnd().EndsWith('?'))
            {
                if (LooksLikeQuestion(original))
                {
                    return true;
                }

                var originalWords = WordSet(original);
                if (originalWords.Count >= 4 && !candidate.Any(char.IsDigit))
                {
                    var shared = 0;
                    foreach (var word in candidateWords)
                    {
                        if (originalWords.Contains(word))
                        {
                            shared++;
                        }
                    }

                    if (shared * 2 < candidateWords.Count)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public static HashSet<string> WordSet(string text)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match match in WordToken.Matches(text))
            {
                set.Add(match.Value.ToLowerInvariant());
            }

            return set;
        }

        private static bool LooksLikeQuestion(string text) =>
            !string.IsNullOrWhiteSpace(text) && (text.TrimEnd().EndsWith('?') || QuestionOpener.IsMatch(text));
    }
}
