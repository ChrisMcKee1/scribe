using Scribe.Core.Cleanup;
using Scribe.Core.Models;

namespace Scribe.Core.Tray;

public enum TrayAiCleanupKind
{
    SetUp,
    Toggle,
    Refused,
}

public readonly record struct TrayAiCleanupItem(TrayAiCleanupKind Kind, string Label, bool Checked, bool Enabled)
{
    public bool IsCheckItem => Kind != TrayAiCleanupKind.SetUp;
}

public static class TrayAiCleanup
{
    public static TrayAiCleanupItem Describe(AppSettings settings, bool setupComplete, CleanupStatus status, bool settingsRecovered)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settingsRecovered)
        {
            return new TrayAiCleanupItem(TrayAiCleanupKind.Refused, "AI cleanup", settings.EnableAiCleanup, true);
        }

        var isSetUp = setupComplete && ProviderConfigured(settings);
        if (!isSetUp)
        {
            return new TrayAiCleanupItem(TrayAiCleanupKind.SetUp, "Set up AI cleanup...", false, true);
        }

        var label = settings.EnableAiCleanup && status == CleanupStatus.Unavailable
            ? "AI cleanup (not ready)"
            : "AI cleanup";
        return new TrayAiCleanupItem(TrayAiCleanupKind.Toggle, label, settings.EnableAiCleanup, true);
    }

    private static bool ProviderConfigured(AppSettings settings) => settings.AiCleanupProvider switch
    {
        CleanupProvider.FoundryLocal => !string.IsNullOrWhiteSpace(settings.AiCleanupModel),
        CleanupProvider.AzureFoundry => !string.IsNullOrWhiteSpace(settings.AiCleanupAzureEndpoint) && !string.IsNullOrWhiteSpace(settings.AiCleanupAzureDeployment),
        CleanupProvider.OpenAiCompatible => !string.IsNullOrWhiteSpace(settings.AiCleanupCustomEndpoint) && !string.IsNullOrWhiteSpace(settings.AiCleanupCustomModel),
        CleanupProvider.GitHubCopilot => true,
        _ => false,
    };
}
