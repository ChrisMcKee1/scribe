using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

public sealed class QuickAddSourcesTests
{
    [Fact]
    public void Forget_keeps_unsaved_correction_when_corrected_current_source_is_deleted()
    {
        var state = QuickAddSources.Forget(
            ["cloud pilot"],
            currentOriginal: "cloud pilot",
            deletedOriginal: "cloud pilot",
            hasSavableCorrection: true);

        Assert.Empty(state.Sources);
        Assert.True(state.CurrentRemoved);
        Assert.True(state.KeepCorrection);
        Assert.Equal(QuickAddSources.RemovedMessage, state.Message);
    }

    [Fact]
    public void Clear_history_keeps_savable_correction_but_removes_sources()
    {
        var state = QuickAddSources.Clear(hasSavableCorrection: true);

        Assert.Empty(state.Sources);
        Assert.True(state.CurrentRemoved);
        Assert.True(state.KeepCorrection);
        Assert.Equal(QuickAddSources.RemovedMessage, state.Message);
    }
}
