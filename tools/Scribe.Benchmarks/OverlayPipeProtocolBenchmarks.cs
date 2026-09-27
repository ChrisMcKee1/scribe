using System.Globalization;
using BenchmarkDotNet.Attributes;
using Scribe.Core.Overlay;

namespace Scribe.Benchmarks;

[MemoryDiagnoser]
public class OverlayPipeProtocolBenchmarks
{
    private int _level;

    [Benchmark(Baseline = true)]
    public string LegacyMeterLine()
    {
        var level = NextLevel();
        return OverlayPipeProtocol.Meter + " " + level.ToString(CultureInfo.InvariantCulture);
    }

    [Benchmark]
    public string CachedMeterLine()
    {
        var level = NextLevel();
        return OverlayPipeProtocol.MeterLine(level);
    }

    private int NextLevel()
    {
        _level = (_level + 37) % 1001;
        return _level;
    }
}

[MemoryDiagnoser]
public class OverlayPipeParsingBenchmarks
{
    private readonly string _meterLine = "METER 731";

    [Benchmark(Baseline = true)]
    public bool LegacyMeterVerbParse()
    {
        var trimmed = _meterLine.Trim();
        var sp = trimmed.IndexOf(' ');
        var cmd = sp < 0 ? trimmed : trimmed[..sp];
        return cmd.ToUpperInvariant() == OverlayPipeProtocol.Meter;
    }

    [Benchmark]
    public bool SpanMeterVerbParse()
    {
        var trimmed = _meterLine.AsSpan().Trim();
        var sp = trimmed.IndexOf(' ');
        var cmd = sp < 0 ? trimmed : trimmed[..sp];
        return cmd.Equals(OverlayPipeProtocol.Meter, StringComparison.OrdinalIgnoreCase);
    }
}
