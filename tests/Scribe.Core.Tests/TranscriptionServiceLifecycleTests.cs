using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Scribe.Core.Models;
using Scribe.Core.Tests.Concurrency;
using Scribe.Core.Transcription;

namespace Scribe.Core.Tests;

/// <summary>
/// TranscriptionService's own contract, driven through a fake recognizer so none of it loads sherpa-onnx: the
/// recognized text never reaches the log; ensure-ready plus decode is one step against Unload and Dispose, whichever
/// arrives first; cancellation is cooperative and never yields a partial transcript; and a cold load is reported apart
/// from the decode it delayed.
/// </summary>
public sealed class TranscriptionServiceLifecycleTests
{
    // Distinctive enough that finding any fragment of it in a log can only mean the transcript leaked.
    private const string Transcript = "Quetzalcoatl ferried 4417 marmalade crates";

    [Fact]
    public void The_decode_log_carries_counts_and_timings_but_never_the_transcript()
    {
        var logger = new CapturingLogger<TranscriptionService>();
        var engine = new FakeEngine { Text = Transcript };
        using var service = new TranscriptionService(engine.Load, logger);

        var result = service.Transcribe(Speech(seconds: 2));

        Assert.Equal(Transcript, result.Text);
        var decode = Assert.Single(logger.Entries, entry => entry.Message.StartsWith("Decoded ", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Debug, decode.Level);
        Assert.Equal(Transcript.Length, Convert.ToInt32(decode.Value("Chars")));
        Assert.Equal(2000, Convert.ToInt32(decode.Value("AudioMs")));
        Assert.NotNull(decode.Value("DecodeMs"));
        Assert.NotNull(decode.Value("LoadMs"));
        AssertNoFragmentOf(Transcript, logger.Entries);
    }

    [Fact]
    public void A_chunked_decode_logs_no_transcript_either()
    {
        var logger = new CapturingLogger<TranscriptionService>();
        var engine = new FakeEngine { DecodeText = index => $"{Transcript} part {index}" };
        using var service = new TranscriptionService(engine.Load, logger);

        var result = service.Transcribe(Speech(seconds: 65));

        Assert.Equal(3, engine.Decodes);
        Assert.Contains("part 3", result.Text, StringComparison.Ordinal);
        AssertNoFragmentOf(Transcript, logger.Entries);
    }

    [Fact]
    public void An_unload_before_transcribe_is_undone_by_a_reload_inside_the_same_call()
    {
        var engine = new FakeEngine();
        using var service = new TranscriptionService(engine.Load, Logger());
        service.Initialize();
        service.Unload();
        Assert.False(service.IsReady);

        var result = service.Transcribe(Speech(seconds: 1));

        Assert.Equal(engine.Text, result.Text);
        Assert.Equal(FakeEngine.ModelId, result.ModelId);
        Assert.True(service.IsReady);
        Assert.Equal(2, engine.Loads);
        Assert.Null(engine.Violation);
    }

    /// <summary>
    /// The audit's exact interleaving. Transcribe used to read IsReady outside the gate; while an Unload was still
    /// disposing, IsReady was true, so the reload was skipped and the call then found no recognizer and failed the
    /// dictation with "Recognizer is not initialized."
    /// </summary>
    [Fact]
    public void A_transcribe_queued_behind_an_unload_reloads_instead_of_failing()
    {
        using var inDispose = new ManualResetEventSlim();
        using var releaseDispose = new ManualResetEventSlim();
        var engine = new FakeEngine();
        using var service = new TranscriptionService(engine.Load, Logger());
        service.Initialize();
        service.Transcribe(CapturedAudio.Empty); // compiles the call path before the race
        engine.OnDispose = () =>
        {
            inDispose.Set();
            releaseDispose.Wait();
        };

        var unloader = BlockedThreads.Start(service.Unload);
        Assert.True(inDispose.Wait(BlockedThreads.SafetyTimeout));
        Assert.True(service.IsReady); // what the old outside-the-gate check saw at this moment

        TranscriptionResult? result = null;
        Exception? failure = null;
        var decoder = BlockedThreads.Start(() =>
        {
            try
            {
                result = service.Transcribe(Speech(seconds: 1));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        BlockedThreads.WaitUntilBlocked(decoder);

        engine.OnDispose = null;
        releaseDispose.Set();
        BlockedThreads.Join(unloader);
        BlockedThreads.Join(decoder);

        Assert.Null(failure);
        Assert.Equal(engine.Text, result!.Text);
        Assert.Equal(2, engine.Loads);
        Assert.Null(engine.Violation);
    }

    [Fact]
    public void An_unload_arriving_during_a_decode_waits_and_never_frees_the_recognizer_underneath()
    {
        using var inDecode = new ManualResetEventSlim();
        using var releaseDecode = new ManualResetEventSlim();
        var engine = new FakeEngine();
        using var service = new TranscriptionService(engine.Load, Logger());
        service.Unload(); // compiles the call path; a no-op before anything is loaded
        engine.OnDecode = _ =>
        {
            inDecode.Set();
            releaseDecode.Wait();
        };

        TranscriptionResult? result = null;
        var decoder = BlockedThreads.Start(() => result = service.Transcribe(Speech(seconds: 1)));
        Assert.True(inDecode.Wait(BlockedThreads.SafetyTimeout));

        var unloader = BlockedThreads.Start(service.Unload);
        BlockedThreads.WaitUntilBlocked(unloader);
        Assert.Equal(0, engine.Disposals);

        releaseDecode.Set();
        BlockedThreads.Join(decoder);
        BlockedThreads.Join(unloader);

        Assert.Equal(engine.Text, result!.Text);
        Assert.Equal(1, engine.Disposals);
        Assert.False(service.IsReady);
        Assert.Null(engine.Violation);
    }

    [Fact]
    public void Dispose_during_a_decode_waits_for_it_and_nothing_loads_afterwards()
    {
        using var inDecode = new ManualResetEventSlim();
        using var releaseDecode = new ManualResetEventSlim();
        var engine = new FakeEngine();
        var service = new TranscriptionService(engine.Load, Logger());
        engine.OnDecode = _ =>
        {
            inDecode.Set();
            releaseDecode.Wait();
        };

        TranscriptionResult? result = null;
        var decoder = BlockedThreads.Start(() => result = service.Transcribe(Speech(seconds: 1)));
        Assert.True(inDecode.Wait(BlockedThreads.SafetyTimeout));

        var disposer = BlockedThreads.Start(service.Dispose);
        BlockedThreads.WaitUntilBlocked(disposer);
        Assert.Equal(0, engine.Disposals);

        engine.OnDecode = null;
        releaseDecode.Set();
        BlockedThreads.Join(decoder);
        BlockedThreads.Join(disposer);

        Assert.Equal(engine.Text, result!.Text);
        Assert.Equal(1, engine.Disposals);
        Assert.Throws<ObjectDisposedException>(() => service.Transcribe(Speech(seconds: 1)));
        Assert.Throws<ObjectDisposedException>(service.Initialize);
        Assert.Equal(1, engine.Loads);
        Assert.Null(engine.Violation);
    }

    [Fact]
    public void A_transcribe_queued_behind_dispose_is_refused_and_never_resurrects_the_engine()
    {
        using var inDispose = new ManualResetEventSlim();
        using var releaseDispose = new ManualResetEventSlim();
        var engine = new FakeEngine();
        var service = new TranscriptionService(engine.Load, Logger());
        service.Initialize();
        service.Transcribe(CapturedAudio.Empty); // compiles the call path before the race
        engine.OnDispose = () =>
        {
            inDispose.Set();
            releaseDispose.Wait();
        };

        var disposer = BlockedThreads.Start(service.Dispose);
        Assert.True(inDispose.Wait(BlockedThreads.SafetyTimeout));

        Exception? failure = null;
        var decoder = BlockedThreads.Start(() =>
        {
            try
            {
                service.Transcribe(Speech(seconds: 1));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        BlockedThreads.WaitUntilBlocked(decoder);

        releaseDispose.Set();
        BlockedThreads.Join(disposer);
        BlockedThreads.Join(decoder);

        Assert.IsType<ObjectDisposedException>(failure);
        Assert.Equal(1, engine.Loads);
        Assert.Equal(0, engine.Decodes);
    }

    [Fact]
    public void A_call_canceled_before_it_starts_loads_nothing()
    {
        var engine = new FakeEngine();
        using var service = new TranscriptionService(engine.Load, Logger());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => service.Transcribe(Speech(seconds: 1), cts.Token));
        Assert.Equal(0, engine.Loads);
        Assert.False(service.IsReady);
    }

    [Fact]
    public void A_call_canceled_while_queued_for_the_engine_gives_up_without_decoding()
    {
        using var inDecode = new ManualResetEventSlim();
        using var releaseDecode = new ManualResetEventSlim();
        var engine = new FakeEngine();
        using var service = new TranscriptionService(engine.Load, Logger());
        service.Transcribe(CapturedAudio.Empty); // compiles the call path before the race
        engine.OnDecode = _ =>
        {
            inDecode.Set();
            releaseDecode.Wait();
        };

        var first = BlockedThreads.Start(() => service.Transcribe(Speech(seconds: 1)));
        Assert.True(inDecode.Wait(BlockedThreads.SafetyTimeout));

        using var cts = new CancellationTokenSource();
        Exception? failure = null;
        var second = BlockedThreads.Start(() =>
        {
            try
            {
                service.Transcribe(Speech(seconds: 1), cts.Token);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        BlockedThreads.WaitUntilBlocked(second);
        cts.Cancel();

        engine.OnDecode = null;
        releaseDecode.Set();
        BlockedThreads.Join(first);
        BlockedThreads.Join(second);

        Assert.IsType<OperationCanceledException>(failure);
        Assert.Equal(1, engine.Decodes);
    }

    [Fact]
    public void Cancellation_during_the_load_stops_before_the_first_decode_and_keeps_the_load()
    {
        using var cts = new CancellationTokenSource();
        var engine = new FakeEngine { OnLoad = cts.Cancel };
        using var service = new TranscriptionService(engine.Load, Logger());

        Assert.Throws<OperationCanceledException>(() => service.Transcribe(Speech(seconds: 1), cts.Token));
        Assert.Equal(0, engine.Decodes);
        Assert.True(service.IsReady); // the next dictation reuses it instead of loading again
    }

    [Fact]
    public void Cancellation_between_chunks_throws_instead_of_returning_a_partial_transcript()
    {
        using var cts = new CancellationTokenSource();
        var engine = new FakeEngine
        {
            DecodeText = index => $"part {index}",
            OnDecode = index =>
            {
                if (index == 1)
                {
                    cts.Cancel();
                }
            },
        };
        using var service = new TranscriptionService(engine.Load, Logger());

        Assert.Throws<OperationCanceledException>(() => service.Transcribe(Speech(seconds: 65), cts.Token));

        // The chunk in progress ran to completion (a native decode cannot be interrupted); nothing after it ran.
        Assert.Equal(1, engine.Decodes);
        Assert.Null(engine.Violation);
    }

    [Fact]
    public void An_uncanceled_long_capture_decodes_every_chunk_in_order()
    {
        using var cts = new CancellationTokenSource();
        var engine = new FakeEngine { DecodeText = index => $"part {index}" };
        using var service = new TranscriptionService(engine.Load, Logger());

        var result = service.Transcribe(Speech(seconds: 65), cts.Token);

        Assert.Equal("part 1 part 2 part 3", result.Text);
    }

    [Fact]
    public void A_cold_load_is_reported_apart_from_the_decode_it_delayed()
    {
        // A guaranteed minimum load time, measured rather than slept, so the claim below is about attribution and not
        // about how fast this machine is.
        var minimumLoad = TimeSpan.FromMilliseconds(400);
        var logger = new CapturingLogger<TranscriptionService>();
        var engine = new FakeEngine
        {
            OnLoad = () =>
            {
                var started = Stopwatch.GetTimestamp();
                while (Stopwatch.GetElapsedTime(started) < minimumLoad)
                {
                    Thread.SpinWait(1_000);
                }
            },
        };
        using var service = new TranscriptionService(engine.Load, logger);

        var cold = service.Transcribe(Speech(seconds: 1));
        var warm = service.Transcribe(Speech(seconds: 1));

        Assert.True(
            cold.DecodeDuration < minimumLoad,
            $"The decode duration ({cold.DecodeDuration.TotalMilliseconds} ms) must not include the model load.");
        var decodes = logger.Entries.Where(entry => entry.Message.StartsWith("Decoded ", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, decodes.Count);
        Assert.True(Convert.ToInt64(decodes[0].Value("LoadMs")) >= (long)minimumLoad.TotalMilliseconds);
        Assert.Equal(0L, Convert.ToInt64(decodes[1].Value("LoadMs")));
        Assert.True(warm.DecodeDuration < minimumLoad);
    }

    [Fact]
    public void Empty_audio_short_circuits_before_any_load_or_cancellation_check()
    {
        var engine = new FakeEngine();
        using var service = new TranscriptionService(engine.Load, Logger());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.True(service.Transcribe(CapturedAudio.Empty, cts.Token).IsEmpty);
        Assert.Equal(0, engine.Loads);
    }

    private static CapturedAudio Speech(int seconds)
    {
        // Low-level alternating samples: never silence, so the chunk planner has no quiet preference to follow.
        var samples = new float[16_000 * seconds];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (i & 1) == 0 ? 0.1f : -0.1f;
        }

        return new CapturedAudio(samples, 16_000);
    }

    private static CapturingLogger<TranscriptionService> Logger() => new();

    private static void AssertNoFragmentOf(string transcript, IEnumerable<CapturedLogEntry> entries)
    {
        foreach (var entry in entries)
        {
            Assert.False(entry.Mentions(transcript), $"The transcript leaked into: {entry.Message}");
            foreach (var word in transcript.Split(' '))
            {
                Assert.False(entry.Mentions(word), $"A transcript word ({word}) leaked into: {entry.Message}");
            }
        }
    }

    private sealed class FakeEngine
    {
        public const string ModelId = "fake-model";

        private int _loads;
        private int _decodes;
        private int _disposals;

        public string Text { get; init; } = "hello world";

        public Func<int, string>? DecodeText { get; init; }

        public Action? OnLoad { get; init; }

        public Action<int>? OnDecode { get; set; }

        public Action? OnDispose { get; set; }

        public int Loads => Volatile.Read(ref _loads);

        public int Decodes => Volatile.Read(ref _decodes);

        public int Disposals => Volatile.Read(ref _disposals);

        /// <summary>Set when the recognizer is freed during a decode or used after being freed.</summary>
        public string? Violation { get; private set; }

        public ISpeechRecognizer Load()
        {
            Interlocked.Increment(ref _loads);
            OnLoad?.Invoke();
            return new Recognizer(this);
        }

        private sealed class Recognizer(FakeEngine engine) : ISpeechRecognizer
        {
            private int _decoding;
            private bool _disposed;

            public string ModelId => FakeEngine.ModelId;

            public string Decode(float[] samples, int sampleRate)
            {
                if (_disposed)
                {
                    engine.Violation = "decoded after dispose";
                }

                Interlocked.Increment(ref _decoding);
                try
                {
                    var index = Interlocked.Increment(ref engine._decodes);
                    engine.OnDecode?.Invoke(index);
                    return engine.DecodeText?.Invoke(index) ?? engine.Text;
                }
                finally
                {
                    Interlocked.Decrement(ref _decoding);
                }
            }

            public void Dispose()
            {
                if (Volatile.Read(ref _decoding) > 0)
                {
                    engine.Violation = "disposed during a decode";
                }

                engine.OnDispose?.Invoke();
                _disposed = true;
                Interlocked.Increment(ref engine._disposals);
            }
        }
    }
}
