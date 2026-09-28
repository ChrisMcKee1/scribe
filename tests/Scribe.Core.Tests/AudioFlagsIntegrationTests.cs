using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Scribe.Core.Audio;
using Scribe.Core.Diagnostics;
using Scribe.Core.Infrastructure;
using Scribe.Core.Models;
using Scribe.Core.Tests.Concurrency;
using Scribe.Core.Transcription;
using Scribe.Core.Vad;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// AUDIO-IR-03: the three audio flags, parsed explicitly and handed to the real services, over every committed speech
/// fixture, against the same services with every flag off. Each fixture goes through the dictation's stop as a 48 kHz
/// stereo float device would deliver it: the capture service on fake endpoints (no microphone) with the real conversion,
/// the real Silero VAD through the public token-bearing <see cref="IVadService.Trim(CapturedAudio, CancellationToken)"/>,
/// and the real recognizer. The two arms must give the same captured audio, trimmed audio, voiced time and transcript,
/// and each flag must show that it was active: the capture timing and VAD lines only in the enabled arm, the warm-up run
/// once there and never in the old arm, and a canceled token honoured only there. Like the other speech tests it returns
/// early when the models are not found.
/// </summary>
public sealed class AudioFlagsIntegrationTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string TimingWithAudio =
        "#{Id} capture timing: device open {OpenMs:F1} ms, stream start {StartMs:F1} ms, first packet " +
        "{FirstPacketMs:F1} ms after the stream start was requested, {Packets} packets; stream end seen {StopMs:F1} ms " +
        "after the stop request, signal analysis {AnalysisMs:F1} ms, conversion {ConversionMs:F1} ms.";

    private const string VadTiming = "VAD ran {Windows} windows over {AudioMs} ms of audio in {ElapsedMs:F1} ms (speech found: {Found}).";

    private static readonly WaveFormat DeviceFormat = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);

    private static readonly PerfFlags AudioFlags = PerfFlags.Parse(
        $"{PerfFlags.CaptureTimingDiagnostics},{PerfFlags.VadWindowCancellation},{PerfFlags.WarmManagedAudioPath}");

    [Fact]
    public void The_audio_flags_change_nothing_a_dictation_gets_and_each_one_is_active()
    {
        // A data root of the test's own, so nothing reads the app's data folder; the models come from SCRIBE_MODELS_DIR.
        var locator = new ModelLocator(new AppPaths(Path.Combine(Path.GetTempPath(), "ScribeAudioFlagsTest", "no-data-root")));
        var models = locator.Resolve();
        if (!models.AsrComplete || !models.VadAvailable) return;

        Assert.Equal(3, AudioFlags.On.Count);
        var fixtures = Directory.GetFiles(Path.Combine(RepositoryRoot(), "tests", "fixtures", "speech"), "*.wav")
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(fixtures);

        // The warm-up as the app runs it once the models are loaded: once in the enabled arm, never in the old one.
        var warmupLog = new CapturingLogger<ManagedAudioPathWarmup>();
        Assert.False(new ManagedAudioPathWarmup(PerfFlags.None, warmupLog).RunOnce());
        Assert.Empty(warmupLog.Entries);
        var warmup = new ManagedAudioPathWarmup(AudioFlags, warmupLog);
        Assert.True(warmup.RunOnce());
        Assert.False(warmup.RunOnce());
        Assert.Equal("Managed audio path warmed in {ElapsedMs:F1} ms.", Assert.Single(warmupLog.Entries).Value("{OriginalFormat}"));

        // One recognizer for both arms: no flag reaches it, so each arm's transcript depends only on the audio it trimmed.
        using var transcription = new TranscriptionService(
            locator, Options.Create(new TranscriptionOptions { NumThreads = 4 }), NullLogger<TranscriptionService>.Instance);
        var old = new Arm(PerfFlags.None, locator);
        var flagged = new Arm(AudioFlags, locator);
        using (old)
        using (flagged)
        {
            using var live = new CancellationTokenSource();
            var owner = 0L;
            CapturedAudio? sample = null;
            foreach (var fixture in fixtures)
            {
                var packets = DevicePackets(fixture);
                owner++;
                var expected = old.Dictate(packets, owner, transcription, live.Token);
                var actual = flagged.Dictate(packets, owner, transcription, live.Token);
                var name = Path.GetFileName(fixture);

                Assert.True(SameBits(expected.Captured, actual.Captured), $"{name}: the captured audio moved.");
                Assert.True(SameBits(expected.Trimmed, actual.Trimmed), $"{name}: the trimmed audio moved.");
                Assert.Equal(expected.SpeechSeconds, actual.SpeechSeconds);
                Assert.Equal(expected.Text, actual.Text);
                Assert.False(string.IsNullOrWhiteSpace(actual.Text), $"{name}: nothing was recognized.");
                output.WriteLine($"{name}: {actual.Captured.Length} samples, {actual.Trimmed.Length} trimmed, {actual.Text.Length} characters");
                sample ??= new CapturedAudio(actual.Captured, 16_000);
            }

            // Capture timing: one line with audio per enabled stop, and the VAD's own line; none of either in the old arm.
            Assert.Equal(fixtures.Length, flagged.CaptureLog.Entries.Count(entry => Template(entry) == TimingWithAudio));
            Assert.DoesNotContain(flagged.CaptureLog.Entries, entry => Template(entry).StartsWith("#{Id} capture timing:", StringComparison.Ordinal) && Template(entry) != TimingWithAudio);
            Assert.Equal(fixtures.Length, flagged.VadLog.Entries.Count(entry => Template(entry) == VadTiming));
            Assert.DoesNotContain(old.CaptureLog.Entries, entry => Template(entry).StartsWith("#{Id} capture timing:", StringComparison.Ordinal));
            Assert.DoesNotContain(old.VadLog.Entries, entry => Template(entry) == VadTiming);

            // Window cancellation: the enabled service honours a canceled token through the public call; the old one
            // does not look at it and trims as before.
            var canceled = new CancellationToken(canceled: true);
            Assert.Throws<OperationCanceledException>(() => flagged.Vad.Trim(sample!, canceled));
            Assert.False(old.Vad.Trim(sample!, canceled).IsEmpty);
        }
    }

    /// <summary>One side of the comparison: a capture service on fake endpoints and the real VAD, both with its flags.</summary>
    private sealed class Arm : IDisposable
    {
        private readonly FakeCaptureStack _stack = new() { CaptureFormat = DeviceFormat };
        private readonly AudioCaptureService _capture;
        private readonly VadService _vad;

        public Arm(PerfFlags flags, ModelLocator locator)
        {
            _capture = _stack.CreateService(TimeSpan.FromSeconds(5), flags);
            _vad = new VadService(locator, VadLog, flags);
        }

        public CapturingLogger<AudioCaptureService> CaptureLog => _stack.Log;

        public CapturingLogger<VadService> VadLog { get; } = new();

        /// <summary>The service through the interface the controller holds.</summary>
        public IVadService Vad => _vad;

        // The stop of one dictation as the controller runs it: the packets arrive, the release requests the stop, the
        // processing stops the capture, trims it with its lifetime token and decodes what is left.
        public Result Dictate(byte[][] packets, long owner, TranscriptionService transcription, CancellationToken token)
        {
            Assert.True(_capture.Start(owner: owner));
            var device = _stack.Capture;
            using var delivered = new CountdownEvent(packets.Length);
            void OnLevel(object? sender, float level) => delivered.Signal();
            _capture.LevelChanged += OnLevel;
            try
            {
                foreach (var packet in packets)
                {
                    device.Deliver(packet);
                }

                Assert.True(delivered.Wait(BlockedThreads.SafetyTimeout), "The fake capture thread did not deliver every packet.");
            }
            finally
            {
                _capture.LevelChanged -= OnLevel;
            }

            _capture.RequestStop(owner);
            var captured = _capture.Stop(owner);
            var trimmed = Vad.Trim(captured, token);
            var speech = Vad.LastSpeechSeconds;
            var text = trimmed.IsEmpty ? string.Empty : transcription.Transcribe(trimmed, token).Text;
            return new Result(captured.Samples, trimmed.Samples, speech, text);
        }

        public void Dispose()
        {
            _vad.Dispose();
            _capture.Dispose();
        }
    }

    private sealed record Result(float[] Captured, float[] Trimmed, double? SpeechSeconds, string Text);

    // The fixture as a 48 kHz stereo float device delivers it: resampled with NAudio's WDL resampler, the same signal on
    // both channels, in 10 ms packets.
    private static byte[][] DevicePackets(string wav)
    {
        using var reader = new AudioFileReader(wav);
        ISampleProvider source = reader.WaveFormat.Channels == 1 ? reader : new StereoToMonoSampleProvider(reader);
        var resampled = new WdlResamplingSampleProvider(source, DeviceFormat.SampleRate);

        var mono = new List<float>();
        var buffer = new float[DeviceFormat.SampleRate];
        int read;
        while ((read = resampled.Read(buffer.AsSpan())) > 0)
        {
            mono.AddRange(buffer.AsSpan(0, read).ToArray());
        }

        var framesPerPacket = DeviceFormat.SampleRate / 100;
        var packets = new List<byte[]>();
        for (var start = 0; start < mono.Count; start += framesPerPacket)
        {
            var frames = Math.Min(framesPerPacket, mono.Count - start);
            var interleaved = new float[frames * DeviceFormat.Channels];
            for (var frame = 0; frame < frames; frame++)
            {
                interleaved[frame * 2] = mono[start + frame];
                interleaved[(frame * 2) + 1] = mono[start + frame];
            }

            packets.Add(MemoryMarshal.AsBytes(interleaved.AsSpan()).ToArray());
        }

        return [.. packets];
    }

    private static bool SameBits(float[] expected, float[] actual) =>
        expected.Length == actual.Length
        && MemoryMarshal.AsBytes(expected.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(actual.AsSpan()));

    private static string Template(CapturedLogEntry entry) => entry.Value("{OriginalFormat}") as string ?? string.Empty;

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return root.FullName;
    }
}
