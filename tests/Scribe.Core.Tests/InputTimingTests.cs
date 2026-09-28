using Scribe.Core.Diagnostics;
using Scribe.Core.Models;
using Scribe.Core.TextInjection;

namespace Scribe.Core.Tests;

public sealed class InputTimingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Native_attempts_retry_sleeps_and_settles_have_separate_intervals(bool enabled)
    {
        var rig = new Rig(enabled);
        rig.Platform.Deliver = (attempt, inputs) =>
        {
            rig.Clock.Advance(3);
            return attempt switch { 0 => 20u, 1 => 0u, _ => (uint)inputs.Length };
        };

        var result = rig.Inject(new string('x', 60));

        Assert.True(result.Succeeded);
        Assert.Equal(120, result.Sent);
        Assert.Equal([12, 12, 5], rig.Platform.Sleeps);
        Assert.Equal([100, 80, 80, 20], rig.Platform.Batches.Select(batch => batch.Length));
        if (!enabled)
        {
            Assert.Null(result.Timings);
            Assert.DoesNotContain(rig.Log.Entries, line => line.Contains("Input timing:", StringComparison.Ordinal));
            return;
        }

        Assert.Equal(new InjectionTimings(41, 41, 12, 24, 5, 0, 0, 3, 4, 280, 120, 2), result.Timings);
        Assert.Single(rig.Log.Entries, line => line.Contains("Input timing:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Clipboard_waits_are_not_typing_settles_and_content_never_reaches_timing(bool enabled)
    {
        var rig = new Rig(enabled);
        rig.Clipboard.SeedText("PRIVATE-PREVIOUS-c713");
        rig.Platform.Deliver = (_, inputs) =>
        {
            rig.Clock.Advance(2);
            return (uint)inputs.Length;
        };

        var result = rig.Inject("PRIVATE-DICTATION-b818", InjectionMethod.ClipboardPaste);

        Assert.True(result.Succeeded);
        Assert.Equal("clipboard", result.Method);
        Assert.Equal([30, 130], rig.Platform.Sleeps);
        if (enabled)
        {
            Assert.Equal(new InjectionTimings(162, 162, 2, 0, 0, 160, 0, 2, 1, 4, 4, 0), result.Timings);
        }
        else
        {
            Assert.Null(result.Timings);
        }

        Assert.All(rig.Log.Entries, line => Assert.DoesNotContain("PRIVATE-", line));
    }

    [Fact]
    public void Clipboard_acquisition_before_a_typing_fallback_stays_in_other_worker_time()
    {
        var rig = new Rig(true);
        rig.Clipboard.OpenAttemptSucceeds = _ => false;

        var result = rig.Inject("typed", InjectionMethod.ClipboardPaste);

        Assert.True(result.Succeeded);
        Assert.Equal("unicode", result.Method);
        Assert.Equal(PasteDelivery.ClipboardBusy, result.Paste);
        Assert.Equal(75, result.Timings!.OtherWorkerMs);
        Assert.Equal(0, result.Timings.RetrySleepMs);
        Assert.Equal(0, result.Timings.SettleMs);
    }

    [Fact]
    public void Caller_time_includes_work_before_the_worker_and_worker_time_does_not()
    {
        var rig = new Rig(true);
        var platform = new PreflightPlatform(rig.Platform, rig.Clock);
        var injector = new TextInjector(rig.Log, platform, rig.Clipboard, PerfFlags.Parse(PerfFlags.InputTimings), rig.Clock);

        var result = injector.Inject("hello", InjectionMethod.UnicodeType, 0x4242);

        Assert.Equal(21, result.Timings!.CallerMs);
        Assert.Equal(14, result.Timings.WorkerMs);
        Assert.Equal(14, result.Timings.OtherWorkerMs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Early_focus_loss_never_sends_or_opens_the_clipboard(bool enabled)
    {
        var rig = new Rig(enabled);
        rig.Platform.Foreground = 9;

        var result = rig.Inject("not sent");

        Assert.False(result.Succeeded);
        Assert.Empty(rig.Platform.Batches);
        Assert.Equal(0, rig.Clipboard.OpenAttempts);
        Assert.Equal(enabled, result.Timings is not null);
        if (enabled)
        {
            Assert.Equal(0, result.Timings!.NativeCalls);
            Assert.Equal(0, result.Timings.WorkerMs);
            Assert.Contains(rig.Log.Entries, line => line.Contains("NoInsertion=True", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Empty_text_has_zero_native_calls_and_is_identified_as_no_worker(bool enabled)
    {
        var rig = new Rig(enabled);

        var result = rig.Inject("");

        Assert.True(result.Succeeded);
        Assert.Equal("none", result.Method);
        Assert.Empty(rig.Platform.Batches);
        Assert.Equal(0, rig.Clipboard.OpenAttempts);
        Assert.Empty(rig.Platform.ScanCodeRequests);
        if (enabled)
        {
            Assert.Equal(new InjectionTimings(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), result.Timings);
            Assert.Contains(rig.Log.Entries, line => line.Contains("NoInsertion=True", StringComparison.Ordinal));
        }
        else
        {
            Assert.Null(result.Timings);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_edit_path_records_no_keyboard_calls_and_preserves_its_result(bool enabled)
    {
        var rig = new Rig(enabled);
        rig.Platform.StandardEdit = true;

        var result = rig.Inject("edit text");

        Assert.True(result.Succeeded);
        Assert.Equal("win32-edit", result.Method);
        Assert.Equal(9, result.Sent);
        Assert.Equal(9, result.Total);
        Assert.Equal(["edit text"], rig.Platform.StandardEditTexts);
        Assert.Empty(rig.Platform.Batches);
        Assert.Equal(0, rig.Clipboard.OpenAttempts);
        Assert.Empty(rig.Platform.ScanCodeRequests);
        if (enabled)
        {
            Assert.Equal(0, result.Timings!.NativeCalls);
            Assert.Equal(0, result.Timings.AcceptedEvents);
            Assert.Contains(rig.Log.Entries, line => line.Contains("NoInsertion=False", StringComparison.Ordinal));
        }
        else
        {
            Assert.Null(result.Timings);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Remote_pacing_is_measured_without_changing_its_events_or_waits(bool enabled)
    {
        var rig = new Rig(enabled);

        var result = rig.Inject(new string('x', 130), target: "msrdc");

        Assert.True(result.Succeeded);
        Assert.Equal(260, result.Sent);
        Assert.Equal(9, rig.Platform.Batches.Count);
        Assert.Equal(Enumerable.Repeat(20, 8), rig.Platform.Sleeps);
        if (enabled)
        {
            Assert.Equal(new InjectionTimings(160, 160, 0, 0, 160, 0, 0, 0, 9, 260, 260, 0), result.Timings);
        }
        else
        {
            Assert.Null(result.Timings);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Focus_lost_between_batches_keeps_the_partial_result_and_counts(bool enabled)
    {
        var rig = new Rig(enabled);
        rig.AfterSleep = _ => rig.Platform.Foreground = 9;

        var result = rig.Inject(new string('x', 60));

        Assert.False(result.Succeeded);
        Assert.Equal(100, result.Sent);
        Assert.Equal(120, result.Total);
        Assert.Single(rig.Platform.Batches);
        Assert.Equal([5], rig.Platform.Sleeps);
        if (enabled)
        {
            Assert.Equal(new InjectionTimings(5, 5, 0, 0, 5, 0, 0, 0, 1, 100, 100, 0), result.Timings);
        }
        else
        {
            Assert.Null(result.Timings);
        }
    }

    [Fact]
    public void The_returned_and_logged_measurement_is_the_same_snapshot()
    {
        var clock = new Clock { TickOnRead = true };
        var log = new TimingLogger();
        var injector = new TextInjector(log, new TextInjectionFakes.Platform(), new TextInjectionFakes.Clipboard(),
            PerfFlags.Parse(PerfFlags.InputTimings), clock);

        var result = injector.Inject("one snapshot", InjectionMethod.UnicodeType);

        Assert.Equal(result.Timings!.CallerMs, log.CallerMs);
    }

    [Fact]
    public void A_throwing_timing_log_cannot_fail_an_insertion_that_was_delivered()
    {
        var rig = new Rig(true);
        rig.Log.ThrowOn = line => line.Contains("Input timing:", StringComparison.Ordinal);

        Assert.True(rig.Inject("hello").Succeeded);
    }

    [Fact]
    public void Native_faults_still_report_the_attempt_without_exposing_exception_text()
    {
        var rig = new Rig(true);
        rig.Platform.Deliver = (index, inputs) =>
        {
            rig.Clock.Advance(3);
            return index == 0 ? throw new InvalidOperationException("PRIVATE-FAULT") : (uint)inputs.Length;
        };

        Assert.Throws<InvalidOperationException>(() => rig.Inject("hello"));
        var line = Assert.Single(rig.Log.Entries, entry => entry.Contains("Input timing:", StringComparison.Ordinal));
        Assert.Contains("Calls=2", line);
        Assert.DoesNotContain("PRIVATE-FAULT", line);
    }

    [Fact]
    public void Timing_tags_are_allowlisted_numbers()
    {
        var tags = new Dictionary<string, object?>
        {
            [ScribeTelemetry.TagInjectNativeMs] = 3.0,
            [ScribeTelemetry.TagInjectRetrySleepMs] = 12.0,
            [ScribeTelemetry.TagInjectSettleMs] = 5.0,
            [ScribeTelemetry.TagInjectClipboardSleepMs] = 30.0,
            [ScribeTelemetry.TagInjectNativeCalls] = 2,
            [ScribeTelemetry.TagInjectRequestedEvents] = 8L,
            [ScribeTelemetry.TagInjectAcceptedEvents] = 4L,
            [ScribeTelemetry.TagInjectRetries] = 1,
            [ScribeTelemetry.TagInjectMaxCallMs] = 2.0,
        };

        Assert.DoesNotContain("omitted", TraceTagPolicy.FormatSpan(ScribeTelemetry.InjectActivity, tags, TimeSpan.Zero));
    }

    private sealed class Rig(bool enabled)
    {
        public Clock Clock { get; } = new();
        public TextInjectionFakes.Platform Platform { get; } = new();
        public TextInjectionFakes.Clipboard Clipboard { get; } = new();
        public TextInjectionFakes.CapturingLogger<TextInjector> Log { get; } = new();
        public Action<int>? AfterSleep { get; set; }

        public InjectionResult Inject(
            string text, InjectionMethod method = InjectionMethod.UnicodeType, string? target = null)
        {
            Platform.OnSleep = ms =>
            {
                Clock.Advance(ms);
                AfterSleep?.Invoke(ms);
            };
            return new TextInjector(Log, Platform, Clipboard,
                enabled ? PerfFlags.Parse(PerfFlags.InputTimings) : PerfFlags.None, Clock)
                .Inject(text, method, 0x4242, targetProcessName: target);
        }
    }

    private sealed class Clock : TimeProvider
    {
        private long _now;
        public bool TickOnRead { get; init; }
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => TickOnRead ? Interlocked.Increment(ref _now) : Interlocked.Read(ref _now);
        public void Advance(int milliseconds) => Interlocked.Add(ref _now, milliseconds);
    }

    private sealed class TimingLogger : Microsoft.Extensions.Logging.ILogger<TextInjector>
    {
        public double? CallerMs { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => true;
        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                CallerMs = values.FirstOrDefault(pair => pair.Key == "CallerMs").Value as double?;
            }
        }
    }

    private sealed class PreflightPlatform(TextInjectionFakes.Platform inner, Clock clock) : IInjectionPlatform
    {
        public nint GetForegroundWindow()
        {
            clock.Advance(7);
            return inner.GetForegroundWindow();
        }
        public uint SendInput(ReadOnlySpan<InjectionNativeMethods.INPUT> inputs) => inner.SendInput(inputs);
        public bool TryInsertIntoStandardEdit(string text, nint expectedForegroundWindow) => false;
        public void Sleep(int milliseconds) => clock.Advance(milliseconds);
        public KeyScanCode ScanCodeOf(ushort virtualKey) => inner.ScanCodeOf(virtualKey);
    }
}
