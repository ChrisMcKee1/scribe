using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;

namespace Scribe.Core.Settings;

/// <summary>
/// Which still-used library terms the dictionary cleanup copies into the user's dictionary before it switches their
/// libraries off. A library is switched off as a whole, so its working terms have to move into the dictionary first,
/// and each copy has to be the rule dictation applies today, or switching the library off changes what the next
/// dictation writes.
/// </summary>
/// <remarks>
/// <para>
/// Libraries are handled in precedence order (<see cref="LibraryPrecedence"/>), never in the order the review lists
/// them (most unused terms first), because the first copy of a spoken form wins: when two libraries switched off
/// together keep the same spoken form with different written forms, dictation applies the one earlier in precedence.
/// Until this was moved here the review's order decided, so the library with more unused terms won and dictation
/// changed after Save, a flaw present since 0.4.3.
/// </para>
/// <para>
/// For the same reason a kept term is not copied when a library that stays on and comes earlier supplies its spoken
/// form: that library's rule is the one dictation applies, and still will, while a copy in the dictionary would
/// override it. A library that stays on and comes later does not block the copy, since the switched-off library won
/// over it until now. An earlier switched-off library's unused row for the same spoken form does not block it either:
/// the scan found no trace of that row, which is why it is being dropped.
/// </para>
/// </remarks>
public static class LibrarySwitchOffCopy
{
    /// <summary>One row of the dictionary grid as the cleanup leaves it: its spoken form and whether it is on.</summary>
    public readonly record struct Row(string? Pattern, bool Enabled);

    /// <param name="Copies">Entries to add to the dictionary, switched on, in the order they were decided.</param>
    /// <param name="Collided">
    /// Still-used terms that could not be copied because a dictionary row that is switched off already has the spoken
    /// form (a duplicate would block Save), so the user has to be told they are losing a rule that works today.
    /// </param>
    public sealed record Result(IReadOnlyList<DictionaryEntry> Copies, int Collided);

    /// <param name="rows">The dictionary rows after the cleanup has deleted or switched off its own entries.</param>
    /// <param name="enabledLibraries">Every library that is on before the switch, in any order.</param>
    /// <param name="switchingOff">The libraries being switched off, with the terms to keep, in any order.</param>
    public static Result Plan(
        IEnumerable<Row> rows,
        IEnumerable<DictionaryLibrary> enabledLibraries,
        IEnumerable<LibraryUsage> switchingOff)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(enabledLibraries);
        ArgumentNullException.ThrowIfNull(switchingOff);

        // Case-insensitive and trimmed, as the post-processor and the duplicate check on Save compare spoken forms.
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var enabledRows = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var pattern = row.Pattern?.Trim();
            if (string.IsNullOrEmpty(pattern))
            {
                continue;
            }

            existing.Add(pattern);
            if (row.Enabled)
            {
                enabledRows.Add(pattern);
            }
        }

        var pending = switchingOff.Where(usage => usage is not null).ToList();
        var suppliedEarlier = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var copies = new List<DictionaryEntry>();
        var collided = 0;

        foreach (var library in LibraryPrecedence.Order(enabledLibraries))
        {
            var index = pending.FindIndex(usage => IsFor(usage, library));
            if (index < 0)
            {
                // A library that stays on: every spoken form it supplies from here on is its to keep.
                foreach (var entry in library.EnabledEntries)
                {
                    var pattern = entry.Pattern?.Trim();
                    if (!string.IsNullOrEmpty(pattern))
                    {
                        suppliedEarlier.Add(pattern);
                    }
                }

                continue;
            }

            Copy(pending[index]);
            pending.RemoveAt(index);
        }

        // A listed library that is not on (a caller's slip) is still handled, after every library that is.
        foreach (var usage in LibraryPrecedence.Order(pending, usage => usage.Id, usage => usage.BuiltIn))
        {
            Copy(usage);
        }

        return new Result(copies, collided);

        void Copy(LibraryUsage usage)
        {
            foreach (var term in usage.KeepTerms)
            {
                var pattern = term?.Pattern?.Trim();
                if (term is null || string.IsNullOrEmpty(pattern) || suppliedEarlier.Contains(pattern))
                {
                    continue;
                }

                if (!existing.Add(pattern))
                {
                    // Usually a dictionary row that is on already does the same job. A row that is off does not,
                    // and a duplicate spoken form would block Save, so that rule is lost and has to be reported.
                    if (!enabledRows.Contains(pattern))
                    {
                        collided++;
                    }

                    continue;
                }

                enabledRows.Add(pattern);
                copies.Add(new DictionaryEntry(0, pattern, term.Replacement, term.WholeWord, Enabled: true));
            }
        }
    }

    private static bool IsFor(LibraryUsage usage, DictionaryLibrary library) =>
        usage.BuiltIn == library.BuiltIn && string.Equals(usage.Id, library.Id, StringComparison.OrdinalIgnoreCase);
}
