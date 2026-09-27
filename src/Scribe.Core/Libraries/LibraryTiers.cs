using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Scribe.Core.Cleanup;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;

namespace Scribe.Core.Libraries;

/// <summary>
/// The glossary's term budget, as dictation uses it: <see cref="CleanupPrompt.GlossaryTermBudget"/> for the prompt style
/// and provider in use. The character budget (<see cref="CleanupPrompt.MaxGlossaryChars"/>) applies whatever the term
/// budget is, exactly as it does when the glossary is built.
/// </summary>
/// <param name="MaxTerms">The most terms the glossary may carry.</param>
public readonly record struct GlossaryBudget(int MaxTerms)
{
    /// <summary>The budget dictation uses for this prompt style and provider.</summary>
    public static GlossaryBudget For(CleanupPromptStyle style, CleanupProvider provider) =>
        new(CleanupPrompt.GlossaryTermBudget(style, provider));
}

/// <summary>Decision 1's tiers: which kind of library term a rule is.</summary>
public enum RuleTier
{
    /// <summary>A custom library row, or an edited, added, pinned or no-longer-shipped built-in row.</summary>
    Authored,

    /// <summary>A built-in row exactly as the running version ships it.</summary>
    Shipped,
}

/// <summary>One rule of a composition: the row that supplies a spoken form, and why it won.</summary>
/// <param name="Entry">The rule as dictation applies it (the row's values; id 0).</param>
/// <param name="LibraryId">The library that supplies it.</param>
/// <param name="Key">The spoken form it competes for (<see cref="LibraryTermKey"/> of its spoken value).</param>
/// <param name="Tier">Its tier.</param>
/// <param name="LegacyMarkerActive">It is a legacy-marked row whose marker is active (it competed after the shipped rows).</param>
public sealed record ComposedRule(DictionaryEntry Entry, string LibraryId, LibraryTermKey Key, RuleTier Tier, bool LegacyMarkerActive);

/// <summary>Who writes a row's spoken form, from the row's point of view.</summary>
public enum TermWinner
{
    /// <summary>This row supplies the rule.</summary>
    ThisRow,

    /// <summary>An enabled entry of the personal dictionary has the spoken form, and the dictionary always wins.</summary>
    Dictionary,

    /// <summary>Another row supplies the rule: another library's, or an earlier row of this library with the same spoken form.</summary>
    OtherLibrary,

    /// <summary>Nothing applies the spoken form: this row is not in effect and no other source has it.</summary>
    None,
}

/// <summary>The most urgent fact about a row, shown beside its written value (UX-07). Declared in order of urgency.</summary>
public enum TermMarker
{
    /// <summary>Nothing worth saying; an identical result elsewhere stays silent.</summary>
    None,

    /// <summary>An upgrade changed a field the user also changed: a review is owed.</summary>
    Update,

    /// <summary>A legacy marker is active: the built-in's spelling is in use, as before the update.</summary>
    Check,

    /// <summary>Another source writes the spoken form, differently.</summary>
    NotUsed,

    /// <summary>The row is turned off here, and another source still applies the spoken form.</summary>
    OffHere,

    /// <summary>The row is a removal rule: it deletes the words it matches.</summary>
    Removes,

    /// <summary>An authored row of a built-in library.</summary>
    Changed,
}

/// <summary>Whether a row's term is in the AI cleanup glossary this composition's budget renders.</summary>
public enum GlossaryInclusion
{
    /// <summary>Its line is in the glossary.</summary>
    Included,

    /// <summary>Eligible, but the term or character budget cut the glossary before its line.</summary>
    OverBudget,

    /// <summary>It supplies the rule, but its library may not be sent to AI cleanup.</summary>
    NotPermitted,

    /// <summary>
    /// It supplies the rule and is permitted, but the glossary leaves it out: no written form, a written form that is not
    /// vocabulary (<see cref="CleanupPrompt.IsVocabularyReplacement"/>: one spanning lines or running past 100 characters,
    /// a template dictation still applies), or a line another term already put in the glossary.
    /// </summary>
    NotEligible,

    /// <summary>It does not supply the rule: another source wins its spoken form, or the row or its library is off.</summary>
    NotApplied,
}

/// <summary>The Libraries page's Show filter.</summary>
public enum TermFilter
{
    /// <summary>Every row.</summary>
    All,

    /// <summary>Rows the user authored: in a built-in, its edited, added, pinned, turned-off and no-longer-shipped rows.</summary>
    ChangedByYou,

    /// <summary>Rows turned off.</summary>
    TurnedOff,

    /// <summary>Rows that owe the user a decision: a review, or an active legacy marker.</summary>
    NeedsAttention,
}

/// <summary>Everything the page says about one row, for the saved result or the result after Save (<see cref="LibraryComposition.IsPreview"/>).</summary>
/// <param name="Marker">The most urgent fact.</param>
/// <param name="Winner">Who writes the row's spoken form.</param>
/// <param name="WinningLibraryId">The library that writes it, when a library does.</param>
/// <param name="WinningEntry">What writes it: the dictionary entry or the winning rule.</param>
/// <param name="SameResultIn">Other libraries in use that have the spoken form with the same result, in precedence order.</param>
/// <param name="DifferentResultIn">Other libraries in use that have the spoken form with a different result, in precedence order.</param>
/// <param name="Review">The review the row owes after an upgrade, if any.</param>
/// <param name="LegacyMarkerActive">The row keeps the built-in's pre-upgrade result until the user chooses Use my spelling.</param>
/// <param name="Glossary">Whether the row's term is in the AI cleanup glossary.</param>
public sealed record TermStatus(
    TermMarker Marker,
    TermWinner Winner,
    string? WinningLibraryId,
    DictionaryEntry? WinningEntry,
    IReadOnlyList<string> SameResultIn,
    IReadOnlyList<string> DifferentResultIn,
    TermReview? Review,
    bool LegacyMarkerActive,
    GlossaryInclusion Glossary);

/// <summary>The small rules composition, adoption and the switch-off copy share about rows, tiers and legacy markers.</summary>
internal static class LibraryTiers
{
    /// <summary>
    /// The id prefix every built-in beyond the eleven that 0.4.2 and 0.4.3 ship must carry (C-15), so an older build,
    /// which cannot list such a library, is never read as having turned it off.
    /// </summary>
    public const string NewBuiltInPrefix = "scribe.";

    /// <summary>A library whose file could be used: a paused one (unreadable, or a newer edits document) supplies nothing.</summary>
    public static bool IsUsable(LibraryFileState state) =>
        state is not (LibraryFileState.Unreadable or LibraryFileState.Newer);

    /// <summary>Every origin but <see cref="TermOrigin.Shipped"/> is authored (the middle tier of Decision 1).</summary>
    public static bool IsAuthored(LibraryRow row) => row.Origin != TermOrigin.Shipped;

    /// <summary>The spoken form a row competes for: its values' spoken form, not its identity (review question 2).</summary>
    public static LibraryTermKey CompetingKey(LibraryRow row) => LibraryTermKey.From(row.Values.Spoken);

    /// <summary>Two rows give the same result when they write the same text (ordinal) with the same word boundaries (decision 3).</summary>
    public static bool SameResult(TermValues a, TermValues b) =>
        string.Equals(a.Written, b.Written, StringComparison.Ordinal) && a.WholeWord == b.WholeWord;

    /// <summary>Whether a dictionary entry writes what a row writes.</summary>
    public static bool SameResult(DictionaryEntry entry, TermValues values) =>
        string.Equals(entry.Replacement, values.Written, StringComparison.Ordinal) && entry.WholeWord == values.WholeWord;

    /// <summary>The name a library ranks by among custom libraries: its physical file name; null for a built-in.</summary>
    public static string? PrecedenceFileName(string id, bool builtIn, string? fileName) =>
        builtIn ? null : fileName ?? id + ".csv";

    /// <summary>
    /// What legacy markers compare spoken forms by: <see cref="SpokenFormFold"/> of the trimmed form, which is broader
    /// than every comparison dictation makes. Two spoken forms the matcher can take for one text (the Kelvin sign and a k,
    /// under different keys), or the composer for one key (a final sigma and a sigma), fold alike (round 2, review
    /// finding A3); so do forms only the fold links (the dotted and dotless i with i).
    /// </summary>
    public static string MarkerFold(string? spoken) => SpokenFormFold.Fold((spoken ?? string.Empty).Trim());

    /// <summary>
    /// Whether <paramref name="custom"/>, applied where <paramref name="builtIn"/> applies, writes exactly what it writes in
    /// any text, so a legacy row needs no marker to keep 0.4.3's result: the same written form and word boundaries, and a
    /// spoken form the matcher reads as the same text that is also the same ignoring case, which is what the
    /// post-processor's guard against expanding a written form that already holds its spoken form compares with. A final
    /// sigma or a dotless i is read differently by the matcher, and a Kelvin sign by the guard, so each is marked even with
    /// the same written form (round 2, A3).
    /// </summary>
    public static bool AppliesAlike(TermValues builtIn, TermValues custom) =>
        SameResult(builtIn, custom) &&
        string.Equals(builtIn.Spoken, custom.Spoken, StringComparison.OrdinalIgnoreCase) &&
        MatcherReadsAlike(builtIn.Spoken, custom.Spoken);

    /// <summary>
    /// Whether the matcher, which compiles every spoken form as an escaped literal with
    /// <see cref="TextPostProcessor.DictionaryMatchOptions"/>, matches exactly the same text for both: one character against
    /// one, each pair the same or equivalent to the regex engine itself.
    /// </summary>
    public static bool MatcherReadsAlike(string a, string b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i] && !CharactersMatchAlike(a[i], b[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static readonly ConcurrentDictionary<int, bool> s_charactersMatchAlike = new();

    // Two different characters the matcher treats as one, asked of the regex engine both ways and remembered.
    private static bool CharactersMatchAlike(char a, char b) =>
        s_charactersMatchAlike.GetOrAdd(Math.Min(a, b) << 16 | Math.Max(a, b), static pair =>
        {
            var low = ((char)(pair >> 16)).ToString();
            var high = ((char)(pair & 0xFFFF)).ToString();
            return MatchesWhole(low, high) && MatchesWhole(high, low);
        });

    private static bool MatchesWhole(string pattern, string text) =>
        Regex.IsMatch(text, $@"\A{Regex.Escape(pattern)}\z", TextPostProcessor.DictionaryMatchOptions);

    /// <summary>
    /// What the running version ships for every built-in spoken form, from the built-ins' shipped values (an edited row
    /// still carries them; an added row has none), by <see cref="MarkerFold"/>: the base legacy markers are computed
    /// against, as at the upgrade, when no built-in had edits.
    /// </summary>
    public static Dictionary<string, List<TermValues>> ShippedValues(IEnumerable<LibraryContent> builtIns)
    {
        var shipped = new Dictionary<string, List<TermValues>>(StringComparer.Ordinal);
        foreach (var library in builtIns)
        {
            foreach (var row in library.Rows)
            {
                var values = row.Shipped ?? (row.Origin == TermOrigin.Shipped ? row.Values : null);
                if (values is not { Enabled: true })
                {
                    continue;
                }

                var fold = MarkerFold(values.Spoken);
                if (fold.Length == 0)
                {
                    continue;
                }

                if (!shipped.TryGetValue(fold, out var list))
                {
                    shipped[fold] = list = [];
                }

                list.Add(values);
            }
        }

        return shipped;
    }

    /// <summary>
    /// The legacy markers of a custom library as at the upgrade (Decision 1, plan 3.2): one for each spoken form of an
    /// enabled row for which some built-in ships an enabled row whose spoken form folds alike (<see cref="MarkerFold"/>)
    /// and which the row would not apply exactly alike (<see cref="AppliesAlike"/>): a different written form or word
    /// boundaries, or a spoken form the matcher or the expansion guard reads differently. Built-ins that are off count
    /// too, since turning one on later must still give its result; an inactive marker changes nothing, and neither does a
    /// marker on a row the matcher never takes for the built-in's text. In row order, each spoken form once.
    /// </summary>
    public static IEnumerable<LegacyMarker> UpgradeMarkers(
        LibraryContent custom, IReadOnlyDictionary<string, List<TermValues>> shipped)
    {
        var seen = new HashSet<LibraryTermKey>();
        foreach (var row in custom.Rows)
        {
            if (!row.Values.Enabled)
            {
                continue;
            }

            var key = CompetingKey(row);
            if (key.IsEmpty ||
                !shipped.TryGetValue(MarkerFold(row.Values.Spoken), out var builtIn) ||
                builtIn.All(values => AppliesAlike(values, row.Values)))
            {
                continue;
            }

            if (seen.Add(key))
            {
                yield return new LegacyMarker(custom.Id, key);
            }
        }
    }
}
