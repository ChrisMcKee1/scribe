using NAudio.Wave;
using Scribe.Core.Models;

namespace Scribe.Core.Tests;

/// <summary>Test helpers for loading WAV fixtures into <see cref="CapturedAudio"/>.</summary>
internal static class TestAudio
{
    /// <summary>
    /// Loads a WAV file as mono float samples at its native sample rate. sherpa-onnx resamples
    /// to 16 kHz internally, so the native rate (the fixtures are 24 kHz) is forwarded as-is.
    /// </summary>
    public static CapturedAudio LoadWav(string path)
    {
        using var reader = new AudioFileReader(path);
        var channels = reader.WaveFormat.Channels;
        var sampleRate = reader.WaveFormat.SampleRate;

        var interleaved = new List<float>(capacity: (int)(reader.Length / sizeof(float)));
        var block = new float[sampleRate * channels];
        int read;
        while ((read = reader.Read(block, 0, block.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
                interleaved.Add(block[i]);
        }

        var samples = channels == 1
            ? interleaved.ToArray()
            : Downmix(interleaved, channels);

        return new CapturedAudio(samples, sampleRate);
    }

    private static float[] Downmix(List<float> interleaved, int channels)
    {
        var frames = interleaved.Count / channels;
        var mono = new float[frames];
        for (var f = 0; f < frames; f++)
        {
            var sum = 0f;
            for (var c = 0; c < channels; c++)
                sum += interleaved[(f * channels) + c];
            mono[f] = sum / channels;
        }

        return mono;
    }
}
