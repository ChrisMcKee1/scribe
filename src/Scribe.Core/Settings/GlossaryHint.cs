using System.Globalization;
using System.Text;
using Scribe.Core.Cleanup;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;

namespace Scribe.Core.Settings;

/// <summary>
/// The status line under the dictionary grid: how many entries are on, whether they are applied on this
/// PC and, while AI cleanup is on, how many terms every cleanup request carries as vocabulary and who
/// receives them.
/// <para>
/// The number is the one dictation sends. The page's rows are built the way Save builds them
/// (<see cref="DictionaryEntryBuilder.Build"/>) and put in the order dictation reads the saved dictionary
/// (<see cref="DictionaryRepository.GetEnabled"/>, by pattern under SQLite's BINARY collation). The enabled
/// libraries arrive already composed, the way the library service hands them to dictation's glossary
/// (<see cref="DictionaryLibraryComposer.ComposeLibraries"/>, in precedence order), and are never reordered here.
/// Then the pipeline's own composition, budget and selection count them (<see cref="CleanupPrompt.ComposeVocabulary"/>,
/// <see cref="CleanupPrompt.GlossaryTermBudget"/>, <see cref="CleanupPrompt.CountGlossary"/>). Counting in the
/// grid's own order said "the first 312 of 3,100" after an out-of-order import when 3,000 were sent, because
/// the size budget stops at a different line in a different order.
/// </para>
/// </summary>
public static class GlossaryHint
{
    /// <summary>What the page shows: its rows as typed, its word pack entries, and the settings on screen.</summary>
    /// <param name="LibraryEntries">
    /// The enabled word pack entries that local dictation applies, as the draft composition returns them: one row per
    /// spoken form, in precedence order. They are counted as given: a flattened list no longer says which word pack a row
    /// came from, so any reordering here could only lose which word pack's row wins.
    /// </param>
    /// <param name="AiLibraryEntries">
    /// The subset of <paramref name="LibraryEntries"/> that AI cleanup may receive, in the same composition order. Null
    /// keeps the old contract for callers that have no separate AI permission.
    /// </param>
    public sealed record Input(
        IReadOnlyList<DictionaryEntryBuilder.Row> Rows,
        IReadOnlyList<DictionaryEntry> LibraryEntries,
        bool AiCleanupOn,
        bool PostProcessingOn,
        CleanupProvider Provider,
        CleanupPromptStyle PromptStyle,
        IReadOnlyList<DictionaryEntry>? AiLibraryEntries = null);

    public static string Describe(Input input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Entries are the rows with a spoken form, the rows Save keeps; a rule that deletes its phrase is as
        // enabled as one that replaces it.
        var entries = DictionaryEntryBuilder.Build(input.Rows).Entries;
        var enabled = entries.Where(e => e.Enabled).ToList();
        var text = new StringBuilder($"{Count(enabled.Count)} of {Count(entries.Count)} words are on");

        // Dictation reads only enabled entries, so a disabled row never keeps a library term with the same
        // spoken form out of the vocabulary. The page's own rows are put in the order the repository reads the
        // saved dictionary back; that is the only reordering here, and it touches no library entry.
        var personal = enabled.OrderBy(e => e.Pattern, SqliteBinaryCollation.Instance).ToList();
        var localLibraries = input.LibraryEntries;
        var aiLibraries = input.AiLibraryEntries ?? localLibraries;
        var localVocabulary = CleanupPrompt.ComposeVocabulary(personal, localLibraries);
        var aiVocabulary = CleanupPrompt.ComposeVocabulary(personal, aiLibraries);
        var personalTerms = DictionaryLibraryComposer.Merge(personal, []).Count;

        if (!input.AiCleanupOn)
        {
            text.Append('.');
            return AppendLocalUse(text, localVocabulary.Count, input.PostProcessingOn, onlyWhenOff: true).ToString();
        }

        if (localLibraries.Count > 0 && localVocabulary.Count > personalTerms)
        {
            text.Append($", plus {Count(localVocabulary.Count - personalTerms)} from word packs that are on");
        }

        text.Append('.');
        AppendLocalUse(text, localVocabulary.Count, input.PostProcessingOn, onlyWhenOff: false);

        var local = CleanupPrompt.ResolvePromptStyle(input.PromptStyle, input.Provider) == CleanupPromptStyle.Local;
        var glossary = CleanupPrompt.CountGlossary(
            aiVocabulary, CleanupPrompt.GlossaryTermBudget(input.PromptStyle, input.Provider));
        var templates = aiVocabulary.Count(e => e.Enabled && !string.IsNullOrWhiteSpace(e.Replacement) &&
                                             !CleanupPrompt.IsVocabularyReplacement(e.Replacement));

        if (glossary.Eligible == 0)
        {
            text.Append(" AI cleanup receives no vocabulary.");
        }
        else
        {
            var receiver = input.Provider == CleanupProvider.FoundryLocal ? "The AI model on this PC" : "Your AI service";
            if (glossary.Included == glossary.Eligible)
            {
                var (terms, them) = glossary.Eligible == 1
                    ? ("that word", "it")
                    : ($"all {Count(glossary.Eligible)} words", "them");
                text.Append(
                    $" {receiver} receives {terms} as vocabulary with every cleanup request, whether or not the " +
                    $"dictation mentions {them}.");
            }
            else
            {
                text.Append(
                    $" {receiver} receives the first {Count(glossary.Included)} of {Count(glossary.Eligible)} words " +
                    "as vocabulary with every cleanup request, whether or not the dictation mentions them. Your own " +
                    "words come first.");
                text.Append(local
                    ? $" With the short instructions, the list stops at {Count(CleanupPrompt.MaxGlossaryTermsLocal)} " +
                      "words or phrases so a small model can take it in."
                    : $" The list stops at {Count(CleanupPrompt.MaxGlossaryTermsCloud)} words or phrases, or at " +
                      $"{Count(CleanupPrompt.MaxGlossaryChars)} characters.");
            }
        }

        if (templates > 0)
        {
            text.Append(
                $" {Count(templates)} {(templates == 1 ? "word is" : "words are")} left out because what Scribe writes " +
                $"for {(templates == 1 ? "it" : "them")} spans more than one line or runs past " +
                $"{Count(CleanupPrompt.MaxGlossaryTermChars)} characters.");
        }

        return text.ToString();
    }

    // Whether the entries are applied to dictation here: the post-processing switch decides, whatever AI
    // cleanup does with them.
    private static StringBuilder AppendLocalUse(StringBuilder text, int entries, bool postProcessingOn, bool onlyWhenOff)
    {
        if (entries == 0 || (postProcessingOn && onlyWhenOff))
        {
            return text;
        }

        return text.Append((postProcessingOn, entries == 1) switch
        {
            (true, true) => " It is applied on this PC.",
            (true, false) => " All of them are applied on this PC.",
            (false, true) => " Your dictionary and snippets are turned off, so it is not applied on this PC.",
            (false, false) => " Your dictionary and snippets are turned off, so none of them are applied on this PC.",
        });
    }

    private static string Count(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
