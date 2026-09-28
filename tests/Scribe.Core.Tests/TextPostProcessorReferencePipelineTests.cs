using Scribe.Core.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// The post-processor against a copy of the pipeline as it was before its rules collected their matches with
/// <see cref="Regex.EnumerateMatches(ReadOnlySpan{char})"/> into one list: the same regular expressions, but each rule a
/// <see cref="Regex.Matches(string)"/> iterator merged with SelectMany and sorted with LINQ. Every generated case must give the
/// same text and the same replacement records, in the same order, on both of the post-processor's rule paths.
/// </summary>
public sealed class TextPostProcessorReferencePipelineTests
{
    private static readonly string[] Words =
    [
        "azure", "Azure", "dev", "ops", "devops", "new", "york", "New", "York", "c#", ".net", "net", "gpt", "five", "GPT-5",
        "k8s", "a", "an", "the", "is", "on", "api", "API", "e-mail", "email", "co", "pilot", "copilot", "x", "xx", "sql",
        "SQL", "comma", "period",
    ];

    private static readonly string[] Separators = [" ", " ", " ", "  ", "\t", ", ", ". ", "? ", " ,", " .", "! ", ": ", "\n", " \t "];

    // The matcher's flag sets (0.5.1): none, and the prefilter.
    public static TheoryData<bool, string> ReuseAndMatcherFlags => new()
    {
        { false, "" },
        { true, "" },
        { false, PerfFlags.MatcherPrefilter },
        { true, PerfFlags.MatcherPrefilter },
    };

    public static TheoryData<string> MatcherFlagSets => new() { "", PerfFlags.MatcherPrefilter };

    [Theory]
    [MemberData(nameof(ReuseAndMatcherFlags))]
    public void Generated_dictations_match_the_reference_pipeline_record_for_record(bool reuse, string matcherFlags)
    {
        var random = new Random(reuse ? 20_260_927 : 20_260_928);
        for (var i = 0; i < 400; i++)
        {
            var dictionary = RandomEntries(random, random.Next(0, 14));
            var snippets = RandomSnippets(random);
            var processor = new TextPostProcessor(
                new DictionaryStub(dictionary), NullLogger<TextPostProcessor>.Instance, new SnippetStub(snippets),
                perfFlags: PerfFlags.Parse(matcherFlags))
            {
                ReuseIdenticalSourceScan = reuse,
            };
            var compiled = processor.Compile(dictionary, []);

            for (var j = 0; j < 6; j++)
            {
                var (text, source) = RandomTexts(random);
                var expected = Reference.Process(text, source, dictionary, snippets, reuse);
                var context = Describe(i, j, dictionary, snippets, text, source);

                AssertSameResult(expected, processor.ProcessDetailed(text, source, compiled), context + " (compiled rules)");
                AssertSameResult(expected, processor.ProcessDetailed(text, source), context + " (the processor's own rules)");
            }
        }
    }

    [Theory]
    [MemberData(nameof(MatcherFlagSets))]
    public void Generated_word_pack_entries_merged_under_the_dictionary_match_the_reference_pipeline(string matcherFlags)
    {
        var random = new Random(20_260_929);
        for (var i = 0; i < 200; i++)
        {
            var dictionary = RandomEntries(random, random.Next(0, 6));
            var library = RandomEntries(random, random.Next(1, 14));
            var snippets = RandomSnippets(random);
            var processor = new TextPostProcessor(
                new DictionaryStub(dictionary), NullLogger<TextPostProcessor>.Instance, new SnippetStub(snippets),
                perfFlags: PerfFlags.Parse(matcherFlags));
            var compiled = processor.Compile(dictionary, library);
            var effective = DictionaryLibraryComposer.Merge(dictionary, library);

            for (var j = 0; j < 4; j++)
            {
                var (text, source) = RandomTexts(random);
                var expected = Reference.Process(text, source, effective, snippets, reuse: true);
                AssertSameResult(
                    expected,
                    processor.ProcessDetailed(text, source, compiled),
                    Describe(i, j, effective, snippets, text, source));
            }
        }
    }

    [Fact]
    public void Applying_one_rule_matches_the_reference_pipeline()
    {
        var random = new Random(20_260_930);
        for (var i = 0; i < 600; i++)
        {
            var entry = RandomEntries(random, 1)[0];
            var (text, _) = RandomTexts(random);
            Assert.Equal(Reference.ApplyRule(text, entry), TextPostProcessor.ApplyRule(text, entry));
        }
    }

    [Theory]
    [MemberData(nameof(TieCases))]
    public void Ties_and_the_expansion_guard_resolve_as_the_reference_pipeline_resolves_them_on_every_matcher_path(
        string text, string firstPattern, string firstReplacement, string secondPattern, string secondReplacement)
    {
        AssertTies(text, firstPattern, firstReplacement, secondPattern, secondReplacement, PerfFlags.Parse(PerfFlags.MatcherPrefilter));
    }

    public static TheoryData<string, string, string, string, string> TieCases => new()
    {
        { "we flew to new york and york", "new york", "New York", "york", "York" },
        { "New York is not york", "york", "New York", "new", "NEW" },
        { "azure azure devops", "azure", "Azure", "azure", "AZURE" },
        { "hello comma world period", "comma", ",", "period", "." },
        { "use c# and .net on k8s", "c#", "C#", ".net", ".NET" },
        { "gpt five gpt five", "gpt five", "GPT-5", "gpt", "GPT" },
        { "\u212Aube kube KUBE", "kube", "Kube", "k", "K" },
    };

    [Theory]
    [InlineData("we flew to new york and york", "new york", "New York", "york", "York")]
    [InlineData("New York is not york", "york", "New York", "new", "NEW")]
    [InlineData("azure azure devops", "azure", "Azure", "azure", "AZURE")]
    [InlineData("hello comma world period", "comma", ",", "period", ".")]
    [InlineData("use c# and .net on k8s", "c#", "C#", ".net", ".NET")]
    [InlineData("gpt five gpt five", "gpt five", "GPT-5", "gpt", "GPT")]
    public void Ties_and_the_expansion_guard_resolve_as_the_reference_pipeline_resolves_them(
        string text, string firstPattern, string firstReplacement, string secondPattern, string secondReplacement) =>
        AssertTies(text, firstPattern, firstReplacement, secondPattern, secondReplacement, PerfFlags.None);

    private static void AssertTies(
        string text, string firstPattern, string firstReplacement, string secondPattern, string secondReplacement, PerfFlags flags)
    {
        DictionaryEntry[] dictionary =
        [
            new(1, firstPattern, firstReplacement),
            new(2, secondPattern, secondReplacement),
            new(3, secondPattern, secondReplacement.ToLowerInvariant(), WholeWord: false),
        ];
        var processor = new TextPostProcessor(new DictionaryStub(dictionary), NullLogger<TextPostProcessor>.Instance, perfFlags: flags);
        var compiled = processor.Compile(dictionary, []);

        foreach (var source in new[] { null, text, text.ToUpperInvariant() })
        {
            AssertSameResult(
                Reference.Process(text, source, dictionary, [], reuse: true),
                processor.ProcessDetailed(text, source, compiled),
                $"text '{text}', source '{source}'");
        }
    }

    private static void AssertSameResult(TextPostProcessingResult expected, TextPostProcessingResult actual, string context)
    {
        Assert.True(
            string.Equals(expected.Text, actual.Text, StringComparison.Ordinal),
            $"{context}: text '{Escape(actual.Text)}', the reference pipeline gave '{Escape(expected.Text)}'.");
        Assert.True(
            expected.Replacements.SequenceEqual(actual.Replacements),
            $"{context}: replacements [{string.Join("; ", actual.Replacements)}], the reference pipeline gave " +
            $"[{string.Join("; ", expected.Replacements)}].");
    }

    private static string Describe(
        int round, int text, IReadOnlyList<DictionaryEntry> dictionary, IReadOnlyList<Snippet> snippets, string input, string? source) =>
        $"case {round}.{text}, rules [{string.Join("; ", dictionary.Select(e => $"'{Escape(e.Pattern)}' to '{Escape(e.Replacement)}' whole {e.WholeWord}"))}], " +
        $"snippets [{string.Join("; ", snippets.Select(s => $"'{Escape(s.Phrase)}' to '{Escape(s.Template)}'"))}], " +
        $"text '{Escape(input)}', source '{(source is null ? "null" : Escape(source))}'";

    private static string Escape(string value) => value.Replace("\n", "\\n").Replace("\t", "\\t");

    private static List<DictionaryEntry> RandomEntries(Random random, int count)
    {
        var entries = new List<DictionaryEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var pattern = Phrase(random, random.Next(1, 3));
            var replacement = random.Next(9) switch
            {
                0 => pattern.ToUpperInvariant(),
                1 => "New " + pattern,
                2 => pattern + " City",
                3 => ",",
                4 => ".",
                5 => string.Empty,
                6 => pattern,
                7 => pattern.Length > 1 ? char.ToUpperInvariant(pattern[0]) + pattern[1..] : pattern,
                _ => Words[random.Next(Words.Length)],
            };
            entries.Add(new DictionaryEntry(i + 1, pattern, replacement, WholeWord: random.Next(3) != 0));
        }

        return entries;
    }

    private static List<Snippet> RandomSnippets(Random random)
    {
        var count = random.Next(0, 3);
        var snippets = new List<Snippet>(count);
        for (var i = 0; i < count; i++)
        {
            var template = random.Next(3) switch
            {
                0 => Phrase(random, random.Next(1, 5)),
                1 => "Regards,\n" + Phrase(random, 2),
                _ => "\t" + Phrase(random, 3) + "  end",
            };
            snippets.Add(new Snippet(i + 1, Phrase(random, random.Next(1, 3)), template));
        }

        return snippets;
    }

    private static (string Text, string? Source) RandomTexts(Random random)
    {
        var words = RandomWords(random);
        var text = Join(random, words);
        string? source = random.Next(4) switch
        {
            0 => null,
            1 => text,
            2 => Join(random, [.. words.Select(word => random.Next(2) == 0 ? word.ToLowerInvariant() : word)]),
            _ => Join(random, RandomWords(random)),
        };
        return (text, source);
    }

    private static string[] RandomWords(Random random) =>
        [.. Enumerable.Range(0, random.Next(1, 20)).Select(_ => Words[random.Next(Words.Length)])];

    private static string Phrase(Random random, int words) =>
        string.Join(' ', Enumerable.Range(0, words).Select(_ => Words[random.Next(Words.Length)]));

    private static string Join(Random random, string[] words)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < words.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(Separators[random.Next(Separators.Length)]);
            }

            builder.Append(words[i]);
        }

        if (random.Next(3) == 0)
        {
            builder.Append(". ");
        }

        return builder.ToString();
    }

    // The pipeline as it was: TextPostProcessor's normalization, snippet and dictionary passes, source pass and located
    // replacements, with every rule's matches found by a Regex.Matches iterator and merged by SelectMany.
    private static class Reference
    {
        private const RegexOptions MatchOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        private static readonly Regex HorizontalWhitespace = new(@"[ \t\f\v]+");
        private static readonly Regex SpaceBeforePunctuation = new(@"[ \t]+([,.!?;:])(?![\p{L}\p{N}])");

        public static TextPostProcessingResult Process(
            string text,
            string? sourceText,
            IReadOnlyList<DictionaryEntry> dictionary,
            IReadOnlyList<Snippet> snippets,
            bool reuse)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return new TextPostProcessingResult(string.Empty, []);
            }

            var rules = dictionary.Where(entry => !string.IsNullOrEmpty(entry.Pattern)).Select(entry => new Rule(entry)).ToArray();
            var snippetRules = snippets
                .Where(snippet => !string.IsNullOrWhiteSpace(snippet.Phrase) && !string.IsNullOrEmpty(snippet.Template))
                .Select(snippet => new SnippetRule(snippet))
                .ToArray();

            var normalized = NormalizeWhitespace(text);
            var source = string.IsNullOrWhiteSpace(sourceText)
                ? null
                : reuse && string.Equals(sourceText, text, StringComparison.Ordinal)
                    ? normalized
                    : NormalizeWhitespace(sourceText);

            var snippetApplications = new List<TextReplacement>();
            var dictionaryInput = ApplySinglePass(
                normalized,
                snippetRules.SelectMany((rule, order) => rule.Find(normalized, order)),
                snippetApplications,
                TextReplacementKind.Snippet);

            var replacements = new List<TextReplacement>();
            var output = ApplySinglePass(
                dictionaryInput,
                rules.SelectMany((rule, order) => rule.Find(dictionaryInput, order)),
                replacements);

            var sourceTraces = reuse &&
                source is not null &&
                string.Equals(source, dictionaryInput, StringComparison.Ordinal)
                    ? replacements.ToArray()
                    : null;

            var canonicalSnippets = snippetApplications.Select(application =>
            {
                var canonical = ApplySinglePass(
                    application.Replacement,
                    rules.SelectMany((rule, order) => rule.Find(application.Replacement, order)));
                return application with { Length = canonical.Length, Replacement = canonical };
            });
            AddLocatedReplacements(output, canonicalSnippets, replacements, replaceOverlaps: true);

            if (source is not null)
            {
                IReadOnlyList<TextReplacement> glossaryApplications =
                    sourceTraces is not null ? sourceTraces : ScanSource(source, rules);
                AddLocatedReplacements(output, glossaryApplications, replacements, replaceOverlaps: false);
            }

            return new TextPostProcessingResult(
                output,
                replacements.OrderBy(replacement => replacement.Start).ToList());
        }

        public static string ApplyRule(string? text, DictionaryEntry? entry)
        {
            if (string.IsNullOrEmpty(text) || entry is null || string.IsNullOrWhiteSpace(entry.Pattern))
            {
                return text ?? string.Empty;
            }

            var rule = new Rule(entry with { Pattern = entry.Pattern.Trim() });
            var normalized = NormalizeWhitespace(text);
            return ApplySinglePass(normalized, rule.Find(normalized, 0));
        }

        private static List<TextReplacement> ScanSource(string source, Rule[] rules)
        {
            var glossaryApplications = new List<TextReplacement>();
            _ = ApplySinglePass(
                source,
                rules.SelectMany((rule, order) => rule.Find(source, order)),
                glossaryApplications);
            return glossaryApplications;
        }

        private static string NormalizeWhitespace(string text)
        {
            text = HorizontalWhitespace.Replace(text, " ");
            text = SpaceBeforePunctuation.Replace(text, "$1");
            return text.Trim();
        }

        private static string ApplySinglePass(
            string text,
            IEnumerable<Candidate> candidates,
            List<TextReplacement>? replacements = null,
            TextReplacementKind kind = TextReplacementKind.Dictionary)
        {
            var selected = candidates
                .OrderBy(candidate => candidate.Index)
                .ThenByDescending(candidate => candidate.Length)
                .ThenBy(candidate => candidate.RuleOrder)
                .ToList();
            if (selected.Count == 0)
            {
                return text;
            }

            var builder = new StringBuilder(text.Length);
            var position = 0;
            foreach (var candidate in selected)
            {
                if (candidate.Index < position)
                {
                    continue;
                }

                var prefixLength = candidate.Index - position;
                if (prefixLength > 0 &&
                    char.IsWhiteSpace(text[candidate.Index - 1]) &&
                    candidate.Replacement.Length > 0 &&
                    candidate.Replacement.All(IsTightPunctuation))
                {
                    prefixLength--;
                }

                builder.Append(text, position, prefixLength);
                var replacementStart = builder.Length;
                builder.Append(candidate.Replacement);
                if (replacements is not null &&
                    !string.Equals(candidate.Original, candidate.Replacement, StringComparison.Ordinal))
                {
                    replacements.Add(new TextReplacement(
                        replacementStart,
                        candidate.Replacement.Length,
                        candidate.Pattern,
                        candidate.Replacement,
                        kind));
                }
                position = candidate.Index + candidate.Length;
            }

            builder.Append(text, position, text.Length - position);
            return builder.ToString();
        }

        private static void AddLocatedReplacements(
            string text,
            IEnumerable<TextReplacement> traces,
            List<TextReplacement> replacements,
            bool replaceOverlaps)
        {
            var searchStart = 0;
            foreach (var trace in traces)
            {
                if (trace.Replacement.Length == 0)
                {
                    continue;
                }

                var index = text.IndexOf(trace.Replacement, searchStart, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                {
                    index = text.IndexOf(trace.Replacement, StringComparison.OrdinalIgnoreCase);
                }
                if (index < 0)
                {
                    continue;
                }

                var end = index + trace.Replacement.Length;
                var overlaps = replacements
                    .Where(existing => existing.Start < end && index < existing.Start + existing.Length)
                    .ToList();
                if (overlaps.Count > 0)
                {
                    if (!replaceOverlaps)
                    {
                        continue;
                    }
                    replacements.RemoveAll(overlaps.Contains);
                }

                replacements.Add(trace with { Start = index, Length = trace.Replacement.Length });
                searchStart = end;
            }
        }

        private static bool IsTightPunctuation(char value) => value is ',' or '.' or '!' or '?' or ';' or ':';

        private sealed record Candidate(int Index, int Length, string Replacement, int RuleOrder, string Pattern, string Original);

        private sealed class SnippetRule(Snippet snippet)
        {
            private readonly Regex _regex = new(
                $@"(?<!\w){Regex.Escape(snippet.Phrase.Trim())}(?!\w)[.!?,;:]?", MatchOptions);

            public IEnumerable<Candidate> Find(string text, int order) =>
                _regex.Matches(text).Select(match =>
                    new Candidate(match.Index, match.Length, snippet.Template, order, snippet.Phrase, match.Value));
        }

        private sealed class Rule
        {
            private readonly Regex _regex;
            private readonly string _pattern;
            private readonly string _replacement;
            private readonly bool _replacementContainsPattern;

            public Rule(DictionaryEntry entry)
            {
                _pattern = entry.Pattern;
                _replacement = entry.Replacement;
                var escaped = Regex.Escape(entry.Pattern);
                _regex = new Regex(entry.WholeWord ? $@"(?<!\w){escaped}(?!\w)" : escaped, MatchOptions);
                _replacementContainsPattern =
                    !string.IsNullOrEmpty(entry.Pattern) &&
                    _replacement.Length > entry.Pattern.Length &&
                    _replacement.Contains(entry.Pattern, StringComparison.OrdinalIgnoreCase);
            }

            public IEnumerable<Candidate> Find(string text, int order)
            {
                var canonicalStarts = _replacementContainsPattern ? CollectReplacementStarts(text) : [];
                foreach (Match match in _regex.Matches(text))
                {
                    var replacement = canonicalStarts.Count > 0 &&
                        IsInsideAnyReplacement(canonicalStarts, match.Index, match.Length)
                        ? match.Value
                        : _replacement;
                    yield return new Candidate(match.Index, match.Length, replacement, order, _pattern, match.Value);
                }
            }

            private List<int> CollectReplacementStarts(string text)
            {
                var starts = new List<int>();
                var from = 0;
                while (from <= text.Length - _replacement.Length)
                {
                    var idx = text.IndexOf(_replacement, from, StringComparison.OrdinalIgnoreCase);
                    if (idx < 0)
                    {
                        break;
                    }

                    starts.Add(idx);
                    from = idx + 1;
                }

                return starts;
            }

            private bool IsInsideAnyReplacement(List<int> starts, int matchStart, int matchLength)
            {
                var matchEnd = matchStart + matchLength;
                foreach (var idx in starts)
                {
                    if (idx > matchStart)
                    {
                        break;
                    }

                    if (matchEnd <= idx + _replacement.Length)
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }

    internal sealed class DictionaryStub(IReadOnlyList<DictionaryEntry> entries) : IDictionaryRepository
    {
        public IReadOnlyList<DictionaryEntry> GetAll() => entries;
        public IReadOnlyList<DictionaryEntry> GetEnabled() => entries;
        public DictionaryEntry Add(DictionaryEntry entry) => throw new NotSupportedException();
        public IReadOnlyList<DictionaryEntry> AddRange(IReadOnlyList<DictionaryEntry> added) => throw new NotSupportedException();
        public void Update(DictionaryEntry entry) => throw new NotSupportedException();
        public void Delete(long id) => throw new NotSupportedException();
        public void SaveAll(IReadOnlyList<DictionaryEntry> updated) => throw new NotSupportedException();
        public int SeedIfEmpty(IEnumerable<DictionaryEntry> seed) => throw new NotSupportedException();
        public int DisableUnmodifiedEntries(IEnumerable<DictionaryEntry> retired) => throw new NotSupportedException();
    }

    internal sealed class SnippetStub(IReadOnlyList<Snippet> snippets) : ISnippetRepository
    {
        public IReadOnlyList<Snippet> GetAll() => snippets;
        public IReadOnlyList<Snippet> GetEnabled() => snippets;
        public void SaveAll(IReadOnlyList<Snippet> updated) => throw new NotSupportedException();
    }
}
