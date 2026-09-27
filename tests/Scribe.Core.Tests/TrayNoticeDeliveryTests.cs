using Scribe.Core.Tray;

namespace Scribe.Core.Tests;

public sealed class TrayNoticeDeliveryTests
{
    [Theory]
    [InlineData(TrayNoticeKind.Info, TrayNotificationIcon.Info, true, true, false)]
    [InlineData(TrayNoticeKind.Warning, TrayNotificationIcon.Warning, false, true, false)]
    [InlineData(TrayNoticeKind.RecordingWarning, TrayNotificationIcon.Warning, true, false, true)]
    [InlineData(TrayNoticeKind.Error, TrayNotificationIcon.Error, false, true, false)]
    public void Kinds_map_to_icons_and_delivery_flags(TrayNoticeKind kind, TrayNotificationIcon icon, bool silent, bool quietTime, bool realtime)
    {
        var delivery = TrayNoticeDelivery.For(kind);

        Assert.Equal(icon, delivery.Icon);
        Assert.Equal(silent, delivery.Silent);
        Assert.Equal(quietTime, delivery.RespectQuietTime);
        Assert.Equal(realtime, delivery.Realtime);
    }

    [Fact]
    public void Recording_warnings_are_silent_and_ignore_quiet_time()
    {
        var delivery = TrayNoticeDelivery.For(TrayNoticeKind.RecordingWarning);

        Assert.True(delivery.Silent);
        Assert.False(delivery.RespectQuietTime);
        Assert.True(delivery.Realtime);
    }
}
