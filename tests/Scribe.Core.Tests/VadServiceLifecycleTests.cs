using Scribe.Core.Models;
using Scribe.Core.Tests.Concurrency;
using Scribe.Core.Vad;

namespace Scribe.Core.Tests;

/// <summary>
/// VadService's own contract, driven through a fake detector so none of it loads Silero: the trim is exactly the
/// detected span, and load plus trim is one step against Unload and Dispose, whichever arrives first. The old shape
/// loaded outside the gate, so an Unload landing in between turned Trim into a silent pass-through of untrimmed audio.
/// </summary>
public sealed class VadServiceLifecycleTests
{
    private const int Rate = 16_000;

    [Fact]
    public void Trim_returns_exactly_the_detected_span_and_the_summed_voiced_time()
    {
        var detectors = new FakeDetectors { Segments = [(16_000, 8_000), (32_000, 4_000)] };
        using var vad = new VadService(detectors.Load, new CapturingLogger<VadService>());
        var audio = Numbered(seconds: 3);

        var trimmed = vad.Trim(audio);

        Assert.Equal(20_000, trimmed.Samples.Length); // first segment start to last segment end
        Assert.Equal(audio.Samples.AsSpan(16_000, 20_000).ToArray(), trimmed.Samples);
        Assert.Equal(12_000 / (double)Rate, vad.LastSpeechSeconds);
        Assert.Equal(audio.Samples.Length / detectors.WindowSize, detectors.Accepted);
    }

    [Fact]
    public void No_detected_speech_rejects_the_capture()
    {
        var detectors = new FakeDetectors();
        using var vad = new VadService(detectors.Load, new CapturingLogger<VadService>());

        Assert.True(vad.Trim(Numbered(seconds: 1)).IsEmpty);
        Assert.Null(vad.LastSpeechSeconds);
    }

    [Fact]
    public void A_missing_model_passes_audio_through_unchanged()
    {
        var detectors = new FakeDetectors { ModelMissing = true };
        using var vad = new VadService(detectors.Load, new CapturingLogger<VadService>());
        var audio = Numbered(seconds: 1);

        Assert.Same(audio, vad.Trim(audio));
        Assert.False(vad.IsAvailable);
    }

    [Fact]
    public void An_unload_before_trim_is_undone_by_a_reload_and_the_capture_is_still_trimmed()
    {
        var detectors = new FakeDetectors { Segments = [(16_000, 8_000)] };
        using var vad = new VadService(detectors.Load, new CapturingLogger<VadService>());
        vad.Initialize();
        vad.Unload();
        Assert.False(vad.IsAvailable);

        var audio = Numbered(seconds: 2);
        var trimmed = vad.Trim(audio);

        Assert.NotSame(audio, trimmed);
        Assert.Equal(8_000, trimmed.Samples.Length);
        Assert.Equal(2, detectors.Loads);
        Assert.Null(detectors.Violation);
    }

    /// <summary>
    /// The audit's exact interleaving: Trim decided the model was loaded, an Unload then ran, and Trim returned the
    /// capture untrimmed without saying so.
    /// </summary>
    [Fact]
    public void A_trim_queued_behind_an_unload_reloads_instead_of_passing_audio_through()
    {
        using var inDispose = new ManualResetEventSlim();
        using var releaseDispose = new ManualResetEventSlim();
        var detectors = new FakeDetectors { Segments = [(16_000, 8_000)] };
        using var vad = new VadService(detectors.Load, new CapturingLogger<VadService>());
        vad.Initialize();
        vad.Trim(CapturedAudio.Empty); // compiles the call path before the race
        detectors.OnDispose = () =>
        {
            inDispose.Set();
            releaseDispose.Wait();
        };

        var unloader = BlockedThreads.Start(vad.Unload);
        Assert.True(inDispose.Wait(BlockedThreads.SafetyTimeout));

        var audio = Numbered(seconds: 2);
        CapturedAudio? trimmed = null;
        var trimmer = BlockedThreads.Start(() => trimmed = vad.Trim(audio));
        BlockedThreads.WaitUntilBlocked(trimmer);

        detectors.OnDispose = null;
        releaseDispose.Set();
        BlockedThreads.Join(unloader);
        BlockedThreads.Join(trimmer);

        Assert.NotSame(audio, trimmed);
        Assert.Equal(8_000, trimmed!.Samples.Length);
        Assert.Equal(2, detectors.Loads);
        Assert.Null(detectors.Violation);
    }

    [Fact]
    public void An_unload_arriving_during_a_trim_waits_and_never_frees_the_detector_underneath()
    {
        using var inTrim = new ManualResetEventSlim();
        using var releaseTrim = new ManualResetEventSlim();
        var detectors = new FakeDetectors { Segments = [(16_000, 8_000)] };
        using var vad = new VadService(detectors.Load, new CapturingLogger<VadService>());
        vad.Unload(); // compiles the call path; a no-op before anything is loaded
        detectors.OnAccept = () =>
        {
            inTrim.Set();
            releaseTrim.Wait();
        };

        CapturedAudio? trimmed = null;
        var trimmer = BlockedThreads.Start(() => trimmed = vad.Trim(Numbered(seconds: 2)));
        Assert.True(inTrim.Wait(BlockedThreads.SafetyTimeout));

        var unloader = BlockedThreads.Start(vad.Unload);
        BlockedThreads.WaitUntilBlocked(unloader);
        Assert.Equal(0, detectors.Disposals);

        detectors.OnAccept = null;
        releaseTrim.Set();
        BlockedThreads.Join(trimmer);
        BlockedThreads.Join(unloader);

        Assert.Equal(8_000, trimmed!.Samples.Length);
        Assert.Equal(1, detectors.Disposals);
        Assert.False(vad.IsAvailable);
        Assert.Null(detectors.Violation);
    }

    [Fact]
    public void Dispose_during_a_trim_waits_for_it_and_nothing_loads_afterwards()
    {
        using var inTrim = new ManualResetEventSlim();
        using var releaseTrim = new ManualResetEventSlim();
        var detectors = new FakeDetectors { Segments = [(16_000, 8_000)] };
        var vad = new VadService(detectors.Load, new CapturingLogger<VadService>());
        detectors.OnAccept = () =>
        {
            inTrim.Set();
            releaseTrim.Wait();
        };

        CapturedAudio? trimmed = null;
        var trimmer = BlockedThreads.Start(() => trimmed = vad.Trim(Numbered(seconds: 2)));
        Assert.True(inTrim.Wait(BlockedThreads.SafetyTimeout));

        var disposer = BlockedThreads.Start(vad.Dispose);
        BlockedThreads.WaitUntilBlocked(disposer);
        Assert.Equal(0, detectors.Disposals);

        detectors.OnAccept = null;
        releaseTrim.Set();
        BlockedThreads.Join(trimmer);
        BlockedThreads.Join(disposer);

        Assert.Equal(8_000, trimmed!.Samples.Length);
        Assert.Equal(1, detectors.Disposals);
        Assert.Throws<ObjectDisposedException>(() => vad.Trim(Numbered(seconds: 1)));
        Assert.Throws<ObjectDisposedException>(vad.Initialize);
        Assert.Equal(1, detectors.Loads);
        Assert.Null(detectors.Violation);
    }

    [Fact]
    public void A_trim_after_dispose_is_refused_without_loading()
    {
        var detectors = new FakeDetectors();
        var vad = new VadService(detectors.Load, new CapturingLogger<VadService>());
        vad.Dispose();

        Assert.Throws<ObjectDisposedException>(() => vad.Trim(Numbered(seconds: 1)));
        Assert.Equal(0, detectors.Loads);
        Assert.False(vad.IsAvailable);
    }

    [Fact]
    public void A_trim_queued_behind_dispose_is_refused_and_never_reloads_the_detector()
    {
        using var inDispose = new ManualResetEventSlim();
        using var releaseDispose = new ManualResetEventSlim();
        var detectors = new FakeDetectors { Segments = [(16_000, 8_000)] };
        var vad = new VadService(detectors.Load, new CapturingLogger<VadService>());
        vad.Initialize();
        vad.Trim(CapturedAudio.Empty); // compiles the call path before the race
        detectors.OnDispose = () =>
        {
            inDispose.Set();
            releaseDispose.Wait();
        };

        var disposer = BlockedThreads.Start(vad.Dispose);
        Assert.True(inDispose.Wait(BlockedThreads.SafetyTimeout));

        var audio = Numbered(seconds: 2);
        Exception? failure = null;
        var trimmer = BlockedThreads.Start(() =>
        {
            try
            {
                vad.Trim(audio);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        BlockedThreads.WaitUntilBlocked(trimmer);

        releaseDispose.Set();
        BlockedThreads.Join(disposer);
        BlockedThreads.Join(trimmer);

        // Refused outright: neither a silent pass-through of untrimmed audio nor a detector brought back to life.
        Assert.IsType<ObjectDisposedException>(failure);
        Assert.Equal(1, detectors.Loads);
        Assert.Equal(0, detectors.Accepted);
    }

    // Each sample carries its own index, so a trimmed span can be checked against the exact source slice.
    private static CapturedAudio Numbered(int seconds)
    {
        var samples = new float[Rate * seconds];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = i / (float)samples.Length;
        }

        return new CapturedAudio(samples, Rate);
    }

    private sealed class FakeDetectors
    {
        private int _loads;
        private int _disposals;
        private int _accepted;

        public int WindowSize { get; } = 512;

        public bool ModelMissing { get; init; }

        /// <summary>Segments (absolute start, length) the detector reports once the capture is flushed.</summary>
        public IReadOnlyList<(int Start, int Length)> Segments { get; init; } = [];

        public Action? OnAccept { get; set; }

        public Action? OnDispose { get; set; }

        public int Loads => Volatile.Read(ref _loads);

        public int Disposals => Volatile.Read(ref _disposals);

        public int Accepted => Volatile.Read(ref _accepted);

        /// <summary>Set when the detector is freed while in use, or used after being freed.</summary>
        public string? Violation { get; private set; }

        public ISpeechSegmentDetector? Load()
        {
            Interlocked.Increment(ref _loads);
            return ModelMissing ? null : new Detector(this);
        }

        private sealed class Detector(FakeDetectors owner) : ISpeechSegmentDetector
        {
            private readonly Queue<(int Start, int Length)> _pending = new();
            private int _inUse;
            private bool _disposed;

            public int WindowSize => owner.WindowSize;

            public void Reset() => Use(_pending.Clear);

            public void AcceptWaveform(float[] window) => Use(() =>
            {
                Interlocked.Increment(ref owner._accepted);
                owner.OnAccept?.Invoke();
            });

            public void Flush() => Use(() =>
            {
                foreach (var segment in owner.Segments)
                {
                    _pending.Enqueue(segment);
                }
            });

            public bool TryPopSegment(out int start, out int length)
            {
                var popped = (Start: 0, Length: 0);
                var found = false;
                Use(() => found = _pending.TryDequeue(out popped));
                (start, length) = popped;
                return found;
            }

            public void Dispose()
            {
                if (Volatile.Read(ref _inUse) > 0)
                {
                    owner.Violation = "disposed while in use";
                }

                owner.OnDispose?.Invoke();
                _disposed = true;
                Interlocked.Increment(ref owner._disposals);
            }

            private void Use(Action action)
            {
                if (_disposed)
                {
                    owner.Violation = "used after dispose";
                }

                Interlocked.Increment(ref _inUse);
                try
                {
                    action();
                }
                finally
                {
                    Interlocked.Decrement(ref _inUse);
                }
            }
        }
    }
}
