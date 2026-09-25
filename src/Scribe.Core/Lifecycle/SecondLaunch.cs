namespace Scribe.Core.Lifecycle;

public enum SecondLaunchAction
{
    OpenSettingsInRunningInstance,
    ShowAlreadyRunningNotice,
}

public static class SecondLaunch
{
    public static SecondLaunchAction Decide(bool signalRaised) => signalRaised ? SecondLaunchAction.OpenSettingsInRunningInstance : SecondLaunchAction.ShowAlreadyRunningNotice;
}
