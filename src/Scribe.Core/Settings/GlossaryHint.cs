using System.Globalization;
using System.Text;
using Scribe.Core.Cleanup;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;

namespace Scribe.Core.Settings;

/// <summary>
/// The status line under the dictionary grid: how many entries are on and, while AI cleanup is on,
/// how many terms every cleanup request carries as vocabulary, and who receives them.
/// <para>
/// It used to count only this page's rows and tell a cloud user that "all of them" were sent, when
/// the enabled libraries went too and the list stops at a budget. The count now comes from the
/// glossary's own selection (<see cref="CleanupPrompt.CountGlossary"/>) over the merge dictation
/// uses (<see cref="DictionaryLibraryComposer.Merge"/>), from entries built the way Save builds them
/// (<see cref="DictionaryEntryBuilder.Build"/>), so the number shown is the number sent.
/// </para>
/// </summary>
public static class GlossaryHint
{
    /// <summary>What the page shows: its rows as typed, the enabled libraries' entries, and the AI settings on screen.</summary>
    public sealed record Input(
        IReadOnlyList<DictionaryEntryBuilder.Row> Rows,
        IReadOnlyList<DictionaryEntry> EnabledLibraryEntries,
        bool AiCleanupOn,
        CleanupProvider Provider,
        CleanupPromptStyle PromptStyle);

    public static string Describe(Input input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var rows = input.Rows;
        var enabledRows = rows.Count(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Replacement));
        var text = new StringBuilder($"{Count(enabledRows)} of {Count(rows.Count)} entries enabled");
        if (!input.AiCleanupOn)
        {
            return text.Append('.').ToString();
        }

        // Only enabled entries take part: dictation reads the enabled dictionary, so a disabled row
        // never keeps a library term with the same spoken form out of the glossary.
        var personal = DictionaryEntryBuilder.Build(rows).Entries.Where(e => e.Enabled).ToList();
        var personalTerms = DictionaryLibraryComposer.Merge(personal, []).Count;
        var effective = DictionaryLibraryComposer.Merge(personal, input.EnabledLibraryEntries);
        if (effective.Count > personalTerms)
        {
            text.Append($" plus {Count(effective.Count - personalTerms)} from enabled libraries");
        }

        text.Append('.');
        if (effective.Count > 0)
        {
            text.Append(" All of them are replaced locally.");
        }

        var local = CleanupPrompt.ResolvePromptStyle(input.PromptStyle, input.Provider) == CleanupPromptStyle.Local;
        var glossary = CleanupPrompt.CountGlossary(
            effective, local ? CleanupPrompt.MaxGlossaryTermsLocal : CleanupPrompt.MaxGlossaryTermsCloud);
        if (glossary.Eligible == 0)
        {
            return text.Append(" AI cleanup receives no vocabulary.").ToString();
        }

        var receiver = input.Provider == CleanupProvider.FoundryLocal ? "The on-device model" : "Your AI provider";
        if (glossary.Included == glossary.Eligible)
        {
            var terms = glossary.Eligible == 1 ? "that term" : $"all {Count(glossary.Eligible)} terms";
            return text.Append(
                $" {receiver} receives {terms} as vocabulary with every cleanup request, whether or not the " +
                "dictation mentions them.").ToString();
        }

        text.Append(
            $" {receiver} receives the first {Count(glossary.Included)} of {Count(glossary.Eligible)} terms as " +
            "vocabulary with every cleanup request, whether or not the dictation mentions them. Your own entries " +
            "come first, and local replacement still covers the rest.");
        return text.Append(local
            ? $" The Local prompt style stops the list at {Count(CleanupPrompt.MaxGlossaryTermsLocal)} terms so it " +
              "fits a small model's context."
            : $" The list stops at {Count(CleanupPrompt.MaxGlossaryTermsCloud)} terms or " +
              $"{Count(CleanupPrompt.MaxGlossaryChars)} characters.").ToString();
    }

    private static string Count(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
