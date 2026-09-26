using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

public sealed class SettingsClosePromptTests
{
    [Theory]
    [InlineData(CloseTrigger.CancelButton, "Save changes before closing?", "Save and close")]
    [InlineData(CloseTrigger.Escape, "Save changes before closing?", "Save and close")]
    [InlineData(CloseTrigger.AltF4, "Save changes before closing?", "Save and close")]
    [InlineData(CloseTrigger.CloseButton, "Save changes before closing?", "Save and close")]
    [InlineData(CloseTrigger.TrayQuit, "Save changes before quitting?", "Save and quit")]
    [InlineData(CloseTrigger.UpdateRestart, "Save changes before restarting?", "Save and restart")]
    public void Texts_per_trigger(CloseTrigger trigger, string title, string primary)
    {
        var prompt = SettingsClosePrompt.For(trigger);

        Assert.Equal(title, prompt.Title);
        Assert.Equal("Your changes haven't been saved. Things that already happened, such as deleting history, aren't undone.", prompt.Body);
        Assert.Equal(primary, prompt.PrimaryButton);
        Assert.Equal("Discard changes", prompt.DiscardButton);
        Assert.Equal("Keep editing", prompt.CancelButton);
    }

    [Fact]
    public void Keep_editing_is_always_the_default()
    {
        foreach (var trigger in Enum.GetValues<CloseTrigger>())
        {
            Assert.Equal("Keep editing", SettingsClosePrompt.For(trigger).DefaultButton);
        }
    }
}
