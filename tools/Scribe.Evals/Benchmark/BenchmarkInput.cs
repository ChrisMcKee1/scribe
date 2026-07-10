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

/// <summary>
/// Produces the case transcripts fed to every model so the only variable is the cleanup model.
/// Each authored case is synthesized to speech (Windows SAPI, no extra package) and run through
/// Scribe's real Parakeet ASR, so models receive genuine speech-pipeline output, garbles included,
/// with a real ASR latency datapoint per case. If TTS or ASR is unavailable a case falls back to
/// its authored spoken text (already written to look like ASR output: lowercase, no punctuation,
/// fillers, run-ons, self-corrections).
/// </summary>
internal static class BenchmarkInput
{
    public static async Task<List<BenchCaseInput>> PrepareCasesAsync(
        string artifactsDir, string? modelsDir, bool synthesize, ILogger log, CancellationToken ct)
    {
        var cases = new List<BenchCaseInput>();
        if (!synthesize)
        {
            foreach (var c in BenchmarkCases.All)
            {
                cases.Add(new BenchCaseInput(
                    c.Id,
                    c.TranscriptOverride ?? c.Spoken,
                    c.Golden,
                    c.TranscriptOverride is null ? "authored" : "authored phonetic transcript",
                    null,
                    null,
                    null));
            }

            return cases;
        }

        Directory.CreateDirectory(artifactsDir);

        var resolvedModels = modelsDir ?? FindBundledModelsDir();
        if (resolvedModels is not null)
        {
            Environment.SetEnvironmentVariable("SCRIBE_MODELS_DIR", resolvedModels);
        }

        // One recognizer for the whole suite: model load dominates, transcription is fast.
        var paths = new AppPaths();
        var locator = new ModelLocator(paths);
        using var asr = new TranscriptionService(
            locator, Options.Create(new TranscriptionOptions()), NullLogger<TranscriptionService>.Instance);
        var asrReady = false;

        foreach (var c in BenchmarkCases.All)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var wavPath = Path.Combine(artifactsDir, $"case-{c.Id}.wav");
                SynthesizeWav(c.Spoken, c.SpeechMarkup, wavPath);
                var (samples, seconds) = await Task.Run(() => LoadWavAsMono16k(wavPath), ct).ConfigureAwait(false);

                var sw = Stopwatch.StartNew();
                var result = await Task.Run(() =>
                {
                    if (!asrReady)
                    {
                        asr.Initialize();
                        asrReady = true;
                    }

                    return asr.Transcribe(new CapturedAudio(samples, 16000));
                }, ct).ConfigureAwait(false);
                sw.Stop();

                var text = result.Text?.Trim() ?? string.Empty;
                var transcript = c.TranscriptOverride?.Trim() ?? text;
                if (transcript.Length >= 30)
                {
                    log.LogInformation(
                        "Case {Case}: WAV+ASR ok ({Ms:F0} ms, {Sec:F1}s audio){Override}.",
                        c.Id,
                        sw.Elapsed.TotalMilliseconds,
                        seconds,
                        c.TranscriptOverride is null ? string.Empty : "; using phonetic transcript override");
                    cases.Add(new BenchCaseInput(
                        c.Id,
                        transcript,
                        c.Golden,
                        c.TranscriptOverride is null ? "wav+asr (Parakeet)" : "wav+phonetic transcript",
                        wavPath,
                        sw.Elapsed.TotalMilliseconds,
                        seconds));
                    continue;
                }

                log.LogWarning("Case {Case}: ASR output too short ({Len} chars); using authored text.", c.Id, text.Length);
                cases.Add(new BenchCaseInput(
                    c.Id,
                    c.TranscriptOverride ?? c.Spoken,
                    c.Golden,
                    c.TranscriptOverride is null
                        ? "authored (ASR too short)"
                        : "authored phonetic transcript (ASR too short)",
                    wavPath,
                    sw.Elapsed.TotalMilliseconds,
                    seconds));
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Case {Case}: WAV/ASR path failed; using authored text.", c.Id);
                cases.Add(new BenchCaseInput(
                    c.Id,
                    c.TranscriptOverride ?? c.Spoken,
                    c.Golden,
                    c.TranscriptOverride is null
                        ? "authored (WAV/ASR unavailable)"
                        : "authored phonetic transcript (WAV/ASR unavailable)",
                    null,
                    null,
                    null));
            }
        }

        return cases;
    }

    // Windows SAPI via late-bound COM — no extra NuGet dependency. Writes 22 kHz 16-bit mono PCM.
    private static void SynthesizeWav(string text, string? speechMarkup, string wavPath)
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
            voice.Speak(speechMarkup ?? text, speechMarkup is null ? 0 : 8); // 8 = SVSFIsXML
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
