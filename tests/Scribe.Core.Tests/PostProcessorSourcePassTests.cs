using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// Pins how <see cref="TextPostProcessor.ProcessDetailed"/> reports replacements when a source
/// transcript is supplied. The oracle comes from an executed probe against the real matcher: the
/// source pass changes highlight metadata even when it cannot change the text, so these assert full
/// replacement records (start, length, pattern, replacement, kind), not just the output string.
/// </summary>
public sealed class PostProcessorSourcePassTests
{
    private static TextReplacement Azure(int start) =>
        new(start, 5, "azure", "Azure", TextReplacementKind.Dictionary);

    // Every oracle case runs against the original algorithm (reuse off) and the shipping one, so a
    // change to either is caught against the same executed probe.
    private static (TextPostProcessor Processor, ScribeDatabase Db) CreateAzure(bool reuse)
    {
        var db = ScribeDatabase.CreateInMemory();
        var repo = new DictionaryRepository(db);
        repo.SeedIfEmpty([DictionaryEntry.New("azure", "Azure")]);
        return (new TextPostProcessor(repo, NullLogger<TextPostProcessor>.Instance)
        {
            ReuseIdenticalSourceScan = reuse,
        }, db);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Source_equal_to_text_also_reports_the_already_canonical_occurrence(bool reuse)
    {
        var (processor, db) = CreateAzure(reuse);
        using (db)
        {
            var result = processor.ProcessDetailed("Azure azure", "Azure azure");

            Assert.Equal("Azure Azure", result.Text);
            Assert.Equal(new[] { Azure(0), Azure(6) }, result.Replacements);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Without_a_source_only_the_changed_occurrence_is_reported(bool reuse)
    {
        var (processor, db) = CreateAzure(reuse);
        using (db)
        {
            var result = processor.ProcessDetailed("Azure azure");

            Assert.Equal("Azure Azure", result.Text);
            Assert.Equal(new[] { Azure(6) }, result.Replacements);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Two_raw_occurrences_report_both_spans_with_or_without_a_source(bool reuse)
    {
        var (processor, db) = CreateAzure(reuse);
        using (db)
        {
            var withSource = processor.ProcessDetailed("azure azure", "azure azure");
            var withoutSource = processor.ProcessDetailed("azure azure");

            Assert.Equal("Azure Azure", withSource.Text);
            Assert.Equal(new[] { Azure(0), Azure(6) }, withSource.Replacements);
            Assert.Equal("Azure Azure", withoutSource.Text);
            Assert.Equal(new[] { Azure(0), Azure(6) }, withoutSource.Replacements);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Already_canonical_text_reports_nothing_with_or_without_an_equal_source(bool reuse)
    {
        var (processor, db) = CreateAzure(reuse);
        using (db)
        {
            var withSource = processor.ProcessDetailed("Azure Azure", "Azure Azure");
            var withoutSource = processor.ProcessDetailed("Azure Azure");

            Assert.Equal("Azure Azure", withSource.Text);
            Assert.Empty(withSource.Replacements);
            Assert.Equal("Azure Azure", withoutSource.Text);
            Assert.Empty(withoutSource.Replacements);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cleaned_text_reports_terms_the_raw_source_needed_only_when_the_source_is_supplied(bool reuse)
    {
        var (processor, db) = CreateAzure(reuse);
        using (db)
        {
            var withSource = processor.ProcessDetailed("Azure Azure", "azure azure");
            var withoutSource = processor.ProcessDetailed("Azure Azure");

            Assert.Equal("Azure Azure", withSource.Text);
            Assert.Equal(new[] { Azure(0), Azure(6) }, withSource.Replacements);
            Assert.Equal("Azure Azure", withoutSource.Text);
            Assert.Empty(withoutSource.Replacements);
        }
    }

    [Fact]
    public void Source_equal_to_the_snippet_expanded_text_reports_the_same_records_as_a_full_rescan()
    {
        // The one shape where the source matches the dictionary input AND snippet projection has
        // edited the replacement list: the reused traces must be the ones recorded before that edit.
        using var db = ScribeDatabase.CreateInMemory();
        using var emptyDb = ScribeDatabase.CreateInMemory();
        var processors = CreateProcessors(db, emptyDb);

        var expected = processors.Rescanning.ProcessDetailed("please brb azure", "please be right back azure");
        var actual = processors.Reusing.ProcessDetailed("please brb azure", "please be right back azure");

        Assert.Equal("please be right back Azure", actual.Text);
        Assert.Equal(
            new[]
            {
                new TextReplacement(7, 13, "brb", "be right back", TextReplacementKind.Snippet),
                new TextReplacement(21, 5, "azure", "Azure", TextReplacementKind.Dictionary),
            },
            actual.Replacements);
        AssertSameResult(expected, actual);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_source_pass_replays_the_traces_copied_before_the_snippet_projection(bool reuse)
    {
        // The risky shape for the trace reuse. The source is the snippet-expanded dictionary input,
        // and the snippet projection locates "be right back" at the user's own words (0), not at the
        // expansion (20). A trace copy taken after that edit would replay the snippet record as a
        // source trace and add a spurious (20, 13) snippet span.
        using var db = ScribeDatabase.CreateInMemory();
        var dictionary = new DictionaryRepository(db);
        dictionary.SeedIfEmpty([DictionaryEntry.New("azure", "Azure")]);
        var snippets = new SnippetRepository(db);
        snippets.SaveAll([Snippet.New("brb", "be right back")]);
        var processor = new TextPostProcessor(dictionary, NullLogger<TextPostProcessor>.Instance, snippets)
        {
            ReuseIdenticalSourceScan = reuse,
        };

        var result = processor.ProcessDetailed(
            "be right back Azure brb azure", "be right back Azure be right back azure");

        Assert.Equal("be right back Azure be right back Azure", result.Text);
        Assert.Equal(
            new[]
            {
                new TextReplacement(0, 13, "brb", "be right back", TextReplacementKind.Snippet),
                new TextReplacement(14, 5, "azure", "Azure", TextReplacementKind.Dictionary),
                new TextReplacement(34, 5, "azure", "Azure", TextReplacementKind.Dictionary),
            },
            result.Replacements);
    }

    [Fact]
    public void Reusing_an_identical_source_scan_matches_a_full_rescan_for_generated_dictations()
    {
        // Differential check of ProcessDetailed's two reuses against the original algorithm: a source
        // equal to the text is not normalized again, and a normalized source equal to the dictionary
        // input is not rescanned. Inputs mix overlapping rules, longest-match and order precedence,
        // a replacement that embeds its own pattern, punctuation-only replacements, a partial-word
        // rule, literal dollar signs, snippets whose template contains dictionary terms, a snippet
        // trigger that is also a dictionary pattern, and whitespace the normalizer rewrites. Each
        // text is also paired with its own snippet-expanded form as the source, the shape where the
        // snippet projection edits the list the reused traces were copied from.
        using var db = ScribeDatabase.CreateInMemory();
        using var emptyDb = ScribeDatabase.CreateInMemory();
        var processors = CreateProcessors(db, emptyDb);
        var random = new Random(20260922);
        var reusedShapes = 0;
        var snippetExpandedShapes = 0;

        for (var iteration = 0; iteration < 1500; iteration++)
        {
            var raw = GenerateDictation(random, allowSnippetTriggers: iteration % 2 == 0);
            var cleaned = processors.Rescanning.ProcessDetailed(raw).Text;
            var unrelated = GenerateDictation(random, allowSnippetTriggers: true);

            foreach (var text in new[] { raw, cleaned })
            {
                var dictionaryInput = processors.SnippetsOnly.ProcessDetailed(text).Text;
                string?[] sources =
                [
                    null,
                    "   ",
                    raw,
                    new string(raw.AsSpan()),
                    raw.Replace(" ", "  \t", StringComparison.Ordinal),
                    raw.ToUpperInvariant(),
                    cleaned,
                    unrelated,
                    dictionaryInput,
                ];

                foreach (var source in sources)
                {
                    var expected = processors.Rescanning.ProcessDetailed(text, source);
                    var actual = processors.Reusing.ProcessDetailed(text, source);
                    AssertSameResult(expected, actual);

                    // The trace reuse engages exactly when the normalized source is the dictionary input.
                    var normalizedSource = string.IsNullOrWhiteSpace(source)
                        ? null
                        : processors.Plain.ProcessDetailed(source).Text;
                    if (string.Equals(normalizedSource, dictionaryInput, StringComparison.Ordinal))
                    {
                        reusedShapes++;
                        if (!string.Equals(dictionaryInput, processors.Plain.ProcessDetailed(text).Text, StringComparison.Ordinal))
                        {
                            snippetExpandedShapes++;
                        }
                    }
                }
            }
        }

        // Guards against a generator change that silently stops exercising the reuse, and in
        // particular the snippet-expanded shape (6,001 and 831 with this seed).
        Assert.True(reusedShapes >= 4_000, $"{reusedShapes} reused-trace shapes were generated");
        Assert.True(snippetExpandedShapes >= 500, $"{snippetExpandedShapes} snippet-expanded reuse shapes were generated");
    }

    // "be right back" and "big apple" are snippet templates: spoken literally ahead of their trigger,
    // they make the snippet projection locate a template at the user's own words, the shape a trace
    // copy taken after that projection would get wrong.
    private static readonly string[] Vocabulary =
    [
        "azure", "Azure", "AZURE", "azure devops", "Azure DevOps", "new", "york", "new york", "New York",
        "old", "c sharp", "C#", "dot net", ".NET", "comma", ",", "foo", "foobar", "barfoo", "price", "$5",
        "api", "API", "sql", "github", "GitHub", "hello", "world", "the", "and", ".", "!", "?", "rapid",
        "be right back", "big apple",
    ];

    private static readonly string[] SnippetTriggers = ["brb", "insert sig", "Insert sig.", "new york"];

    private static readonly string[] Separators = [" ", " ", " ", "  ", "\t", " \t "];

    private static string GenerateDictation(Random random, bool allowSnippetTriggers)
    {
        var count = random.Next(1, 18);
        var builder = new System.Text.StringBuilder();
        for (var index = 0; index < count; index++)
        {
            if (index > 0)
            {
                builder.Append(Separators[random.Next(Separators.Length)]);
            }

            builder.Append(allowSnippetTriggers && random.Next(6) == 0
                ? SnippetTriggers[random.Next(SnippetTriggers.Length)]
                : Vocabulary[random.Next(Vocabulary.Length)]);
        }

        return builder.ToString();
    }

    private sealed record Processors(
        TextPostProcessor Reusing,
        TextPostProcessor Rescanning,
        TextPostProcessor SnippetsOnly,
        TextPostProcessor Plain);

    private static Processors CreateProcessors(ScribeDatabase db, ScribeDatabase emptyDb)
    {
        var dictionary = new DictionaryRepository(db);
        dictionary.SeedIfEmpty(
        [
            DictionaryEntry.New("azure", "Azure"),
            DictionaryEntry.New("azure devops", "Azure DevOps"),
            DictionaryEntry.New("new", "old"),
            DictionaryEntry.New("new york", "NYC"),
            DictionaryEntry.New("old", "OLDER"),
            DictionaryEntry.New("york", "New York"),
            DictionaryEntry.New("c sharp", "C#"),
            DictionaryEntry.New("dot net", ".NET"),
            DictionaryEntry.New("comma", ","),
            DictionaryEntry.New("foo", "bar", wholeWord: false),
            DictionaryEntry.New("price", "$5"),
            DictionaryEntry.New("api", "API"),
            DictionaryEntry.New("sql", "SQL"),
            DictionaryEntry.New("github", "GitHub"),
        ]);
        var snippets = new SnippetRepository(db);
        snippets.SaveAll(
        [
            Snippet.New("brb", "be right back"),
            Snippet.New("insert sig", "Regards,\n\tthe azure  team"),
            Snippet.New("new york", "big apple"),
        ]);
        var noDictionary = new DictionaryRepository(emptyDb);

        return new Processors(
            new TextPostProcessor(dictionary, NullLogger<TextPostProcessor>.Instance, snippets),
            new TextPostProcessor(dictionary, NullLogger<TextPostProcessor>.Instance, snippets)
            {
                ReuseIdenticalSourceScan = false,
            },
            // With no dictionary, the output is exactly the snippet-expanded input the dictionary
            // pass scans; with no rules at all, it is exactly the normalized text.
            new TextPostProcessor(noDictionary, NullLogger<TextPostProcessor>.Instance, snippets),
            new TextPostProcessor(noDictionary, NullLogger<TextPostProcessor>.Instance));
    }

    private static void AssertSameResult(TextPostProcessingResult expected, TextPostProcessingResult actual)
    {
        Assert.Equal(expected.Text, actual.Text);
        Assert.Equal(expected.Replacements, actual.Replacements);
    }
}
