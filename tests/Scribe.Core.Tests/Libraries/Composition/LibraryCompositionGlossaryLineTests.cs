using Scribe.Core.Cleanup;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;
using Xunit.Abstractions;
using static Scribe.Core.Tests.Libraries.Composition.Lib;

namespace Scribe.Core.Tests.Libraries.Composition;

/// <summary>
/// The glossary inclusion splits the renderer's text and builds each line's key over spans rather than through
/// substrings (ledger LB4). Every line, every key and every inclusion must be what the substring versions gave; the
/// references below are those versions, copied from 10c9a0b.
/// </summary>
[Collection(AllocationMeasurementCollection.Name)]
public sealed class LibraryCompositionGlossaryLineTests(ITestOutputHelper output)
{
    public static TheoryData<string> SplitInputs =>
    [
        string.Empty, "a", "\n", "a\n", "\na", "a\nb", "a\n\nb", "a\nb\n", "\n\n", "header only\n", "header\n- one", "header\n- one\n- two",
        "header\r\n- one\r\n", "no newline at all",
    ];

    public static TheoryData<string> KeyInputs =>
    [
        "- GitHub", "GitHub", "- ", string.Empty, "-", "- a (transcribed as \"b\")", "a (transcribed as \"b\")", "- a|b",
        "- a (transcribed as \"\")", "- (transcribed as \"x\")", "- a (transcribed as \"b\") (transcribed as \"c\")",
        "- GitHub (transcribed as \"get hub\"", "- x (transcribed as \"y\") trailing", "- dash - inside (transcribed as \"dash\")",
    ];

    [Theory]
    [MemberData(nameof(SplitInputs))]
    public void Rendered_lines_split_exactly_as_the_substring_split_did(string glossary) =>
        Assert.Equal(ReferenceRenderedLines(glossary), LibraryComposition.RenderedLines(glossary));

    [Theory]
    [MemberData(nameof(KeyInputs))]
    public void Glossary_keys_are_the_ones_the_substring_version_built(string line) =>
        Assert.Equal(ReferenceGlossaryKey(line), LibraryComposition.GlossaryKey(line));

    [Fact]
    public void A_key_whose_spoken_form_would_overlap_the_separator_still_throws()
    {
        // No line the renderer gives reaches this (its written and spoken forms are never empty and never hold a quote), but
        // the exception must be the one the substring version threw.
        const string line = "- x (transcribed as \")";
        Assert.Throws<ArgumentOutOfRangeException>(() => ReferenceGlossaryKey(line));
        Assert.Throws<ArgumentOutOfRangeException>(() => LibraryComposition.GlossaryKey(line));
    }

    [Fact]
    public void Every_shipped_term_renders_the_same_line_and_key_as_before()
    {
        var entries = BuiltInDictionaryLibraries.All.SelectMany(library => library.Entries).ToList();
        entries.AddRange(
        [
            DictionaryEntry.New("only quotes", "\"\""),
            DictionaryEntry.New("get hub", "GitHub"),
            DictionaryEntry.New("GET HUB", "GitHub"),
            DictionaryEntry.New("multi", "first\nsecond"),
            DictionaryEntry.New("same", "same"),
            new DictionaryEntry(0, "off", "Off", Enabled: false),
        ]);

        var lines = LibraryComposition.GlossaryLines(entries);

        Assert.Equal(ReferenceGlossaryLines(entries), lines);
        foreach (var line in lines.OfType<string>())
        {
            Assert.Equal(ReferenceGlossaryKey(line), LibraryComposition.GlossaryKey(line));
        }
    }

    [Fact]
    public void The_first_status_renders_a_thousand_lines_with_one_string_per_key()
    {
        var rows = Enumerable.Range(0, 1_000)
            .Select(i => Custom(FormattableString.Invariant($"term {i:D4}"), FormattableString.Invariant($"Term{i:D4}")))
            .ToArray();
        var catalog = Catalog(
            State(enabled: ["team"], ai: [("team", true)], accepted: [("team", H1)]), Committed(CustomLibrary("team", rows), H1));
        var key = Key("term 0500");
        IReadOnlyList<DictionaryEntry> dictionary = [];

        long Measure()
        {
            var composition = LibraryComposition.Committed(catalog, dictionary, new GlossaryBudget(80));
            var before = GC.GetAllocatedBytesForCurrentThread();
            var status = composition.StatusOf("team", key);
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(GlossaryInclusion.OverBudget, status.Glossary);
            return bytes;
        }

        for (var i = 0; i < 3; i++)
        {
            Measure();
        }

        var bytes = Math.Min(Measure(), Math.Min(Measure(), Measure()));
        output.WriteLine($"First StatusOf with the glossary, 1,000 lines: {bytes} bytes");

        // The first status of a permitted row renders every line of the vocabulary. At 10c9a0b each 100-line chunk's text was
        // copied without its header before being split, each line with a spoken form took four strings to key, and the
        // chunks, the key set and the inclusion map grew from empty. Measured on x64: 1,507,144 bytes before and 1,113,912
        // after; the bound sits halfway. The renderer itself (CleanupPrompt) is unchanged and is most of what remains.
        Assert.True(bytes <= 1_310_000, $"The first status allocated {bytes} bytes; the bound is 1,310,000.");
    }

    // 10c9a0b's LibraryComposition.RenderedLines.
    private static string[] ReferenceRenderedLines(string glossary) =>
        glossary.Length == 0 ? [] : glossary[(glossary.IndexOf('\n') + 1)..].Split('\n');

    // 10c9a0b's LibraryComposition.GlossaryKey.
    private static string ReferenceGlossaryKey(string line)
    {
        const string separator = " (transcribed as \"";
        var body = line.StartsWith("- ", StringComparison.Ordinal) ? line[2..] : line;
        var at = body.IndexOf(separator, StringComparison.Ordinal);
        return at >= 0 && body.EndsWith("\")", StringComparison.Ordinal)
            ? body[..at] + "|" + body[(at + separator.Length)..^2]
            : body;
    }

    // 10c9a0b's LibraryComposition.GlossaryLines and GlossaryLine.
    private static string?[] ReferenceGlossaryLines(IReadOnlyList<DictionaryEntry> entries)
    {
        const int chunkSize = 100;
        var lines = new string?[entries.Count];
        var chunk = new List<int>(chunkSize);
        for (var i = 0; i <= entries.Count; i++)
        {
            if (i < entries.Count)
            {
                var entry = entries[i];
                if (entry is null || !entry.Enabled || !CleanupPrompt.IsVocabularyReplacement(entry.Replacement))
                {
                    continue;
                }

                chunk.Add(i);
                if (chunk.Count < chunkSize)
                {
                    continue;
                }
            }

            if (chunk.Count == 0)
            {
                continue;
            }

            var rendered = ReferenceRenderedLines(CleanupPrompt.BuildGlossary([.. chunk.Select(index => entries[index])], chunk.Count));
            for (var k = 0; k < chunk.Count; k++)
            {
                lines[chunk[k]] = rendered.Length == chunk.Count ? rendered[k] : ReferenceGlossaryLine(entries[chunk[k]]);
            }

            chunk.Clear();
        }

        return lines;
    }

    private static string? ReferenceGlossaryLine(DictionaryEntry entry)
    {
        if (!entry.Enabled || !CleanupPrompt.IsVocabularyReplacement(entry.Replacement))
        {
            return null;
        }

        var rendered = ReferenceRenderedLines(CleanupPrompt.BuildGlossary([entry], 1));
        return rendered.Length == 0 ? null : rendered[0];
    }
}
