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
    /// <summary>The Add to dictionary line shown while AI cleanup is on.</summary>
    public const string AddToDictionaryVocabularyLine = "AI cleanup also receives your words and word pack words as vocabulary.";

    /// <summary>
    /// The "What leaves this PC" card on the AI cleanup page: what every cleanup request carries, and
    /// where it goes.
    /// </summary>
    public static string WhatCleanupSends { get; } =
        "Foundry Local runs cleanup on this PC, so your text stays on it. Microsoft Foundry, GitHub Copilot " +
        "and any other AI service you set up receive, with every cleanup request, the text Scribe recognized " +
        "for that dictation, the cleanup instructions with your writing style (or the matching app profile's), " +
        "and, as vocabulary, the words from your dictionary plus the word packs you let AI cleanup use that the " +
        "dictation appears to mention, including ones Scribe heard slightly differently: up to " +
        $"{Count(CleanupPrompt.MaxGlossaryTermsCloud)} words or phrases and {Count(CleanupPrompt.MaxGlossaryChars)} " +
        $"characters, or {Count(CleanupPrompt.MaxGlossaryTermsLocal)} words or phrases with the short instructions. " +
        "A word from your dictionary or a word pack is not " +
        "vocabulary, and is not sent, when what Scribe writes for it spans more than one line or runs past " +
        $"{Count(CleanupPrompt.MaxGlossaryTermChars)} characters, such as a signature.";

    /// <summary>
    /// The request that readies a model on a server on this PC as a dictation starts (<see cref="AdmittedCleanup.Prewarm"/>):
    /// what it carries, where it goes, and when it is not sent. Declared before the card that quotes it, because static
    /// initializers run in order.
    /// </summary>
    public static string ReadiesALocalServer { get; } =
        "When AI cleanup runs on a server on this PC, such as Ollama or LM Studio, starting a dictation also sends that " +
        "server the cleanup instructions with no dictated text and none of your vocabulary, unless it answered in the " +
        $"last {LocalAiServer.PrewarmAfterIdleSeconds} seconds, so a model it unloaded while idle is loaded again by " +
        "the time you stop talking.";

    /// <summary>
    /// What Scribe asks Ollama or LM Studio itself (<see cref="LocalServerClient"/>): the models it has and holds, and to
    /// free one. Declared before the card that quotes it.
    /// </summary>
    public static string ManagesALocalApp { get; } =
        "With Ollama or LM Studio at its own address on this PC, Scribe also asks that app which models it has and which " +
        "it holds in memory, and asks it to free a model's memory when AI cleanup stops using it; these requests stay on " +
        "this PC and carry nothing you said, only the API key you saved for that address, if any.";

    /// <summary>The same card's second paragraph: the connection check, the readying request, and what is never sent.</summary>
    public static string WhatCleanupNeverSends { get; } =
        "Each time cleanup connects, for example when Scribe starts or you save a change to where AI cleanup " +
        "runs, it first sends a short test request holding the word \"ok\" and the same instructions, with " +
        "none of your vocabulary. " + ReadiesALocalServer + " " + ManagesALocalApp + " Cleanup never sends your " +
        "snippet templates, and audio never leaves this device. " +
        "GitHub Copilot sends all of this to GitHub under your own Copilot sign-in and GitHub's terms, and " +
        "listing its models contacts GitHub too.";

    /// <summary>
    /// The same card's third paragraph: what a remote service may keep in its prompt cache, which asking it not to store
    /// responses does not cover, and the setting that turns it off for Microsoft Foundry.
    /// </summary>
    public static string WhatTheServiceMayCache { get; } =
        "Asking Microsoft Foundry not to store responses does not turn off its separate prompt cache. Microsoft Foundry " +
        "may keep temporary data derived from cleanup requests, including the dictation, the instructions and your " +
        "vocabulary, for at least 30 minutes (up to 24 hours on some models). Turning off " +
        $"\"{PromptCachingTitle}\" asks Microsoft Foundry not to use its prompt cache for new cleanup requests. That works " +
        "on GPT-5.6 and later models on Standard deployments; earlier models and provisioned deployments can't turn " +
        "caching off. Scribe can't clear what the cache already holds. Another AI service and GitHub Copilot follow " +
        "their own caching policy.";

    /// <summary>The Microsoft Foundry prompt cache setting (<c>AppSettings.AiCleanupPromptCaching</c>), as Settings names it.</summary>
    public const string PromptCachingTitle = "Let Microsoft Foundry cache what Scribe sends";

    /// <summary>The trade-off under the setting.</summary>
    public static string PromptCachingTradeOff { get; } =
        "On: Microsoft Foundry may reuse parts of recent requests to respond faster, and may keep temporary data derived " +
        "from them, including your dictation, the instructions and your vocabulary, for at least 30 minutes (up to 24 " +
        "hours on some models). Off: Scribe asks Microsoft Foundry not to use its prompt cache for new cleanup requests. " +
        "AI cleanup can be slower, and Scribe can't clear what the cache already holds. Off works on GPT-5.6 and later " +
        "models on Standard deployments; earlier models and provisioned deployments can't turn caching off, so AI " +
        "cleanup stops and Scribe types what it hears until you turn this back on.";

    /// <summary>Under another AI service's fields: the setting is Microsoft Foundry's, and this service decides for itself.</summary>
    public const string CustomServiceCaching =
        "Whether this service caches what Scribe sends follows its own policy. Scribe doesn't change it.";

    /// <summary>Under GitHub Copilot's fields, for the same reason.</summary>
    public const string CopilotCaching =
        "Whether GitHub caches what Scribe sends follows GitHub's own policy. Scribe doesn't change it.";

    public static string SummaryFor(CleanupProvider provider) => provider switch
    {
        CleanupProvider.FoundryLocal => "Your text, writing style and vocabulary stay on this PC. Audio never leaves it.",
        CleanupProvider.AzureFoundry => RemoteSummary("your Microsoft Foundry deployment"),
        CleanupProvider.OpenAiCompatible => RemoteSummary("the address you enter"),
        CleanupProvider.GitHubCopilot => RemoteSummary("GitHub"),
        _ => RemoteSummary("the AI service"),
    };

    /// <summary>
    /// <see cref="SummaryFor(CleanupProvider)"/> for the service at <paramref name="customEndpoint"/>: Ollama or LM Studio
    /// at its own address on this PC (<see cref="LocalAiServer.AppAt"/>) keeps everything on this PC, as Foundry Local does.
    /// </summary>
    public static string SummaryFor(CleanupProvider provider, string? customEndpoint) =>
        LocalAiServer.AppServing(provider, customEndpoint) != LocalServerApp.None
            ? SummaryFor(CleanupProvider.FoundryLocal)
            : SummaryFor(provider);

    private static string RemoteSummary(string destination) =>
        $"Each cleanup sends the text Scribe heard, your writing style, and the dictionary and word pack words it mentions to {destination}. Audio never leaves this PC.";

    /// <summary>The title of the confirmation shown before AI dictionary suggestions send dictation text.</summary>
    public const string SuggestionConsentTitle = "Send recent dictations to your AI service?";

    /// <summary>
    /// The confirmation shown before AI dictionary suggestions send recent dictation, naming where it goes:
    /// the provider cleanup is serving, which is the only one the request can reach
    /// (<see cref="ITextCleanupService.CompleteAsync"/> refuses any other). The sample is history as it was
    /// inserted, which is text the dictionary and snippets already changed.
    /// </summary>
    public static string SuggestionConsentFor(CleanupProvider provider) =>
        $"To suggest vocabulary, Scribe will send up to {Count(AiDictionarySuggester.DefaultMaxSampleChars)} " +
        $"characters of your most recent dictations, as they were inserted, to {Destination(provider)}. That " +
        "text can include words your dictionary and snippets added. Your dictionary list, your writing style " +
        "and audio are not sent. If where AI cleanup runs changes before the request goes out, nothing is sent.";

    private static string Destination(CleanupProvider provider) => provider switch
    {
        CleanupProvider.AzureFoundry => "your Microsoft Foundry deployment",
        CleanupProvider.OpenAiCompatible => "the AI service you set up",
        CleanupProvider.GitHubCopilot => "GitHub, through your Copilot sign-in",
        _ => "Foundry Local, which runs on this PC",
    };

    private static string Count(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
