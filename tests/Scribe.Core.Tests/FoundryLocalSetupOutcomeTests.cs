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
}
