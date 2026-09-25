using Scribe.Core.Cleanup;
using Scribe.Core.Models;

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

public sealed record AiCleanupAction(string Text, bool IsEnabled = true);

public sealed record AiCleanupStatusRow2(
    AiCleanupStatusKind Kind,
    string Text,
    AiCleanupAction? PrimaryAction = null,
    AiCleanupAction? SecondaryAction = null)
{
    public string? ActionText => PrimaryAction?.Text;
    public bool ActionEnabled => PrimaryAction?.IsEnabled ?? false;
    public string? SecondaryActionText => SecondaryAction?.Text;
    public bool SecondaryActionEnabled => SecondaryAction?.IsEnabled ?? false;

    public static implicit operator AiCleanupStatusRow(AiCleanupStatusRow2 row) =>
        new(row.Kind, row.Text, row.PrimaryAction?.Text);
}

public enum AzureSetupResult
{
    NotChecked,
    CliMissing,
    NotSignedIn,
    SigningIn,
    SignedIn,
    ListingModels,
    ListingFailed,
    ApiKeyIncomplete,
    ApiKeyComplete,
    ApiKeyVerified,
    ServicePrincipalIncomplete,
    ServicePrincipalComplete,
    ServicePrincipalVerified,
}

public sealed record AzureAiSetupState(
    AzureSetupResult Result,
    AzureAuthMode AuthMode = AzureAuthMode.AzureCli,
    bool ApiKeySelected = false,
    string? Account = null,
    string? SafeReason = null);

public enum CopilotSetupResult
{
    NotChecked,
    ToolNotFound,
    Installing,
    Installed,
    SignedIn,
    ModelsListed,
}

public sealed record CopilotSetupState(CopilotSetupResult Result);

public enum CustomEndpointTestResult
{
    NotTested,
    Testing,
    Connected,
    Failed,
}

public sealed record CustomEndpointSetupState(CustomEndpointTestResult Result, string? Model = null, string? SafeReason = null);

public sealed record AiCleanupPageDescription(
    bool ShowProviderSetup,
    string StatusLine,
    string? OffHelperText,
    AiCleanupStatusRow2? StatusRow);

public static class AiCleanupPageState
{
    public static AiCleanupPageDescription Describe(
        AppSettings savedSettings,
        AppSettings draftSettings,
        CleanupStatus liveStatus,
        FoundryLocalSetupDescription? foundrySetup = null,
        AzureAiSetupState? azureSetup = null,
        CopilotSetupState? copilotSetup = null,
        CustomEndpointSetupState? customSetup = null,
        AiCleanupSetupState savedSetupState = AiCleanupSetupState.NothingConfigured,
        string? providerSummary = null,
        string? modelName = null,
        string? safeReason = null)
    {
        ArgumentNullException.ThrowIfNull(savedSettings);
        ArgumentNullException.ThrowIfNull(draftSettings);

        if (!draftSettings.EnableAiCleanup)
        {
            return DescribeOff(savedSetupState, providerSummary ?? ProviderName(savedSettings.AiCleanupProvider));
        }

        var savedAndActive = RemoteActivityPolicy.IsSavedAndActive(savedSettings, draftSettings);
        var row = DraftStatusRow(
            draftSettings.AiCleanupProvider,
            foundrySetup,
            azureSetup,
            copilotSetup,
            customSetup);

        if (!savedAndActive)
        {
            return new(
                ShowProviderSetup: true,
                "Save to start AI cleanup.",
                OffHelperText: null,
                row);
        }

        return draftSettings.AiCleanupProvider switch
        {
            CleanupProvider.FoundryLocal => DescribeFoundry(foundrySetup, modelName),
            CleanupProvider.AzureFoundry => DescribeAzure(liveStatus, azureSetup ?? new(AzureSetupResult.NotChecked), safeReason),
            CleanupProvider.OpenAiCompatible => DescribeCustom(liveStatus, customSetup ?? new(CustomEndpointTestResult.NotTested), safeReason),
            CleanupProvider.GitHubCopilot => DescribeCopilot(liveStatus, copilotSetup ?? new(CopilotSetupResult.NotChecked), safeReason),
            _ => new(true, "On, but not set up yet. Until it's ready, Scribe types what it hears.", null, row),
        };
    }

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
                new(AiCleanupStatusKind.Warning, "Not set up yet. Setup downloads the AI runtime for this PC, which can take several GB.", new("Set up"))),
            CleanupStatus.Initializing => new(true, "On. Getting ready...", null, new(AiCleanupStatusKind.Busy, "Setting up. The first time can take a while.")),
            CleanupStatus.Downloading => new(true, "On. Getting ready...", null, new(AiCleanupStatusKind.Busy, progressText ?? "Loading the model...")),
            CleanupStatus.Ready => new(true, $"On. Using {model} on this PC.", null, new(AiCleanupStatusKind.Success, $"{model} is ready.", new("Unload"))),
            CleanupStatus.Unavailable => new(
                true,
                "On, but not ready. Until it's ready, Scribe types what it hears.",
                null,
                new(AiCleanupStatusKind.Error, "Couldn't start the on-device AI runtime. Try again, or choose another AI service.", new("Try again"))),
            _ => new(true, "On. Getting ready...", null, null),
        };
    }

    private static AiCleanupPageDescription DescribeFoundry(FoundryLocalSetupDescription? setup, string? modelName)
    {
        var row = setup is null
            ? new AiCleanupStatusRow2(AiCleanupStatusKind.Warning, "Not set up yet. Setup downloads the AI runtime for this PC, which can take several GB.", new("Set up"))
            : new AiCleanupStatusRow2(setup.Kind, setup.Text, setup.ActionText is null ? null : new(setup.ActionText, setup.CanUnload || setup.ActionText != "Unload"));
        var model = string.IsNullOrWhiteSpace(modelName) ? "the selected model" : modelName;
        var line = row.Kind switch
        {
            AiCleanupStatusKind.Success => $"On. Using {model} on this PC.",
            AiCleanupStatusKind.Busy => "On. Getting ready...",
            AiCleanupStatusKind.Error => "On, but not ready. Until it's ready, Scribe types what it hears.",
            AiCleanupStatusKind.Info when row.Text == "No model is loaded." => "On. Getting ready...",
            _ => "On, but not set up yet. Until it's ready, Scribe types what it hears.",
        };
        return new(true, line, null, row);
    }

    private static AiCleanupPageDescription DescribeRemote(
        CleanupStatus status,
        string? safeReason,
        string notCheckedStatus,
        string? action)
    {
        return status switch
        {
            CleanupStatus.Ready => new(true, "On. AI cleanup is ready.", null, new(AiCleanupStatusKind.Success, "Connected.", action is null ? null : new(action))),
            CleanupStatus.Initializing or CleanupStatus.Downloading => new(true, "On. Getting ready...", null, new(AiCleanupStatusKind.Busy, "Checking...")),
            CleanupStatus.Unavailable => new(
                true,
                string.IsNullOrWhiteSpace(safeReason)
                    ? "On, but not ready. Until it's ready, Scribe types what it hears."
                    : $"On, but not ready: {safeReason}. Until it's ready, Scribe types what it hears.",
                null,
                new(AiCleanupStatusKind.Error, safeReason ?? "AI cleanup isn't ready yet.", action is null ? null : new(action))),
            _ => new(true, notCheckedStatus, null, new(AiCleanupStatusKind.Info, "Not checked yet.", action is null ? null : new(action))),
        };
    }

    private static AiCleanupStatusRow2 DraftStatusRow(CleanupProvider provider) => DraftStatusRow(provider, null, null, null, null);

    private static AiCleanupStatusRow2 DraftStatusRow(
        CleanupProvider provider,
        FoundryLocalSetupDescription? foundry,
        AzureAiSetupState? azure,
        CopilotSetupState? copilot,
        CustomEndpointSetupState? custom) => provider switch
    {
        CleanupProvider.FoundryLocal when foundry is not null => new(foundry.Kind, foundry.Text, foundry.ActionText is null ? null : new(foundry.ActionText, foundry.CanUnload || foundry.ActionText != "Unload")),
        CleanupProvider.FoundryLocal => new(AiCleanupStatusKind.Warning, "Not set up yet. Setup downloads the AI runtime for this PC, which can take several GB.", new("Set up")),
        CleanupProvider.AzureFoundry => AzureRow(azure ?? new(AzureSetupResult.NotChecked)),
        CleanupProvider.OpenAiCompatible => CustomRow(custom ?? new(CustomEndpointTestResult.NotTested)),
        CleanupProvider.GitHubCopilot => CopilotRow(copilot ?? new(CopilotSetupResult.NotChecked)),
        _ => new(AiCleanupStatusKind.Info, "Not checked yet."),
    };

    private static AiCleanupPageDescription DescribeAzure(CleanupStatus liveStatus, AzureAiSetupState setup, string? safeReason)
    {
        var row = AzureRow(setup);
        var line = setup.Result switch
        {
            AzureSetupResult.CliMissing => "On, but not ready: Azure CLI isn't installed.",
            AzureSetupResult.NotSignedIn => "On, but not ready: you're not signed in to Azure.",
            AzureSetupResult.SigningIn => "On, but not ready: you're not signed in to Azure.",
            AzureSetupResult.ServicePrincipalIncomplete => "On, but not ready: the app details aren't complete.",
            AzureSetupResult.ApiKeyIncomplete => "On, but not ready: enter the endpoint, deployment name and key.",
            AzureSetupResult.ListingFailed => "On, but not ready. Until it's ready, Scribe types what it hears.",
            AzureSetupResult.NotChecked when liveStatus != CleanupStatus.Ready => "On. Choose Check sign-in to continue.",
            _ when liveStatus is CleanupStatus.Initializing or CleanupStatus.Downloading => "On. Getting ready...",
            _ when liveStatus == CleanupStatus.Unavailable && !string.IsNullOrWhiteSpace(safeReason) => $"On, but not ready: {safeReason}. Until it's ready, Scribe types what it hears.",
            _ => "On. AI cleanup is ready.",
        };
        return new(true, line, null, row);
    }

    private static AiCleanupStatusRow2 AzureRow(AzureAiSetupState setup) => setup.Result switch
    {
        AzureSetupResult.CliMissing => new(AiCleanupStatusKind.Warning, "Azure CLI isn't installed.", new("Install Azure CLI"), new("Use an API key instead")),
        AzureSetupResult.NotSignedIn => new(AiCleanupStatusKind.Info, "Not signed in to Azure.", new("Sign in"), new("Use an API key instead")),
        AzureSetupResult.SigningIn => new(AiCleanupStatusKind.Busy, "Finish signing in in your browser."),
        AzureSetupResult.SignedIn => new(AiCleanupStatusKind.Success, string.IsNullOrWhiteSpace(setup.Account) ? "Signed in." : $"Signed in as {setup.Account}.", new("Refresh models")),
        AzureSetupResult.ListingModels => new(AiCleanupStatusKind.Busy, "Finding your models..."),
        AzureSetupResult.ListingFailed => new(AiCleanupStatusKind.Error, $"Couldn't list your models. {setup.SafeReason ?? "Try again."}", new("Try again")),
        AzureSetupResult.ApiKeyIncomplete or AzureSetupResult.ServicePrincipalIncomplete => new(AiCleanupStatusKind.Info, "Fill in the details above, then choose Verify.", new("Verify", IsEnabled: false)),
        AzureSetupResult.ApiKeyComplete or AzureSetupResult.ServicePrincipalComplete => new(AiCleanupStatusKind.Info, "Fill in the details above, then choose Verify.", new("Verify")),
        AzureSetupResult.ApiKeyVerified => new(AiCleanupStatusKind.Success, "Azure accepted the key.", new("Verify")),
        AzureSetupResult.ServicePrincipalVerified => new(AiCleanupStatusKind.Success, "Verified.", new("Verify")),
        _ => new(AiCleanupStatusKind.Info, "Not checked yet.", new("Check sign-in")),
    };

    private static AiCleanupPageDescription DescribeCopilot(CleanupStatus liveStatus, CopilotSetupState setup, string? safeReason)
    {
        var row = CopilotRow(setup);
        var line = setup.Result switch
        {
            CopilotSetupResult.ToolNotFound => "On, but not ready: GitHub Copilot isn't installed.",
            CopilotSetupResult.NotChecked => "On. Choose Get models to see what your subscription includes.",
            _ when liveStatus is CleanupStatus.Initializing or CleanupStatus.Downloading => "On. Getting ready...",
            _ when liveStatus == CleanupStatus.Unavailable && !string.IsNullOrWhiteSpace(safeReason) => $"On, but not ready: {safeReason}. Until it's ready, Scribe types what it hears.",
            _ => "On. AI cleanup is ready.",
        };
        return new(true, line, null, row);
    }

    private static AiCleanupStatusRow2 CopilotRow(CopilotSetupState setup) => setup.Result switch
    {
        CopilotSetupResult.ToolNotFound => new(AiCleanupStatusKind.Warning, "GitHub Copilot isn't installed on this PC.", new("Install"), new("Check again")),
        CopilotSetupResult.Installing => new(AiCleanupStatusKind.Info, "The installer is open. Finish it, then choose Check again.", new("Check again")),
        CopilotSetupResult.Installed => new(AiCleanupStatusKind.Success, "GitHub Copilot is installed.", new("Sign in"), new("Get models")),
        CopilotSetupResult.SignedIn => new(AiCleanupStatusKind.Success, "GitHub Copilot is installed.", new("Get models")),
        CopilotSetupResult.ModelsListed => new(AiCleanupStatusKind.Success, "GitHub Copilot is ready.", new("Get models")),
        _ => new(AiCleanupStatusKind.Info, "Not checked yet.", new("Get models")),
    };

    private static AiCleanupPageDescription DescribeCustom(CleanupStatus liveStatus, CustomEndpointSetupState setup, string? safeReason)
    {
        var row = CustomRow(setup);
        var line = liveStatus == CleanupStatus.Unavailable && !string.IsNullOrWhiteSpace(safeReason)
            ? $"On, but not ready: {safeReason}. Until it's ready, Scribe types what it hears."
            : "On. Set up the AI service, then test the connection.";
        return new(true, line, null, row);
    }

    private static AiCleanupStatusRow2 CustomRow(CustomEndpointSetupState setup) => setup.Result switch
    {
        CustomEndpointTestResult.Testing => new(AiCleanupStatusKind.Busy, "Testing..."),
        CustomEndpointTestResult.Connected => new(AiCleanupStatusKind.Success, string.IsNullOrWhiteSpace(setup.Model) ? "Connected." : $"Connected. {setup.Model} answered.", new("Test connection")),
        CustomEndpointTestResult.Failed => new(AiCleanupStatusKind.Error, setup.SafeReason ?? "Couldn't connect.", new("Try again")),
        _ => new(AiCleanupStatusKind.Info, "Not tested yet.", new("Test connection")),
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
