using System.Reflection;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;
using Scribe.Core.Audio;
using Scribe.Core.Diagnostics;
using Scribe.Core.Models;
using Scribe.Core.Vad;

namespace Scribe.Benchmarks;

/// <summary>
/// AUDIO-O-13 (<c>PerfFlags.WarmManagedAudioPath</c>): what the stop of the first dictation after a launch compiles, in a
/// fresh process, without the warm-up ("none") and after the warm-up the flag runs once the models are loaded
/// ("warm-up", only on a build that has it, found by reflection so this file also builds on the baseline): the first
/// conversion, the first VAD trim (which the warm-up does not touch: it measured no slower cold than warm) and the first
/// long capture's seam plan, and the warm-up's own cost. Run it as a cold start, where every measurement is the one
/// invocation of a process of its own: <c>--filter *ManagedAudioPathFirstCallBenchmarks* --strategy ColdStart
/// --launchCount 6 --warmupCount 0 --iterationCount 1</c>. Under the tool's default ShortRun job the same methods run
/// warm, which is the floor the warm-up can reach. Every arm loads the VAD model in its setup, as the app's warm-load
/// does before the warm-up runs; that needs the Silero model through <c>SCRIBE_MODELS_DIR</c>.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("Audio")]
public class ManagedAudioPathFirstCallBenchmarks
{
    private static readonly Type? WarmupType =
        typeof(AudioCaptureService).Assembly.GetType("Scribe.Core.Audio.ManagedAudioPathWarmup");

    private readonly WaveFormat _format = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);
    private CaptureRecording _recording = new([], reused: false);
    private CaptureScratchPool _pool = new();
    private VadService? _vad;
    private CapturedAudio _speech = CapturedAudio.Empty;
    private float[] _longSpeech = [];

    [ParamsSource(nameof(WarmUps))]
    public string WarmUp { get; set; } = string.Empty;

    public static IEnumerable<string> WarmUps => WarmupType is null ? ["none"] : ["none", "warm-up"];

    [GlobalSetup(Target = nameof(FirstConversion))]
    public void SetupConversion()
    {
        // The median dictation: 9.5 s of 48 kHz stereo float. Nothing of the conversion runs before the measurement.
        var raw = AudioBenchmarkInputs.DeviceCapture(_format, 9.5, seed: 95);
        _recording = new CaptureRecording(new byte[raw.Length], reused: false);
        _recording.Write(raw);
        _pool = new CaptureScratchPool();
        LoadTheVadAndWarmUp();
    }

    [GlobalSetup(Target = nameof(FirstTrim))]
    public void SetupTrim()
    {
        // 9.5 s of speech, read from the fixtures before the VAD loads, as a capture arrives before its trim.
        _speech = new CapturedAudio(AudioBenchmarkInputs.Speech(9.5), 16_000);
        LoadTheVadAndWarmUp();
    }

    [GlobalSetup(Target = nameof(WarmUpItself))]
    public void SetupWarmUpItself()
    {
        _vad = new VadService(AudioBenchmarkInputs.Models(), NullLogger<VadService>.Instance);
        _vad.Initialize();
    }

    [GlobalSetup(Target = nameof(FirstLongCapturePlan))]
    public void SetupLongCapturePlan()
    {
        // 61 s of speech: the first dictation over the 30 s chunk limit plans its seams.
        _longSpeech = AudioBenchmarkInputs.Speech(61);
        LoadTheVadAndWarmUp();
    }

    [GlobalCleanup]
    public void Cleanup() => _vad?.Dispose();

    [Benchmark]
    public CapturedAudio FirstConversion() => AudioCaptureService.ConvertCapture(_recording, _format, static _ => { }, null, _pool);

    [Benchmark]
    public CapturedAudio FirstTrim() => _vad!.Trim(_speech);

    /// <summary>The seam planner's first run in a process, over a capture two chunks long.</summary>
    [Benchmark]
    public int FirstLongCapturePlan() => Scribe.Core.Transcription.TranscriptionChunker.Plan(_longSpeech, 16_000).Count;

    /// <summary>What the warm-up itself costs the warm-load task in a fresh process (nothing on a build without it).</summary>
    [Benchmark]
    public bool WarmUpItself()
    {
        if (WarmupType is null)
        {
            return false;
        }

        RunTheWarmUp();
        return true;
    }

    // What the app's warm-load does before the first dictation: load the VAD, then (with the flag) warm the audio path.
    private void LoadTheVadAndWarmUp()
    {
        _vad = new VadService(AudioBenchmarkInputs.Models(), NullLogger<VadService>.Instance);
        _vad.Initialize();
        if (!_vad.IsAvailable)
        {
            throw new InvalidOperationException("The Silero VAD model was not found; set SCRIBE_MODELS_DIR to the models folder.");
        }

        if (WarmUp == "warm-up")
        {
            RunTheWarmUp();
        }
    }

    // The warm-up exactly as the app runs it: ManagedAudioPathWarmup with the flag on.
    private static void RunTheWarmUp()
    {
        var logger = typeof(NullLogger<>).MakeGenericType(WarmupType!).GetField("Instance")!.GetValue(null);
        var warmup = Activator.CreateInstance(WarmupType!, PerfFlags.Parse("WarmManagedAudioPath"), logger)!;
        var ran = (bool)WarmupType!.GetMethod("RunOnce", BindingFlags.Instance | BindingFlags.Public)!.Invoke(warmup, null)!;
        if (!ran)
        {
            throw new InvalidOperationException("The warm-up did not run.");
        }
    }
}
