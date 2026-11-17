namespace Scribe.Core.Tests;

public sealed class SettingsWindowConnectionSourceTests
{
    [Fact]
    public void Azure_model_listing_outcomes_reach_the_visible_status_and_retry_lists_again()
    {
        var source = SettingsWindowSource;
        var listing = Slice(source, "private async Task ListAzureDeploymentsAsync()", "private async Task<(IReadOnlyList<AzureFoundryDeployment>");
        var visible = Slice(source, "private AzureAiSetupState CurrentAzureSetup()", "private CopilotSetupState CurrentCopilotSetup()");
        var retry = Slice(source, "private async Task RunAzurePrimaryActionAsync", "private void AzureEndpointBox_TextChanged");

        Assert.Contains("_azureModelListing = new(AzureSetupResult.ListingModels)", listing, StringComparison.Ordinal);
        Assert.Contains("_azureModelListing = new(AzureSetupResult.ListingFailed", listing, StringComparison.Ordinal);
        Assert.Contains("_azureModelListing ?? new(AzureSetupResult.SignedIn", visible, StringComparison.Ordinal);
        Assert.Contains("AiCleanupActionId.RefreshModels or AiCleanupActionId.TryAgain", retry, StringComparison.Ordinal);
        var refresh = Slice(source, "private async Task RefreshAzureConnectionAsync(", "private async Task<AzureSignInStatus>");
        var retire = refresh.IndexOf("++_azureDeploymentLoadVersion", StringComparison.Ordinal);
        var clear = refresh.IndexOf("_azureModelListing = null;", StringComparison.Ordinal);
        var signIn = refresh.IndexOf("await AzureCliSignIn", StringComparison.Ordinal);
        Assert.True(signIn >= 0);
        Assert.True(retire >= 0 && retire < signIn);
        Assert.True(clear >= 0 && clear < signIn);
        Assert.Contains("AiCleanupPageState.AzureModelListing(deployments.Count, discovery.FailedTenantCount)", listing,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Both_save_validation_paths_keep_the_explicit_API_key_choice_and_show_its_refusal()
    {
        var source = File.ReadAllText(FindRepoFile("src", "Scribe.App", "Settings", "SettingsWindow.Closing.cs"));
        var preflight = Slice(source, "private async Task<SavePreflightInput?> PrepareSavePreflightAsync()", "private ");
        var direct = Slice(source, "private bool ValidateDraftBeforeSave()", "private IReadOnlyList<DurationDraftField>");
        Assert.Contains("AzureApiKeySelected: IsAzureApiKeySelected", preflight, StringComparison.Ordinal);
        Assert.Contains("AzureApiKeySelected: IsAzureApiKeySelected", direct, StringComparison.Ordinal);
        Assert.Contains("case ValidationCode.ApiKeyEmpty:", source, StringComparison.Ordinal);
        Assert.Contains("RevealAzureSpeechCredentials(nameof(AzureApiKeyBox))", source, StringComparison.Ordinal);
    }

    private static string SettingsWindowSource =>
        File.ReadAllText(FindRepoFile("src", "Scribe.App", "Settings", "SettingsWindow.xaml.cs"));

    [Fact]
    public void Test_connection_candidate_reads_the_prompt_editors_not_only_saved_settings()
    {
        var source = SettingsWindowSource;
        var method = Slice(source, "private AppSettings CurrentAiDraftSettings()", "// --- Filterable model dropdowns");

        Assert.Contains("AiWritingStyleBox", method, StringComparison.Ordinal);
        Assert.Contains("SelectedPromptStyle", method, StringComparison.Ordinal);
        Assert.Contains("AiFrontierPromptBox", method, StringComparison.Ordinal);
        Assert.Contains("AiLocalPromptBox", method, StringComparison.Ordinal);
        Assert.Contains("CleanupConnectionTestPolicy.Canonicalize", method, StringComparison.Ordinal);
    }

    [Fact]
    public void Azure_connection_rows_do_not_fall_back_to_unbound_verification_outcomes()
    {
        var source = SettingsWindowSource;
        var method = Slice(source, "private AzureAiSetupState CurrentAzureSetup()", "private CopilotSetupState CurrentCopilotSetup()");

        Assert.DoesNotContain("ToApiKeyResult", method, StringComparison.Ordinal);
        Assert.DoesNotContain("ToServicePrincipalResult", method, StringComparison.Ordinal);
        Assert.Contains("CleanupConnectionTestPolicy.CanTest", method, StringComparison.Ordinal);
        Assert.Contains("AzureSetupResult.ApiKeyComplete", method, StringComparison.Ordinal);
        Assert.Contains("AzureSetupResult.ServicePrincipalComplete", method, StringComparison.Ordinal);
    }

    private static string Slice(string source, string start, string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Missing start marker {start}.");
        var endIndex = source.IndexOf(end, startIndex + start.Length, StringComparison.Ordinal);
        Assert.True(endIndex > startIndex, $"Missing end marker {end}.");
        return source[startIndex..endIndex];
    }

    private static string FindRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine([dir.FullName, .. parts]);
            if (File.Exists(path))
            {
                return path;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the repository root.");
    }
}
