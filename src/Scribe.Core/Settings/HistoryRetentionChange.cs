namespace Scribe.Core.Settings;

public sealed record HistoryRetentionConfirmation(
    string Title,
    string Body,
    string KeepEditingText,
    string DeleteAndSaveText);

public static class HistoryRetentionChange
{
    public const string KeepEditingText = "Keep editing";
    public const string DeleteAndSaveText = "Delete and save";

    public static bool RequiresConfirmation(int loadedDays, int draftDays) =>
        draftDays > 0 && (loadedDays == 0 || draftDays < loadedDays);

    public static HistoryRetentionConfirmation? Describe(int loadedDays, int draftDays)
    {
        if (!RequiresConfirmation(loadedDays, draftDays))
        {
            return null;
        }

        var period = FormatPeriod(draftDays);
        return new HistoryRetentionConfirmation(
            $"Delete dictations older than {period}?",
            $"Scribe will delete dictations older than {period}, and their recordings, soon after you save. This can't be undone, and Usage totals will change.",
            KeepEditingText,
            DeleteAndSaveText);
    }

    public static string FormatPeriod(int days) => days switch
    {
        0 => "until I delete them",
        1 => "1 day",
        365 => "1 year",
        _ => $"{days} days",
    };
}
