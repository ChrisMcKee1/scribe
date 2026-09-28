using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Scribe.Core.Audio;
using Scribe.Core.Diagnostics;
using Scribe.Core.Tests.Concurrency;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// <see cref="PerfFlags.CaptureTimingDiagnostics"/> through the capture service and fake endpoints that behave like NAudio's
/// capture: one timing record per capture, bound before its stream starts, so a packet that arrives before Start returns
/// is still timed for its own recording; the time goes on a line of its own after the stop, never on the start line; a
/// capture without a packet says so instead of timing one; a failed start leaves nothing for the next capture; and the
/// packet callback allocates nothing. With the flag off none of it exists.
/// </summary>
public sealed class CaptureTimingDiagnosticsTests
{
    private const string WithAudioTemplate =
        "#{Id} capture timing: device open {OpenMs:F1} ms, stream start {StartMs:F1} ms, first packet " +
        "{FirstPacketMs:F1} ms after the stream start was requested, {Packets} packets; stream end seen {StopMs:F1} ms " +
        "after the stop request, signal analysis {AnalysisMs:F1} ms, conversion {ConversionMs:F1} ms.";

    private const string WithoutAudioTemplate =
        "#{Id} capture timing: device open {OpenMs:F1} ms, stream start {StartMs:F1} ms, no packet with audio " +
        "arrived; stream end seen {StopMs:F1} ms after the stop request.";

    private const string UnseenPacketTemplate =
        "#{Id} capture timing: device open {OpenMs:F1} ms, stream start {StartMs:F1} ms, no packet was seen by " +
        "the timing; stream end seen {StopMs:F1} ms after the stop request, signal analysis {AnalysisMs:F1} ms, " +
        "conversion {ConversionMs:F1} ms.";

    private static readonly PerfFlags TimingOn = PerfFlags.Parse(PerfFlags.CaptureTimingDiagnostics);

    [Fact]
    public void With_the_flag_off_no_capture_is_timed_and_nothing_is_logged_for_it()
    {
        var stack = new FakeCaptureStack();
        using var service = stack.CreateService(TimeSpan.FromSeconds(5), PerfFlags.None);

        Assert.True(service.Start(owner: 3));
        DeliverAndWait(service, stack.Capture, Packet(), count: 5);
        Assert.False(service.Stop(owner: 3).IsEmpty);

        Assert.DoesNotContain(stack.Log.Entries, entry => entry.Message.Contains("capture timing", StringComparison.Ordinal));
        Assert.Null(Timing(service));
    }

    [Fact]
    public void A_packet_that_comes_before_Start_returns_is_timed_for_its_own_recording()
    {
        // The fake's capture thread handles this packet inside StartRecording, before Start assigns the recording's owner
        // and returns: only a record bound before the stream started can take it.
        var stack = new FakeCaptureStack { PacketsBeforeStartReturns = [Packet()] };
        using var service = stack.CreateService(TimeSpan.FromSeconds(5), TimingOn);

        Assert.True(service.Start(owner: 7));
        Assert.Contains("early packets handled", stack.Events);
        DeliverAndWait(service, stack.Capture, Packet(), count: 2);
        Assert.False(service.Stop(owner: 7).IsEmpty);

        var line = Assert.Single(TimingLines(stack));
        Assert.Equal(WithAudioTemplate, Template(line));
        Assert.Equal(7L, line.Value("Id"));
        Assert.Equal(3L, line.Value("Packets"));
        Assert.True(
            (double)line.Value("FirstPacketMs")! <= (double)line.Value("StartMs")!,
            "A packet that came before the start returned must read as the smaller number.");
    }

    [Fact]
    public void A_packet_after_Start_returned_is_timed_after_the_start()
    {
        var stack = new FakeCaptureStack();
        using var service = stack.CreateService(TimeSpan.FromSeconds(5), TimingOn);

        Assert.True(service.Start(owner: 4));
        Thread.Sleep(20); // the start has returned; the first packet comes measurably later
        DeliverAndWait(service, stack.Capture, Packet(), count: 3);
        Assert.False(service.Stop(owner: 4).IsEmpty);

        var line = Assert.Single(TimingLines(stack));
        Assert.Equal(WithAudioTemplate, Template(line));
        Assert.Equal(4L, line.Value("Id"));
        Assert.Equal(3L, line.Value("Packets"));
        Assert.True((double)line.Value("FirstPacketMs")! >= (double)line.Value("StartMs")! + 10);
    }

    [Fact]
    public void A_capture_without_a_packet_says_so_instead_of_timing_one()
    {
        var stack = new FakeCaptureStack();
        using var service = stack.CreateService(TimeSpan.FromSeconds(5), TimingOn);

        Assert.True(service.Start(owner: 5));
        Assert.True(service.Stop(owner: 5).IsEmpty);

        var line = Assert.Single(TimingLines(stack));
        Assert.Equal(WithoutAudioTemplate, Template(line));
        Assert.Equal(5L, line.Value("Id"));
        Assert.Null(line.Value("FirstPacketMs"));
        Assert.Null(line.Value("Packets"));
    }

    [Fact]
    public void A_stop_requested_before_the_first_packet_is_timed_from_its_request()
    {
        var stack = new FakeCaptureStack();
        using var service = stack.CreateService(TimeSpan.FromSeconds(5), TimingOn);

        Assert.True(service.Start(owner: 6));
        service.RequestStop(owner: 6);

        // Queued behind the stop, so the fake's loop, like NAudio's, never hands it over.
        stack.Capture.Deliver(Packet());
        Thread.Sleep(60);
        Assert.True(service.Stop(owner: 6).IsEmpty);

        var line = Assert.Single(TimingLines(stack));
        Assert.Equal(WithoutAudioTemplate, Template(line));
        Assert.True((double)line.Value("StopMs")! >= 30, "The stop is timed from RequestStop, not from Stop.");
    }

    [Fact]
    public void A_failed_start_leaves_no_record_and_the_next_capture_is_timed_on_its_own()
    {
        var stack = new FakeCaptureStack { StartRecordingFailure = new InvalidOperationException("The endpoint went away.") };
        using var service = stack.CreateService(TimeSpan.FromSeconds(5), TimingOn);

        Assert.Throws<InvalidOperationException>(() => service.Start(owner: 8));
        Assert.Null(Timing(service));
        Assert.Empty(TimingLines(stack));

        stack.StartRecordingFailure = null;
        Assert.True(service.Start(owner: 9));
        DeliverAndWait(service, stack.Capture, Packet(), count: 2);
        Assert.False(service.Stop(owner: 9).IsEmpty);

        var line = Assert.Single(TimingLines(stack));
        Assert.Equal(9L, line.Value("Id"));
        Assert.Equal(2L, line.Value("Packets"));
        Assert.Null(Timing(service));
    }

    [Fact]
    public void A_packet_from_another_sender_is_not_this_captures()
    {
        var capture = new object();
        var timing = new CaptureTiming(owner: 1, capture, requested: 1);

        timing.MarkPacket(new object());
        timing.MarkPacket(null);
        Assert.Equal(0, timing.Packets);
        Assert.Equal(0, timing.FirstPacket);

        timing.MarkPacket(capture);
        var first = timing.FirstPacket;
        Assert.NotEqual(0, first);
        timing.MarkPacket(capture);
        Assert.Equal(2, timing.Packets);
        Assert.Equal(first, timing.FirstPacket);
    }

    [Fact]
    public void Audio_from_a_sender_the_record_does_not_know_is_never_timed_as_its_first_packet()
    {
        // Only another sender's packet reaches the recording here, so the stop has audio and no stamp: it says the timing
        // saw no packet, rather than timing one from a stamp that was never taken.
        var stack = new FakeCaptureStack();
        using var service = stack.CreateService(TimeSpan.FromSeconds(5), TimingOn);
        Assert.True(service.Start(owner: 11));

        var packet = Packet();
        service.OnPacket(new object(), packet, packet.Length);
        Assert.False(service.Stop(owner: 11).IsEmpty);

        var line = Assert.Single(TimingLines(stack));
        Assert.Equal(UnseenPacketTemplate, Template(line));
        Assert.Equal(11L, line.Value("Id"));
    }

    [Fact]
    public void Every_timing_line_carries_a_literal_template_and_numbers_only()
    {
        var stack = new FakeCaptureStack { PacketsBeforeStartReturns = [Packet()] };
        using var service = stack.CreateService(TimeSpan.FromSeconds(5), TimingOn);
        Assert.True(service.Start(owner: 12));
        Assert.False(service.Stop(owner: 12).IsEmpty);

        stack.PacketsBeforeStartReturns = null;
        Assert.True(service.Start(owner: 13));
        Assert.True(service.Stop(owner: 13).IsEmpty);

        Assert.True(service.Start(owner: 14));
        var packet = Packet();
        service.OnPacket(new object(), packet, packet.Length);
        Assert.False(service.Stop(owner: 14).IsEmpty);

        var lines = TimingLines(stack);
        Assert.Equal(3, lines.Count);
        foreach (var line in lines)
        {
            Assert.Contains(Template(line), new[] { WithAudioTemplate, WithoutAudioTemplate, UnseenPacketTemplate });
            Assert.Equal(LogLevel.Debug, line.Level);
            Assert.Null(line.Exception);
            foreach (var (key, value) in line.State)
            {
                if (key == "{OriginalFormat}")
                {
                    continue;
                }

                Assert.True(value is long or double, $"{key} is {value?.GetType().Name ?? "null"}, not a number.");
            }
        }
    }

    [Fact]
    public void The_start_line_is_unchanged_and_carries_no_timing()
    {
        var stack = new FakeCaptureStack();
        using var service = stack.CreateService(TimeSpan.FromSeconds(5), TimingOn);
        Assert.True(service.Start(owner: 15));
        service.Stop(owner: 15);

        var start = Assert.Single(stack.Log.Entries, entry => Template(entry).StartsWith("Starting capture on", StringComparison.Ordinal));
        Assert.Equal("Starting capture on '{Device}' ({Source}) at {Rate} Hz, {Channels} ch, {Bits}-bit {Encoding}.", Template(start));
    }

    [Fact]
    public void A_timing_line_that_cannot_be_written_never_costs_the_capture()
    {
        // The same guarantee as the VAD's timing line (AUDIO-IR-01): a sink that fails for the timing template, with audio
        // and without, and the stop still hands over exactly what it captured.
        var stack = new FakeCaptureStack();
        var logger = new TemplateFailingLogger<AudioCaptureService>("#{Id} capture timing:");
        using var service = new AudioCaptureService(logger, stack.Devices, TimeSpan.FromSeconds(5), watch: null, TimingOn);

        Assert.True(service.Start(owner: 16));
        DeliverAndWait(service, stack.Capture, Packet(), count: 3);
        var captured = service.Stop(owner: 16);
        Assert.Equal(480, captured.Samples.Length);

        Assert.True(service.Start(owner: 17));
        Assert.True(service.Stop(owner: 17).IsEmpty);

        Assert.Equal(2, logger.Thrown);
        Assert.Contains(logger.Written, template => template.StartsWith("Capture complete:", StringComparison.Ordinal));
    }

    // Measured alone (the collection runs after every parallel test), on the thread that calls it, as the capture thread
    // would: the packet path must allocate nothing with the flag on, as it allocates nothing with it off.
    [Collection(AllocationMeasurementCollection.Name)]
    public sealed class Allocations
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void The_packet_callback_allocates_nothing(bool timingOn)
        {
            var stack = new FakeCaptureStack();
            using var service = stack.CreateService(TimeSpan.FromSeconds(5), timingOn ? TimingOn : PerfFlags.None);
            Assert.True(service.Start(owner: 21));
            var capture = stack.Capture;
            var packet = Packet();

            // Warm across the first calls and the JIT's tiers; 10 ms packets, well inside the 30 s reservation.
            for (var i = 0; i < 200; i++)
            {
                service.OnPacket(capture, packet, packet.Length);
            }

            var work = RuntimeWork.Now();
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 400; i++)
            {
                service.OnPacket(capture, packet, packet.Length);
            }

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            var during = RuntimeWork.Now().Since(work);

            AllocationMeasurement.AssertZero(
                allocated,
                during,
                $"400 packets with capture timing {(timingOn ? "on" : "off")}",
                () => service.OnPacket(capture, packet, packet.Length));

            Assert.False(service.Stop(owner: 21).IsEmpty);
        }
    }

    private static void DeliverAndWait(AudioCaptureService service, FakeCapture capture, byte[] packet, int count)
    {
        using var delivered = new CountdownEvent(count);
        void OnLevel(object? sender, float level) => delivered.Signal();
        service.LevelChanged += OnLevel;
        try
        {
            for (var i = 0; i < count; i++)
            {
                capture.Deliver(packet);
            }

            Assert.True(delivered.Wait(BlockedThreads.SafetyTimeout), "The fake capture thread did not deliver every packet.");
        }
        finally
        {
            service.LevelChanged -= OnLevel;
        }
    }

    // 10 ms of 16 kHz mono float, the fake capture's format: a tone, so every packet carries audio.
    private static byte[] Packet()
    {
        var samples = new float[160];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = 0.25f * MathF.Sin(i * 0.2f);
        }

        return System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
    }

    private static IReadOnlyList<CapturedLogEntry> TimingLines(FakeCaptureStack stack) =>
        [.. stack.Log.Entries.Where(entry => Template(entry).StartsWith("#{Id} capture timing:", StringComparison.Ordinal))];

    private static string Template(CapturedLogEntry entry) => entry.Value("{OriginalFormat}") as string ?? string.Empty;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_timing")]
    private static extern ref CaptureTiming? TimingField(AudioCaptureService service);

    private static CaptureTiming? Timing(AudioCaptureService service) => TimingField(service);
}
