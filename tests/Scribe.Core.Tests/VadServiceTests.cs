using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Scribe.Core.Infrastructure;
using Scribe.Core.Models;
using Scribe.Core.Vad;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// VAD behavior against the real Silero v5 model. The model-dependent tests early-return when
/// the model/fixtures are not present (e.g. CI without Download-Models.ps1).
/// </summary>
public sealed class VadServiceTests
{
    [Fact]
    public void Trim_passes_through_when_sample_rate_is_not_16k()
    {
        var locator = new ModelLocator(new AppPaths());
        using var vad = new VadService(locator, NullLogger<VadService>.Instance);

        var audio = new CapturedAudio(new float[8000], 8000);
        var result = vad.Trim(audio);

        Assert.Same(audio, result); // unchanged: VAD only operates on 16 kHz
    }

    [Fact]
    public void Trim_rejects_pure_silence()
    {
        var locator = new ModelLocator(new AppPaths());
        if (!locator.Resolve().VadAvailable) return;

        using var vad = new VadService(locator, NullLogger<VadService>.Instance);
        var silence = new CapturedAudio(new float[RequiredSampleRate(2)], 16000);

        var result = vad.Trim(silence);

        Assert.True(result.IsEmpty);
    }

    [Fact]
    public void Trim_keeps_speech_and_removes_padding_silence()
    {
        var locator = new ModelLocator(new AppPaths());
        var models = locator.Resolve();
        if (!models.VadAvailable) return;

        var wav = Path.Combine(models.Directory, "test_wavs", "en.wav");
        if (!File.Exists(wav)) return;

        var speech = LoadResampled16kMono(wav);
        if (speech.Length == 0) return;

        // 1s of silence on each side of the real speech.
        var pad = new float[RequiredSampleRate(1)];
        var padded = new float[pad.Length + speech.Length + pad.Length];
        Array.Copy(speech, 0, padded, pad.Length, speech.Length);

        using var vad = new VadService(locator, NullLogger<VadService>.Instance);
        var result = vad.Trim(new CapturedAudio(padded, 16000));

        Assert.True(vad.IsAvailable);
        Assert.False(result.IsEmpty);
        Assert.True(result.Samples.Length < padded.Length,
            "Expected leading/trailing silence to be trimmed.");
        Assert.True(result.Samples.Length >= speech.Length / 2,
            "Expected the bulk of the speech to be retained.");
    }

    private static int RequiredSampleRate(int seconds) => 16000 * seconds;

    private static float[] LoadResampled16kMono(string wavPath)
    {
        using var reader = new AudioFileReader(wavPath);
        ISampleProvider source = reader;
        if (reader.WaveFormat.Channels > 1)
            source = new StereoToMonoSampleProvider(reader);

        var resampler = new WdlResamplingSampleProvider(source, 16000);

        var all = new List<float>(capacity: 16000 * 8);
        var buffer = new float[16000];
        int read;
        while ((read = resampler.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
                all.Add(buffer[i]);
        }

        return all.ToArray();
    }
}
