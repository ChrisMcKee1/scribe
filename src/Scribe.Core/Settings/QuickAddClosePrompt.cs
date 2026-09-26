namespace Scribe.Core.Settings;

public sealed record QuickAddClosePrompt(string Title, string Body, string PrimaryButton, string DiscardButton, string CancelButton, string DefaultButton)
{
    public static QuickAddClosePrompt ForUnsavedWord() => new("Save this word before closing?", "Your correction hasn't been saved.", "Save and close", "Discard", "Keep editing", "Keep editing");
}
