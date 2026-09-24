using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;

namespace Scribe.Core.Settings;

/// <summary>
/// Which still-used library terms the dictionary cleanup copies into the user's dictionary before it switches their
/// libraries off. A library is switched off as a whole, so a working rule of its has to move into the dictionary first,
/// and the copy must keep what dictation writes: the dictionary outranks every library, so a copy of a rule that is not
/// the one dictation applies today would override the rule that is.
/// </summary>
/// <remarks>
/// <para>
/// A kept term is copied only when all of these hold. Its library's row is the rule dictation applies today for that
/// spoken form: no enabled dictionary row has the spoken form, and no library earlier in precedence
/// (<see cref="LibraryPrecedence"/>) that is on, whether it stays on or is switched off too, supplies it. And the
/// libraries that stay on would not write the same thing without it. The winners come from
/// <see cref="DictionaryLibraryOverlapAnalyzer.Coverage"/>, the composition the badges show, never from the order the
/// review lists the libraries in (most unused terms first).
/// </para>
/// <para>
/// Until this was moved here the window copied first-wins in the review's order against the dictionary alone, a flaw
/// present since 0.4.3: switching off only the library that loses a spoken form copied its losing rule over the winner
/// that stays on, and of two libraries switched off together the one with more unused terms won. A winner whose row the
/// scan found no trace of is not kept, so nothing is copied for its spoken form; that is the cleanup dropping an unused
/// rule, as intended.
/// </para>
/// </remarks>
public static class LibrarySwitchOffCopy
{
    /// <summary>One row of the dictionary grid as the cleanup leaves it: its spoken form and whether it is on.</summary>
    public readonly record struct Row(string? Pattern, bool Enabled);

    /// <param name="Copies">Entries to add to the dictionary, switched on, in precedence order of their libraries.</param>
    /// <param name="Collided">
    /// Rules dictation applies today that could not be copied because a dictionary row that is switched off already has
    /// the spoken form (a duplicate would block Save), and that the libraries staying on would write differently, so
    /// the user has to be told the result changes.
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

        var usages = switchingOff.Where(usage => usage is not null).ToList();
        var enabled = LibraryPrecedence.Order(enabledLibraries);
        var staying = enabled.Where(library => !usages.Any(usage => IsFor(usage, library))).ToList();

        // What each spoken form's winning library row is now, and what it would be with only the libraries that stay on.
        var current = DictionaryLibraryOverlapAnalyzer.Coverage(enabled, enabled.Select(library => library.Id));
        var afterSwitch = DictionaryLibraryOverlapAnalyzer.Coverage(staying, staying.Select(library => library.Id));

        var copies = new List<DictionaryEntry>();
        var collided = 0;
        foreach (var usage in LibraryPrecedence.Order(usages, usage => usage.Id, usage => usage.BuiltIn))
        {
            foreach (var term in usage.KeepTerms)
            {
                var pattern = term?.Pattern?.Trim();
                if (term is null || string.IsNullOrEmpty(pattern) || enabledRows.Contains(pattern))
                {
                    // Nothing to keep, or the dictionary writes this spoken form today and still will.
                    continue;
                }

                if (!current.TryGetValue(pattern, out var winner) || !IsFor(usage, winner) || !winner.Entry.Equals(term))
                {
                    // Another library's rule (or another row of this one) is what dictation applies today.
                    continue;
                }

                if (afterSwitch.TryGetValue(pattern, out var next) && WritesTheSame(next.Entry, term))
                {
                    // A library that stays on writes the same thing, so switching this one off changes nothing.
                    continue;
                }

                if (!existing.Add(pattern))
                {
                    // A dictionary row that is off has the spoken form, and a duplicate would block Save.
                    collided++;
                    continue;
                }

                enabledRows.Add(pattern);
                copies.Add(new DictionaryEntry(0, pattern, term.Replacement, term.WholeWord, Enabled: true));
            }
        }

        return new Result(copies, collided);
    }

    private static bool IsFor(LibraryUsage usage, DictionaryLibrary library) =>
        usage.BuiltIn == library.BuiltIn && string.Equals(usage.Id, library.Id, StringComparison.OrdinalIgnoreCase);

    private static bool IsFor(LibraryUsage usage, LibraryCoverage coverage) =>
        usage.BuiltIn == coverage.BuiltIn && string.Equals(usage.Id, coverage.LibraryId, StringComparison.OrdinalIgnoreCase);

    // The same text for the same words: matching ignores the case of the spoken form but not its other characters, and
    // the written form and the word-boundary rule are applied as they are.
    private static bool WritesTheSame(DictionaryEntry a, DictionaryEntry b) =>
        string.Equals(a.Pattern, b.Pattern, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.Replacement, b.Replacement, StringComparison.Ordinal) &&
        a.WholeWord == b.WholeWord;
}
