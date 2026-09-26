namespace Scribe.Core.Settings;

// The texts of the redesigned close prompt, for the triggers of D's SettingsCloseGuard. Whether to ask at all is the
// guard's decision; sign-out and shutdown never ask there, so they only need a safe text here.
public sealed record SettingsClosePrompt(string Title, string Body, string PrimaryButton, string DiscardButton, string CancelButton, string DefaultButton)
{
    public static SettingsClosePrompt For(CloseTrigger trigger)
    {
        var title = trigger switch
        {
            CloseTrigger.TrayQuit => "Save changes before quitting?",
            CloseTrigger.UpdateRestart => "Save changes before restarting?",
            _ => "Save changes before closing?",
        };
        var primary = trigger switch
        {
            CloseTrigger.TrayQuit => "Save and quit",
            CloseTrigger.UpdateRestart => "Save and restart",
            _ => "Save and close",
        };
        return new SettingsClosePrompt(title, "Your changes haven't been saved. Things that already happened, such as deleting history, aren't undone.", primary, "Discard changes", "Keep editing", "Keep editing");
    }
}
