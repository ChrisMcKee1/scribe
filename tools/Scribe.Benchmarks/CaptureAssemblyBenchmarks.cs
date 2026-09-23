using BenchmarkDotNet.Attributes;
using NAudio.Wave;
using Scribe.Core.Audio;
using Scribe.Core.Models;

namespace Scribe.Benchmarks;

/// <summary>
/// Allocation cost of assembling one capture from device-format packets and converting it to the
/// 16 kHz mono the recognizer consumes, with synthetic packets and no device. The baseline arm is
/// the shape the service had before its working buffer was reused: a fresh 30-second
/// <see cref="MemoryStream"/> reservation per capture (11,520,000 bytes at 48 kHz stereo float, on
/// the large object heap). The other arm is the shipping path through <see cref="CaptureBufferPool"/>.
/// Both arms share the metering, analysis and resampling code. At 40 s both outgrow the reservation,
/// and the pool deliberately drops a grown buffer instead of keeping it, so the two arms converge.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("Audio")]
public class CaptureAssemblyBenchmarks
{
    private readonly CaptureBufferPool _pool = new();
    private WaveFormat _format = SyntheticCapture.DefaultDeviceFormat;
    private byte[][] _packets = [];
    private int _reservation;

    [Params(2, 8, 25, 40)]
    public int Seconds { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _format = SyntheticCapture.DefaultDeviceFormat;
        _packets = SyntheticCapture.OneSecondOfPackets(_format);
        _reservation = AudioCaptureService.ReservationBytes(_format);

        // Steady state: the pool already holds the buffer the previous press left behind.
        _pool.Return(_pool.Rent(_reservation), retain: true);
    }

    [Benchmark(Baseline = true)]
    public CapturedAudio FreshReservation()
    {
        using var raw = new MemoryStream(_reservation);
        for (var packet = 0; packet < Seconds * SyntheticCapture.PacketsPerSecond; packet++)
        {
            var bytes = _packets[packet % _packets.Length];
            raw.Write(bytes, 0, bytes.Length);
            _ = AudioCaptureService.ComputePeak(bytes, _format);
        }

        _ = CaptureSignalAnalyzer.Analyze(raw.GetBuffer().AsSpan(0, (int)raw.Length), _format);
        return new CapturedAudio(AudioCaptureService.ResampleToTarget(raw.GetBuffer(), (int)raw.Length, _format));
    }

    [Benchmark]
    public CapturedAudio ReusedBuffer()
    {
        var recording = _pool.Rent(_reservation);
        try
        {
            for (var packet = 0; packet < Seconds * SyntheticCapture.PacketsPerSecond; packet++)
            {
                _ = AudioCaptureService.AppendChunk(recording, _packets[packet % _packets.Length], _format);
            }

            return AudioCaptureService.ConvertCapture(recording, _format, _ => { });
        }
        finally
        {
            _pool.Return(recording, retain: true);
        }
    }
}
