using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;
using Scribe.Core.Audio;
using Scribe.Core.Cleanup;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;

namespace Scribe.Benchmarks;

[MemoryDiagnoser]
public class HotPathBenchmarks
{
    private const int SampleRate = 16_000;

    private readonly float[] _audioSamples = BuildAudioSamples(seconds: 10);
    private readonly ArraySampleProvider _sampleProvider;
    private readonly string _longTranscript = string.Concat(
        Enumerable.Repeat("This is a complete sentence that carries some weight. ", 900));
    private readonly string _dictionaryTranscript = string.Join(' ',
        Enumerable.Range(0, 500).Select(index => $"term{index % 100:D3}"));
    private readonly TextPostProcessor _postProcessor;

    public HotPathBenchmarks()
    {
        _sampleProvider = new ArraySampleProvider(_audioSamples, SampleRate);
        var entries = Enumerable.Range(0, 100)
            .Select(index => DictionaryEntry.New($"term{index:D3}", $"TERM{index:D3}"))
            .ToArray();
        _postProcessor = new TextPostProcessor(
            new DictionaryStub(entries),
            NullLogger<TextPostProcessor>.Instance);
        _postProcessor.Reload();
    }

    [Benchmark]
    [BenchmarkCategory("Cleanup")]
    public List<string> ChunkLongTranscript() =>
        TextCleanupService.ChunkForCleanup(_longTranscript, targetChars: 2_400);

    [Benchmark]
    [BenchmarkCategory("Audio")]
    public float[] ReadAllAudio()
    {
        _sampleProvider.Reset();
        return AudioCaptureService.ReadAll(_sampleProvider);
    }

    [Benchmark]
    [BenchmarkCategory("PostProcessing")]
    public string ProcessDictionary() =>
        _postProcessor.Process(_dictionaryTranscript);

    [Benchmark]
    [BenchmarkCategory("Persistence")]
    public float[] SerializeAudioRoundTrip() =>
        HistoryRepository.ToFloats(HistoryRepository.ToBytes(_audioSamples));

    private static float[] BuildAudioSamples(int seconds)
    {
        var samples = new float[SampleRate * seconds];
        for (var index = 0; index < samples.Length; index++)
        {
            samples[index] = MathF.Sin(2 * MathF.PI * 220 * index / SampleRate) * 0.25f;
        }

        return samples;
    }

    private sealed class ArraySampleProvider(float[] samples, int sampleRate) : ISampleProvider
    {
        private int _position;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            var available = Math.Min(count, samples.Length - _position);
            samples.AsSpan(_position, available).CopyTo(buffer.AsSpan(offset, available));
            _position += available;
            return available;
        }

        public void Reset() => _position = 0;
    }

    private sealed class DictionaryStub(IReadOnlyList<DictionaryEntry> entries) : IDictionaryRepository
    {
        public IReadOnlyList<DictionaryEntry> GetAll() => entries;
        public IReadOnlyList<DictionaryEntry> GetEnabled() => entries;
        public DictionaryEntry Add(DictionaryEntry entry) => throw new NotSupportedException();
        public IReadOnlyList<DictionaryEntry> AddRange(IReadOnlyList<DictionaryEntry> entries) =>
            throw new NotSupportedException();
        public void Update(DictionaryEntry entry) => throw new NotSupportedException();
        public void Delete(long id) => throw new NotSupportedException();
        public void SaveAll(IReadOnlyList<DictionaryEntry> updatedEntries) => throw new NotSupportedException();
        public int SeedIfEmpty(IEnumerable<DictionaryEntry> seedEntries) => throw new NotSupportedException();
    }
}