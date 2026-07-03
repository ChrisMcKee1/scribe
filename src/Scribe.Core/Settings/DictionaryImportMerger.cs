using Scribe.Core.Models;

namespace Scribe.Core.Settings;

/// <summary>
/// Pure merge of imported dictionary entries into an existing set, matched by spoken form
/// (case-insensitive) to mirror the duplicate rule the save validation enforces. The settings
/// window owns persistence (an import can still be cancelled), so this only decides an ordered plan
/// and the counts; the WPF grid mutation stays in the UI as a thin adapter applying the plan.
/// </summary>
public static class DictionaryImportMerger
{
    /// <summary>An existing row's identity: its position, current spoken form, and current fields.</summary>
    public readonly record struct ExistingRow(
        int Index, long Id, string? Pattern, string? Replacement, bool WholeWord, bool Enabled);

    /// <summary>What to do for one imported entry, in import order.</summary>
    public enum OperationKind
    {
        /// <summary>Replace the existing row at <see cref="Operation.Index"/> with <see cref="Operation.Entry"/>.</summary>
        Update,

        /// <summary>Append <see cref="Operation.Entry"/> as a new row.</summary>
        Add,
    }

    /// <summary>
    /// A single merge step. For <see cref="OperationKind.Update"/>, <see cref="Index"/> is the
    /// existing row position to replace; for <see cref="OperationKind.Add"/> it is -1.
    /// </summary>
    public readonly record struct Operation(OperationKind Kind, int Index, DictionaryEntry Entry);

    /// <summary>The ordered operations to apply plus the (added, updated, unchanged) counts.</summary>
    public readonly record struct Plan(
        IReadOnlyList<Operation> Operations, int Added, int Updated, int Unchanged);

    /// <summary>
    /// Merges <paramref name="imported"/> into <paramref name="existing"/> by spoken form: unchanged
    /// rows are counted only; differing rows produce an update that keeps the existing Id and spoken
    /// form; unmatched imports become additions. Additions register as match targets for later
    /// imports in the same batch, matching the grid's original single-pass behaviour.
    /// </summary>
    public static Plan Merge(IReadOnlyList<ExistingRow> existing, IReadOnlyList<DictionaryEntry> imported)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(imported);

        // First writer wins per spoken form, matching the grid's TryAdd de-dupe of existing rows.
        var indexByPattern = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var byIndex = new Dictionary<int, ExistingRow>();
        foreach (var row in existing)
        {
            byIndex[row.Index] = row;
            var pattern = row.Pattern?.Trim();
            if (!string.IsNullOrEmpty(pattern))
            {
                indexByPattern.TryAdd(pattern, row.Index);
            }
        }

        var operations = new List<Operation>();
        int added = 0, updated = 0, unchanged = 0;

        // The next synthetic index an addition occupies, so a later import can update it in-batch.
        var nextIndex = existing.Count == 0 ? 0 : existing.Max(r => r.Index) + 1;

        foreach (var entry in imported)
        {
            if (indexByPattern.TryGetValue(entry.Pattern, out var index) && byIndex.TryGetValue(index, out var row))
            {
                if (string.Equals(row.Replacement?.Trim(), entry.Replacement, StringComparison.Ordinal) &&
                    row.WholeWord == entry.WholeWord && row.Enabled == entry.Enabled)
                {
                    unchanged++;
                    continue;
                }

                // Keep the existing Id and original spoken form; only the other fields change.
                var replacement = new DictionaryEntry(
                    row.Id, row.Pattern ?? entry.Pattern, entry.Replacement, entry.WholeWord, entry.Enabled);
                operations.Add(new Operation(OperationKind.Update, index, replacement));
                byIndex[index] = row with
                {
                    Replacement = entry.Replacement,
                    WholeWord = entry.WholeWord,
                    Enabled = entry.Enabled,
                };
                updated++;
            }
            else
            {
                operations.Add(new Operation(OperationKind.Add, -1, entry));

                // Register the addition so a later duplicate import updates it rather than re-adding.
                var addedIndex = nextIndex++;
                indexByPattern[entry.Pattern] = addedIndex;
                byIndex[addedIndex] = new ExistingRow(
                    addedIndex, entry.Id, entry.Pattern, entry.Replacement, entry.WholeWord, entry.Enabled);
                added++;
            }
        }

        return new Plan(operations, added, updated, unchanged);
    }
}
