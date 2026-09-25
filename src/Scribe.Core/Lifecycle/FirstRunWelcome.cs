namespace Scribe.Core.Lifecycle;

public static class FirstRunWelcome
{
    public static bool ShouldShow(bool hasCompletedFirstRun, bool settingsRecovered) => !hasCompletedFirstRun && !settingsRecovered;
}
