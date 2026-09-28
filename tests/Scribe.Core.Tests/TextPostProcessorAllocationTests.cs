using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// A rule that does not match costs a dictation nothing. Each rule used to cost an iterator, an empty list, a
/// MatchCollection with its own list and an enumerator on every pass, about 200 bytes a rule: a pass over every word pack
/// allocated about 300 KB, and a cleaned dictation makes two passes (its text and the raw source).
/// </summary>
[Collection(AllocationMeasurementCollection.Name)]
public sealed class TextPostProcessorAllocationTests
{
    private const string Raw = "so i pushed the dot net api changes to github and the azure devops pipeline ran the tests";
    private const string Cleaned = "So I pushed the .NET API changes to GitHub, and the Azure DevOps pipeline ran the tests.";

    // Spoken forms no dictation contains, half of them whole-word, as the word packs' mostly are.
    private static readonly DictionaryEntry[] Unmatched =
    [
        .. Enumerable.Range(0, 1_000).Select(i => DictionaryEntry.New($"zq{i:D4}x", $"ZQ{i:D4}X", wholeWord: i % 2 == 0)),
    ];

    [Fact]
    public void Collecting_the_matches_of_rules_that_do_not_match_allocates_nothing()
    {
        var rules = Processor().Compile(Unmatched, []).Rules;

        // Each regex creates its runner at its first scan, and the loop is jitted.
        for (var i = 0; i < 50; i++)
        {
            _ = TextPostProcessor.Candidates(rules, Cleaned);
        }

        _ = RuntimeWork.Now().Since(RuntimeWork.Now());

        var work = RuntimeWork.Now();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var candidates = TextPostProcessor.Candidates(rules, Cleaned);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var during = RuntimeWork.Now().Since(work);

        Assert.Null(candidates);
        AllocationMeasurement.AssertZero(
            allocated, during, "A pass of 1,000 rules that do not match", () => TextPostProcessor.Candidates(rules, Cleaned));
    }

    [Fact]
    public void A_cleaned_dictation_costs_the_same_with_ten_or_a_thousand_rules_that_do_not_match()
    {
        var processor = Processor();
        var few = processor.Compile(Unmatched[..10], []);
        var many = processor.Compile(Unmatched, []);
        for (var i = 0; i < 50; i++)
        {
            _ = processor.ProcessDetailed(Cleaned, Raw, few);
            _ = processor.ProcessDetailed(Cleaned, Raw, many);
        }

        var withFew = Allocated(() => processor.ProcessDetailed(Cleaned, Raw, few));
        var withMany = Allocated(() => processor.ProcessDetailed(Cleaned, Raw, many));

        // Everything else the call allocates (the normalized text and source, the result) depends on the text alone, and the
        // cleaned text differs from the raw source, so both passes run: 990 more rules, twice, and not one byte more.
        Assert.Equal(withFew, withMany);
    }

    private static TextPostProcessor Processor() =>
        new(new TextPostProcessorReferencePipelineTests.DictionaryStub([]), NullLogger<TextPostProcessor>.Instance);

    private static long Allocated(Func<TextPostProcessingResult> run)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = run();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(result);
        return allocated;
    }
}
