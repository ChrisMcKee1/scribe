using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scribe.Core.Diagnostics;
using Scribe.Core.Infrastructure;
using Scribe.Core.Models;
using Scribe.Core.Transcription;

namespace Scribe.AsrCheck;

/// <summary>
/// Decodes known speech fixtures through the real transcription stack and asserts the result.
///
/// This exists because the unit tests deliberately do not touch the native ASR engine, which leaves
/// the riskiest part of a multi-architecture build unverified: whether sherpa-onnx and its bundled
/// ONNX Runtime actually load and decode on the silicon we just built for. A build can succeed,
/// publish clean binaries, and still fail on first launch if the wrong native was packaged. Running
/// this on an Arm64 runner turns that into a CI failure rather than a user's crash report.
/// </summary>
internal static class Program
{
    // A correct decode of a clean TTS phrase overlaps almost entirely. This threshold tolerates a
    // synthetic voice fumbling the odd word while still failing loudly on a broken native, which
    // produces empty output or noise rather than most of the right words.
    private const double MinimumWordOverlap = 0.6;

    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            // A native load failure surfaces here as a DllNotFoundException or BadImageFormatException.
            // Those are exactly the architecture regressions this tool exists to catch, so report the
            // type explicitly instead of letting the runtime print a bare stack trace.
            Console.Error.WriteLine($"ASR check failed: {ex.GetType().Name}: {ex.Message}");
            return 3;
        }
    }

    private static int Run(string[] args)
    {
        var repoRoot = FindRepoRoot();
        var fixtureDir = ArgValue(args, "--fixtures")
            ?? Path.Combine(repoRoot ?? AppContext.BaseDirectory, "tests", "fixtures", "speech");

        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("Scribe ASR check");
        Console.WriteLine($"  {ComputeCapabilityReport.Detect().Describe()}");
        Console.WriteLine($"  process={RuntimeInformation.ProcessArchitecture} os={RuntimeInformation.OSArchitecture}");
        Console.WriteLine($"  fixtures={fixtureDir}");
        Console.WriteLine();

        var fixtures = LoadFixtures(fixtureDir);
        if (fixtures.Count == 0)
        {
            Console.Error.WriteLine("No fixtures found. They are committed under tests/fixtures/speech; run scripts/New-SpeechFixtures.ps1 to regenerate them.");
            return 2;
        }

        // The shipped app always decodes greedily, so a check that cannot select another method has
        // no way to reproduce a decoder regression against real audio.
        var decoding = ArgValue(args, "--decoding") ?? TranscriptionDecoding.Greedy;
        Console.WriteLine($"  decoding={decoding}");

        using var service = new TranscriptionService(
            new ModelLocator(new AppPaths()),
            Options.Create(new TranscriptionOptions
            {
                DecodingMethod = decoding,
                AllowUnsafeDecodingMethod = true,
            }),
            NullLogger<TranscriptionService>.Instance);

        var load = Stopwatch.StartNew();
        service.Initialize();
        load.Stop();
        Console.WriteLine($"Model loaded in {load.ElapsedMilliseconds} ms");
        Console.WriteLine();

        var failures = 0;
        foreach (var fixture in fixtures)
        {
            var samples = WavReader.ReadMonoFloat(fixture.Path, out var sampleRate);
            var result = service.Transcribe(new CapturedAudio(samples, sampleRate));

            var overlap = WordOverlap(fixture.Text, result.Text);
            var ok = overlap >= MinimumWordOverlap;
            if (!ok) failures++;

            // RealTimeFactor is decode/audio, so invert it into the "x realtime" figure the
            // diagnostics panel and the model leaderboard both quote.
            var pace = result.RealTimeFactor > 0 ? 1 / result.RealTimeFactor : 0;

            Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {fixture.Name}");
            Console.WriteLine($"       expected: {fixture.Text}");
            Console.WriteLine($"       actual  : {result.Text}");
            Console.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "       overlap {0:P0} | {1:0.0}s audio decoded in {2:0} ms ({3:0.0}x realtime)",
                overlap,
                result.AudioDuration.TotalSeconds,
                result.DecodeDuration.TotalMilliseconds,
                pace));
            Console.WriteLine();
        }

        if (args.Contains("--long-audio"))
        {
            failures += RunLongAudioCheck(service, fixtures);
        }

        if (args.Contains("--channel-mix"))
        {
            failures += RunChannelMixCheck(service, fixtures);
        }

        if (args.Contains("--degraded"))
        {
            failures += RunDegradedAudioCheck(service, fixtures);
        }

        Console.WriteLine(failures == 0
            ? $"All {fixtures.Count} fixtures decoded correctly."
            : $"{failures} check(s) failed.");

        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Decodes progressively longer audio in a single shot and reports where, if anywhere, the
    /// recognizer stops producing text.
    /// <para>
    /// This exists because of a real user report. A Store user on 0.3.10 said dictation "cut out
    /// after seven to ten seconds"; their log showed the opposite of what that sounds like. Audio
    /// captured fine, all 37 seconds of it, and the recogniser then returned an <b>empty string</b>
    /// for every capture over about 13 s while everything under 11 s decoded correctly. Three of
    /// their six dictations were lost that way.
    /// </para>
    /// <para>
    /// sherpa-onnx documents VAD-segmented decoding (<c>sherpa-onnx-vad-with-offline-asr</c>) as the
    /// way to run Parakeet TDT over long audio, and Scribe feeds it one unsegmented span. This check
    /// measures how far that holds on the machine it runs on, which is the number nobody had.
    /// </para>
    /// </summary>
    private static int RunLongAudioCheck(TranscriptionService service, List<Fixture> fixtures)
    {
        Console.WriteLine("Long-audio single-shot decode");
        Console.WriteLine();

        var clips = fixtures
            .Select(f => WavReader.ReadMonoFloat(f.Path, out _))
            .Where(s => s.Length > 0)
            .ToList();
        if (clips.Count == 0)
        {
            Console.Error.WriteLine("No usable fixture audio for the long-audio check.");
            return 1;
        }

        const int sampleRate = 16_000;
        var gap = new float[sampleRate / 4]; // 250 ms, short enough to stay one VAD segment
        var failures = 0;

        foreach (var targetSeconds in new[] { 5, 10, 15, 20, 30, 45, 60, 90 })
        {
            var buffer = new List<float>(targetSeconds * sampleRate);
            for (var i = 0; buffer.Count < targetSeconds * sampleRate; i++)
            {
                buffer.AddRange(clips[i % clips.Count]);
                buffer.AddRange(gap);
            }

            var samples = buffer.Take(targetSeconds * sampleRate).ToArray();
            var result = service.Transcribe(new CapturedAudio(samples, sampleRate));

            // Characters per second of audio. A healthy decode of speech-dense fixtures sits around
            // 10-14; a collapse to zero, or to a couple of stray tokens, is the failure being hunted.
            var density = result.Text.Length / (double)targetSeconds;
            var collapsed = result.Text.Length == 0 || density < 2;
            if (collapsed) failures++;

            Console.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "[{0}] {1,3}s -> {2,5} chars ({3:0.0} chars/s) in {4:0} ms",
                collapsed ? "COLLAPSE" : "  ok    ",
                targetSeconds,
                result.Text.Length,
                density,
                result.DecodeDuration.TotalMilliseconds));
        }

        Console.WriteLine();
        return failures;
    }

    /// <summary>
    /// Measures what Scribe's multi-channel downmix does to a decode.
    /// <para>
    /// <see cref="Scribe.Core.Audio.MonoDownmixSampleProvider"/> averages every capture channel.
    /// That is right for a genuine stereo microphone and wrong for the two shapes a USB speakerphone
    /// or headset commonly presents: a second channel that is <b>silent</b> (averaging halves the
    /// speech, costing 6 dB), or a second channel carrying a <b>different signal</b> such as an
    /// echo-cancellation reference (averaging mixes foreign audio into the speech). Neither shows up
    /// as a fault: the level meter moves, VAD finds speech, and the recogniser quietly degrades.
    /// </para>
    /// <para>
    /// The 0.3.10 user whose log prompted this was on a 2-channel "Echo Cancelling Speakerphone",
    /// and even their successful decodes ran at ~7-11 chars/s against a ~13 chars/s clean baseline.
    /// </para>
    /// </summary>
    private static int RunChannelMixCheck(TranscriptionService service, List<Fixture> fixtures)
    {
        Console.WriteLine("Channel-downmix effect on decode");
        Console.WriteLine();

        const int sampleRate = 16_000;
        var clips = fixtures.Select(f => WavReader.ReadMonoFloat(f.Path, out _)).ToList();
        if (clips.Count < 2)
        {
            Console.Error.WriteLine("Need at least two fixtures for the channel-mix check.");
            return 1;
        }

        // One ~20 s speech buffer, and a second unrelated one to stand in for a reference channel.
        var speech = Concatenate(clips, sampleRate, seconds: 20, offset: 0);
        var foreign = Concatenate(clips, sampleRate, seconds: 20, offset: 1);

        var baseline = service.Transcribe(new CapturedAudio(speech, sampleRate));
        Report("mono, untouched (baseline)", baseline.Text, baseline, baseline);

        var halved = Scale(speech, 0.5f);
        var halvedResult = service.Transcribe(new CapturedAudio(halved, sampleRate));
        Report("2ch with a SILENT second channel (speech averaged to 0.5)", halvedResult.Text, halvedResult, baseline);

        var quartered = Scale(speech, 0.25f);
        var quarteredResult = service.Transcribe(new CapturedAudio(quartered, sampleRate));
        Report("4ch with three silent channels (speech averaged to 0.25)", quarteredResult.Text, quarteredResult, baseline);

        var contaminated = Average(speech, foreign);
        var contaminatedResult = service.Transcribe(new CapturedAudio(contaminated, sampleRate));
        Report("2ch with a FOREIGN second channel (AEC reference averaged in)", contaminatedResult.Text, contaminatedResult, baseline);

        Console.WriteLine();

        // Reported, never asserted. This measures how the pipeline degrades, and a threshold here
        // would only encode whatever this machine happened to do on the day it was written.
        return 0;

        static void Report(string label, string text, TranscriptionResult result, TranscriptionResult baseline)
        {
            var density = text.Length / result.AudioDuration.TotalSeconds;
            var retained = baseline.Text.Length == 0 ? 0 : text.Length / (double)baseline.Text.Length;
            Console.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "  {0,-62} {1,5} chars ({2,4:0.0}/s, {3:P0} of baseline)",
                label, text.Length, density, retained));
        }
    }

    /// <summary>
    /// Sweeps audio quality against duration, because the failure this was written for needs both.
    /// <para>
    /// Clean audio decodes correctly at 90 s (see <c>--long-audio</c>), and halving or contaminating
    /// the signal costs almost nothing (see <c>--channel-mix</c>), so neither length nor the downmix
    /// explains a user losing every capture over ~13 s. What that user had was a far-field
    /// <b>speakerphone</b>: low SNR and heavy room reverberation. This grid asks whether a transducer
    /// that copes with degraded audio at short durations stops coping as the sequence gets longer,
    /// which is the shape their log actually has.
    /// </para>
    /// </summary>
    private static int RunDegradedAudioCheck(TranscriptionService service, List<Fixture> fixtures)
    {
        Console.WriteLine("Degraded audio against duration (chars per second of audio)");
        Console.WriteLine();

        const int sampleRate = 16_000;
        var clips = fixtures.Select(f => WavReader.ReadMonoFloat(f.Path, out _)).ToList();
        int[] durations = [10, 20, 40];

        Console.WriteLine("  condition                   " + string.Join("", durations.Select(d => $"{d,10}s")));

        foreach (var (label, transform) in Conditions())
        {
            var cells = new List<string>();
            foreach (var seconds in durations)
            {
                var samples = transform(Concatenate(clips, sampleRate, seconds, offset: 0));
                var result = service.Transcribe(new CapturedAudio(samples, sampleRate));
                var density = result.Text.Length / (double)seconds;
                cells.Add(string.Format(CultureInfo.InvariantCulture, "{0,10:0.0}", density));
            }

            Console.WriteLine($"  {label,-28}" + string.Join("", cells));
        }

        Console.WriteLine();
        Console.WriteLine("  (a healthy decode of these fixtures is ~13 chars/s; 0.0 is a total collapse)");
        Console.WriteLine();

        // Diagnostic, not a gate. Numbers here depend on the machine and the fixture voice.
        return 0;

        static IEnumerable<(string Label, Func<float[], float[]> Transform)> Conditions()
        {
            yield return ("clean", s => s);
            yield return ("noise 20 dB SNR", s => AddNoise(s, 20));
            yield return ("noise 10 dB SNR", s => AddNoise(s, 10));
            yield return ("noise 5 dB SNR", s => AddNoise(s, 5));
            yield return ("noise 0 dB SNR", s => AddNoise(s, 0));
            yield return ("reverb (far-field)", s => AddReverb(s, 0.35f));
            yield return ("reverb + noise 10 dB", s => AddNoise(AddReverb(s, 0.35f), 10));
            yield return ("reverb heavy + noise 5 dB", s => AddNoise(AddReverb(s, 0.6f), 5));
        }
    }

    /// <summary>Adds white noise at a target signal-to-noise ratio, measured on RMS.</summary>
    private static float[] AddNoise(float[] samples, double snrDb)
    {
        double sumSquares = 0;
        foreach (var s in samples) sumSquares += s * (double)s;
        var signalRms = Math.Sqrt(sumSquares / Math.Max(1, samples.Length));
        var noiseRms = signalRms / Math.Pow(10, snrDb / 20);

        // Fixed seed: a sweep that changes its answer between runs is not a measurement.
        var random = new Random(20260820);
        var mixed = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            // Box-Muller for gaussian noise; uniform noise has the wrong spectral character.
            var u1 = 1.0 - random.NextDouble();
            var u2 = random.NextDouble();
            var gaussian = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            mixed[i] = (float)Math.Clamp(samples[i] + (gaussian * noiseRms), -1.0, 1.0);
        }

        return mixed;
    }

    /// <summary>
    /// Crude exponential-decay reverberation, standing in for a speakerphone sitting on a desk
    /// several feet from the speaker. Not an impulse response, just enough smearing to move the
    /// recogniser off clean-studio conditions.
    /// </summary>
    private static float[] AddReverb(float[] samples, float mix)
    {
        int[] delaysMs = [23, 37, 53, 79];
        var output = (float[])samples.Clone();
        foreach (var delayMs in delaysMs)
        {
            var delay = delayMs * 16;
            var decay = mix * 0.6f;
            for (var i = delay; i < output.Length; i++)
            {
                output[i] = Math.Clamp(output[i] + (output[i - delay] * decay), -1f, 1f);
            }
        }

        return output;
    }

    private static float[] Concatenate(List<float[]> clips, int sampleRate, int seconds, int offset)
    {
        var gap = new float[sampleRate / 4];
        var buffer = new List<float>(seconds * sampleRate);
        for (var i = 0; buffer.Count < seconds * sampleRate; i++)
        {
            buffer.AddRange(clips[(i + offset) % clips.Count]);
            buffer.AddRange(gap);
        }

        return buffer.Take(seconds * sampleRate).ToArray();
    }

    private static float[] Scale(float[] samples, float factor)
    {
        var scaled = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++) scaled[i] = samples[i] * factor;
        return scaled;
    }

    private static float[] Average(float[] a, float[] b)
    {
        var mixed = new float[a.Length];
        for (var i = 0; i < a.Length; i++) mixed[i] = (a[i] + (i < b.Length ? b[i] : 0f)) / 2f;
        return mixed;
    }

    private static string? ArgValue(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>Walks up from the binary to the repo root so the tool works via `dotnet run`.</summary>
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Scribe.slnx"))) return dir.FullName;
        }

        return null;
    }

    private static List<Fixture> LoadFixtures(string directory)
    {
        var manifestPath = Path.Combine(directory, "fixtures.json");
        if (!File.Exists(manifestPath)) return [];

        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var fixtures = new List<Fixture>();
        foreach (var entry in doc.RootElement.EnumerateObject())
        {
            var file = entry.Value.GetProperty("file").GetString()!;
            var text = entry.Value.GetProperty("text").GetString()!;
            fixtures.Add(new Fixture(entry.Name, Path.Combine(directory, file), text));
        }

        return fixtures;
    }

    /// <summary>
    /// Fraction of expected words that appear in the transcript. Deliberately order- and
    /// punctuation-insensitive: the pipeline is allowed to punctuate differently from the prompt,
    /// and this check asks "did the engine decode speech", not "how accurate is it".
    /// </summary>
    internal static double WordOverlap(string expected, string actual)
    {
        var expectedWords = Tokenize(expected);
        if (expectedWords.Count == 0) return 0;

        var actualWords = Tokenize(actual).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return expectedWords.Count(actualWords.Contains) / (double)expectedWords.Count;
    }

    private static List<string> Tokenize(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : new string(text.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ').ToArray())
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .ToList();

    private sealed record Fixture(string Name, string Path, string Text);
}
