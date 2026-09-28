using BenchmarkDotNet.Attributes;
using Scribe.Core.Overlay;

namespace Scribe.Benchmarks;

[MemoryDiagnoser]
public class OverlayMeterDeliveryBenchmarks
{
    [Params(false, true)]
    public bool Deduplicate { get; set; }

    [Params(false, true)]
    public bool Changing { get; set; }

    [Benchmark]
    public int DispatchOneSecondOfSyntheticLevels()
    {
        var delivery = new OverlayMeterDelivery(Deduplicate);
        var sent = 0;
        for (var now = 0; now < 1000; now += 30)
        {
            var value = Changing ? now : 0;
            if (delivery.ShouldSend(value, now))
            {
                delivery.Sent(value, now);
                sent++;
            }
        }
        return sent;
    }
}
