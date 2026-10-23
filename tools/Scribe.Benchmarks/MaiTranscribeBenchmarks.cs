using BenchmarkDotNet.Attributes;
using Scribe.Core.Models;
using Scribe.Core.Transcription;

namespace Scribe.Benchmarks;

/// <summary>
/// Measures the in-memory PCM16 WAV encoding (<see cref="MaiWavEncoder.EncodePcm16Wav"/>) and
/// <c>MAI-Transcribe-2</c> definition JSON composition (<see cref="MaiTranscribeClient.BuildDefinitionJson"/>)
/// for a 4-second push-to-talk clip and a 23-second passage with a 500-term vocabulary phrase list.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("Transcription")]
public class MaiTranscribeBenchmarks
{
    private float[] _shortSamples = [];
    private float[] _longSamples = [];
    private MaiTranscribeOptions _optionsWithPhrases = MaiTranscribeOptions.Disabled;

    [GlobalSetup]
    public void Setup()
    {
        _shortSamples = new float[16_000 * 4];
        for (var i = 0; i < _shortSamples.Length; i++)
        {
            _shortSamples[i] = MathF.Sin(i * 0.05f) * 0.5f;
        }

        _longSamples = new float[16_000 * 23];
        for (var i = 0; i < _longSamples.Length; i++)
        {
            _longSamples[i] = MathF.Sin(i * 0.05f) * 0.5f;
        }

        var entries = Enumerable.Range(1, 600)
            .Select(i => new DictionaryEntry(i, $"term {i}", $"CanonicalTerm{i}", WholeWord: true, Enabled: true))
            .ToArray();
        var phrases = MaiTranscribePhraseList.Build(entries);
        _optionsWithPhrases = new MaiTranscribeOptions(
            Enabled: true,
            MaiEndpointOverride: "https://foundry-cmckee.cognitiveservices.azure.com",
            FoundryEndpoint: null,
            ApiKey: null,
            Style: MaiTranscribeStyle.Clean,
            PhraseList: phrases);
    }

    [Benchmark]
    public byte[] EncodeWav4Seconds() => MaiWavEncoder.EncodePcm16Wav(_shortSamples, 16_000);

    [Benchmark]
    public byte[] EncodeWav23Seconds() => MaiWavEncoder.EncodePcm16Wav(_longSamples, 16_000);

    [Benchmark]
    public string BuildDefinition500Phrases() => MaiTranscribeClient.BuildDefinitionJson(_optionsWithPhrases);
}
