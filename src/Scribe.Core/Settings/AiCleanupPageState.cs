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

public enum AiCleanupActionId
{
    SetUp,
    DownloadAndLoad,
    Load,
    Unload,
    TryAgain,
    CheckSignIn,
    SignIn,
    RefreshModels,
    InstallAzureCli,
    UseApiKeyInstead,
    Verify,
    InstallCopilot,
    UpdateCopilot,
    CheckAgain,
    SignInCopilot,
    TestConnection,
}

public sealed record AiCleanupAction(AiCleanupActionId Id, string Text, bool IsEnabled = true);

public sealed record AiCleanupStatusRow(
    AiCleanupStatusKind Kind,
    string Text,
    AiCleanupAction? Primary = null,
    AiCleanupAction? Secondary = null)
{
    public string? ActionText => Primary?.Text;
    public bool ActionEnabled => Primary?.IsEnabled ?? false;
    public string? SecondaryActionText => Secondary?.Text;
    public bool SecondaryActionEnabled => Secondary?.IsEnabled ?? false;
}

public enum AzureVerificationOutcomeKind
{
    NotRun,
    Succeeded,
    Failed,
    ChangedSince,
}

public sealed record AzureVerificationOutcome(AzureVerificationOutcomeKind Kind, string? SafeMessage = null)
{
    public static AzureVerificationOutcome NotRun { get; } = new(AzureVerificationOutcomeKind.NotRun);

    public static AzureVerificationOutcome Succeeded(string? safeMessage = null) => new(AzureVerificationOutcomeKind.Succeeded, safeMessage);

    public static AzureVerificationOutcome Failed(string safeReason) => new(AzureVerificationOutcomeKind.Failed, safeReason);

    public static AzureVerificationOutcome ChangedSince { get; } = new(AzureVerificationOutcomeKind.ChangedSince);

    public AzureSetupResult ToApiKeyResult(bool complete) => ToResult(complete, AzureSetupResult.ApiKeyComplete, AzureSetupResult.ApiKeyVerified, AzureSetupResult.ApiKeyVerificationFailed, AzureSetupResult.ApiKeyVerifyAgain, AzureSetupResult.ApiKeyIncomplete);

    public AzureSetupResult ToServicePrincipalResult(bool complete) => ToResult(complete, AzureSetupResult.ServicePrincipalComplete, AzureSetupResult.ServicePrincipalVerified, AzureSetupResult.ServicePrincipalVerificationFailed, AzureSetupResult.ServicePrincipalVerifyAgain, AzureSetupResult.ServicePrincipalIncomplete);

    private AzureSetupResult ToResult(
        bool complete,
        AzureSetupResult completeResult,
        AzureSetupResult succeeded,
        AzureSetupResult failed,
        AzureSetupResult changed,
        AzureSetupResult incomplete)
    {
        if (!complete)
        {
            return incomplete;
        }

        return Kind switch
        {
            AzureVerificationOutcomeKind.Succeeded => succeeded,
            AzureVerificationOutcomeKind.Failed => failed,
            AzureVerificationOutcomeKind.ChangedSince => changed,
            _ => completeResult,
        };
    }
}

public enum AzureSetupResult
{
    NotChecked,
    CheckingSignIn,
    CliMissing,
    NotSignedIn,
    SigningIn,
    SignedIn,
    ListingModels,
    ListingFailed,
    Verifying,
    ApiKeyIncomplete,
    ApiKeyComplete,
    ApiKeyVerified,
    ApiKeyVerificationFailed,
    ApiKeyVerifyAgain,
    ServicePrincipalIncomplete,
    ServicePrincipalComplete,
    ServicePrincipalVerified,
    ServicePrincipalVerificationFailed,
    ServicePrincipalVerifyAgain,
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
    Outdated,
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
    AiCleanupStatusRow? StatusRow);

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

    public static AiCleanupSetupState SavedSetupState(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.EnableAiCleanup && settings.AiCleanupProvider == CleanupProvider.FoundryLocal)
        {
            return AiCleanupSetupState.NothingConfigured;
        }

        if (!HasProviderConfiguration(settings, settings.AiCleanupProvider))
        {
            return settings.AiCleanupProvider == CleanupProvider.FoundryLocal
                ? AiCleanupSetupState.Incomplete
                : AiCleanupSetupState.NothingConfigured;
        }

        return AiCleanupSetupState.Complete;
    }

    public static string ProviderSetupSummary(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return settings.AiCleanupProvider switch
        {
            CleanupProvider.FoundryLocal => "On this PC (Foundry Local)",
            CleanupProvider.AzureFoundry when !string.IsNullOrWhiteSpace(settings.AiCleanupAzureDeployment) =>
                $"Microsoft Foundry ({settings.AiCleanupAzureDeployment})",
            CleanupProvider.AzureFoundry => "Microsoft Foundry",
            CleanupProvider.OpenAiCompatible when !string.IsNullOrWhiteSpace(settings.AiCleanupCustomModel) =>
                $"Another AI service ({settings.AiCleanupCustomModel})",
            CleanupProvider.OpenAiCompatible => "Another AI service",
            CleanupProvider.GitHubCopilot when !string.IsNullOrWhiteSpace(settings.AiCleanupCopilotModel) =>
                $"GitHub Copilot ({settings.AiCleanupCopilotModel})",
            CleanupProvider.GitHubCopilot => "GitHub Copilot",
            _ => "AI cleanup",
        };
    }

    public static bool HasProviderConfiguration(AppSettings settings, CleanupProvider provider)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return provider switch
        {
            CleanupProvider.FoundryLocal => !string.IsNullOrWhiteSpace(settings.AiCleanupModel),
            CleanupProvider.AzureFoundry => !string.IsNullOrWhiteSpace(settings.AiCleanupAzureEndpoint) &&
                !string.IsNullOrWhiteSpace(settings.AiCleanupAzureDeployment) &&
                (!string.IsNullOrWhiteSpace(settings.AiCleanupAzureApiKey) ||
                 settings.AiCleanupAzureAuthMode == AzureAuthMode.AzureCli ||
                 AzureServicePrincipalValidator.IsComplete(settings.AiCleanupAzureTenantId, settings.AiCleanupAzureClientId, settings.AiCleanupAzureClientSecret)),
            CleanupProvider.OpenAiCompatible => !string.IsNullOrWhiteSpace(settings.AiCleanupCustomEndpoint) &&
                !string.IsNullOrWhiteSpace(settings.AiCleanupCustomModel),
            CleanupProvider.GitHubCopilot => true,
            _ => false,
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
                new(AiCleanupStatusKind.Warning, "Not set up yet. Setup downloads the AI runtime for this PC, which can take several GB.", new(AiCleanupActionId.SetUp, "Set up"))),
            CleanupStatus.Initializing => new(true, "On. Getting ready...", null, new(AiCleanupStatusKind.Busy, "Setting up. The first time can take a while.")),
            CleanupStatus.Downloading => new(true, "On. Getting ready...", null, new(AiCleanupStatusKind.Busy, progressText ?? "Loading the model...")),
            CleanupStatus.Ready => new(true, $"On. Using {model} on this PC.", null, new(AiCleanupStatusKind.Success, $"{model} is ready.", new(AiCleanupActionId.Unload, "Unload"))),
            CleanupStatus.Unavailable => new(
                true,
                "On, but not ready. Until it's ready, Scribe types what it hears.",
                null,
                new(AiCleanupStatusKind.Error, "Couldn't start the on-device AI runtime. Try again, or choose another AI service.", new(AiCleanupActionId.TryAgain, "Try again"))),
            _ => new(true, "On. Getting ready...", null, null),
        };
    }

    private static AiCleanupPageDescription DescribeFoundry(FoundryLocalSetupDescription? setup, string? modelName)
    {
        var row = setup is null
            ? new AiCleanupStatusRow(AiCleanupStatusKind.Warning, "Not set up yet. Setup downloads the AI runtime for this PC, which can take several GB.", new(AiCleanupActionId.SetUp, "Set up"))
            : new AiCleanupStatusRow(setup.Kind, setup.Text, setup.ActionText is null ? null : ActionFor(setup.ActionText, setup.CanUnload || setup.ActionText != "Unload"));
        var model = string.IsNullOrWhiteSpace(modelName) ? "the selected model" : modelName;

        // The top card follows the setup stage (the 6.2.1 rows), not the row's color: "Ready to download" is an Info row
        // whose top card still reads "Getting ready".
        var line = (setup?.Stage ?? FoundryLocalSetupStage.NotSetUp) switch
        {
            FoundryLocalSetupStage.Loaded => $"On. Using {model} on this PC.",
            FoundryLocalSetupStage.SettingUp or FoundryLocalSetupStage.RuntimeReady or FoundryLocalSetupStage.CachedUnloaded
                or FoundryLocalSetupStage.Checking or FoundryLocalSetupStage.DownloadingOrLoading => "On. Getting ready...",
            FoundryLocalSetupStage.Failed or FoundryLocalSetupStage.ModelFailed => NotReadyLine,
            _ => "On, but not set up yet. Until it's ready, Scribe types what it hears.",
        };
        return new(true, line, null, row);
    }

    private const string NotReadyLine = "On, but not ready. Until it's ready, Scribe types what it hears.";

    // The saved, active remote provider's top card reports the running service (plan 6.2.1 "after Save: live status").
    // A discovery or verification outcome belongs to the setup row: a failed model list does not stop a running
    // deployment. A known setup problem only explains why the service is unavailable.
    private static string LiveRemoteLine(CleanupStatus liveStatus, string? knownCause, string? safeReason) => liveStatus switch
    {
        CleanupStatus.Ready => "On. AI cleanup is ready.",
        CleanupStatus.Initializing or CleanupStatus.Downloading => "On. Getting ready...",
        CleanupStatus.Unavailable when knownCause is not null => knownCause,
        CleanupStatus.Unavailable when !string.IsNullOrWhiteSpace(safeReason) =>
            $"On, but not ready: {safeReason}. Until it's ready, Scribe types what it hears.",
        CleanupStatus.Unavailable => NotReadyLine,
        _ => "On, but not set up yet. Until it's ready, Scribe types what it hears.",
    };

    private static AiCleanupPageDescription DescribeRemote(
        CleanupStatus status,
        string? safeReason,
        string notCheckedStatus,
        string? action)
    {
        return status switch
        {
            CleanupStatus.Ready => new(true, "On. AI cleanup is ready.", null, new(AiCleanupStatusKind.Success, "Connected.", action is null ? null : ActionFor(action))),
            CleanupStatus.Initializing or CleanupStatus.Downloading => new(true, "On. Getting ready...", null, new(AiCleanupStatusKind.Busy, "Checking...")),
            CleanupStatus.Unavailable => new(
                true,
                string.IsNullOrWhiteSpace(safeReason)
                    ? "On, but not ready. Until it's ready, Scribe types what it hears."
                    : $"On, but not ready: {safeReason}. Until it's ready, Scribe types what it hears.",
                null,
                new(AiCleanupStatusKind.Error, safeReason ?? "AI cleanup isn't ready yet.", action is null ? null : ActionFor(action))),
            _ => new(true, notCheckedStatus, null, new(AiCleanupStatusKind.Info, "Not checked yet.", action is null ? null : ActionFor(action))),
        };
    }

    private static AiCleanupStatusRow DraftStatusRow(CleanupProvider provider) => DraftStatusRow(provider, null, null, null, null);

    private static AiCleanupStatusRow DraftStatusRow(
        CleanupProvider provider,
        FoundryLocalSetupDescription? foundry,
        AzureAiSetupState? azure,
        CopilotSetupState? copilot,
        CustomEndpointSetupState? custom) => provider switch
    {
        CleanupProvider.FoundryLocal when foundry is not null => new(foundry.Kind, foundry.Text, foundry.ActionText is null ? null : ActionFor(foundry.ActionText, foundry.CanUnload || foundry.ActionText != "Unload")),
        CleanupProvider.FoundryLocal => new(AiCleanupStatusKind.Warning, "Not set up yet. Setup downloads the AI runtime for this PC, which can take several GB.", new(AiCleanupActionId.SetUp, "Set up")),
        CleanupProvider.AzureFoundry => AzureRow(azure ?? new(AzureSetupResult.NotChecked)),
        CleanupProvider.OpenAiCompatible => CustomRow(custom ?? new(CustomEndpointTestResult.NotTested)),
        CleanupProvider.GitHubCopilot => CopilotRow(copilot ?? new(CopilotSetupResult.NotChecked)),
        _ => new(AiCleanupStatusKind.Info, "Not checked yet."),
    };

    private static AiCleanupPageDescription DescribeAzure(CleanupStatus liveStatus, AzureAiSetupState setup, string? safeReason)
    {
        var knownCause = setup.Result switch
        {
            AzureSetupResult.CliMissing => "On, but not ready: Azure CLI isn't installed.",
            AzureSetupResult.NotSignedIn or AzureSetupResult.SigningIn => "On, but not ready: you're not signed in to Azure.",
            AzureSetupResult.ServicePrincipalIncomplete => "On, but not ready: the app details aren't complete.",
            AzureSetupResult.ApiKeyIncomplete => "On, but not ready: enter the endpoint, deployment name and key.",
            _ => null,
        };
        return new(true, LiveRemoteLine(liveStatus, knownCause, safeReason), null, AzureRow(setup));
    }

    private static AiCleanupStatusRow AzureRow(AzureAiSetupState setup) => setup.Result switch
    {
        AzureSetupResult.CheckingSignIn => new(AiCleanupStatusKind.Busy, "Checking your Azure sign-in..."),
        AzureSetupResult.CliMissing => new(AiCleanupStatusKind.Warning, "Azure CLI isn't installed. Scribe uses it to sign you in and find your models.", new(AiCleanupActionId.InstallAzureCli, "Install Azure CLI"), new(AiCleanupActionId.UseApiKeyInstead, "Use an API key instead")),
        AzureSetupResult.NotSignedIn => new(AiCleanupStatusKind.Info, "Not signed in to Azure.", new(AiCleanupActionId.SignIn, "Sign in"), new(AiCleanupActionId.UseApiKeyInstead, "Use an API key instead")),
        AzureSetupResult.SigningIn => new(AiCleanupStatusKind.Busy, "Finish signing in in your browser."),
        AzureSetupResult.SignedIn => new(AiCleanupStatusKind.Success, string.IsNullOrWhiteSpace(setup.Account) ? "Signed in." : $"Signed in as {setup.Account}.", new(AiCleanupActionId.RefreshModels, "Refresh models")),
        AzureSetupResult.ListingModels => new(AiCleanupStatusKind.Busy, "Finding your models..."),
        AzureSetupResult.ListingFailed => new(AiCleanupStatusKind.Error, $"Couldn't list your models. {setup.SafeReason ?? "Try again."}", new(AiCleanupActionId.TryAgain, "Try again")),
        AzureSetupResult.Verifying => new(AiCleanupStatusKind.Busy, "Verifying..."),
        AzureSetupResult.ApiKeyIncomplete or AzureSetupResult.ServicePrincipalIncomplete => new(AiCleanupStatusKind.Info, "Fill in the details above, then choose Verify.", new(AiCleanupActionId.Verify, "Verify", IsEnabled: false)),
        AzureSetupResult.ApiKeyComplete or AzureSetupResult.ServicePrincipalComplete => new(AiCleanupStatusKind.Info, "Fill in the details above, then choose Verify.", new(AiCleanupActionId.Verify, "Verify")),
        AzureSetupResult.ApiKeyVerified => new(AiCleanupStatusKind.Success, "Azure accepted the key.", new(AiCleanupActionId.Verify, "Verify")),
        AzureSetupResult.ServicePrincipalVerified => new(AiCleanupStatusKind.Success, "Verified.", new(AiCleanupActionId.Verify, "Verify")),
        AzureSetupResult.ApiKeyVerificationFailed or AzureSetupResult.ServicePrincipalVerificationFailed => new(AiCleanupStatusKind.Error, setup.SafeReason ?? "Couldn't verify the details.", new(AiCleanupActionId.Verify, "Verify")),
        AzureSetupResult.ApiKeyVerifyAgain or AzureSetupResult.ServicePrincipalVerifyAgain => new(AiCleanupStatusKind.Info, "Changed since the last check. Choose Verify.", new(AiCleanupActionId.Verify, "Verify")),
        _ => new(AiCleanupStatusKind.Info, "Not checked yet.", new(AiCleanupActionId.CheckSignIn, "Check sign-in")),
    };

    private static AiCleanupPageDescription DescribeCopilot(CleanupStatus liveStatus, CopilotSetupState setup, string? safeReason)
    {
        var knownCause = setup.Result is CopilotSetupResult.ToolNotFound or CopilotSetupResult.Installing
            ? "On, but not ready: GitHub Copilot isn't installed."
            : null;
        return new(true, LiveRemoteLine(liveStatus, knownCause, safeReason), null, CopilotRow(setup));
    }

    private static AiCleanupStatusRow CopilotRow(CopilotSetupState setup) => setup.Result switch
    {
        CopilotSetupResult.ToolNotFound => new(AiCleanupStatusKind.Warning, "GitHub Copilot isn't installed on this PC.", new(AiCleanupActionId.InstallCopilot, "Install"), new(AiCleanupActionId.CheckAgain, "Check again")),
        CopilotSetupResult.Outdated => new(AiCleanupStatusKind.Warning, "GitHub Copilot needs an update.", new(AiCleanupActionId.UpdateCopilot, "Update"), new(AiCleanupActionId.CheckAgain, "Check again")),
        CopilotSetupResult.Installing => new(AiCleanupStatusKind.Info, "The installer is open. Finish it, then choose Check again.", new(AiCleanupActionId.CheckAgain, "Check again")),
        CopilotSetupResult.Installed => new(AiCleanupStatusKind.Success, "GitHub Copilot is installed.", new(AiCleanupActionId.SignInCopilot, "Sign in")),
        CopilotSetupResult.SignedIn => new(AiCleanupStatusKind.Success, "GitHub Copilot is installed."),
        CopilotSetupResult.ModelsListed => new(AiCleanupStatusKind.Success, "GitHub Copilot is installed."),
        _ => new(AiCleanupStatusKind.Busy, "Looking for GitHub Copilot..."),
    };

    private static AiCleanupPageDescription DescribeCustom(CleanupStatus liveStatus, CustomEndpointSetupState setup, string? safeReason) =>
        new(true, LiveRemoteLine(liveStatus, knownCause: null, safeReason), null, CustomRow(setup));

    private static AiCleanupStatusRow CustomRow(CustomEndpointSetupState setup) => setup.Result switch
    {
        CustomEndpointTestResult.Testing => new(AiCleanupStatusKind.Busy, "Testing..."),
        CustomEndpointTestResult.Connected => new(AiCleanupStatusKind.Success, string.IsNullOrWhiteSpace(setup.Model) ? "Connected." : $"Connected. {setup.Model} answered.", new(AiCleanupActionId.TestConnection, "Test connection")),
        CustomEndpointTestResult.Failed => new(AiCleanupStatusKind.Error, setup.SafeReason ?? "Couldn't connect.", new(AiCleanupActionId.TryAgain, "Try again")),
        _ => new(AiCleanupStatusKind.Info, "Not tested yet."),
    };

    private static AiCleanupAction? ActionFor(string? text, bool enabled = true) => text switch
    {
        "Set up" => new(AiCleanupActionId.SetUp, text, enabled),
        "Download and load" => new(AiCleanupActionId.DownloadAndLoad, text, enabled),
        "Load" => new(AiCleanupActionId.Load, text, enabled),
        "Unload" => new(AiCleanupActionId.Unload, text, enabled),
        "Try again" => new(AiCleanupActionId.TryAgain, text, enabled),
        "Check sign-in" => new(AiCleanupActionId.CheckSignIn, text, enabled),
        "Sign in" => new(AiCleanupActionId.SignIn, text, enabled),
        "Refresh models" => new(AiCleanupActionId.RefreshModels, text, enabled),
        "Test connection" => new(AiCleanupActionId.TestConnection, text, enabled),
        null => null,
        _ => null,
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
