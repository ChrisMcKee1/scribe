using System.Globalization;
using System.Text;

namespace Scribe.Core.Settings;

public enum SettingsSearchRequirementKind
{
    CheckBox,
    Radio,
    View,
    Action,
}

public sealed record SettingsSearchRequirement(
    string ControlName,
    string Label,
    SettingsSearchRequirementKind Kind);

public sealed record SettingsSearchEntry(
    string Id,
    SettingsPage Page,
    string ControlName,
    string Label,
    string? Context = null,
    IReadOnlyList<string>? Keywords = null,
    IReadOnlyList<SettingsSearchRequirement>? Requirements = null)
{
    public string PageLabel => SettingsNavigation.Title(Page);

    public string DisplayLabel => string.IsNullOrWhiteSpace(Context)
        ? Label
        : $"{Label} ({Context})";
}

public sealed record SettingsSearchResult(SettingsSearchEntry Entry, string DisplayText)
{
    public SettingsPage Page => Entry.Page;
    public string ControlName => Entry.ControlName;
    public string Label => Entry.Label;
    public string? Context => Entry.Context;
    public IReadOnlyList<SettingsSearchRequirement> Requirements => Entry.Requirements ?? [];
}

public static class SettingsSearchIndex
{
    private const int MaxResults = 8;

    private static readonly SettingsSearchRequirement RequiresAi =
        new("AiCleanupCheck", "Use AI cleanup", SettingsSearchRequirementKind.CheckBox);
    private static readonly SettingsSearchRequirement RequiresLocal =
        new("AiProviderLocalRadio", "On this PC", SettingsSearchRequirementKind.Radio);
    private static readonly SettingsSearchRequirement RequiresScribeModel =
        new("LocalAppScribeRadio", "Let Scribe manage it", SettingsSearchRequirementKind.Radio);
    private static readonly SettingsSearchRequirement RequiresFoundry =
        new("AiProviderFoundryRadio", "Microsoft Foundry", SettingsSearchRequirementKind.Radio);
    private static readonly SettingsSearchRequirement RequiresCustom =
        new("AiProviderCustomRadio", "Another AI service", SettingsSearchRequirementKind.Radio);
    private static readonly SettingsSearchRequirement RequiresCopilot =
        new("AiProviderCopilotRadio", "GitHub Copilot", SettingsSearchRequirementKind.Radio);
    private static readonly SettingsSearchRequirement RequiresOllama =
        new("LocalAppOllamaRadio", "Ollama", SettingsSearchRequirementKind.Radio);
    private static readonly SettingsSearchRequirement RequiresLmStudio =
        new("LocalAppLmStudioRadio", "LM Studio", SettingsSearchRequirementKind.Radio);
    private static readonly SettingsSearchRequirement RequiresAzureCli =
        new("AzureCliRadio", "Your Azure account (Azure CLI) (recommended)", SettingsSearchRequirementKind.Radio);
    private static readonly SettingsSearchRequirement RequiresAzureServicePrincipal =
        new("AzureServicePrincipalRadio", "An app registration (service principal)", SettingsSearchRequirementKind.Radio);
    private static readonly SettingsSearchRequirement RequiresAzureApiKey =
        new("AzureApiKeyRadio", "An API key", SettingsSearchRequirementKind.Radio);
    private static readonly SettingsSearchRequirement RequiresAzureSignIn =
        new("AzureSignInStatusRow", "Sign in to Azure", SettingsSearchRequirementKind.Action);
    private static readonly SettingsSearchRequirement RequiresAzureManualDetails =
        new("AzureManualToggleButton", "Enter details manually", SettingsSearchRequirementKind.View);

    public static IReadOnlyList<SettingsSearchEntry> Entries { get; } =
    [
        Entry("dictation.microphone", SettingsPage.Dictation, "DeviceCombo", "Microphone", ["input", "device", "sound"]),
        Entry("dictation.shortcut", SettingsPage.Dictation, "HotkeyBox", "Dictation shortcut", ["hotkey", "shortcut", "key", "keyboard", "push to talk", "push-to-talk", "hold", "toggle", "press"]),
        Entry("dictation.shortcut.raw", SettingsPage.Dictation, "DictationOnlyHotkeyBox", "Shortcut without AI cleanup", ["hotkey", "shortcut", "key", "raw", "dictation only", "hold", "toggle", "press"]),
        Entry("dictation.silence-stop", SettingsPage.Dictation, "AutoStopCheck", "Stop when I stop talking", ["vad", "silence", "automatic stop", "toggle"]),
        Entry("dictation.space", SettingsPage.Dictation, "SpaceAfterDictationCheck", "Add a space after each dictation", ["typing", "trailing space", "spacing"]),
        Entry("dictation.indicator", SettingsPage.Dictation, "OverlayCheck", "Show the recording indicator", ["overlay", "pill", "recording", "indicator"]),
        Entry("dictation.startup", SettingsPage.Dictation, "LaunchCheck", "Start with Windows", ["startup", "boot", "launch", "sign in"]),

        Entry("try.page", SettingsPage.TryDictation, "PlaygroundInput", "Try dictation", ["playground", "test", "sample", "try"]),

        Entry("ai.enabled", SettingsPage.AiCleanup, "AiCleanupCheck", "Use AI cleanup", ["polish", "grammar", "punctuation"]),
        Entry("ai.local", SettingsPage.AiCleanup, "AiProviderLocalRadio", "On this PC", ["provider", "offline", "local", "private"], null, [RequiresAi]),
        Entry("ai.copilot", SettingsPage.AiCleanup, "AiProviderCopilotRadio", "GitHub Copilot", ["provider", "github"], null, [RequiresAi]),
        Entry("ai.foundry", SettingsPage.AiCleanup, "AiProviderFoundryRadio", "Microsoft Foundry", ["provider", "azure"], null, [RequiresAi]),
        Entry("ai.custom", SettingsPage.AiCleanup, "AiProviderCustomRadio", "Another AI service", ["provider", "openrouter", "openai", "server"], null, [RequiresAi]),
        Entry("ai.local.scribe", SettingsPage.AiCleanup, "LocalAppScribeRadio", "Let Scribe manage it", ["foundry local", "download", "local model", "scribe"], "On this PC", [RequiresAi, RequiresLocal]),
        Entry("ai.local.ollama", SettingsPage.AiCleanup, "LocalAppOllamaRadio", "Ollama", ["local model", "gemma", "llama", "free memory"], "On this PC", [RequiresAi, RequiresLocal]),
        Entry("ai.local.lmstudio", SettingsPage.AiCleanup, "LocalAppLmStudioRadio", "LM Studio", ["lm studio", "lmstudio", "local model", "free memory"], "On this PC", [RequiresAi, RequiresLocal]),
        Entry("ai.model", SettingsPage.AiCleanup, "AiModelBox", "Model", ["foundry local", "download", "load", "free memory"], "On this PC", [RequiresAi, RequiresLocal, RequiresScribeModel]),
        Entry("ai.local.scribe.vocabulary", SettingsPage.AiCleanup, "FoundryWholeVocabularyCheck", LocalModelTuningText.WholeVocabularyTitle, ["vocabulary", "dictionary", "word packs", "context", "foundry local"], "On this PC", [RequiresAi, RequiresLocal, RequiresScribeModel]),
        Entry("ai.local.ollama.context", SettingsPage.AiCleanup, "OllamaContextSizeCombo", LocalModelTuningText.ContextSizeTitle, ["context", "context window", "context length", "num_ctx", "tokens", "memory"], "Ollama", [RequiresAi, RequiresLocal, RequiresOllama]),
        Entry("ai.local.ollama.vocabulary", SettingsPage.AiCleanup, "OllamaWholeVocabularyCheck", LocalModelTuningText.WholeVocabularyTitle, ["vocabulary", "dictionary", "word packs", "context"], "Ollama", [RequiresAi, RequiresLocal, RequiresOllama]),
        Entry("ai.local.lmstudio.context", SettingsPage.AiCleanup, "LmStudioContextSizeCombo", LocalModelTuningText.ContextSizeTitle, ["context", "context window", "context length", "tokens", "memory"], "LM Studio", [RequiresAi, RequiresLocal, RequiresLmStudio]),
        Entry("ai.local.lmstudio.vocabulary", SettingsPage.AiCleanup, "LmStudioWholeVocabularyCheck", LocalModelTuningText.WholeVocabularyTitle, ["vocabulary", "dictionary", "word packs", "context"], "LM Studio", [RequiresAi, RequiresLocal, RequiresLmStudio]),
        Entry("ai.azure.auth.cli", SettingsPage.AiCleanup, "AzureCliRadio", "Your Azure account (Azure CLI) (recommended)", ["sign in", "browser", "tenant"], "Microsoft Foundry", [RequiresAi, RequiresFoundry]),
        Entry("ai.azure.auth.sp", SettingsPage.AiCleanup, "AzureServicePrincipalRadio", "An app registration (service principal)", ["sign in", "entra", "client"], "Microsoft Foundry", [RequiresAi, RequiresFoundry]),
        Entry("ai.azure.auth.key", SettingsPage.AiCleanup, "AzureApiKeyRadio", "An API key", ["sign in", "resource key"], "Microsoft Foundry", [RequiresAi, RequiresFoundry]),
        Entry("ai.azure.tenant", SettingsPage.AiCleanup, "AzureTenantBox", "Tenant ID (optional)", ["directory", "azure cli"], "Microsoft Foundry", [RequiresAi, RequiresFoundry, RequiresAzureCli]),
        Entry("ai.azure.subscription", SettingsPage.AiCleanup, "AzureSubscriptionBox", "Subscription", ["azure"], "Microsoft Foundry", [RequiresAi, RequiresFoundry, RequiresAzureCli, RequiresAzureSignIn]),
        Entry("ai.azure.model", SettingsPage.AiCleanup, "AzureModelBox", "Model", ["deployment", "foundry"], "Microsoft Foundry", [RequiresAi, RequiresFoundry, RequiresAzureCli, RequiresAzureSignIn]),
        Entry("ai.azure.sp.tenant", SettingsPage.AiCleanup, "SpTenantBox", "Directory (tenant) ID", ["service principal", "entra"], "Microsoft Foundry", [RequiresAi, RequiresFoundry, RequiresAzureServicePrincipal]),
        Entry("ai.azure.sp.client", SettingsPage.AiCleanup, "SpClientIdBox", "Application (client) ID", ["service principal", "app registration"], "Microsoft Foundry", [RequiresAi, RequiresFoundry, RequiresAzureServicePrincipal]),
        Entry("ai.azure.sp.secret", SettingsPage.AiCleanup, "SpClientSecretBox", "Client secret", ["service principal", "password"], "Microsoft Foundry", [RequiresAi, RequiresFoundry, RequiresAzureServicePrincipal]),
        Entry("ai.azure.endpoint", SettingsPage.AiCleanup, "AzureEndpointBox", "Endpoint", ["address", "url", "foundry"], "Microsoft Foundry", [RequiresAi, RequiresFoundry, RequiresAzureManualDetails]),
        Entry("ai.azure.deployment", SettingsPage.AiCleanup, "AzureDeploymentBox", "Deployment name", ["model", "foundry"], "Microsoft Foundry", [RequiresAi, RequiresFoundry, RequiresAzureManualDetails]),
        Entry("ai.azure.key", SettingsPage.AiCleanup, "AzureApiKeyBox", "API key", ["resource key"], "Microsoft Foundry", [RequiresAi, RequiresFoundry, RequiresAzureApiKey]),
        Entry("ai.azure.cache", SettingsPage.AiCleanup, "AiPromptCachingCheck", "Let Microsoft Foundry cache what Scribe sends", ["cache", "caching", "prompt cache", "privacy", "retention"], "Microsoft Foundry", [RequiresAi, RequiresFoundry]),
        Entry("ai.custom.endpoint", SettingsPage.AiCleanup, "CustomEndpointBox", "Server address", ["url", "openrouter", "address"], "Another AI service", [RequiresAi, RequiresCustom]),
        Entry("ai.custom.api", SettingsPage.AiCleanup, "CustomApiStyleCombo", "API", ["chat completions", "responses", "openai"], "Another AI service", [RequiresAi, RequiresCustom]),
        Entry("ai.custom.model", SettingsPage.AiCleanup, "CustomModelBox", "Model name", ["model", "openrouter"], "Another AI service", [RequiresAi, RequiresCustom]),
        Entry("ai.custom.key", SettingsPage.AiCleanup, "CustomApiKeyBox", "API key (optional)", ["secret", "token"], "Another AI service", [RequiresAi, RequiresCustom]),
        Entry("ai.copilot.model", SettingsPage.AiCleanup, "CopilotModelCombo", "Model name", ["github", "copilot"], "GitHub Copilot", [RequiresAi, RequiresCopilot]),
        Entry("ai.writing-style", SettingsPage.AiCleanup, "AiWritingStyleBox", "Writing style", ["prompt", "tone"], null, [RequiresAi]),
        Entry("ai.prompt-style", SettingsPage.AiCleanup, "AiPromptStyleCombo", "Instructions for the AI model", ["prompt", "advanced"], null, [RequiresAi]),
        Entry("ai.prompt.detailed", SettingsPage.AiCleanup, "AiFrontierPromptBox", "Detailed instructions", ["prompt", "frontier"], null, [RequiresAi]),
        Entry("ai.prompt.short", SettingsPage.AiCleanup, "AiLocalPromptBox", "Short instructions", ["prompt", "local"], null, [RequiresAi]),

        Entry("dictionary.words", SettingsPage.Dictionary, "DictionaryTabs", "Your words", ["dictionary", "vocabulary", "words", "replacement", "spelling"]),
        Entry("dictionary.word-packs", SettingsPage.Dictionary, "DictionaryTabs", "Word packs", ["library", "libraries", "vocabulary", "packs", "terms"]),

        Entry("snippets.page", SettingsPage.VoiceSnippets, "SnippetList", "Voice snippets", ["snippet", "template", "phrase", "trigger", "expand", "email", "sign-off"]),
        Entry("profiles.page", SettingsPage.AppProfiles, "ProfileList", "App profiles", ["profile", "per app", "program", "process", "writing style", "line breaks"]),

        Entry("history.keep", SettingsPage.History, "HistoryRetentionCombo", "Keep dictations", ["retention", "delete", "days"]),
        Entry("history.recordings", SettingsPage.History, "StoreAudioCheck", "Save a recording with each dictation", ["audio", "recording", "history"]),

        Entry("usage.period", SettingsPage.Usage, "UsagePeriodBox", "Period", ["usage", "range", "statistics"]),

        Entry("advanced.speech-model", SettingsPage.Advanced, "TranscriptionModelCombo", "Speech model", ["model", "recognition", "parakeet", "moonshine"]),
        Entry("advanced.threads", SettingsPage.Advanced, "ThreadsCombo", "Processor threads", ["cpu", "decode", "advanced"]),
        Entry("advanced.free-memory", SettingsPage.Advanced, "IdleReleaseCombo", "Free memory when Scribe isn't used", ["idle", "release", "model", "memory"]),
        Entry("advanced.trim-silence", SettingsPage.Advanced, "VadCheck", "Trim silence", ["vad", "voice activity detection", "silence"]),
        Entry("advanced.longest-recording", SettingsPage.Advanced, "MaxDictationCombo", "Longest recording", ["duration", "limit", "minutes"]),
        Entry("advanced.typing-method", SettingsPage.Advanced, "InjectionCombo", "Typing method", ["paste", "clipboard", "type"]),
        Entry("advanced.line-breaks", SettingsPage.Advanced, "NewlineCombo", "Line breaks", ["newline", "enter", "terminal"]),
        Entry("advanced.chat-lines", SettingsPage.Advanced, "ShiftEnterCheck", "Don't send chat messages early", ["teams", "slack", "enter", "shift enter"]),
        Entry("advanced.text-changes", SettingsPage.Advanced, "PostCheck", "Apply your dictionary and snippets", ["dictionary", "snippets", "post processing", "vocabulary"]),
        Entry("advanced.accent", SettingsPage.Advanced, "AccentSourceCheck", "Use my Windows accent color", ["appearance", "theme", "color", "palette"]),
    ];

    public static IReadOnlyList<SettingsSearchResult> Search(string? query) => Search(query, MaxResults);

    public static IReadOnlyList<SettingsSearchResult> Search(string? query, int maxResults)
    {
        if (string.IsNullOrWhiteSpace(query) || maxResults <= 0)
        {
            return [];
        }

        var terms = Words(query).ToArray();
        if (terms.Length == 0)
        {
            return [];
        }

        var entryWords = EntryWordsIndex.All;
        return Entries
            .Select((entry, index) => new { Entry = entry, Index = index, Rank = Rank(entryWords[index], terms) })
            .Where(candidate => candidate.Rank < int.MaxValue)
            .OrderBy(candidate => candidate.Rank)
            .ThenBy(candidate => SettingsNavigation.Items.First(item => item.Page == candidate.Entry.Page).Position)
            .ThenBy(candidate => candidate.Index)
            .Take(Math.Min(maxResults, MaxResults))
            .Select(candidate => new SettingsSearchResult(candidate.Entry, $"{candidate.Entry.DisplayLabel} on {candidate.Entry.PageLabel}"))
            .ToArray();
    }

    private static SettingsSearchEntry Entry(
        string id,
        SettingsPage page,
        string controlName,
        string label,
        IReadOnlyList<string> keywords,
        string? context = null,
        IReadOnlyList<SettingsSearchRequirement>? requirements = null) =>
        new(id, page, controlName, label, context, keywords, requirements);

    private static int Rank(EntryWords words, string[] terms)
    {
        if (AllTermsMatch(terms, words.Label))
        {
            return 0;
        }

        if (words.LabelAndKeywords is { } labelAndKeywords && AllTermsMatch(terms, labelAndKeywords))
        {
            return 2;
        }

        if (AllTermsMatch(terms, words.Page))
        {
            return 3;
        }

        return int.MaxValue;
    }

    private static bool AllTermsMatch(string[] terms, string[] words)
    {
        foreach (var term in terms)
        {
            if (!StartsAWord(term, words))
            {
                return false;
            }
        }

        return true;
    }

    private static bool StartsAWord(string term, string[] words)
    {
        foreach (var word in words)
        {
            if (word.StartsWith(term, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> Words(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Normalize(NormalizationForm.FormD))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
                continue;
            }

            if (builder.Length > 0)
            {
                yield return builder.ToString();
                builder.Clear();
            }
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString();
        }
    }

    // The words of an entry's label; of its label and then each keyword, when it has keywords; and of its page.
    private sealed record EntryWords(string[] Label, string[]? LabelAndKeywords, string[] Page);

    // The entries are static and Words depends only on its text (FormD, marks dropped, invariant lower case), so their words
    // are found once, on the first search, and a keystroke tokenizes only the query. Entries of one page share its words.
    private static class EntryWordsIndex
    {
        public static readonly EntryWords[] All = Build();

        private static EntryWords[] Build()
        {
            var pages = new Dictionary<SettingsPage, string[]>();
            var all = new EntryWords[Entries.Count];
            for (var i = 0; i < all.Length; i++)
            {
                var entry = Entries[i];
                string[] label = [.. Words(entry.DisplayLabel)];
                string[]? labelAndKeywords = entry.Keywords is { Count: > 0 } keywords
                    ? [.. label, .. keywords.SelectMany(Words)]
                    : null;
                if (!pages.TryGetValue(entry.Page, out var page))
                {
                    page = [.. Words(entry.PageLabel)];
                    pages.Add(entry.Page, page);
                }

                all[i] = new EntryWords(label, labelAndKeywords, page);
            }

            return all;
        }
    }
}
