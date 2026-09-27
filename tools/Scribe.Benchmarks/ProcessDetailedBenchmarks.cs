using BenchmarkDotNet.Attributes;
using Scribe.Core.PostProcessing;

namespace Scribe.Benchmarks;

/// <summary>
/// <see cref="TextPostProcessor.ProcessDetailed"/> across the shapes the dictation pipeline really
/// passes it: short and long text, a seed-sized and an every-library dictionary, a source identical
/// to the text (AI cleanup off or skipped) or a raw transcript behind cleaned text, and snippets
/// configured or not. The text always ends with a snippet trigger, so the snippets-on arms expand it.
/// <para>
/// The baseline arm runs the original algorithm (the source normalized and scanned a second time
/// even when it is the text itself), kept behind an internal switch for the differential tests; the
/// other arm is what ships. Both return identical results.
/// </para>
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("PostProcessing")]
public class ProcessDetailedBenchmarks
{
    private TextPostProcessor _shipping = null!;
    private TextPostProcessor _fullRescan = null!;
    private string _text = string.Empty;
    private string? _source;

    [Params(WorkloadTextLength.Short, WorkloadTextLength.Long)]
    public WorkloadTextLength Text { get; set; }

    [Params(WorkloadDictionary.Small, WorkloadDictionary.Large)]
    public WorkloadDictionary Dictionary { get; set; }

    [Params(SourceShape.SameAsText, SourceShape.RawTranscript)]
    public SourceShape Source { get; set; }

    [Params(false, true)]
    public bool Snippets { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _shipping = RepresentativeWorkload.CreatePostProcessor(Dictionary, Snippets);
        _fullRescan = RepresentativeWorkload.CreatePostProcessor(
            Dictionary, Snippets, reuseIdenticalSourceScan: false, useLoopCandidateScan: false);
        var raw = RepresentativeWorkload.RawTranscript(Text);
        (_text, _source) = Source == SourceShape.SameAsText
            ? (raw, raw)
            : (RepresentativeWorkload.CleanedTranscript(Text), raw);
    }

    [Benchmark(Baseline = true)]
    public TextPostProcessingResult FullRescan() => _fullRescan.ProcessDetailed(_text, _source);

    [Benchmark]
    public TextPostProcessingResult ProcessDetailed() => _shipping.ProcessDetailed(_text, _source);
}

/// <summary>
/// Candidate collection only: both arms keep source trace reuse on, and differ only in the LINQ
/// candidate pipeline versus the loop pipeline.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("PostProcessing")]
public class CandidatePipelineBenchmarks
{
    private TextPostProcessor _linq = null!;
    private TextPostProcessor _loop = null!;
    private string _text = string.Empty;
    private string? _source;

    [Params(WorkloadTextLength.Short, WorkloadTextLength.Long)]
    public WorkloadTextLength Text { get; set; }

    [Params(WorkloadDictionary.Small, WorkloadDictionary.Large)]
    public WorkloadDictionary Dictionary { get; set; }

    [Params(SourceShape.SameAsText, SourceShape.RawTranscript)]
    public SourceShape Source { get; set; }

    [Params(false, true)]
    public bool Snippets { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _linq = RepresentativeWorkload.CreatePostProcessor(
            Dictionary, Snippets, reuseIdenticalSourceScan: true, useLoopCandidateScan: false);
        _loop = RepresentativeWorkload.CreatePostProcessor(Dictionary, Snippets);
        var raw = RepresentativeWorkload.RawTranscript(Text);
        (_text, _source) = Source == SourceShape.SameAsText
            ? (raw, raw)
            : (RepresentativeWorkload.CleanedTranscript(Text), raw);
    }

    [Benchmark(Baseline = true)]
    public TextPostProcessingResult LinqCandidates() => _linq.ProcessDetailed(_text, _source);

    [Benchmark]
    public TextPostProcessingResult LoopCandidates() => _loop.ProcessDetailed(_text, _source);
}
