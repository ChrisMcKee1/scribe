using System.Globalization;
using System.Text;

namespace Scribe.Core.Settings;

public sealed record SettingsSearchEntry(
    string Id,
    SettingsPage Page,
    string ControlName,
    string Label,
    string? ParentControlName = null,
    string? ParentLabel = null,
    IReadOnlyList<string>? Keywords = null)
{
    public string PageLabel => SettingsNavigation.Title(Page);
}

public sealed record SettingsSearchResult(SettingsSearchEntry Entry, string DisplayText)
{
    public SettingsPage Page => Entry.Page;
    public string ControlName => Entry.ControlName;
    public string Label => Entry.Label;
    public string? ParentControlName => Entry.ParentControlName;
    public string? ParentLabel => Entry.ParentLabel;
}

public static class SettingsSearchIndex
{
    private const int MaxResults = 8;

    public static IReadOnlyList<SettingsSearchEntry> Entries { get; } =
    [
        Entry("dictation.microphone", SettingsPage.Dictation, "DeviceCombo", "Microphone", ["input", "device", "sound"]),
        Entry("dictation.shortcut", SettingsPage.Dictation, "HotkeyBox", "Dictation shortcut", ["hotkey", "shortcut", "key", "keyboard", "push to talk", "push-to-talk"]),
        Entry("dictation.shortcut.mode", SettingsPage.Dictation, "ModeCombo", "Dictation shortcut, hold or toggle", ["hotkey", "shortcut", "hold", "toggle", "press"]),
        Entry("dictation.shortcut.raw", SettingsPage.Dictation, "DictationOnlyHotkeyBox", "Shortcut without AI cleanup", ["hotkey", "shortcut", "key", "raw", "dictation only"]),
        Entry("dictation.shortcut.raw.mode", SettingsPage.Dictation, "DictationOnlyModeCombo", "Shortcut without AI cleanup, hold or toggle", ["hotkey", "shortcut", "hold", "toggle", "press"]),
        Entry("dictation.silence-stop", SettingsPage.Dictation, "AutoStopCheck", "Stop when I stop talking", ["vad", "silence", "automatic stop", "toggle"]),
        Entry("dictation.space", SettingsPage.Dictation, "SpaceAfterDictationCheck", "Add a space after each dictation", ["typing", "trailing space", "spacing"]),
        Entry("dictation.indicator", SettingsPage.Dictation, "OverlayCheck", "Show the recording indicator", ["overlay", "pill", "recording", "indicator"]),
        Entry("dictation.startup", SettingsPage.Dictation, "LaunchCheck", "Start with Windows", ["startup", "boot", "launch", "sign in"]),

        Entry("try.input", SettingsPage.TryDictation, "PlaygroundInput", "Try dictation box", ["playground", "test", "sample"]),

        Entry("ai.enabled", SettingsPage.AiCleanup, "AiCleanupCheck", "Use AI cleanup", ["polish", "grammar", "punctuation"]),
        Entry("ai.local", SettingsPage.AiCleanup, "AiProviderLocalRadio", "On this PC (Foundry Local)", "AiCleanupCheck", "Use AI cleanup", ["provider", "offline", "local"]),
        Entry("ai.copilot", SettingsPage.AiCleanup, "AiProviderCopilotRadio", "GitHub Copilot", "AiCleanupCheck", "Use AI cleanup", ["provider", "github"]),
        Entry("ai.foundry", SettingsPage.AiCleanup, "AiProviderFoundryRadio", "Microsoft Foundry", "AiCleanupCheck", "Use AI cleanup", ["provider", "azure"]),
        Entry("ai.custom", SettingsPage.AiCleanup, "AiProviderCustomRadio", "Another AI service", "AiCleanupCheck", "Use AI cleanup", ["provider", "ollama", "lm studio", "openrouter", "openai"]),
        Entry("ai.model", SettingsPage.AiCleanup, "AiModelBox", "Model", "AiCleanupCheck", "Use AI cleanup", ["foundry local", "download", "load"]),
        Entry("ai.azure.auth.cli", SettingsPage.AiCleanup, "AzureCliRadio", "Your Azure account (Azure CLI) (recommended)", "AiCleanupCheck", "Use AI cleanup", ["sign in", "browser", "tenant"]),
        Entry("ai.azure.auth.sp", SettingsPage.AiCleanup, "AzureServicePrincipalRadio", "An app registration (service principal)", "AiCleanupCheck", "Use AI cleanup", ["sign in", "entra", "client"]),
        Entry("ai.azure.auth.key", SettingsPage.AiCleanup, "AzureApiKeyRadio", "An API key", "AiCleanupCheck", "Use AI cleanup", ["sign in", "resource key"]),
        Entry("ai.azure.tenant", SettingsPage.AiCleanup, "AzureTenantBox", "Tenant ID (optional)", "AiCleanupCheck", "Use AI cleanup", ["directory", "azure cli"]),
        Entry("ai.azure.subscription", SettingsPage.AiCleanup, "AzureSubscriptionBox", "Subscription", "AiCleanupCheck", "Use AI cleanup", ["azure"]),
        Entry("ai.azure.model", SettingsPage.AiCleanup, "AzureModelBox", "Model", "AiCleanupCheck", "Use AI cleanup", ["deployment", "foundry"]),
        Entry("ai.azure.sp.tenant", SettingsPage.AiCleanup, "SpTenantBox", "Directory (tenant) ID", "AiCleanupCheck", "Use AI cleanup", ["service principal", "entra"]),
        Entry("ai.azure.sp.client", SettingsPage.AiCleanup, "SpClientIdBox", "Application (client) ID", "AiCleanupCheck", "Use AI cleanup", ["service principal", "app registration"]),
        Entry("ai.azure.sp.secret", SettingsPage.AiCleanup, "SpClientSecretBox", "Client secret", "AiCleanupCheck", "Use AI cleanup", ["service principal", "password"]),
        Entry("ai.azure.endpoint", SettingsPage.AiCleanup, "AzureEndpointBox", "Endpoint", "AiCleanupCheck", "Use AI cleanup", ["address", "url", "foundry"]),
        Entry("ai.azure.deployment", SettingsPage.AiCleanup, "AzureDeploymentBox", "Deployment name", "AiCleanupCheck", "Use AI cleanup", ["model", "foundry"]),
        Entry("ai.azure.key", SettingsPage.AiCleanup, "AzureApiKeyBox", "API key", "AiCleanupCheck", "Use AI cleanup", ["resource key"]),
        Entry("ai.custom.endpoint", SettingsPage.AiCleanup, "CustomEndpointBox", "Server address", "AiCleanupCheck", "Use AI cleanup", ["url", "ollama", "lm studio", "openrouter"]),
        Entry("ai.custom.model", SettingsPage.AiCleanup, "CustomModelBox", "Model name", "AiCleanupCheck", "Use AI cleanup", ["model", "ollama", "lm studio", "openrouter"]),
        Entry("ai.custom.key", SettingsPage.AiCleanup, "CustomApiKeyBox", "API key (optional)", "AiCleanupCheck", "Use AI cleanup", ["secret", "token"]),
        Entry("ai.copilot.model", SettingsPage.AiCleanup, "CopilotModelCombo", "Model name", "AiCleanupCheck", "Use AI cleanup", ["github", "copilot"]),
        Entry("ai.writing-style", SettingsPage.AiCleanup, "AiWritingStyleBox", "Writing style", "AiCleanupCheck", "Use AI cleanup", ["prompt", "tone"]),
        Entry("ai.prompt-style", SettingsPage.AiCleanup, "AiPromptStyleCombo", "Instructions for the AI model", "AiCleanupCheck", "Use AI cleanup", ["prompt", "advanced"]),
        Entry("ai.prompt.detailed", SettingsPage.AiCleanup, "AiFrontierPromptBox", "Detailed instructions", "AiCleanupCheck", "Use AI cleanup", ["prompt", "frontier"]),
        Entry("ai.prompt.short", SettingsPage.AiCleanup, "AiLocalPromptBox", "Short instructions", "AiCleanupCheck", "Use AI cleanup", ["prompt", "local"]),

        Entry("dictionary.words", SettingsPage.Dictionary, "DictionaryTabs", "Your words", ["dictionary", "vocabulary", "words", "replacement", "spelling"]),
        Entry("dictionary.word-packs", SettingsPage.Dictionary, "DictionaryTabs", "Word packs", ["library", "libraries", "vocabulary", "packs", "terms"]),

        Entry("snippets.enabled", SettingsPage.VoiceSnippets, "SnippetEnabledCheck", "On", ["voice snippet", "template"]),
        Entry("snippets.phrase", SettingsPage.VoiceSnippets, "SnippetPhraseBox", "When you say", ["trigger", "voice snippet", "phrase"]),
        Entry("snippets.template", SettingsPage.VoiceSnippets, "SnippetTemplateBox", "Scribe types", ["template", "expands", "voice snippet"]),

        Entry("profiles.name", SettingsPage.AppProfiles, "ProfileNameBox", "Name", ["profile"]),
        Entry("profiles.apps", SettingsPage.AppProfiles, "ProfileProcessesBox", "Apps", ["program", "process", "application"]),
        Entry("profiles.style", SettingsPage.AppProfiles, "ProfileStyleBox", "Writing style for these apps", ["profile", "tone", "prompt"]),
        Entry("profiles.line-breaks", SettingsPage.AppProfiles, "ProfileNewlineCombo", "Line breaks in these apps", ["profile", "newline", "enter"]),

        Entry("history.keep", SettingsPage.History, "HistoryRetentionCombo", "Keep dictations", ["retention", "delete", "days"]),
        Entry("history.keep.custom", SettingsPage.History, "HistoryRetentionCustomBox", "Keep dictations custom days", ["retention", "custom", "days"]),
        Entry("history.recordings", SettingsPage.History, "StoreAudioCheck", "Save a recording with each dictation", ["audio", "recording", "history"]),

        Entry("usage.period", SettingsPage.Usage, "UsagePeriodBox", "Period", ["usage", "range", "statistics"]),

        Entry("advanced.speech-model", SettingsPage.Advanced, "TranscriptionModelCombo", "Speech model", ["model", "recognition", "parakeet", "moonshine"]),
        Entry("advanced.threads", SettingsPage.Advanced, "ThreadsCombo", "Processor threads", ["cpu", "decode", "advanced"]),
        Entry("advanced.free-memory", SettingsPage.Advanced, "IdleReleaseCombo", "Free memory when Scribe isn't used", ["idle", "release", "model", "memory"]),
        Entry("advanced.free-memory.custom", SettingsPage.Advanced, "IdleReleaseCustomBox", "Free memory custom minutes", ["idle", "custom", "memory"]),
        Entry("advanced.trim-silence", SettingsPage.Advanced, "VadCheck", "Trim silence", ["vad", "voice activity detection", "silence"]),
        Entry("advanced.longest-recording", SettingsPage.Advanced, "MaxDictationCombo", "Longest recording", ["duration", "limit", "minutes"]),
        Entry("advanced.longest-recording.custom", SettingsPage.Advanced, "MaxDictationCustomBox", "Longest recording custom minutes", ["duration", "custom", "limit"]),
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

        return Entries
            .Select((entry, index) => new { Entry = entry, Index = index, Rank = Rank(entry, terms) })
            .Where(candidate => candidate.Rank < int.MaxValue)
            .OrderBy(candidate => candidate.Rank)
            .ThenBy(candidate => SettingsNavigation.Items.First(item => item.Page == candidate.Entry.Page).Position)
            .ThenBy(candidate => candidate.Index)
            .Take(maxResults)
            .Select(candidate => new SettingsSearchResult(candidate.Entry, $"{candidate.Entry.Label} on {candidate.Entry.PageLabel}"))
            .ToArray();
    }

    private static SettingsSearchEntry Entry(
        string id,
        SettingsPage page,
        string controlName,
        string label,
        IReadOnlyList<string> keywords) =>
        new(id, page, controlName, label, null, null, keywords);

    private static SettingsSearchEntry Entry(
        string id,
        SettingsPage page,
        string controlName,
        string label,
        string? parentControlName,
        string? parentLabel,
        IReadOnlyList<string> keywords) =>
        new(id, page, controlName, label, parentControlName, parentLabel, keywords);

    private static int Rank(SettingsSearchEntry entry, IReadOnlyList<string> terms)
    {
        if (AllTermsMatch(terms, entry.Label))
        {
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(entry.ParentLabel) && AllTermsMatch(terms, entry.ParentLabel))
        {
            return 1;
        }

        if (entry.Keywords is { Count: > 0 } keywords && AllTermsMatch(terms, keywords))
        {
            return 2;
        }

        if (AllTermsMatch(terms, entry.PageLabel))
        {
            return 3;
        }

        return int.MaxValue;
    }

    private static bool AllTermsMatch(IReadOnlyList<string> terms, params string?[] values) =>
        terms.All(term => values.SelectMany(Words).Any(word => word.StartsWith(term, StringComparison.Ordinal)));

    private static bool AllTermsMatch(IReadOnlyList<string> terms, IEnumerable<string> values) =>
        terms.All(term => values.SelectMany(Words).Any(word => word.StartsWith(term, StringComparison.Ordinal)));

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
}
