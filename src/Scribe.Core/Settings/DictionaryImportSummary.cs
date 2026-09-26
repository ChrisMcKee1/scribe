namespace Scribe.Core.Settings;

public sealed record DictionaryImportSummary(string Title, string Body);

public static class DictionaryImportSummaryBuilder
{
    public static DictionaryImportSummary Build(int added, int updated, int unchanged, IReadOnlyList<string> skippedRows)
    {
        ArgumentNullException.ThrowIfNull(skippedRows);

        var changed = added + updated;
        if (skippedRows.Count > 0)
        {
            var body = $"{added:N0} added, {updated:N0} updated, {skippedRows.Count:N0} couldn't be read.";
            if (changed > 0)
            {
                body += " Nothing changes until you save.";
            }

            body += "\n\n" + string.Join('\n', skippedRows.Take(8));
            if (skippedRows.Count > 8)
            {
                body += $"\n...and {skippedRows.Count - 8:N0} more.";
            }

            return new DictionaryImportSummary("Some rows couldn't be imported", body);
        }

        var success = $"{added:N0} added, {updated:N0} updated.";
        if (unchanged > 0)
        {
            success += $" {unchanged:N0} already up to date.";
        }

        if (changed > 0)
        {
            success += " Nothing changes until you save.";
        }

        return new DictionaryImportSummary("Dictionary imported", success);
    }
}
