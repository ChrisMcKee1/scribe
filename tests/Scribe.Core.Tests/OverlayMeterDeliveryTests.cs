using Scribe.Core.Overlay;

namespace Scribe.Core.Tests;

public sealed class OverlayMeterDeliveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Equal_values_are_resent_at_the_bound_and_changes_are_never_delayed(bool enabled)
    {
        var delivery = new OverlayMeterDelivery(enabled);
        Assert.True(delivery.ShouldSend(0, 100));
        delivery.Sent(0, 100);
        Assert.Equal(!enabled, delivery.ShouldSend(0, 349));
        Assert.True(delivery.ShouldSend(0, 350));
        Assert.True(delivery.ShouldSend(1, 101));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_new_recording_writer_or_helper_resends_the_same_value(bool enabled)
    {
        var delivery = new OverlayMeterDelivery(enabled);
        for (var reset = 0; reset < 3; reset++)
        {
            delivery.Sent(731, 100);
            delivery.Reset();
            Assert.True(delivery.ShouldSend(731, 101));
        }
    }

    [Fact]
    public void Failed_writes_do_not_count_as_delivery()
    {
        var delivery = new OverlayMeterDelivery(true);
        Assert.True(delivery.ShouldSend(500, 10));
        Assert.True(delivery.ShouldSend(500, 11));
        delivery.Sent(500, 12);
        Assert.False(delivery.ShouldSend(500, 13));
        Assert.True(delivery.ShouldSend(500, 262));
    }

    [Theory]
    [InlineData(false, false, 34)]
    [InlineData(true, false, 4)]
    [InlineData(false, true, 34)]
    [InlineData(true, true, 34)]
    public void A_steady_trace_keeps_bounded_refresh_and_a_changing_trace_loses_no_value(
        bool enabled, bool changing, int expectedWrites)
    {
        var delivery = new OverlayMeterDelivery(enabled);
        var writes = 0;
        for (var now = 0; now < 1000; now += 30)
        {
            var level = changing ? now : 0;
            if (delivery.ShouldSend(level, now))
            {
                writes++;
                delivery.Sent(level, now);
            }
        }
        Assert.Equal(expectedWrites, writes);
    }

    [Fact]
    public void The_client_checks_health_before_suppression_and_keeps_the_original_meter()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Scribe.slnx")))
        {
            directory = directory.Parent;
        }
        Assert.NotNull(directory);
        var source = File.ReadAllText(Path.Combine(directory.FullName, "src", "Scribe.App", "Overlay", "OverlayProcessClient.cs"));
        var method = source[source.IndexOf("private void HandleMeter()", StringComparison.Ordinal)..];
        method = method[..method.IndexOf("private void HandleRelease", StringComparison.Ordinal)];

        Assert.True(method.IndexOf("ObserveHelper", StringComparison.Ordinal) < method.IndexOf("_meterDelivery.ShouldSend", StringComparison.Ordinal));
        Assert.True(method.IndexOf("_lifetime.OnMeter", StringComparison.Ordinal) < method.IndexOf("_meterDelivery.ShouldSend", StringComparison.Ordinal));
        Assert.True(method.IndexOf("WriteWithTimeout", StringComparison.Ordinal) < method.IndexOf("_meterDelivery.Sent", StringComparison.Ordinal));
        Assert.Contains("_meterDelivery.ShouldSend(level, nowMs)", method);
        Assert.Contains("_meterDelivery.Sent(level, nowMs)", method);
        Assert.Contains("private readonly PillLevelMeter _meter = new();", source);
        Assert.Contains("_process is { HasExited: false }", source);
        Assert.Contains("write.Wait(WriteTimeoutMs)", source);
    }
}
