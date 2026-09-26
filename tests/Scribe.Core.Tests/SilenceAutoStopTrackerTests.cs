using System.Runtime.InteropServices;
using NAudio.Wave;
using Scribe.Core.Audio;
using Xunit;

namespace Scribe.Core.Tests;

public sealed class SilenceAutoStopTrackerTests
{
    private const float Voice = 0.3f;
    private const float Quiet = 0.005f;

    [Fact]
    public void Fires_after_the_hold_window_of_silence_following_speech()
    {
        var tracker = new SilenceAutoStopTracker(startedMs: 0, silenceHoldMs: 4_000);

        Assert.False(tracker.Update(Voice, 500));    // speaking; the window measures from here
        Assert.False(tracker.Update(Quiet, 1_000));  // silence begins
        Assert.False(tracker.Update(Quiet, 4_400));  // 3.9s since the last voice; not yet
        Assert.True(tracker.Update(Quiet, 4_500));   // 4.0s; stop
    }

    [Fact]
    public void Speech_resets_the_silence_window()
    {
        var tracker = new SilenceAutoStopTracker(startedMs: 0, silenceHoldMs: 4_000);

        tracker.Update(Voice, 500);
        tracker.Update(Quiet, 3_000);
        Assert.False(tracker.Update(Voice, 4_000));  // spoke again; window restarts
        Assert.False(tracker.Update(Quiet, 7_900));
        Assert.True(tracker.Update(Quiet, 8_000));
    }

    [Fact]
    public void A_constant_level_is_steady_noise_however_loud_and_ends_on_the_lead_in()
    {
        // This used to be "continuous speech never fires", but a level that never moves is exactly
        // what steady noise looks like, and steady noise must not hold the microphone open. Real
        // speech never holds one level; the synthetic-speech test below pins that case.
        var tracker = new SilenceAutoStopTracker(startedMs: 0, silenceHoldMs: 4_000, leadInLimitMs: 10_000);

        long? stoppedAt = null;
        for (long t = 10; t <= 30_000 && stoppedAt is null; t += 10)
        {
            if (tracker.Update(Voice, t))
            {
                stoppedAt = t;
            }
        }

        Assert.Equal(10_000, stoppedAt);
        Assert.False(tracker.HeardSpeech);
    }

    [Fact]
    public void Pure_silence_fires_at_the_lead_in_limit_so_a_muted_mic_cannot_stay_hot()
    {
        var tracker = new SilenceAutoStopTracker(startedMs: 0, silenceHoldMs: 4_000, leadInLimitMs: 10_000);

        Assert.False(tracker.Update(Quiet, 9_900));
        Assert.True(tracker.Update(Quiet, 10_000));
    }

    [Fact]
    public void Reports_whether_it_ever_heard_speech_and_how_loud_the_input_got()
    {
        // The two stops look identical to the user ("it cut out after a few seconds") and need
        // opposite fixes: one is the feature working, the other is a microphone that never
        // delivered a usable level. The log says which, so this is what it reads.
        var wentQuiet = new SilenceAutoStopTracker(startedMs: 0, silenceHoldMs: 4_000);
        wentQuiet.Update(Voice, 500);
        Assert.True(wentQuiet.Update(Quiet, 4_500));
        Assert.True(wentQuiet.HeardSpeech);
        Assert.Equal(Voice, wentQuiet.PeakLevel);

        var neverHeard = new SilenceAutoStopTracker(startedMs: 0, leadInLimitMs: 10_000);
        Assert.True(neverHeard.Update(Quiet, 10_000));
        Assert.False(neverHeard.HeardSpeech);
        Assert.Equal(Quiet, neverHeard.PeakLevel);
    }

    [Fact]
    public void Peak_level_survives_a_quiet_sample_after_a_loud_one()
    {
        var tracker = new SilenceAutoStopTracker(startedMs: 0);

        tracker.Update(0.42f, 100);
        tracker.Update(Quiet, 200);

        Assert.Equal(0.42f, tracker.PeakLevel);
    }

    [Fact]
    public void Lead_in_is_longer_than_the_post_speech_hold()
    {
        // A thinking pause before the first word must not cut the dictation off at the (shorter)
        // hold window; only the lead-in limit applies until speech is heard.
        var tracker = new SilenceAutoStopTracker(startedMs: 0, silenceHoldMs: 4_000, leadInLimitMs: 10_000);

        Assert.False(tracker.Update(Quiet, 5_000));  // 5s of thinking silence; still recording
        Assert.False(tracker.Update(Voice, 6_000));  // first words arrive
        Assert.False(tracker.Update(Quiet, 9_900));
        Assert.True(tracker.Update(Quiet, 10_000));  // 4s after the last word
    }

    // Scenarios below run real 10 ms sample buffers at 48 kHz through the production meter
    // (AudioCaptureService.ComputePeak), which is what the capture service hands the tracker.
    // Every signal is synthetic and seeded, so each case is deterministic.

    [Fact]
    public void Quiet_speech_peaking_at_minus_40_dBFS_is_heard_and_stops_after_the_hold_not_the_lead_in()
    {
        // The fixed 0.02 threshold never heard this: toggle mode ended it at the 10 s lead-in while
        // the user was still talking, although VAD and the recognizer decode such audio.
        var selfNoise = Pink(seconds: 24, rmsDbfs: -75, seed: 11);
        var audio = Mix(selfNoise, SyntheticSpeech(seconds: 15, peakDbfs: -40, seed: 5), startSeconds: 1);

        var outcome = Run(audio);

        Assert.True(outcome.HeardSpeech);
        Assert.InRange(outcome.StoppedAtMs!.Value, 16_000, 20_000);
    }

    [Fact]
    public void Loud_speech_survives_a_two_second_pause_and_stops_within_the_hold_after_the_last_word()
    {
        var room = Pink(seconds: 20, rmsDbfs: -60, seed: 3);
        var audio = Mix(room, SyntheticSpeech(seconds: 5, peakDbfs: -6, seed: 8), startSeconds: 1);
        audio = Mix(audio, SyntheticSpeech(seconds: 4, peakDbfs: -6, seed: 9), startSeconds: 8);

        var outcome = Run(audio);

        Assert.True(outcome.HeardSpeech);
        Assert.InRange(outcome.StoppedAtMs!.Value, 12_000, 16_000);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Steady_pink_noise_at_minus_30_dBFS_is_learned_and_ends_on_the_lead_in_as_no_speech(int seed)
    {
        // The fixed threshold counted every buffer of this noise as speech and never stopped.
        var tracker = new SilenceAutoStopTracker(startedMs: 0);
        var noise = Pink(seconds: 30, rmsDbfs: -30, seed: seed);

        var outcome = Run(noise, tracker);

        Assert.Equal(10_000, outcome.StoppedAtMs);
        Assert.False(outcome.HeardSpeech);
        Assert.True(tracker.VoiceThreshold > outcome.LoudestBufferPeak);
    }

    [Fact]
    public void Speech_in_steady_noise_is_heard_and_the_noise_alone_then_lets_the_capture_end()
    {
        var noise = Pink(seconds: 25, rmsDbfs: -30, seed: 4);
        var audio = Mix(noise, SyntheticSpeech(seconds: 10, peakDbfs: -6, seed: 6), startSeconds: 2);

        var outcome = Run(audio);

        Assert.True(outcome.HeardSpeech);
        Assert.InRange(outcome.StoppedAtMs!.Value, 12_000, 16_000);
    }

    [Fact]
    public void Noise_that_starts_mid_capture_is_learned_so_it_cannot_hold_the_capture_open()
    {
        // Speech in a quiet room, then a fan starts. Until the floor climbs to the fan the noise
        // reads as voice, so the capture ends later than the speech alone would have, but it ends.
        var room = Pink(seconds: 40, rmsDbfs: -65, seed: 7);
        var audio = Mix(room, SyntheticSpeech(seconds: 3, peakDbfs: -6, seed: 10), startSeconds: 1);
        audio = Mix(audio, Pink(seconds: 34, rmsDbfs: -30, seed: 12), startSeconds: 6);

        var outcome = Run(audio);

        Assert.True(outcome.HeardSpeech);
        Assert.InRange(outcome.StoppedAtMs!.Value, 6_000, 20_000);
    }

    [Fact]
    public void Noise_that_rises_gradually_is_followed_and_never_mistaken_for_speech()
    {
        // 1 dB/s from -60 to -20 dBFS, then level. A long lead-in makes any buffer that ever read as
        // voice visible: the stop would then come 4 s after it, not exactly at the lead-in.
        var tracker = new SilenceAutoStopTracker(startedMs: 0, leadInLimitMs: 60_000);
        var noise = Ramp(Pink(seconds: 62, rmsDbfs: 0, seed: 13), fromDbfs: -60, toDbfs: -20, overSeconds: 40);

        var outcome = Run(noise, tracker);

        Assert.Equal(60_000, outcome.StoppedAtMs);
        Assert.False(outcome.HeardSpeech);
    }

    [Fact]
    public void Speech_that_fades_gradually_is_heard_well_below_the_old_threshold()
    {
        // The speaker drifts away from the microphone: -10 to -55 dBFS peak over 30 s. The old
        // fixed -34 dBFS threshold lost this about 17 s in; the absolute floor keeps it until the
        // speech drops under about -45 dBFS, roughly 24 s in.
        var room = Pink(seconds: 40, rmsDbfs: -80, seed: 14);
        var speech = Ramp(SyntheticSpeech(seconds: 30, peakDbfs: 0, seed: 15), fromDbfs: -10, toDbfs: -55, overSeconds: 30);
        var audio = Mix(room, speech, startSeconds: 1);

        var outcome = Run(audio);

        Assert.True(outcome.HeardSpeech);
        Assert.InRange(outcome.StoppedAtMs!.Value, 24_000, 31_000);
    }

    [Fact]
    public void Digital_silence_ends_on_the_lead_in_as_no_speech()
    {
        var outcome = Run(new float[15 * SampleRate]);

        Assert.Equal(10_000, outcome.StoppedAtMs);
        Assert.False(outcome.HeardSpeech);
        Assert.Equal(0f, outcome.LoudestBufferPeak);
    }

    [Fact]
    public void Synthetic_speech_with_natural_gaps_never_stops_however_long_it_runs()
    {
        var room = Pink(seconds: 60, rmsDbfs: -60, seed: 16);
        var audio = Mix(room, SyntheticSpeech(seconds: 60, peakDbfs: -12, seed: 17), startSeconds: 0);

        var outcome = Run(audio);

        Assert.Null(outcome.StoppedAtMs);
        Assert.True(outcome.HeardSpeech);
    }

    // In the collection that runs alone (stream TR, item 1): nothing else in the process runs while it measures.
    [Collection(AllocationMeasurementCollection.Name)]
    public sealed class Allocations
    {
        [Fact]
        public void Updating_allocates_nothing()
        {
            // It runs once per capture buffer on the audio thread, for the whole dictation.
            var tracker = new SilenceAutoStopTracker(startedMs: 0, leadInLimitMs: long.MaxValue);
            for (var i = 1; i <= 1_000; i++)
            {
                tracker.Update(0.1f * (i % 7), i * 10L);
            }

            _ = RuntimeWork.Now().Since(RuntimeWork.Now());

            // The tracker allocates nothing, but with the whole suite running in parallel the runtime has been
            // seen to allocate about 3 KB on this thread during one measured pass (3 full-suite runs in 20,
            // never with this class alone). That is one-time work, while an allocation in Update recurs on
            // every pass of 100,000 buffers, so one clean pass proves the claim and a real allocation still
            // fails all five. (Stream TR kept these passes as they were, and runs the test alone.)
            var allocated = long.MaxValue;
            var during = default(RuntimeWork);
            var next = 1_001;
            for (var pass = 0; pass < 5 && allocated != 0; pass++)
            {
                var work = RuntimeWork.Now();
                var before = GC.GetAllocatedBytesForCurrentThread();
                Pass(next);
                allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                during = RuntimeWork.Now().Since(work);
                next += 100_000;
            }

            AllocationMeasurement.AssertZero(allocated, during, "The last of five passes of 100,000 updates", () => Pass(next));

            void Pass(int first)
            {
                for (var i = first; i < first + 100_000; i++)
                {
                    tracker.Update(i % 3 == 0 ? 0f : 0.05f * (i % 11), i * 10L);
                }
            }
        }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-0.1f)]
    [InlineData(1.5f)]
    [InlineData(float.NaN)]
    public void An_absolute_floor_outside_the_level_range_is_rejected(float absoluteFloor)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SilenceAutoStopTracker(0, absoluteFloor));
    }

    private const int SampleRate = 48_000;
    private const int SamplesPerBuffer = SampleRate / 100;
    private static readonly WaveFormat MonoFloat48k = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1);

    private sealed record Outcome(long? StoppedAtMs, bool HeardSpeech, float LoudestBufferPeak);

    private static Outcome Run(float[] samples, SilenceAutoStopTracker? tracker = null)
    {
        tracker ??= new SilenceAutoStopTracker(startedMs: 0);
        var loudest = 0f;
        for (var buffer = 0; (buffer + 1) * SamplesPerBuffer <= samples.Length; buffer++)
        {
            var span = samples.AsSpan(buffer * SamplesPerBuffer, SamplesPerBuffer);
            var peak = AudioCaptureService.ComputePeak(MemoryMarshal.AsBytes(span), MonoFloat48k);
            loudest = Math.Max(loudest, peak);
            var now = (buffer + 1) * 10L;
            if (tracker.Update(peak, now))
            {
                return new Outcome(now, tracker.HeardSpeech, loudest);
            }
        }

        return new Outcome(null, tracker.HeardSpeech, loudest);
    }

    /// <summary>Pink noise (Paul Kellet's filter over seeded Gaussian white noise) at an RMS level.</summary>
    private static float[] Pink(int seconds, double rmsDbfs, int seed)
    {
        var random = new Random(seed);
        double b0 = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0, b5 = 0, b6 = 0;
        var samples = new float[seconds * SampleRate];
        for (var i = 0; i < samples.Length; i++)
        {
            var white = Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
            b0 = (0.99886 * b0) + (white * 0.0555179);
            b1 = (0.99332 * b1) + (white * 0.0750759);
            b2 = (0.96900 * b2) + (white * 0.1538520);
            b3 = (0.86650 * b3) + (white * 0.3104856);
            b4 = (0.55000 * b4) + (white * 0.5329522);
            b5 = (-0.7616 * b5) - (white * 0.0168980);
            samples[i] = (float)(b0 + b1 + b2 + b3 + b4 + b5 + b6 + (white * 0.5362));
            b6 = white * 0.115926;
        }

        var rms = Math.Sqrt(samples.Average(sample => (double)sample * sample));
        var gain = (float)(Math.Pow(10, rmsDbfs / 20) / rms);
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] *= gain;
        }

        return samples;
    }

    /// <summary>
    /// Speech-shaped signal: voiced syllables of 120 to 260 ms on a 110 to 180 Hz harmonic
    /// complex, each rising and decaying, with a spread of 12 dB between syllables, short gaps
    /// inside words and longer ones between them, scaled so its loudest sample sits at the peak.
    /// </summary>
    private static float[] SyntheticSpeech(int seconds, double peakDbfs, int seed)
    {
        var random = new Random(seed);
        var samples = new float[seconds * SampleRate];
        var position = 0;
        var syllable = 0;
        while (position < samples.Length)
        {
            var length = (int)(SampleRate * (0.12 + (random.NextDouble() * 0.14)));
            var amplitude = Math.Pow(10, (random.NextDouble() - 0.5) * 12 / 20);
            var pitch = 110 + (random.NextDouble() * 70);
            for (var i = 0; i < length && position + i < samples.Length; i++)
            {
                var phase = 2 * Math.PI * pitch * (position + i) / SampleRate;
                var voice = Math.Sin(phase) + (0.5 * Math.Sin(2 * phase)) + (0.25 * Math.Sin(3 * phase));
                samples[position + i] = (float)(amplitude * Math.Sin(Math.PI * i / length) * voice);
            }

            position += length;
            var wordEnds = ++syllable % 3 == 0;
            position += (int)(SampleRate * (wordEnds ? 0.15 + (random.NextDouble() * 0.2) : 0.03 + (random.NextDouble() * 0.06)));
        }

        var loudest = samples.Max(Math.Abs);
        var gain = (float)(Math.Pow(10, peakDbfs / 20) / loudest);
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] *= gain;
        }

        return samples;
    }

    /// <summary>Applies a gain that moves linearly in dB from one level to another, then holds.</summary>
    private static float[] Ramp(float[] samples, double fromDbfs, double toDbfs, double overSeconds)
    {
        var ramped = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            var progress = Math.Min(1, i / (overSeconds * SampleRate));
            ramped[i] = (float)(samples[i] * Math.Pow(10, (fromDbfs + ((toDbfs - fromDbfs) * progress)) / 20));
        }

        return ramped;
    }

    private static float[] Mix(float[] bed, float[] overlay, double startSeconds)
    {
        var mixed = (float[])bed.Clone();
        var offset = (int)(startSeconds * SampleRate);
        for (var i = 0; i < overlay.Length && offset + i < mixed.Length; i++)
        {
            mixed[offset + i] += overlay[i];
        }

        return mixed;
    }
}
