using Microsoft.Extensions.Logging;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;
using Xunit;
using Xunit.Abstractions;

namespace Scribe.Core.Tests;

/// <summary>
/// A vocabulary generation reuses the compiled rules of the build before it wherever a rule's spoken form, written form and
/// whole-word flag are unchanged, instead of constructing every rule's regular expression again: a build of every word
/// pack compiled about 1,500 of them, about 5 MB, at every Settings save, quick add and library change. Each generation
/// still gets rule sets of its own, so nothing a dictation holds ever changes.
/// </summary>
public sealed class TextPostProcessorCompileReuseTests
{
    private static readonly DictionaryEntry[] Entries =
    [
        DictionaryEntry.New("azure", "Azure"),
        DictionaryEntry.New("dev ops", "DevOps"),
        DictionaryEntry.New("york", "New York"),
        DictionaryEntry.New("comma", ","),
        DictionaryEntry.New("um", string.Empty),
        DictionaryEntry.New("k eight s", "K8s", wholeWord: false),
    ];

    [Fact]
    public void Compiling_the_same_entries_again_reuses_every_rule_in_a_new_rule_set()
    {
        var processor = Processor();

        var first = processor.Compile(Entries, []);
        var second = processor.Compile(Entries, []);

        Assert.NotSame(first, second);
        Assert.NotSame(first.Rules, second.Rules);
        Assert.Equal(Entries.Length, second.Count);
        for (var i = 0; i < first.Rules.Length; i++)
        {
            Assert.Same(first.Rules[i], second.Rules[i]);
        }
    }

    [Theory]
    [InlineData("written form")]
    [InlineData("whole-word flag")]
    [InlineData("spoken form's case")]
    public void A_rule_whose_spoken_form_written_form_or_whole_word_flag_changed_is_compiled_again(string change)
    {
        var processor = Processor();
        var first = processor.Compile(Entries, []);
        var changed = Entries.ToArray();
        changed[0] = change switch
        {
            "written form" => changed[0] with { Replacement = "AZURE" },
            "whole-word flag" => changed[0] with { WholeWord = false },
            // The spoken form is what a replacement reports as its pattern, so a case-only change is a different rule.
            _ => changed[0] with { Pattern = "Azure" },
        };

        var second = processor.Compile(changed, []);

        Assert.NotSame(first.Rules[0], second.Rules[0]);
        for (var i = 1; i < first.Rules.Length; i++)
        {
            Assert.Same(first.Rules[i], second.Rules[i]);
        }

        Assert.Equal(
            new TextPostProcessor(new TextPostProcessorReferencePipelineTests.DictionaryStub(changed), QuietLog.Instance)
                .ProcessDetailed("azure dev ops in york, comma um k eight s"),
            processor.ProcessDetailed("azure dev ops in york, comma um k eight s", null, second),
            ResultComparer.Instance);
    }

    [Fact]
    public void An_entry_that_differs_only_in_its_id_or_its_switch_reuses_its_rule()
    {
        var processor = Processor();
        var first = processor.Compile(Entries, []);
        var changed = Entries.ToArray();
        changed[1] = changed[1] with { Id = 99, Enabled = false };

        var second = processor.Compile(changed, []);

        Assert.Same(first.Rules[1], second.Rules[1]);
    }

    [Fact]
    public void A_duplicate_entry_shares_one_rule_and_still_resolves_as_two()
    {
        var processor = Processor();
        DictionaryEntry[] entries = [DictionaryEntry.New("azure", "Azure"), DictionaryEntry.New("azure", "Azure")];

        var rules = processor.Compile(entries, []);

        Assert.Equal(2, rules.Count);
        Assert.Same(rules.Rules[0], rules.Rules[1]);
        var result = processor.ProcessDetailed("azure and azure", null, rules);
        Assert.Equal("Azure and Azure", result.Text);
        Assert.Equal(2, result.Replacements.Count);
    }

    [Fact]
    public void Rules_reused_across_generations_give_what_freshly_compiled_rules_give()
    {
        var shipped = DictionaryLibraryComposer.ComposeLibraries(BuiltInDictionaryLibraries.All);
        var random = new Random(20_260_927);
        var reusing = Processor();

        for (var generation = 0; generation < 10; generation++)
        {
            // Each generation keeps about two thirds of the shipped rows and changes the written form of about one in ten.
            var entries = shipped
                .Where(_ => random.Next(3) != 0)
                .Select(entry => random.Next(10) == 0 ? entry with { Replacement = entry.Replacement + " (edited)" } : entry)
                .ToList();
            var reused = reusing.Compile(entries, []);
            var fresh = Processor().Compile(entries, []);
            Assert.Equal(fresh.Count, reused.Count);

            for (var i = 0; i < 20; i++)
            {
                var text = string.Join(
                    " and ",
                    Enumerable.Range(0, 6).Select(_ => shipped[random.Next(shipped.Count)].Pattern));
                Assert.Equal(
                    reusing.ProcessDetailed(text, null, fresh),
                    reusing.ProcessDetailed(text, null, reused),
                    ResultComparer.Instance);
            }
        }
    }

    [Fact]
    public void An_entry_the_matcher_rejects_is_logged_at_every_build()
    {
        var log = new CountingLog();
        var processor = new TextPostProcessor(new TextPostProcessorReferencePipelineTests.DictionaryStub([]), log);
        DictionaryEntry[] entries = [DictionaryEntry.New("azure", "Azure"), new DictionaryEntry(7, "broken", null!)];

        var first = processor.Compile(entries, []);
        var second = processor.Compile(entries, []);

        Assert.Equal(1, first.Count);
        Assert.Equal(1, second.Count);
        Assert.Same(first.Rules[0], second.Rules[0]);
        Assert.Equal(2, log.SkippedEntries);
    }

    private static TextPostProcessor Processor() =>
        new(new TextPostProcessorReferencePipelineTests.DictionaryStub([]), QuietLog.Instance);

    // In the collection that runs alone: no other test runs while it measures.
    [Collection(AllocationMeasurementCollection.Name)]
    public sealed class Allocations(ITestOutputHelper output)
    {
        [Fact]
        public void A_second_build_of_unchanged_entries_constructs_no_regular_expression()
        {
            var shipped = DictionaryLibraryComposer.ComposeLibraries(BuiltInDictionaryLibraries.All);
            _ = Processor().Compile(shipped, []);
            var processor = Processor();

            var first = Allocated(() => processor.Compile(shipped, []));
            var second = Allocated(() => processor.Compile(shipped, []));
            output.WriteLine($"{shipped.Count} rows: first build {first:N0} bytes, second build {second:N0} bytes.");

            // The first build constructs a regular expression for every row (a few KB each); the second allocates only its
            // own rule list and lookup, far under a tenth of that.
            Assert.True(second * 10 < first, $"The first build allocated {first:N0} bytes and the second {second:N0}.");
        }

        private static long Allocated(Func<CompiledDictionaryRules> build)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var rules = build();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            GC.KeepAlive(rules);
            return allocated;
        }
    }

    private sealed class ResultComparer : IEqualityComparer<TextPostProcessingResult>
    {
        public static ResultComparer Instance { get; } = new();

        public bool Equals(TextPostProcessingResult? x, TextPostProcessingResult? y) =>
            x is not null && y is not null &&
            string.Equals(x.Text, y.Text, StringComparison.Ordinal) &&
            x.Replacements.SequenceEqual(y.Replacements);

        public int GetHashCode(TextPostProcessingResult obj) => obj.Text.GetHashCode(StringComparison.Ordinal);
    }

    private sealed class QuietLog : ILogger<TextPostProcessor>
    {
        public static QuietLog Instance { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => false;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
        }
    }

    private sealed class CountingLog : ILogger<TextPostProcessor>
    {
        public int SkippedEntries { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning &&
                formatter(state, exception).StartsWith("Skipping invalid dictionary entry", StringComparison.Ordinal))
            {
                SkippedEntries++;
            }
        }
    }
}
