using NAudio.Wave;
using Scribe.Core.Infrastructure;

namespace Scribe.Benchmarks;

/// <summary>
/// Inputs for the audio benchmarks: device-format captures made of tones and noise, speech made of the repository's
/// synthetic speech fixtures (<c>tests\fixtures\speech</c>, generated with the Windows text-to-speech engine; nothing
/// recorded), and a model locator that finds the speech models through <c>SCRIBE_MODELS_DIR</c> under a data root of its
/// own, so no benchmark ever reads or creates the app's data folder.
/// </summary>
internal static class AudioBenchmarkInputs
{
    /// <summary>A capture of <paramref name="seconds"/> in <paramref name="format"/> (32-bit float or 16-bit PCM).</summary>
    public static byte[] DeviceCapture(WaveFormat format, double seconds, int seed)
    {
        var frames = (int)(format.SampleRate * seconds);
        var random = new Random(seed);
        var bytesPerSample = format.BitsPerSample / 8;
        var bytes = new byte[frames * format.BlockAlign];
        for (var frame = 0; frame < frames; frame++)
        {
            var t = frame / (double)format.SampleRate;
            var envelope = 0.5 + (0.5 * Math.Sin(2 * Math.PI * 3 * t));
            var voiced = (0.20 * Math.Sin(2 * Math.PI * 180 * t)) + (0.08 * Math.Sin(2 * Math.PI * 720 * t));
            for (var channel = 0; channel < format.Channels; channel++)
            {
                var value = (envelope * voiced) + ((random.NextDouble() - 0.5) * 0.004);
                var at = bytes.AsSpan(((frame * format.Channels) + channel) * bytesPerSample, bytesPerSample);
                if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
                {
                    BitConverter.TryWriteBytes(at, (float)value);
                }
                else if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16)
                {
                    BitConverter.TryWriteBytes(at, (short)(value * short.MaxValue));
                }
                else
                {
                    throw new ArgumentException("Only 32-bit float and 16-bit PCM captures are generated.", nameof(format));
                }
            }
        }

        return bytes;
    }

    /// <summary>
    /// <paramref name="seconds"/> of 16 kHz speech: the speech fixtures with their silence trimmed, joined by
    /// <paramref name="gapSeconds"/> of faint noise, over and over, then cut to length.
    /// </summary>
    public static float[] Speech(double seconds, double gapSeconds = 0.4, int seed = 7)
    {
        var clips = SpeechClips();
        var total = (int)(seconds * 16_000);
        var gap = (int)(gapSeconds * 16_000);
        var output = new float[total];
        var random = new Random(seed);
        for (var i = 0; i < output.Length; i++)
        {
            output[i] = (float)((random.NextDouble() - 0.5) * 0.001);
        }

        var position = 0;
        for (var index = 0; position < total; index++)
        {
            var clip = clips[index % clips.Count];
            var count = Math.Min(clip.Length, total - position);
            for (var i = 0; i < count; i++)
            {
                output[position + i] += clip[i];
            }

            position += count + gap;
        }

        return output;
    }

    /// <summary>Finds the speech models through <c>SCRIBE_MODELS_DIR</c>; the data root is a folder nothing creates.</summary>
    public static ModelLocator Models() =>
        new(new AppPaths(Path.Combine(Path.GetTempPath(), "ScribeBenchmarks", "no-data-root")));

    private static List<float[]> SpeechClips()
    {
        var folder = Path.Combine(RepositoryRoot(), "tests", "fixtures", "speech");
        var clips = new List<float[]>();
        foreach (var path in Directory.GetFiles(folder, "*.wav").OrderBy(p => p, StringComparer.Ordinal))
        {
            using var reader = new WaveFileReader(path);
            var provider = reader.ToSampleProvider();
            if (provider.WaveFormat.SampleRate != 16_000 || provider.WaveFormat.Channels != 1)
            {
                continue;
            }

            var samples = new List<float>();
            var buffer = new float[16_000];
            int read;
            while ((read = provider.Read(buffer.AsSpan())) > 0)
            {
                samples.AddRange(buffer.AsSpan(0, read).ToArray());
            }

            var clip = samples.ToArray();
            var first = Array.FindIndex(clip, s => Math.Abs(s) > 0.02f);
            var last = Array.FindLastIndex(clip, s => Math.Abs(s) > 0.02f);
            if (first >= 0 && last > first)
            {
                clips.Add(clip[Math.Max(0, first - 800)..Math.Min(clip.Length, last + 800)]);
            }
        }

        return clips.Count > 0
            ? clips
            : throw new InvalidOperationException($"No 16 kHz mono speech fixtures were found in {folder}.");
    }

    // BenchmarkDotNet runs each benchmark from a project it generates below this tool's output folder, so the repository
    // is found by walking up to the solution file.
    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Scribe.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root (Scribe.slnx) was not found above " + AppContext.BaseDirectory);
    }
}
