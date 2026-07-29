using Scribe.Core.Models;

namespace Scribe.Core.Settings;

/// <summary>
/// How a personal dictionary entry relates to an enabled library that covers the same spoken form.
/// </summary>
public enum DictionaryOverlapKind
{
    /// <summary>
    /// The library already produces exactly this replacement, with the same word-boundary behavior.
    /// The personal entry changes nothing, so it is pure clutter.
    /// </summary>
    Redundant,

    /// <summary>
    /// A library covers the same spoken form but writes it differently. The personal entry wins, so
    /// this is a deliberate override worth confirming rather than a mistake worth removing. "v s"
    /// meaning "versus" rather than the library's "Visual Studio" is the motivating case.
    /// </summary>
    Override,
}

/// <summary>One personal entry that collides with an enabled library.</summary>
public readonly record struct DictionaryOverlap(
    DictionaryOverlapKind Kind,
    string Pattern,
    string Replacement,
    string LibraryReplacement,
    string LibraryId)
{
    public bool IsRedundant => Kind == DictionaryOverlapKind.Redundant;
}

/// <summary>The overlaps found, split by what the user should be asked about.</summary>
public readonly record struct DictionaryOverlapReport(IReadOnlyList<DictionaryOverlap> Overlaps)
{
    public IEnumerable<DictionaryOverlap> Redundant =>
        Overlaps.Where(o => o.Kind == DictionaryOverlapKind.Redundant);

    public IEnumerable<DictionaryOverlap> Overrides =>
        Overlaps.Where(o => o.Kind == DictionaryOverlapKind.Override);

    public int RedundantCount => Redundant.Count();
    public int OverrideCount => Overrides.Count();
    public bool HasAny => Overlaps.Count > 0;
}

/// <summary>
/// Compares the dictionary a user is about to save against the libraries they have switched on, so
/// the settings window can tell them when an entry is already covered.
/// </summary>
/// <remarks>
/// This exists because a personal dictionary silently accumulates entries that a library later
/// started covering, and there is no way to notice: both layers produce the same output, so nothing
/// looks wrong. The cost is real though. Personal entries are merged ahead of library entries and
/// consume the glossary budget first, so redundant ones displace the terms a model genuinely cannot
/// guess. Pure and UI-free so the classification is testable on its own.
/// </remarks>
public static class DictionaryLibraryOverlapAnalyzer
{
    /// <summary>
    /// Classifies each personal entry that shares a spoken form with an enabled library entry.
    /// Disabled entries on either side are ignored: a disabled entry produces no output, so it can
    /// neither be redundant with nor override anything.
    /// </summary>
    /// <param name="personal">The dictionary the user is saving.</param>
    /// <param name="libraryEntries">Composed entries from the libraries currently switched on.</param>
    /// <param name="libraryIdsByPattern">
    /// Optional map from spoken form to the library that supplied it, used only to name the source
    /// in the message. Missing entries degrade to an empty label rather than failing.
    /// </param>
    public static DictionaryOverlapReport Analyze(
        IEnumerable<DictionaryEntry>? personal,
        IEnumerable<DictionaryEntry>? libraryEntries,
        IReadOnlyDictionary<string, string>? libraryIdsByPattern = null)
    {
        if (personal is null || libraryEntries is null)
        {
            return new DictionaryOverlapReport([]);
        }

        // Case-insensitive to match how the post-processor and the glossary treat patterns.
        var library = new Dictionary<string, DictionaryEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in libraryEntries)
        {
            if (entry is null || !entry.Enabled || string.IsNullOrWhiteSpace(entry.Pattern))
            {
                continue;
            }

            // First wins, matching DictionaryLibraryComposer's precedence.
            library.TryAdd(entry.Pattern.Trim(), entry);
        }

        if (library.Count == 0)
        {
            return new DictionaryOverlapReport([]);
        }

        var overlaps = new List<DictionaryOverlap>();
        foreach (var entry in personal)
        {
            if (entry is null || !entry.Enabled || string.IsNullOrWhiteSpace(entry.Pattern))
            {
                continue;
            }

            var pattern = entry.Pattern.Trim();
            if (!library.TryGetValue(pattern, out var covering))
            {
                continue;
            }

            var replacement = (entry.Replacement ?? string.Empty).Trim();
            var libraryReplacement = (covering.Replacement ?? string.Empty).Trim();

            // Ordinal, not OrdinalIgnoreCase: the whole point of most entries is casing, so
            // "gpt" -> "GPT" and "gpt" -> "gpt" are genuinely different outcomes. Word-boundary
            // behavior counts too, because the same replacement applied differently is not the
            // same replacement.
            var kind = string.Equals(replacement, libraryReplacement, StringComparison.Ordinal)
                       && entry.WholeWord == covering.WholeWord
                ? DictionaryOverlapKind.Redundant
                : DictionaryOverlapKind.Override;

            var libraryId = string.Empty;
            libraryIdsByPattern?.TryGetValue(pattern, out libraryId);

            overlaps.Add(new DictionaryOverlap(
                kind, pattern, replacement, libraryReplacement, libraryId ?? string.Empty));
        }

        return new DictionaryOverlapReport(overlaps);
    }

    /// <summary>
    /// Returns <paramref name="personal"/> with the redundant entries removed, preserving order.
    /// Used when the user chooses to let the libraries cover those terms.
    /// </summary>
    /// <remarks>
    /// Matches on pattern <i>and</i> replacement rather than pattern alone. The save path rejects
    /// duplicate spoken forms before this runs, so today the two are equivalent, but the difference
    /// decides what happens if that ever stops being true: keying on the pattern would delete an
    /// entry classified as an Override, which is precisely the entry this feature promises never to
    /// touch. Removing strictly less is the only safe direction for an automated deletion.
    /// </remarks>
    public static IReadOnlyList<DictionaryEntry> RemoveRedundant(
        IReadOnlyList<DictionaryEntry> personal, DictionaryOverlapReport report)
    {
        ArgumentNullException.ThrowIfNull(personal);

        if (report.RedundantCount == 0)
        {
            return personal;
        }

        var drop = new HashSet<(string Pattern, string Replacement)>(
            report.Redundant.Select(o => (o.Pattern, o.Replacement)),
            RedundantKeyComparer.Instance);

        return [.. personal.Where(e =>
            e is null || string.IsNullOrWhiteSpace(e.Pattern) ||
            !drop.Contains((e.Pattern.Trim(), (e.Replacement ?? string.Empty).Trim())))];
    }

    // Spoken forms are matched case-insensitively (as the post-processor does), but replacements are
    // compared ordinally: casing is the entire point of most entries, so "gpt" -> "GPT" and
    // "gpt" -> "gpt" are different outcomes and must not collapse into one key here either.
    private sealed class RedundantKeyComparer : IEqualityComparer<(string Pattern, string Replacement)>
    {
        public static readonly RedundantKeyComparer Instance = new();

        public bool Equals((string Pattern, string Replacement) x, (string Pattern, string Replacement) y) =>
            string.Equals(x.Pattern, y.Pattern, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Replacement, y.Replacement, StringComparison.Ordinal);

        public int GetHashCode((string Pattern, string Replacement) obj) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Pattern),
            StringComparer.Ordinal.GetHashCode(obj.Replacement));
    }
}
