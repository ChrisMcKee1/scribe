using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Scribe.Core.Infrastructure;
using Scribe.Core.Models;
using Scribe.Core.Transcription;

namespace Scribe.Evals.Benchmark;

/// <summary>The transcript every model is graded on, plus where it came from.</summary>
internal sealed record BenchInput(string Transcript, string Source, string? WavPath, double? AsrMs, double? AudioSeconds);

/// <summary>
/// Produces the single hard transcript fed to every model so the only variable is the cleanup model.
/// To honour the "run a WAV through the pipeline" ask, it best-effort synthesizes speech for the
/// authored passage (Windows SAPI, no extra package), runs it through Scribe's real Parakeet ASR, and
/// uses that real STT output as the canonical input — capturing a genuine ASR latency datapoint. If
/// TTS or ASR is unavailable it falls back to the authored transcript (which is already written to
/// look exactly like ASR output: lowercase, no punctuation, fillers, run-on, self-correction,
/// non-native grammar, and a verbatim literary quote the editor must preserve, not execute).
/// </summary>
internal static class BenchmarkInput
{
    public const string AuthoredRaw =
        "um okay so i need to uh send the quarterly report over to sarah on the finance team by friday " +
        "end of day and like make sure the q3 revenue numbers are in there you know the ones we was " +
        "talking about in the meeting last week where it went up like twelve percent uh send it on " +
        "tuesday no wait actually wednesday is better and honestly the report it need to be more better " +
        "and more clearer for the stakeholders cause last time they was confused and um at the very end " +
        "add a line that says we few we happy few we band of brothers and then just you know wrap it up " +
        "nicely thanks";

    public static async Task<BenchInput> PrepareAsync(
        string artifactsDir, string? modelsDir, bool synthesize, ILogger log, CancellationToken ct)
    {
        if (!synthesize)
        {
            return new BenchInput(AuthoredRaw, "authored", null, null, null);
        }

        try
        {
            Directory.CreateDirectory(artifactsDir);
            var wavPath = Path.Combine(artifactsDir, "benchmark-input.wav");
            SynthesizeWav(AuthoredRaw, wavPath);
            log.LogInformation("Synthesized benchmark WAV at {Path}.", wavPath);

            var resolvedModels = modelsDir ?? FindBundledModelsDir();
            if (resolvedModels is not null)
            {
                Environment.SetEnvironmentVariable("SCRIBE_MODELS_DIR", resolvedModels);
            }

            var (samples, seconds) = await Task.Run(() => LoadWavAsMono16k(wavPath), ct).ConfigureAwait(false);

            var paths = new AppPaths();
            var locator = new ModelLocator(paths);
            using var asr = new TranscriptionService(
                locator, Options.Create(new TranscriptionOptions()), NullLogger<TranscriptionService>.Instance);

            var sw = Stopwatch.StartNew();
            var result = await Task.Run(() =>
            {
                asr.Initialize();
                return asr.Transcribe(new CapturedAudio(samples, 16000));
            }, ct).ConfigureAwait(false);
            sw.Stop();

            var text = result.Text?.Trim() ?? string.Empty;
            if (text.Length >= 40)
            {
                return new BenchInput(text, "wav+asr (Parakeet)", wavPath, sw.Elapsed.TotalMilliseconds, seconds);
            }

            log.LogWarning("ASR output too short ({Len} chars); falling back to authored transcript.", text.Length);
            return new BenchInput(AuthoredRaw, "authored (ASR output too short)", wavPath, sw.Elapsed.TotalMilliseconds, seconds);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "WAV/ASR path failed; using the authored transcript.");
            return new BenchInput(AuthoredRaw, "authored (WAV/ASR unavailable)", null, null, null);
        }
    }

    // Windows SAPI via late-bound COM — no extra NuGet dependency. Writes 22 kHz 16-bit mono PCM.
    private static void SynthesizeWav(string text, string wavPath)
    {
        var voiceType = Type.GetTypeFromProgID("SAPI.SpVoice")
            ?? throw new PlatformNotSupportedException("SAPI.SpVoice is not registered.");
        var streamType = Type.GetTypeFromProgID("SAPI.SpFileStream")
            ?? throw new PlatformNotSupportedException("SAPI.SpFileStream is not registered.");

        dynamic voice = Activator.CreateInstance(voiceType)!;
        dynamic stream = Activator.CreateInstance(streamType)!;
        try
        {
            stream.Format.Type = 22;            // SAFT22kHz16BitMono
            stream.Open(wavPath, 3, false);     // SSFMCreateForWrite
            voice.AudioOutputStream = stream;
            voice.Rate = -1;                    // a touch slower → cleaner ASR
            voice.Speak(text, 0);               // SVSFDefault (synchronous)
        }
        finally
        {
            try { stream.Close(); } catch { /* best effort */ }
        }
    }

    private static (float[] Samples, double Seconds) LoadWavAsMono16k(string wavPath)
    {
        using var reader = new AudioFileReader(wavPath);
        ISampleProvider source = reader;
        if (reader.WaveFormat.Channels > 1)
        {
            source = new StereoToMonoSampleProvider(reader) { LeftVolume = 0.5f, RightVolume = 0.5f };
        }

        var resampler = new WdlResamplingSampleProvider(source, 16000);
        var samples = new List<float>(16000 * 30);
        var buffer = new float[16000];
        int read;
        while ((read = resampler.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                samples.Add(buffer[i]);
            }
        }

        var arr = samples.ToArray();
        return (arr, arr.Length / 16000.0);
    }

    // Walk up from the executable to find the source-bundled models (src/Scribe.App/models).
    private static string? FindBundledModelsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Scribe.App", "models");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
