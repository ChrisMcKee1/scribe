using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

public sealed class FoundryLocalSetupOutcomeTests
{
    [Theory]
    [InlineData(FoundryLocalSetupStage.Failed, true)]
    [InlineData(FoundryLocalSetupStage.ModelFailed, true)]
    [InlineData(FoundryLocalSetupStage.SettingUp, true)]
    [InlineData(FoundryLocalSetupStage.DownloadingOrLoading, true)]
    [InlineData(FoundryLocalSetupStage.RuntimeReady, false)]
    [InlineData(FoundryLocalSetupStage.CachedUnloaded, false)]
    [InlineData(FoundryLocalSetupStage.Loaded, false)]
    [InlineData(FoundryLocalSetupStage.NotSetUp, false)]
    public void A_picker_rebuild_keeps_only_failures_and_running_operations(FoundryLocalSetupStage stage, bool kept)
    {
        // The AI review's round 4 case: a Set up that ended "ready" must not hide the download and the loaded model that a
        // later Save starts, while a failed Load keeps its error through a catalog refresh (round 3).
        var outcome = FoundryLocalSetup.Describe(stage, "Qwen3 1.7B", "1.2 GB");

        Assert.Equal(kept, FoundryLocalSetup.KeepsThroughPickerRebuild(outcome));
    }

    [Fact]
    public void A_setup_summary_written_by_the_page_is_retired_by_a_rebuild()
    {
        // SetupFoundryLocalAsync builds this Info line itself rather than through Describe.
        var summary = new FoundryLocalSetupDescription(
            AiCleanupStatusKind.Info, "Foundry Local is running. 12 models are in the dropdown above.", "Load", false, FoundryLocalSetupStage.RuntimeReady);

        Assert.False(FoundryLocalSetup.KeepsThroughPickerRebuild(summary));
        Assert.True(FoundryLocalSetup.RetiredBySaveThatServesIt(summary));
    }

    [Theory]
    [InlineData(FoundryLocalSetupStage.RuntimeReady, true)]
    [InlineData(FoundryLocalSetupStage.Failed, true)]
    [InlineData(FoundryLocalSetupStage.ModelFailed, true)]
    [InlineData(FoundryLocalSetupStage.Loaded, true)]
    [InlineData(FoundryLocalSetupStage.SettingUp, false)]
    [InlineData(FoundryLocalSetupStage.DownloadingOrLoading, false)]
    public void A_save_that_serves_the_model_retires_every_settled_outcome(FoundryLocalSetupStage stage, bool retired)
    {
        var outcome = FoundryLocalSetup.Describe(stage, "Qwen3 1.7B", "1.2 GB");

        Assert.Equal(retired, FoundryLocalSetup.RetiredBySaveThatServesIt(outcome));
    }

    [Fact]
    public void The_picker_rebuild_decides_from_the_alias_it_restored()
    {
        // AI review, round 5: replacing the picker's items raises SelectionChanged with the selection cleared, when the
        // combo's alias reads as its display text, so a decision taken there cleared a failed Load's error. The rebuild
        // decides once, from the alias it captured; SelectionChanged decides only for a real choice.
        var window = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Scribe.App", "Settings", "SettingsWindow.xaml.cs"));
        var rebuild = Body(window, "private void SetFoundryModelItems(");
        Assert.Contains("FoundryLocalSetup.KeepsThroughPickerRebuild(outcome)", rebuild, StringComparison.Ordinal);
        Assert.Contains("string.Equals(selected, _foundryOperationAlias", rebuild, StringComparison.Ordinal);

        var changed = Body(window, "private void AiModelBox_SelectionChanged(");
        Assert.Contains("if (!_suppressComboFilter)", changed, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedFoundryModelAlias", changed, StringComparison.Ordinal);
    }

    private static string Body(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{signature} was not found.");
        var open = source.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            depth += source[i] switch { '{' => 1, '}' => -1, _ => 0 };
            if (depth == 0)
            {
                return source[open..(i + 1)];
            }
        }

        throw new InvalidOperationException($"{signature} has no end.");
    }

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return root.FullName;
    }
}
