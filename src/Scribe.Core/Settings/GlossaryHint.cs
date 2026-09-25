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
    /// <summary>What the page shows: its rows as typed, its enabled libraries' entries, and the settings on screen.</summary>
    /// <param name="LibraryEntries">
    /// The enabled libraries' entries as <see cref="DictionaryLibraryComposer.ComposeLibraries"/> returns them: one row per
    /// spoken form, in precedence order (<see cref="Libraries.LibraryPrecedence"/>), which is what the library service
    /// gives dictation's glossary. They are counted as given: a flattened list no longer says which library a row came
    /// from, so any reordering here could only lose which library's row wins.
    /// </param>
    public sealed record Input(
        IReadOnlyList<DictionaryEntryBuilder.Row> Rows,
        IReadOnlyList<DictionaryEntry> LibraryEntries,
        bool AiCleanupOn,
        bool PostProcessingOn,
        CleanupProvider Provider,
        CleanupPromptStyle PromptStyle);

    public static string Describe(Input input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Entries are the rows with a spoken form, the rows Save keeps; a rule that deletes its phrase is as
        // enabled as one that replaces it.
        var entries = DictionaryEntryBuilder.Build(input.Rows).Entries;
        var enabled = entries.Where(e => e.Enabled).ToList();
        var text = new StringBuilder($"{Count(enabled.Count)} of {Count(entries.Count)} entries enabled");

        // Dictation reads only enabled entries, so a disabled row never keeps a library term with the same
        // spoken form out of the vocabulary. The page's own rows are put in the order the repository reads the
        // saved dictionary back; that is the only reordering here, and it touches no library entry.
        var personal = enabled.OrderBy(e => e.Pattern, SqliteBinaryCollation.Instance).ToList();
        var libraries = input.LibraryEntries;
        var effective = CleanupPrompt.ComposeVocabulary(personal, libraries);
        var personalTerms = DictionaryLibraryComposer.Merge(personal, []).Count;

        if (!input.AiCleanupOn)
        {
            text.Append('.');
            return AppendLocalUse(text, effective.Count, input.PostProcessingOn, onlyWhenOff: true).ToString();
        }

        if (libraries.Count > 0 && effective.Count > personalTerms)
        {
            text.Append($" plus {Count(effective.Count - personalTerms)} from enabled libraries");
        }

        text.Append('.');
        AppendLocalUse(text, effective.Count, input.PostProcessingOn, onlyWhenOff: false);

        var local = CleanupPrompt.ResolvePromptStyle(input.PromptStyle, input.Provider) == CleanupPromptStyle.Local;
        var glossary = CleanupPrompt.CountGlossary(
            effective, CleanupPrompt.GlossaryTermBudget(input.PromptStyle, input.Provider));
        var templates = effective.Count(e => e.Enabled && !string.IsNullOrWhiteSpace(e.Replacement) &&
                                             !CleanupPrompt.IsVocabularyReplacement(e.Replacement));

        if (glossary.Eligible == 0)
        {
            text.Append(" AI cleanup receives no vocabulary.");
        }
        else
        {
            var receiver = input.Provider == CleanupProvider.FoundryLocal ? "The on-device model" : "Your AI provider";
            if (glossary.Included == glossary.Eligible)
            {
                var (terms, them) = glossary.Eligible == 1
                    ? ("that term", "it")
                    : ($"all {Count(glossary.Eligible)} terms", "them");
                text.Append(
                    $" {receiver} receives {terms} as vocabulary with every cleanup request, whether or not the " +
                    $"dictation mentions {them}.");
            }
            else
            {
                text.Append(
                    $" {receiver} receives the first {Count(glossary.Included)} of {Count(glossary.Eligible)} terms " +
                    "as vocabulary with every cleanup request, whether or not the dictation mentions them. Your own " +
                    "entries come first.");
                text.Append(local
                    ? $" The Local prompt style stops the list at {Count(CleanupPrompt.MaxGlossaryTermsLocal)} terms so " +
                      "it fits a small model's context."
                    : $" The list stops at {Count(CleanupPrompt.MaxGlossaryTermsCloud)} terms or " +
                      $"{Count(CleanupPrompt.MaxGlossaryChars)} characters.");
            }
        }

        if (templates > 0)
        {
            text.Append(
                $" {Count(templates)} {(templates == 1 ? "entry is" : "entries are")} left out because the written " +
                $"form spans more than one line or runs past {Count(CleanupPrompt.MaxGlossaryTermChars)} characters.");
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
            (false, true) => " Post-processing is off, so it is not applied on this PC.",
            (false, false) => " Post-processing is off, so none of them are applied on this PC.",
        });
    }

    private static string Count(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
