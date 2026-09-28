using System.Reflection;
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
        // chunks, the key set and the inclusion map grew from empty. Measured on x64 on the integrated branch, which includes
        // TX-7's change to the glossary builder (d1103b2): 788,952 bytes, and 1,182,184 with 4d5d89e's production changes
        // reverted; the bound sits halfway.
        Assert.True(bytes <= 985_000, $"The first status allocated {bytes} bytes; the bound is 985,000.");
    }

    // Review round 1, item 2: the first-status bound above measures the renderer, the keys and the maps together, so the
    // substring key alone stayed under it. Each part LB4 changed is measured or checked on its own from here on.
    [Fact]
    public void A_glossary_key_allocates_only_the_key_itself()
    {
        var lines = KeyCorpus();
        var keys = new string[lines.Length];
        object SpanKeys()
        {
            for (var i = 0; i < lines.Length; i++)
            {
                keys[i] = LibraryComposition.GlossaryKey(lines[i]);
            }

            return keys;
        }

        object SubstringKeys()
        {
            for (var i = 0; i < lines.Length; i++)
            {
                keys[i] = ReferenceGlossaryKey(lines[i]);
            }

            return keys;
        }

        Assert.Equal(lines.Select(ReferenceGlossaryKey), lines.Select(LibraryComposition.GlossaryKey));
        var bytes = Measure(SpanKeys);
        var substring = Measure(SubstringKeys);
        output.WriteLine($"GlossaryKey over {lines.Length} lines: {bytes} bytes (the substring version: {substring})");

        // A line with a spoken form takes one string, its key; one without takes its body; one with no prefix takes nothing.
        // The substring version also copied each body and both halves of a spoken line. Measured on x64 over this corpus:
        // 76,000 bytes, and 252,000 for the substring version; the bound sits halfway.
        Assert.True(bytes <= 164_000, $"Keying {lines.Length} lines allocated {bytes} bytes; the bound is 164,000.");
    }

    [Fact]
    public void Rendered_lines_allocate_only_the_lines_and_their_array()
    {
        var glossary = "Vocabulary:\n" + string.Join("\n", KeyCorpus().Take(1_000));

        Assert.Equal(ReferenceRenderedLines(glossary), LibraryComposition.RenderedLines(glossary));
        var bytes = Measure(() => LibraryComposition.RenderedLines(glossary));
        var substring = Measure(() => ReferenceRenderedLines(glossary));
        output.WriteLine($"RenderedLines over 1,000 lines: {bytes} bytes (the substring version: {substring})");

        // The lines and the array that holds them; the substring version first copied the whole text after the header.
        // Measured on x64: 112,024 bytes, and 192,048 for the substring version; the bound sits halfway.
        Assert.True(bytes <= 152_000, $"Splitting 1,000 lines allocated {bytes} bytes; the bound is 152,000.");
    }

    // The room each map of the glossary inclusion ends with, read without growing it: exactly what a collection sized for
    // its count gets, never what growth from empty reaches, which for these counts is more.
    [Fact]
    public void The_inclusion_map_and_the_set_of_line_keys_have_exactly_the_room_their_counts_need()
    {
        var rows = Enumerable.Range(0, 1_000)
            .Select(i => Custom(FormattableString.Invariant($"term {i:D4}"), FormattableString.Invariant($"Term{i:D4}")))
            .ToArray();
        var catalog = Catalog(
            State(enabled: ["team"], ai: [("team", true)], accepted: [("team", H1)]), Committed(CustomLibrary("team", rows), H1));
        IReadOnlyList<DictionaryEntry> dictionary =
        [
            .. Enumerable.Range(0, 50)
                .Select(i => DictionaryEntry.New(FormattableString.Invariant($"personal {i:D2}"), FormattableString.Invariant($"Personal{i:D2}"))),
        ];
        var composition = LibraryComposition.Committed(catalog, dictionary, new GlossaryBudget(80));
        var vocabulary = CleanupPrompt.ComposeVocabulary(dictionary, composition.AiLibraryEntries).Count;
        Assert.Equal(1_000, composition.AiLibraryEntries.Count);
        Assert.Equal(1_050, vocabulary);
        Assert.NotEqual(new Dictionary<int, int>(1_000).EnsureCapacity(0), Grown(new Dictionary<int, int>(), 1_000));
        Assert.NotEqual(new HashSet<int>(1_050).EnsureCapacity(0), Grown(new HashSet<int>(), 1_050));

        var (inclusion, lineKeys) = composition.GlossaryCapacities();

        Assert.True(new Dictionary<int, int>(1_000).EnsureCapacity(0) == inclusion, $"The inclusion map has room for {inclusion}.");
        Assert.True(new HashSet<int>(1_050).EnsureCapacity(0) == lineKeys, $"The set of line keys has room for {lineKeys}.");
    }

    [Fact]
    public void Each_batch_the_renderer_gets_is_an_array_filled_by_index()
    {
        var glossaryLines = typeof(LibraryComposition).GetMethod(
            nameof(LibraryComposition.GlossaryLines), BindingFlags.Static | BindingFlags.NonPublic)!;
        var callees = MouseButtonRound7Tests.Callees(glossaryLines).ToList();

        Assert.Contains(callees, callee => callee.DeclaringType == typeof(CleanupPrompt) && callee.Name == nameof(CleanupPrompt.BuildGlossary));
        Assert.DoesNotContain(callees, callee => callee.DeclaringType == typeof(Enumerable));
    }

    // Lines as the renderer writes them: with a spoken form (the key joins two slices into one string), without one (one
    // slice), and a few with no "- " prefix (the line itself is the key, no string at all).
    private static string[] KeyCorpus() =>
    [
        .. Enumerable.Range(0, 1_000).Select(i => FormattableString.Invariant($"- Term{i:D4} (transcribed as \"term {i:D4}\")")),
        .. Enumerable.Range(0, 300).Select(i => FormattableString.Invariant($"- Word{i:D4}")),
        .. Enumerable.Range(0, 100).Select(i => FormattableString.Invariant($"Plain{i:D4}")),
    ];

    private static int Grown(Dictionary<int, int> map, int count)
    {
        for (var i = 0; i < count; i++)
        {
            map.Add(i, i);
        }

        return map.EnsureCapacity(0);
    }

    private static int Grown(HashSet<int> set, int count)
    {
        for (var i = 0; i < count; i++)
        {
            set.Add(i);
        }

        return set.EnsureCapacity(0);
    }

    // The smallest of three measured runs after a warm-up: the first calls JIT the path and load its types, and the
    // smallest run is the one no runtime bookkeeping landed in.
    private static long Measure(Func<object> run)
    {
        for (var i = 0; i < 3; i++)
        {
            GC.KeepAlive(run());
        }

        var smallest = long.MaxValue;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var result = run();
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            GC.KeepAlive(result);
            smallest = Math.Min(smallest, bytes);
        }

        return smallest;
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
