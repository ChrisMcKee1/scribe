using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Scribe.Core.Audio;

/// <summary>
/// <see cref="Scribe.Core.Diagnostics.PerfFlags.CaptureTimingDiagnostics"/>: when one capture's stream started, when its
/// first packet came, and where its stop spent its time, for the log line that follows its stop. Timestamps only
/// (<see cref="Stopwatch.GetTimestamp"/>), never audio.
/// </summary>
/// <remarks>
/// One record per capture, bound to that capture before its stream starts: NAudio starts the stream on its own capture
/// thread, so the first packet can arrive before <c>StartRecording</c> returns, and only a record that already exists,
/// and knows which capture it belongs to, can take it. The capture thread only ever calls <see cref="MarkPacket"/>, which
/// takes no lock, logs nothing and allocates nothing; everything else happens under the capture service's lock or after
/// the stream has ended. The start line is written before any of this is known, so none of it is on that line.
/// </remarks>
internal sealed class CaptureTiming
{
    private long _firstPacket;
    private long _packets;

    public CaptureTiming(long owner, object capture, long requested)
    {
        Owner = owner;
        Capture = capture;
        Requested = requested;
    }

    /// <summary>The recording this capture belongs to (0 for none), as the log's <c>#</c> number.</summary>
    public long Owner { get; }

    /// <summary>The capture this record belongs to: a packet from any other sender is not this capture's.</summary>
    public object Capture { get; }

    /// <summary>When <c>Start</c> was called.</summary>
    public long Requested { get; }

    /// <summary>When the stream was asked to start, once the device was open.</summary>
    public long StreamStartRequested { get; set; }

    /// <summary>When the request to start the stream returned, which can be after the first packet.</summary>
    public long StartReturned { get; set; }

    /// <summary>When the stop was first asked for, by <c>RequestStop</c> or by <c>Stop</c> itself; 0 until then.</summary>
    public long StopRequested { get; private set; }

    /// <summary>When the first packet with audio came, or 0 when none has.</summary>
    public long FirstPacket => Volatile.Read(ref _firstPacket);

    /// <summary>How many packets with audio came.</summary>
    public long Packets => Volatile.Read(ref _packets);

    /// <summary>
    /// On the capture thread, for every packet with audio: counts it and stamps the first, for this record's own capture
    /// only. No lock, no log, no allocation.
    /// </summary>
    public void MarkPacket(object? sender)
    {
        if (!ReferenceEquals(sender, Capture))
        {
            return;
        }

        Interlocked.Increment(ref _packets);
        if (Volatile.Read(ref _firstPacket) == 0)
        {
            Interlocked.CompareExchange(ref _firstPacket, Stopwatch.GetTimestamp(), 0);
        }
    }

    /// <summary>Under the capture service's lock: the first stop request for this capture is the one that counts.</summary>
    public void MarkStopRequested(long now)
    {
        if (StopRequested == 0)
        {
            StopRequested = now;
        }
    }

    /// <summary>
    /// The line for a stop that found no audio: numbers only, and "no packet" said in words of its own template rather
    /// than as a zero that would read as a measured time.
    /// </summary>
    /// <param name="stopEnded">When the stream was known to have ended.</param>
    public void LogWithoutAudio(ILogger logger, long stopEnded) =>
        logger.LogDebug(
            "#{Id} capture timing: device open {OpenMs:F1} ms, stream start {StartMs:F1} ms, no packet with audio " +
            "arrived; stream end seen {StopMs:F1} ms after the stop request.",
            Owner,
            Milliseconds(Requested, StreamStartRequested),
            Milliseconds(StreamStartRequested, StartReturned),
            Milliseconds(StopRequested == 0 ? stopEnded : StopRequested, stopEnded));

    /// <summary>
    /// The line for a stop that converted its audio: numbers only. The first packet is timed from the same moment as the
    /// stream start, so a packet that came before the start returned reads as the smaller number, never as a negative gap.
    /// </summary>
    /// <param name="stopEnded">When the stream was known to have ended, which is when the conversion began.</param>
    /// <param name="analyzed">When the signal analysis ended and the conversion proper began.</param>
    /// <param name="converted">When the conversion ended.</param>
    public void LogWithAudio(ILogger logger, long stopEnded, long analyzed, long converted)
    {
        var firstPacket = FirstPacket;
        if (firstPacket == 0)
        {
            // Audio this record never saw arrive, which only a sender other than its capture could deliver: said as such
            // rather than timed from a stamp that was never taken.
            logger.LogDebug(
                "#{Id} capture timing: device open {OpenMs:F1} ms, stream start {StartMs:F1} ms, no packet was seen by " +
                "the timing; stream end seen {StopMs:F1} ms after the stop request, signal analysis {AnalysisMs:F1} ms, " +
                "conversion {ConversionMs:F1} ms.",
                Owner,
                Milliseconds(Requested, StreamStartRequested),
                Milliseconds(StreamStartRequested, StartReturned),
                Milliseconds(StopRequested == 0 ? stopEnded : StopRequested, stopEnded),
                Milliseconds(stopEnded, analyzed),
                Milliseconds(analyzed, converted));
            return;
        }

        logger.LogDebug(
            "#{Id} capture timing: device open {OpenMs:F1} ms, stream start {StartMs:F1} ms, first packet " +
            "{FirstPacketMs:F1} ms after the stream start was requested, {Packets} packets; stream end seen {StopMs:F1} ms " +
            "after the stop request, signal analysis {AnalysisMs:F1} ms, conversion {ConversionMs:F1} ms.",
            Owner,
            Milliseconds(Requested, StreamStartRequested),
            Milliseconds(StreamStartRequested, StartReturned),
            Milliseconds(StreamStartRequested, firstPacket),
            Packets,
            Milliseconds(StopRequested == 0 ? stopEnded : StopRequested, stopEnded),
            Milliseconds(stopEnded, analyzed),
            Milliseconds(analyzed, converted));
    }

    private static double Milliseconds(long from, long to) =>
        Math.Round(Stopwatch.GetElapsedTime(from, to).TotalMilliseconds, 1);
}
