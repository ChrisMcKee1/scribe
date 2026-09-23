using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Scribe.AsrCheck.Scenarios;

/// <summary>
/// Deterministic signal transforms that turn the committed base phrases into scenario audio.
/// <para>
/// Every generator takes a fixed seed, so a scenario is byte-identical from run to run on the same
/// architecture: the baseline build and the fixed build are measured on exactly the same input.
/// Transcendental maths (noise, hum, resampling filters) can differ in the last bit between x64 and
/// Arm64, so identity is promised per architecture, not across them; the manifest records a SHA-256
/// per WAV so a comparison can prove which it had.
/// </para>
/// </summary>
internal static class AudioTransforms
{
    public const int BaseRate = 16_000;

    public static float Peak(ReadOnlySpan<float> samples)
    {
        var peak = 0f;
        foreach (var sample in samples)
        {
            var abs = Math.Abs(sample);
            if (abs > peak)
            {
                peak = abs;
            }
        }

        return peak;
    }

    public static double Rms(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return 0;
        }

        double sum = 0;
        foreach (var sample in samples)
        {
            sum += sample * (double)sample;
        }

        return Math.Sqrt(sum / samples.Length);
    }

    public static float FromDbfs(double dbfs) => (float)Math.Pow(10, dbfs / 20);

    public static double ToDbfs(double amplitude) => amplitude <= 1e-9 ? -180 : 20 * Math.Log10(amplitude);

    /// <summary>
    /// RMS over the 20 ms frames within 40 dB of the loudest frame: the level of the speech itself.
    /// A whole-clip RMS would count the synthetic voice's own leading and trailing silence as signal
    /// and make every signal-to-noise ratio built on it look kinder than it is.
    /// </summary>
    public static double ActiveRms(float[] samples, int sampleRate)
    {
        var frame = Math.Max(1, sampleRate / 50);
        var frames = new List<double>();
        for (var start = 0; start + frame <= samples.Length; start += frame)
        {
            frames.Add(Rms(samples.AsSpan(start, frame)));
        }

        if (frames.Count == 0)
        {
            return Rms(samples);
        }

        var loudest = frames.Max();
        if (loudest <= 0)
        {
            return 0;
        }

        var floor = loudest / 100; // 40 dB
        double sum = 0;
        var count = 0;
        foreach (var rms in frames.Where(r => r >= floor))
        {
            sum += rms * rms;
            count++;
        }

        return Math.Sqrt(sum / count);
    }

    public static float[] Scale(float[] samples, double gain)
    {
        var scaled = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            scaled[i] = (float)(samples[i] * gain);
        }

        return scaled;
    }

    public static float[] NormalizePeak(float[] samples, double peakDbfs)
    {
        var peak = Peak(samples);
        return peak <= 0 ? (float[])samples.Clone() : Scale(samples, FromDbfs(peakDbfs) / peak);
    }

    public static float[] HardClip(float[] samples, float limit = 1f)
    {
        var clipped = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            clipped[i] = Math.Clamp(samples[i], -limit, limit);
        }

        return clipped;
    }

    public static float[] AddConstant(float[] samples, float offset)
    {
        var shifted = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            shifted[i] = samples[i] + offset;
        }

        return shifted;
    }

    /// <summary>Mains hum: the fundamental plus the odd harmonics a transformer actually radiates.</summary>
    public static float[] AddHum(float[] samples, int sampleRate, float amplitude, double fundamentalHz = 50)
    {
        var hummed = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            var t = i / (double)sampleRate;
            var hum = Math.Sin(2 * Math.PI * fundamentalHz * t)
                + (0.5 * Math.Sin(2 * Math.PI * 3 * fundamentalHz * t))
                + (0.25 * Math.Sin(2 * Math.PI * 5 * fundamentalHz * t));
            hummed[i] = samples[i] + (float)(amplitude * hum / 1.75);
        }

        return hummed;
    }

    /// <summary>Gaussian white noise at a target RMS (Box-Muller; uniform noise has the wrong character).</summary>
    public static float[] WhiteNoise(int length, double rms, int seed)
    {
        var random = new Random(seed);
        var noise = new float[length];
        for (var i = 0; i < length; i++)
        {
            noise[i] = (float)(Gaussian(random) * rms);
        }

        return noise;
    }

    /// <summary>
    /// Pink (1/f) noise at a target RMS, from Paul Kellet's refined filter over Gaussian white noise.
    /// Room tone, fans and traffic are closer to pink than to white, and they bury speech energy
    /// rather than hiss above it, so the two noise sweeps stress the recogniser differently.
    /// </summary>
    public static float[] PinkNoise(int length, double rms, int seed)
    {
        var random = new Random(seed);
        var pink = new float[length];
        double b0 = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0, b5 = 0, b6 = 0;
        for (var i = 0; i < length; i++)
        {
            var white = Gaussian(random);
            b0 = (0.99886 * b0) + (white * 0.0555179);
            b1 = (0.99332 * b1) + (white * 0.0750759);
            b2 = (0.96900 * b2) + (white * 0.1538520);
            b3 = (0.86650 * b3) + (white * 0.3104856);
            b4 = (0.55000 * b4) + (white * 0.5329522);
            b5 = (-0.7616 * b5) - (white * 0.0168980);
            pink[i] = (float)(b0 + b1 + b2 + b3 + b4 + b5 + b6 + (white * 0.5362));
            b6 = white * 0.115926;
        }

        var current = Rms(pink);
        return current <= 0 ? pink : Scale(pink, rms / current);
    }

    /// <summary>A faint analogue noise floor: what a live microphone records when nobody speaks.</summary>
    public static float[] Floor(int length, double dbfsRms, int seed) => WhiteNoise(length, FromDbfs(dbfsRms), seed);

    /// <summary>Mixes <paramref name="noise"/> under <paramref name="speech"/> at an active-speech SNR.</summary>
    public static float[] MixAtSnr(float[] speech, float[] noise, double snrDb, int sampleRate)
    {
        var speechLevel = ActiveRms(speech, sampleRate);
        var noiseLevel = Rms(noise.AsSpan(0, Math.Min(noise.Length, speech.Length)));
        var gain = noiseLevel <= 0 ? 0 : speechLevel / Math.Pow(10, snrDb / 20) / noiseLevel;
        var mixed = new float[speech.Length];
        for (var i = 0; i < speech.Length; i++)
        {
            mixed[i] = speech[i] + (float)((i < noise.Length ? noise[i] : 0f) * gain);
        }

        return mixed;
    }

    /// <summary>
    /// Other people talking: each talker loops its phrases from a different starting offset, and the
    /// talkers are summed. Intelligible competing speech is the hardest noise for a recogniser, because
    /// it is made of exactly the features the model is listening for.
    /// </summary>
    public static float[] Babble(int length, IReadOnlyList<float[]> talkers, int sampleRate, int seed)
    {
        var random = new Random(seed);
        var babble = new float[length];
        var gap = sampleRate / 5;
        foreach (var talker in talkers)
        {
            if (talker.Length == 0)
            {
                continue;
            }

            var cycle = talker.Length + gap;
            var offset = random.Next(cycle);
            for (var i = 0; i < length; i++)
            {
                var position = (i + offset) % cycle;
                if (position < talker.Length)
                {
                    babble[i] += talker[position];
                }
            }
        }

        return babble;
    }

    /// <summary>
    /// Schroeder reverberator: four parallel feedback combs into two series all-pass filters, with
    /// comb gains set from the requested RT60. Not a measured room impulse response, but a standard,
    /// cheap, deterministic stand-in for "a laptop microphone several feet away in a hard room".
    /// The output keeps the input's peak and carries the decay tail past the end of the speech.
    /// </summary>
    public static float[] Reverb(float[] samples, int sampleRate, double rt60Seconds, double wet)
    {
        var tail = (int)(rt60Seconds * sampleRate);
        var dry = new float[samples.Length + tail];
        samples.CopyTo(dry, 0);

        double[] combDelaysMs = [29.7, 37.1, 41.1, 43.7];
        var combSum = new double[dry.Length];
        foreach (var delayMs in combDelaysMs)
        {
            var delay = Math.Max(1, (int)Math.Round(delayMs * sampleRate / 1000));
            var gain = Math.Pow(10, -3.0 * delay / (rt60Seconds * sampleRate));
            var y = new double[dry.Length];
            for (var i = 0; i < dry.Length; i++)
            {
                y[i] = dry[i] + (i >= delay ? gain * y[i - delay] : 0);
                combSum[i] += y[i] / combDelaysMs.Length;
            }
        }

        var diffused = AllPass(AllPass(combSum, (int)Math.Round(5.0 * sampleRate / 1000), 0.7),
            (int)Math.Round(1.7 * sampleRate / 1000), 0.7);

        var dryRms = Rms(dry);
        var wetRms = RmsOf(diffused);
        var wetGain = wetRms <= 0 ? 0 : dryRms / wetRms;
        var mixed = new float[dry.Length];
        for (var i = 0; i < dry.Length; i++)
        {
            mixed[i] = (float)(((1 - wet) * dry[i]) + (wet * wetGain * diffused[i]));
        }

        var peak = Peak(samples);
        return peak <= 0 ? mixed : Scale(mixed, peak / Math.Max(Peak(mixed), 1e-9f));

        static double[] AllPass(double[] input, int delay, double gain)
        {
            var output = new double[input.Length];
            for (var i = 0; i < input.Length; i++)
            {
                var delayedIn = i >= delay ? input[i - delay] : 0;
                var delayedOut = i >= delay ? output[i - delay] : 0;
                output[i] = (-gain * input[i]) + delayedIn + (gain * delayedOut);
            }

            return output;
        }

        static double RmsOf(double[] values)
        {
            double sum = 0;
            foreach (var value in values)
            {
                sum += value * value;
            }

            return values.Length == 0 ? 0 : Math.Sqrt(sum / values.Length);
        }
    }

    /// <summary>
    /// Cuts the synthetic voice's own leading and trailing silence, keeping a small margin, so
    /// concatenated dictation has exactly the pauses the scenario asks for and no more.
    /// </summary>
    public static float[] TrimSilence(float[] samples, int sampleRate, double thresholdDbfs, double marginSeconds)
    {
        var threshold = FromDbfs(thresholdDbfs);
        var first = Array.FindIndex(samples, s => Math.Abs(s) >= threshold);
        if (first < 0)
        {
            return [];
        }

        var last = Array.FindLastIndex(samples, s => Math.Abs(s) >= threshold);
        var margin = (int)(marginSeconds * sampleRate);
        var start = Math.Max(0, first - margin);
        var end = Math.Min(samples.Length, last + 1 + margin);
        return samples[start..end];
    }

    public static float[] Concat(params float[][] parts)
    {
        var joined = new float[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(joined, offset);
            offset += part.Length;
        }

        return joined;
    }

    public static int SamplesFor(double seconds, int sampleRate) => (int)Math.Round(seconds * sampleRate);

    public static float[] Silence(double seconds, int sampleRate) => new float[SamplesFor(seconds, sampleRate)];

    /// <summary>The loudest window of a phrase, with 5 ms fades so the cut itself is not a click.</summary>
    public static float[] LoudestWindow(float[] samples, int sampleRate, double seconds)
    {
        var window = Math.Min(samples.Length, SamplesFor(seconds, sampleRate));
        var hop = Math.Max(1, sampleRate / 100);
        var bestStart = 0;
        var bestEnergy = -1d;
        for (var start = 0; start + window <= samples.Length; start += hop)
        {
            var energy = Rms(samples.AsSpan(start, window));
            if (energy > bestEnergy)
            {
                bestEnergy = energy;
                bestStart = start;
            }
        }

        var cut = samples[bestStart..(bestStart + window)];
        var fade = Math.Min(window / 2, SamplesFor(0.005, sampleRate));
        for (var i = 0; i < fade; i++)
        {
            var gain = i / (float)fade;
            cut[i] *= gain;
            cut[window - 1 - i] *= gain;
        }

        return cut;
    }

    /// <summary>
    /// Keyboard and mouse clicks over a live noise floor: 2 ms decaying bursts. Short, loud and
    /// broadband, which is the shape most likely to fool a voice detector into admitting a segment.
    /// </summary>
    public static float[] Clicks(double seconds, int sampleRate, IReadOnlyList<double> atSeconds, double peakDbfs, int seed)
    {
        var clicks = Floor(SamplesFor(seconds, sampleRate), -70, seed);
        var random = new Random(seed + 1);
        var length = SamplesFor(0.002, sampleRate);
        var peak = FromDbfs(peakDbfs);
        foreach (var at in atSeconds)
        {
            var start = SamplesFor(at, sampleRate);
            for (var i = 0; i < length && start + i < clicks.Length; i++)
            {
                var envelope = Math.Exp(-i / (0.0005 * sampleRate));
                clicks[start + i] += (float)(Math.Clamp(Gaussian(random) / 3, -1, 1) * peak * envelope);
            }
        }

        return clicks;
    }

    public static float[] Delay(float[] samples, int delaySamples)
    {
        var delayed = new float[samples.Length];
        Array.Copy(samples, 0, delayed, delaySamples, Math.Max(0, samples.Length - delaySamples));
        return delayed;
    }

    public static float[] Add(float[] a, float[] b)
    {
        var sum = new float[Math.Max(a.Length, b.Length)];
        for (var i = 0; i < sum.Length; i++)
        {
            sum[i] = (i < a.Length ? a[i] : 0f) + (i < b.Length ? b[i] : 0f);
        }

        return sum;
    }

    public static float[] PadTo(float[] samples, int length)
    {
        if (samples.Length >= length)
        {
            return samples;
        }

        var padded = new float[length];
        samples.CopyTo(padded, 0);
        return padded;
    }

    /// <summary>
    /// Changes the sample rate of SOURCE material, to synthesize what a 44.1, 48, 96 or 8 kHz device
    /// would have delivered. This is scenario construction, not the pipeline under test: the
    /// conversion back to 16 kHz always goes through the production capture path.
    /// </summary>
    public static float[] Resample(float[] samples, int fromRate, int toRate)
    {
        if (fromRate == toRate)
        {
            return (float[])samples.Clone();
        }

        var resampler = new WdlResamplingSampleProvider(new ArraySampleProvider(samples, fromRate), toRate);
        var output = new List<float>((int)((long)samples.Length * toRate / fromRate) + 1024);
        var buffer = new float[8192];
        int read;
        while ((read = resampler.Read(buffer.AsSpan())) > 0)
        {
            output.AddRange(buffer.AsSpan(0, read));
        }

        return [.. output];
    }

    private static double Gaussian(Random random)
    {
        var u1 = 1.0 - random.NextDouble();
        var u2 = random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    private sealed class ArraySampleProvider(float[] samples, int sampleRate) : ISampleProvider
    {
        private int _position;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);

        public int Read(Span<float> buffer)
        {
            var count = Math.Min(buffer.Length, samples.Length - _position);
            samples.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
    }
}
