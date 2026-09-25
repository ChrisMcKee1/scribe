namespace Scribe.Core.Settings;

public enum CloseTrigger
{
    WindowClose,
    TrayQuit,
    RestartToUpdate,
}

public sealed record SettingsClosePrompt(string Title, string Body, string PrimaryButton, string DiscardButton, string CancelButton, string DefaultButton)
{
    public static SettingsClosePrompt For(CloseTrigger trigger)
    {
        var title = trigger switch
        {
            CloseTrigger.TrayQuit => "Save changes before quitting?",
            CloseTrigger.RestartToUpdate => "Save changes before restarting?",
            _ => "Save changes before closing?",
        };
        var primary = trigger switch
        {
            CloseTrigger.TrayQuit => "Save and quit",
            CloseTrigger.RestartToUpdate => "Save and restart",
            _ => "Save and close",
        };
        return new SettingsClosePrompt(title, "Your changes haven't been saved. Things that already happened, such as deleting history, aren't undone.", primary, "Discard changes", "Keep editing", "Keep editing");
    }
}
