using System.Globalization;
using Scribe.Core.PostProcessing;

namespace Scribe.Core.Cleanup;

/// <summary>
/// What AI cleanup and its helpers send, and to whom, in the words Settings shows.
/// <para>
/// Kept in Core, beside the limits it quotes, because this text is a promise and the code is what keeps
/// it. The AI cleanup page used to say remote providers receive "relevant dictionary terms" while every
/// cleanup request carried every enabled dictionary and library term, and the readiness probe carried
/// them too, before a word was dictated. Each number below is read from the constant that enforces
/// it, and CleanupDisclosureTests fails if a limit or a promise changes without this text.
/// </para>
/// </summary>
public static class CleanupDisclosure
{
    /// <summary>
    /// The "What leaves this PC" card on the AI cleanup page: what every cleanup request carries, and
    /// where it goes.
    /// </summary>
    public static string WhatCleanupSends { get; } =
        "Foundry Local runs cleanup on this PC, so your text stays on it. Microsoft Foundry, " +
        "an OpenAI-compatible endpoint and GitHub Copilot receive, with every cleanup request, the text " +
        "Scribe recognized for that dictation, the cleanup instructions with your writing style (or the " +
        "matching app profile's), and your enabled dictionary and library terms as vocabulary: up to " +
        $"{Count(CleanupPrompt.MaxGlossaryTermsCloud)} terms and {Count(CleanupPrompt.MaxGlossaryChars)} " +
        $"characters, or {Count(CleanupPrompt.MaxGlossaryTermsLocal)} terms with the Local prompt style, " +
        "whether or not the dictation mentions them.";

    /// <summary>The same card's second paragraph: the connection check, and what is never sent.</summary>
    public static string WhatCleanupNeverSends { get; } =
        "Each time cleanup connects, for example when Scribe starts or you save a different provider, it " +
        "first sends a short test request holding the word \"ok\" and the same instructions, with none of " +
        "your vocabulary. Cleanup never sends your snippet templates, and audio never leaves this device. " +
        "GitHub Copilot sends all of this to GitHub under your own Copilot sign-in and GitHub's terms, and " +
        "listing its models contacts GitHub too.";

    /// <summary>The title of the confirmation shown before AI dictionary suggestions send dictation text.</summary>
    public const string SuggestionConsentTitle = "Send recent dictations to your AI provider?";

    /// <summary>
    /// The confirmation shown before AI dictionary suggestions send recent dictation to a provider
    /// other than Foundry Local. The sample is history as it was inserted, which is text the dictionary
    /// and snippets already changed.
    /// </summary>
    public static string SuggestionConsent { get; } =
        $"To suggest vocabulary, Scribe will send up to {Count(AiDictionarySuggester.DefaultMaxSampleChars)} " +
        "characters of your most recent dictations, as they were inserted, to the AI cleanup provider you " +
        "saved. That text can include words your dictionary and snippets added. Your dictionary list, your " +
        "writing style and audio are not sent.";

    private static string Count(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
