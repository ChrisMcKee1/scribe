using Scribe.Core.Cleanup;
using Scribe.Core.Models;

namespace Scribe.Core.Tests;

public sealed class CleanupFailureModelTests
{
    [Theory]
    [InlineData(CleanupProvider.FoundryLocal, "local-model")]
    [InlineData(CleanupProvider.AzureFoundry, "azure-model")]
    [InlineData(CleanupProvider.OpenAiCompatible, "deepseek-r1")]
    [InlineData(CleanupProvider.GitHubCopilot, "copilot-model")]
    public void A_failure_names_the_model_selected_for_its_own_place(CleanupProvider provider, string expected)
    {
        var settings = new AppSettings
        {
            AiCleanupProvider = provider,
            AiCleanupModel = "local-model",
            AiCleanupAzureDeployment = "azure-model",
            AiCleanupCustomModel = " deepseek-r1 ",
            AiCleanupCopilotModel = "copilot-model",
            AiCleanupCustomApiKey = "synthetic-private-key",
        };

        var failure = CleanupFailure.FromSettings(settings, "A fixed failure reason.", "synthetic dictation");

        Assert.Equal(expected, failure.Model);
        Assert.Equal(provider.ToString(), failure.Provider);
        Assert.Equal("A fixed failure reason.", failure.Reason);
        Assert.Equal("synthetic dictation", failure.Sample);
        Assert.Equal(0, failure.Id);
        Assert.DoesNotContain("synthetic-private-key", failure.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void A_Copilot_default_never_uses_the_unrelated_local_model_name(string? model)
    {
        var settings = new AppSettings { AiCleanupProvider = CleanupProvider.GitHubCopilot, AiCleanupCopilotModel = model };
        Assert.Null(CleanupFailure.FromSettings(settings, "A fixed failure reason.").Model);
    }

    [Fact]
    public void The_controller_records_the_provider_specific_failure_model()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Scribe.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var source = File.ReadAllText(Path.Combine(directory.FullName, "src", "Scribe.App", "Dictation", "DictationController.cs"));
        var start = source.IndexOf("private void RecordCleanupFailure(", StringComparison.Ordinal);
        var end = source.IndexOf("// Diagnostics near teardown", start, StringComparison.Ordinal);
        var record = source[start..end];
        Assert.Contains("_failureLog.Add(CleanupFailure.FromSettings(settings, reason, rawText));", record, StringComparison.Ordinal);
        Assert.DoesNotContain("settings.AiCleanupModel", record, StringComparison.Ordinal);
    }
}
