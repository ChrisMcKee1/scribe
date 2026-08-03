using System.Text;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;

namespace Scribe.Core.Settings;

/// <summary>
/// Pure logic behind the tray's quick "Add to dictionary" popup: splitting a finished dictation
/// into selectable word chips, turning a chip range back into the exact text Scribe produced, and
/// deciding whether the typed rule creates a new entry, updates an existing one, or changes nothing.
///
/// This lives in Core rather than the WPF window because the interesting parts (where a word
/// starts and ends, what counts as trailing punctuation, whether a rule already exists) are exactly
/// the parts worth testing, and none of them need a UI.
/// </summary>
public static class QuickDictionaryAdd
{
    /// <summary>
    /// Characters trimmed from the ends of a chip selection. Deliberately only sentence
    /// punctuation: stripping every non-alphanumeric would turn "C++" into "C" and "#tag" into
    /// "tag", which are legitimate things to want a rule for. Trimming affects only the ends, so
    /// internal apostrophes in "don't" survive.
    /// </summary>
    private const string EdgePunctuation = ".,!?;:\"'`()[]{}<>…\u2014\u2013\u00ab\u00bb\u201c\u201d\u2018\u2019";

    /// <summary>One selectable chip: the word as it appears, plus its span in the source transcript.</summary>
    public readonly record struct Token(string Text, int Start, int Length);

    public enum PlanKind
    {
        /// <summary>Nothing worth saving; <see cref="Plan.Entry"/> is null.</summary>
        Invalid,

        /// <summary>No rule for this spoken form yet.</summary>
        Create,

        /// <summary>A rule for this spoken form exists and would be rewritten.</summary>
        Update,

        /// <summary>The rule already exists exactly as typed; saving would be a no-op.</summary>
        NoChange,
    }

    /// <summary>
    /// What saving would do, plus the entry to persist and a sentence to show the user.
    /// <see cref="Entry"/> is null only for <see cref="PlanKind.Invalid"/>.
    /// </summary>
    public readonly record struct Plan(PlanKind Kind, DictionaryEntry? Entry, string Message)
    {
        /// <summary>True when there is something to write to the repository.</summary>
        public bool CanSave => Kind is PlanKind.Create or PlanKind.Update;
    }

    /// <summary>
    /// Splits a transcript into whitespace-delimited chips. Punctuation stays attached to its word
    /// so the chips read exactly like the dictation the user is looking at; it is stripped later,
    /// when a selection is turned into a pattern.
    /// </summary>
    public static IReadOnlyList<Token> Tokenize(string? transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return [];
        }

        var tokens = new List<Token>();
        var i = 0;
        while (i < transcript.Length)
        {
            if (char.IsWhiteSpace(transcript[i]))
            {
                i++;
                continue;
            }

            var start = i;
            while (i < transcript.Length && !char.IsWhiteSpace(transcript[i]))
            {
                i++;
            }

            tokens.Add(new Token(transcript[start..i], start, i - start));
        }

        return tokens;
    }

    /// <summary>
    /// Rebuilds the text covered by chips <paramref name="first"/> through <paramref name="last"/>
    /// inclusive. The indices may arrive in either order (dragging right-to-left) and are clamped,
    /// so a stale selection from a previous transcript can never throw.
    ///
    /// The span is taken from the original transcript rather than by joining chip text, which keeps
    /// the punctuation *between* selected words intact. Whitespace runs collapse to single spaces so
    /// a selection spanning a line break still produces a pattern the post-processor can match.
    /// </summary>
    public static string Select(string? transcript, IReadOnlyList<Token> tokens, int first, int last)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        if (string.IsNullOrEmpty(transcript) || tokens.Count == 0)
        {
            return string.Empty;
        }

        if (first > last)
        {
            (first, last) = (last, first);
        }

        first = Math.Clamp(first, 0, tokens.Count - 1);
        last = Math.Clamp(last, 0, tokens.Count - 1);

        var start = tokens[first].Start;
        var end = tokens[last].Start + tokens[last].Length;
        if (start < 0 || end > transcript.Length || end <= start)
        {
            return string.Empty;
        }

        var raw = Collapse(transcript[start..end]);
        var trimmed = raw.Trim(EdgePunctuation.ToCharArray()).Trim();

        // Selecting only punctuation would otherwise clear the box and look broken. Hand back what
        // was actually selected and let the user decide.
        return trimmed.Length == 0 ? raw : trimmed;
    }

    /// <summary>
    /// Works out what saving the typed rule would do against the current dictionary.
    /// Matching is by spoken form, case-insensitive, mirroring the duplicate rule the settings grid
    /// enforces so a quick add can never create a row the grid would immediately flag.
    /// </summary>
    public static Plan Build(
        string? pattern,
        string? replacement,
        bool wholeWord,
        IReadOnlyList<DictionaryEntry> existing)
    {
        ArgumentNullException.ThrowIfNull(existing);

        var spoken = (pattern ?? string.Empty).Trim();
        if (spoken.Length == 0)
        {
            return new Plan(PlanKind.Invalid, null, "Pick a word above, or type what Scribe wrote.");
        }

        // A pattern spanning a line break can never match. The matcher preserves CR/LF in its input
        // and compiles patterns literally, so the break would have to reappear in exactly the same
        // place in a future dictation. Rejecting it beats saving a rule that silently never fires.
        if (spoken.Contains('\n') || spoken.Contains('\r'))
        {
            return new Plan(
                PlanKind.Invalid,
                null,
                "Pick words from a single line. A rule can't stretch across a line break.");
        }

        var written = (replacement ?? string.Empty).Trim();

        // A rule that rewrites text to itself never fires, and is almost always a half-finished
        // edit rather than an intent. Case-only differences ("copilot" to "Copilot") are the single
        // most common real rule, so the comparison is ordinal.
        if (string.Equals(spoken, written, StringComparison.Ordinal))
        {
            return new Plan(PlanKind.Invalid, null, "That is already what Scribe writes, so nothing would change.");
        }

        // The dictionary runs in a single pass: every rule matches the original transcript, and no
        // rule ever sees another rule's output. So a pattern that is some other rule's replacement
        // is unreachable by construction. This is easy to walk into, because the transcript shown in
        // the popup is the finished text, which is exactly where those replacements appear.
        var producer = existing.FirstOrDefault(e =>
            e.Enabled
            && string.Equals(e.Replacement.Trim(), spoken, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(e.Pattern.Trim(), spoken, StringComparison.OrdinalIgnoreCase));

        if (producer is not null)
        {
            return new Plan(
                PlanKind.Invalid,
                null,
                $"\"{producer.Pattern.Trim()}\" is already turned into \"{spoken}\" by another rule, and rules "
                    + $"only run once, so this would never apply. Change that rule's replacement instead.");
        }

        var match = existing.FirstOrDefault(
            e => string.Equals(e.Pattern.Trim(), spoken, StringComparison.OrdinalIgnoreCase));

        if (match is not null)
        {
            if (string.Equals(match.Replacement.Trim(), written, StringComparison.Ordinal)
                && match.WholeWord == wholeWord
                && match.Enabled)
            {
                return new Plan(PlanKind.NoChange, match, $"\"{spoken}\" already becomes \"{Describe(written)}\".");
            }

            // Re-enable on update: the user is explicitly asking for this rule right now, so a
            // previously disabled row should start working rather than silently stay off.
            var updated = new DictionaryEntry(match.Id, spoken, written, wholeWord, Enabled: true);
            return new Plan(
                PlanKind.Update,
                updated,
                $"Replaces the existing rule: \"{spoken}\" becomes \"{Describe(written)}\" "
                    + $"instead of \"{Describe(match.Replacement.Trim())}\".");
        }

        var created = new DictionaryEntry(0, spoken, written, wholeWord, Enabled: true);
        return new Plan(
            PlanKind.Create,
            created,
            // Future tense, deliberately. This runs on every keystroke while the user is still
            // typing, so a past-tense message would announce a save that has not happened and let
            // them close the window believing the rule was stored.
            written.Length == 0
                ? $"Scribe will leave \"{spoken}\" out of what you dictate."
                : $"Scribe will write \"{spoken}\" as \"{written}\".");
    }

    private static string Describe(string written) => written.Length == 0 ? "nothing" : written;

    /// <summary>
    /// Applies one just-saved rule to a transcript the user has already seen, so the copy kept for
    /// clipboard recovery reads the way they just told Scribe it should read.
    ///
    /// Only this rule is applied, never the whole dictionary. The transcript is already
    /// post-dictionary text, so re-running every rule could rewrite words the user never touched.
    ///
    /// The work is delegated to <see cref="TextPostProcessor.ApplyRule"/> on purpose. An earlier
    /// version reimplemented the matcher here and diverged from the live pipeline in two ways that
    /// both produced visibly wrong text: it normalized whitespace after replacing instead of before,
    /// and it lacked the guard that stops "york" to "New York" firing again on text that already
    /// reads "New York".
    /// </summary>
    /// <returns>The rewritten transcript, or the original unchanged when the rule cannot apply.</returns>
    public static string Apply(string? transcript, DictionaryEntry? entry)
        => TextPostProcessor.ApplyRule(transcript, entry);

    /// <summary>
    /// Collapses horizontal whitespace runs to a single space, deliberately preserving line breaks.
    ///
    /// This mirrors <c>TextPostProcessor.NormalizeWhitespace</c>, which collapses only
    /// <c>[ \t\f\v]+</c> and leaves CR/LF intact. Flattening line breaks here instead would build a
    /// space-separated pattern that can never match the text the matcher actually sees, producing a
    /// rule that silently never fires. Keeping them lets <see cref="Build"/> detect and reject the
    /// selection instead.
    /// </summary>
    private static string Collapse(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;

        foreach (var ch in value)
        {
            if (ch is '\r' or '\n')
            {
                pendingSpace = false;
                builder.Append(ch);
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }
}
