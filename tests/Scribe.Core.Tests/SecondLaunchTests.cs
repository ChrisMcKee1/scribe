using Scribe.Core.Lifecycle;

namespace Scribe.Core.Tests;

public sealed class SecondLaunchTests
{
    [Fact] public void Opens_settings_when_signal_succeeds() => Assert.Equal(SecondLaunchAction.OpenSettingsInRunningInstance, SecondLaunch.Decide(signalRaised: true));
    [Fact] public void Shows_notice_only_when_signal_fails() => Assert.Equal(SecondLaunchAction.ShowAlreadyRunningNotice, SecondLaunch.Decide(signalRaised: false));
}
