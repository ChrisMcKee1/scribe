using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

public sealed class QuickAddClosePromptTests
{
    [Fact]
    public void Unsaved_savable_correction_prompts_with_exact_texts()
    {
        var prompt = QuickAddClosePrompt.ForUnsavedWord();

        Assert.Equal("Save this word before closing?", prompt.Title);
        Assert.Equal("Your correction hasn't been saved.", prompt.Body);
        Assert.Equal("Save and close", prompt.PrimaryButton);
        Assert.Equal("Discard", prompt.DiscardButton);
        Assert.Equal("Keep editing", prompt.CancelButton);
        Assert.Equal("Keep editing", prompt.DefaultButton);
    }
}
