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

    /// <summary>
    /// The regression this release fixes. Trimming used to be skipped entirely above a 60 s
    /// capture, so the long recordings that the recogniser handles worst were the only ones that
    /// kept all of their silence. A capture well past that limit must now be trimmed like any
    /// other, and the speech must survive intact.
    /// </summary>
    [Fact]
    public void Trim_still_trims_captures_longer_than_the_detector_buffer_hint()
    {
        var locator = new ModelLocator(new AppPaths());
        var models = locator.Resolve();
        if (!models.VadAvailable) return;

        var wav = Path.Combine(models.Directory, "test_wavs", "en.wav");
        if (!File.Exists(wav)) return;

        var speech = LoadResampled16kMono(wav);
        if (speech.Length == 0) return;

        // 40 s of silence, the speech, then 40 s more: 80 s of padding puts the capture far past
        // the old 60 s cut-off, which would previously have returned it untouched.
        var pad = new float[RequiredSampleRate(40)];
        var padded = new float[pad.Length + speech.Length + pad.Length];
        Array.Copy(speech, 0, padded, pad.Length, speech.Length);

        using var vad = new VadService(locator, NullLogger<VadService>.Instance);
        var audio = new CapturedAudio(padded, 16000);
        var result = vad.Trim(audio);

        Assert.True(audio.Duration.TotalSeconds > 60, "Fixture must exceed the old bypass threshold.");
        Assert.NotSame(audio, result);
        Assert.False(result.IsEmpty);
        Assert.True(result.Samples.Length < padded.Length / 2,
            "Expected the 80 s of padding silence to be trimmed away.");
        Assert.True(result.Samples.Length >= speech.Length / 2,
            "Expected the bulk of the speech to be retained.");
    }

    /// <summary>
    /// Voiced duration must be summed across speech segments, not taken from the trimmed span.
    /// A short utterance followed by a long pause is the exact shape that would be misread as a
    /// collapse, and the trimmed capture keeps that pause.
    /// </summary>
    [Fact]
    public void Trim_reports_voiced_duration_excluding_internal_pauses()
    {
        var locator = new ModelLocator(new AppPaths());
        var models = locator.Resolve();
        if (!models.VadAvailable) return;

        var wav = Path.Combine(models.Directory, "test_wavs", "en.wav");
        if (!File.Exists(wav)) return;

        var speech = LoadResampled16kMono(wav);
        if (speech.Length == 0) return;

        // speech, 5 s of silence, speech again: the returned span covers all of it, but only the
        // two speech runs are voiced.
        var gap = new float[RequiredSampleRate(5)];
        var buffer = new float[speech.Length + gap.Length + speech.Length];
        Array.Copy(speech, 0, buffer, 0, speech.Length);
        Array.Copy(speech, 0, buffer, speech.Length + gap.Length, speech.Length);

        using var vad = new VadService(locator, NullLogger<VadService>.Instance);
        var result = vad.Trim(new CapturedAudio(buffer, 16000));

        Assert.False(result.IsEmpty);
        var voiced = vad.LastSpeechSeconds;
        Assert.NotNull(voiced);

        // The pause is inside the returned span but must not count as voiced.
        Assert.True(voiced!.Value < result.Duration.TotalSeconds - 3,
            $"voiced {voiced:F2}s should exclude the 5 s pause inside the {result.Duration.TotalSeconds:F2}s span.");
        Assert.True(voiced.Value > 0.5, "expected the two speech runs to be counted.");
    }

    [Fact]
    public void LastSpeechSeconds_is_null_when_no_speech_is_found()
    {
        var locator = new ModelLocator(new AppPaths());
        if (!locator.Resolve().VadAvailable) return;

        using var vad = new VadService(locator, NullLogger<VadService>.Instance);
        vad.Trim(new CapturedAudio(new float[RequiredSampleRate(2)], 16000));

        Assert.Null(vad.LastSpeechSeconds);
    }

    [Fact]
    public void Unload_ThenTrim_ReloadsOnDemand()
    {
        var locator = new ModelLocator(new AppPaths());
        var models = locator.Resolve();
        if (!models.VadAvailable) return;

        var wav = Path.Combine(models.Directory, "test_wavs", "en.wav");
        if (!File.Exists(wav)) return;

        var speech = LoadResampled16kMono(wav);
        if (speech.Length == 0) return;

        using var vad = new VadService(locator, NullLogger<VadService>.Instance);
        vad.Initialize();
        Assert.True(vad.IsAvailable);

        vad.Unload();
        Assert.False(vad.IsAvailable);

        // Idle release depends on Trim re-initializing by itself after an unload.
        var result = vad.Trim(new CapturedAudio(speech, 16000));
        Assert.True(vad.IsAvailable);
        Assert.False(result.IsEmpty);
    }

    [Fact]
    public void Unload_BeforeInitialize_IsANoOp()
    {
        var locator = new ModelLocator(new AppPaths());
        using var vad = new VadService(locator, NullLogger<VadService>.Instance);

        vad.Unload(); // must not throw and must not load anything

        Assert.False(vad.IsAvailable);
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
        while ((read = resampler.Read(buffer.AsSpan())) > 0)
        {
            for (var i = 0; i < read; i++)
                all.Add(buffer[i]);
        }

        return all.ToArray();
    }
}
