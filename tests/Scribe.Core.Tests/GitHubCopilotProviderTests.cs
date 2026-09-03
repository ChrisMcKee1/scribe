using Scribe.Core.Cleanup;

namespace Scribe.Core.Tests;

/// <summary>
/// The GitHub Copilot cleanup provider.
/// </summary>
/// <remarks>
/// These pin the two things about this provider that are easy to break and invisible when they
/// break. Its configuration is "actionable" with no endpoint and no key, which is unlike every other
/// provider and reads like an oversight to anyone tightening that switch later. And CLI detection is
/// contractually silent: it is called from a settings window to decide whether to offer an install
/// button, so a throw there would surface as a broken Settings page rather than as a missing
/// dependency.
/// </remarks>
public class GitHubCopilotProviderTests
{
    private static CleanupOptions Copilot(string? model = null) => new(
        Enabled: true,
        Provider: CleanupProvider.GitHubCopilot,
        FoundryModelAlias: CleanupModelCatalog.DefaultAlias,
        AzureEndpoint: null,
        AzureDeployment: null,
        CopilotModel: model);

    [Fact]
    public void Copilot_is_actionable_without_an_endpoint_or_a_key()
    {
        // The Copilot backend takes neither: it drives a locally authenticated CLI. Requiring an
        // endpoint here would make the provider permanently unselectable.
        Assert.True(Copilot().IsActionable);
    }

    [Fact]
    public void A_blank_model_is_still_actionable()
    {
        // Blank means "this GitHub account's default model", which is a working configuration. It is
        // deliberately not treated as missing configuration.
        Assert.True(Copilot(model: null).IsActionable);
        Assert.True(Copilot(model: "   ").IsActionable);
    }

    [Fact]
    public void A_chosen_model_is_carried_on_the_options()
    {
        Assert.Equal("claude-sonnet-4", Copilot("claude-sonnet-4").CopilotModel);
    }

    [Fact]
    public void Disabled_cleanup_is_never_actionable_even_for_copilot()
    {
        Assert.False((Copilot() with { Enabled = false }).IsActionable);
    }

    [Fact]
    public void Cli_detection_never_throws_and_reports_a_coherent_result()
    {
        // Runs on a machine that may or may not have the CLI, so this asserts the contract rather
        // than the outcome: it answers, and a positive answer carries the path it found.
        var status = GitHubCopilotCli.Detect();

        if (status.Found)
        {
            Assert.False(string.IsNullOrWhiteSpace(status.Path));
            Assert.True(File.Exists(status.Path));
        }
        else
        {
            Assert.Null(status.Path);
        }
    }

    [Fact]
    public void A_missing_cli_reports_nothing_found()
    {
        Assert.False(GitHubCopilotCliStatus.Missing.Found);
        Assert.Null(GitHubCopilotCliStatus.Missing.Path);
        Assert.Null(GitHubCopilotCliStatus.Missing.Version);
    }

    [Fact]
    public void A_real_path_in_the_override_is_accepted()
    {
        // The negative case was already covered; this is the positive one. Deterministic: a temp file
        // stands in for the executable, because the assertion is about the resolution rule, not about
        // whether this machine has Copilot installed.
        var original = Environment.GetEnvironmentVariable(GitHubCopilotCli.PathVariable);
        var fake = Path.Combine(Path.GetTempPath(), $"scribe-copilot-{Guid.NewGuid():N}.exe");
        try
        {
            File.WriteAllText(fake, string.Empty);
            Environment.SetEnvironmentVariable(GitHubCopilotCli.PathVariable, fake);

            var status = GitHubCopilotCli.Detect();

            Assert.True(status.Found);
            Assert.Equal(fake, status.Path);
        }
        finally
        {
            Environment.SetEnvironmentVariable(GitHubCopilotCli.PathVariable, original);
            try { File.Delete(fake); } catch (IOException) { /* temp file */ }
        }
    }

    [Fact]
    public void A_cmd_shim_on_PATH_is_found_through_PATHEXT()
    {
        /*
         * The npm install writes `copilot.cmd` and no `.exe` at all, which is why detection walks
         * PATHEXT instead of assuming `.exe`. That branch carries a comment saying an exe-only search
         * "reports a working install as missing", and it was the one untested path in the resolver.
         */
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var originalOverride = Environment.GetEnvironmentVariable(GitHubCopilotCli.PathVariable);
        var dir = Path.Combine(Path.GetTempPath(), $"scribe-copilot-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "copilot.cmd"), string.Empty);

            // The override wins over PATH, so it has to be clear for this to exercise the PATH walk.
            Environment.SetEnvironmentVariable(GitHubCopilotCli.PathVariable, null);
            Environment.SetEnvironmentVariable("PATH", dir);

            var status = GitHubCopilotCli.Detect();

            Assert.True(status.Found);
            // Case-insensitively: the extension comes back in whatever case PATHEXT supplies (".CMD"
            // on a default Windows install) rather than the case of the file on disk, and Windows
            // paths do not distinguish the two. Asserting the exact casing would pin an incidental
            // detail of the environment variable rather than the resolution rule under test.
            Assert.Equal(
                Path.Combine(dir, "copilot.cmd"),
                status.Path,
                StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Environment.SetEnvironmentVariable(GitHubCopilotCli.PathVariable, originalOverride);
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* temp dir */ }
        }
    }

    [Theory]
    [InlineData(CleanupProvider.FoundryLocal)]
    [InlineData(CleanupProvider.AzureFoundry)]
    [InlineData(CleanupProvider.OpenAiCompatible)]
    [InlineData(CleanupProvider.GitHubCopilot)]
    public void A_dictation_is_never_given_less_time_than_one_call(CleanupProvider provider)
    {
        /*
         * The Copilot per-call budget is 120 seconds and the operation-wide cap was a flat 90, so the
         * larger figure was unreachable: the operation token cancelled first. The measured 27 second
         * round trip sat inside both, which is precisely why it looked fine. Pinned for every
         * provider so the next per-call budget cannot quietly exceed the total either.
         */
        var total = TextCleanupService.TotalBudgetFor(provider);
        var single = TimeSpan.FromSeconds(TextCleanupService.SingleCallBudgetSeconds(provider));

        Assert.True(
            total >= single,
            $"{provider}: total budget {total} is below the single-call budget {single}.");
    }

    [Fact]
    public void Detection_honours_the_sdk_path_override()
    {
        // GITHUB_COPILOT_CLI_PATH is the SDK's own override, so a user who installed somewhere
        // unusual has already told us where. A path that does not exist must report missing rather
        // than being taken on trust: the alternative is a provider that looks configured and fails
        // at the first dictation.
        var original = Environment.GetEnvironmentVariable(GitHubCopilotCli.PathVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                GitHubCopilotCli.PathVariable,
                Path.Combine(Path.GetTempPath(), "scribe-no-such-copilot.exe"));

            Assert.False(GitHubCopilotCli.Detect().Found);
        }
        finally
        {
            Environment.SetEnvironmentVariable(GitHubCopilotCli.PathVariable, original);
        }
    }
}
