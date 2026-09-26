namespace Scribe.Core.Tests;

public sealed class SettingsWindowConnectionSourceTests
{
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
        var endIndex = source.IndexOf(end, startIndex, StringComparison.Ordinal);
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
