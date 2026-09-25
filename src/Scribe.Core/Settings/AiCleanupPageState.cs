using Scribe.Core.Cleanup;

namespace Scribe.Core.Settings;

public enum AiCleanupSetupState
{
    NothingConfigured,
    Complete,
    Incomplete,
}

public enum AiCleanupStatusKind
{
    None,
    Info,
    Success,
    Warning,
    Error,
    Busy,
}

public sealed record AiCleanupStatusRow(AiCleanupStatusKind Kind, string Text, string? ActionText = null);

public sealed record AiCleanupPageDescription(
    bool ShowProviderSetup,
    string StatusLine,
    string? OffHelperText,
    AiCleanupStatusRow? StatusRow);

public static class AiCleanupPageState
{
    public static AiCleanupPageDescription Describe(
        bool enabled,
        CleanupProvider savedProvider,
        CleanupProvider shownProvider,
        CleanupStatus status,
        bool draftComplete,
        AiCleanupSetupState savedSetupState = AiCleanupSetupState.NothingConfigured,
        string? providerSummary = null,
        string? modelName = null,
        string? safeReason = null,
        string? progressText = null)
    {
        if (!enabled)
        {
            return DescribeOff(savedSetupState, providerSummary ?? ProviderName(savedProvider));
        }

        if (!draftComplete)
        {
            return new(
                ShowProviderSetup: true,
                "Save to start AI cleanup.",
                OffHelperText: null,
                DraftStatusRow(shownProvider));
        }

        return shownProvider switch
        {
            CleanupProvider.FoundryLocal => DescribeFoundry(status, modelName, safeReason, progressText),
            CleanupProvider.AzureFoundry => DescribeRemote(status, safeReason, "On. Choose Check sign-in to continue.", "Check sign-in"),
            CleanupProvider.OpenAiCompatible => DescribeRemote(status, safeReason, "On. Set up the AI service, then test the connection.", "Test connection"),
            CleanupProvider.GitHubCopilot => DescribeRemote(status, safeReason, "On. Choose Get models to see what your subscription includes.", "Get models"),
            _ => DescribeRemote(status, safeReason, "On, but not set up yet. Until it's ready, Scribe types what it hears.", null),
        };
    }

    private static AiCleanupPageDescription DescribeOff(AiCleanupSetupState setupState, string provider)
    {
        const string offLine = "Off. Scribe types what it hears, with your dictionary and snippets.";
        return setupState switch
        {
            AiCleanupSetupState.Complete => new(false, offLine, $"Set up to use {provider}.", null),
            AiCleanupSetupState.Incomplete => new(false, $"Partly set up for {provider}. Turn on AI cleanup to finish setting it up.", null, null),
            _ => new(false, offLine, "Turn on AI cleanup to choose where it runs and set your writing style.", null),
        };
    }

    private static AiCleanupPageDescription DescribeFoundry(
        CleanupStatus status,
        string? modelName,
        string? safeReason,
        string? progressText)
    {
        var model = string.IsNullOrWhiteSpace(modelName) ? "the selected model" : modelName;
        return status switch
        {
            CleanupStatus.Disabled => new(
                true,
                "On, but not set up yet. Until it's ready, Scribe types what it hears.",
                null,
                new(AiCleanupStatusKind.Warning, "Not set up yet. Setup downloads the AI runtime for this PC, which can take several GB.", "Set up")),
            CleanupStatus.Initializing => new(true, "On. Getting ready...", null, new(AiCleanupStatusKind.Busy, "Setting up. The first time can take a while.")),
            CleanupStatus.Downloading => new(true, "On. Getting ready...", null, new(AiCleanupStatusKind.Busy, progressText ?? "Loading the model...")),
            CleanupStatus.Ready => new(true, $"On. Using {model} on this PC.", null, new(AiCleanupStatusKind.Success, $"{model} is ready.", "Unload")),
            CleanupStatus.Unavailable => new(
                true,
                "On, but not ready. Until it's ready, Scribe types what it hears.",
                null,
                new(AiCleanupStatusKind.Error, "Couldn't start the on-device AI runtime. Try again, or choose another AI service.", "Try again")),
            _ => new(true, "On. Getting ready...", null, null),
        };
    }

    private static AiCleanupPageDescription DescribeRemote(
        CleanupStatus status,
        string? safeReason,
        string notCheckedStatus,
        string? action)
    {
        return status switch
        {
            CleanupStatus.Ready => new(true, "On. AI cleanup is ready.", null, new(AiCleanupStatusKind.Success, "Connected.", action)),
            CleanupStatus.Initializing or CleanupStatus.Downloading => new(true, "On. Getting ready...", null, new(AiCleanupStatusKind.Busy, "Checking...")),
            CleanupStatus.Unavailable => new(
                true,
                string.IsNullOrWhiteSpace(safeReason)
                    ? "On, but not ready. Until it's ready, Scribe types what it hears."
                    : $"On, but not ready: {safeReason}. Until it's ready, Scribe types what it hears.",
                null,
                new(AiCleanupStatusKind.Error, safeReason ?? "AI cleanup isn't ready yet.", action)),
            _ => new(true, notCheckedStatus, null, new(AiCleanupStatusKind.Info, "Not checked yet.", action)),
        };
    }

    private static AiCleanupStatusRow DraftStatusRow(CleanupProvider provider) => provider switch
    {
        CleanupProvider.FoundryLocal => new(AiCleanupStatusKind.Warning, "Not set up yet. Setup downloads the AI runtime for this PC, which can take several GB.", "Set up"),
        CleanupProvider.AzureFoundry => new(AiCleanupStatusKind.Info, "Fill in the details above, then choose Verify.", "Verify"),
        CleanupProvider.OpenAiCompatible => new(AiCleanupStatusKind.Info, "Not tested yet.", "Test connection"),
        CleanupProvider.GitHubCopilot => new(AiCleanupStatusKind.Info, "GitHub Copilot is installed.", "Get models"),
        _ => new(AiCleanupStatusKind.Info, "Not checked yet."),
    };

    private static string ProviderName(CleanupProvider provider) => provider switch
    {
        CleanupProvider.FoundryLocal => "On this PC (Foundry Local)",
        CleanupProvider.AzureFoundry => "Microsoft Foundry",
        CleanupProvider.OpenAiCompatible => "Another AI service",
        CleanupProvider.GitHubCopilot => "GitHub Copilot",
        _ => "AI cleanup",
    };
}
