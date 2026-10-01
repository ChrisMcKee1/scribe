using Scribe.Core.Cleanup;
using Scribe.Core.Models;

namespace Scribe.Core.Settings;

public enum DraftRowOrigin
{
    Saved,
    New,
}

public enum ValidationCode
{
    DictionarySpokenEmpty,
    DictionaryDuplicate,
    SnippetTriggerEmpty,
    SnippetTextEmpty,
    SnippetDuplicate,
    ProfileNameEmpty,
    ProfileAppsEmpty,
    ShortcutsIdentical,
    FoundryEndpointInvalid,
    CustomEndpointInvalid,
    CustomModelEmpty,
    DeploymentEmpty,
    TenantEmpty,
    ClientIdEmpty,
    ClientSecretEmpty,
    DurationOutOfRange,
}

public enum ValidationSeverity
{
    Blocking,
    Warning,
}

public sealed record ValidationIssue(
    ValidationCode Code,
    SettingsPage Page,
    string ControlName,
    string? RowKey,
    string Message,
    ValidationSeverity Severity = ValidationSeverity.Blocking);

public sealed record DictionaryDraftRow(
    string RowKey,
    DraftRowOrigin Origin,
    bool Touched,
    string? Pattern,
    string? Replacement,
    string? LoadedPattern = null,
    string? LoadedReplacement = null,
    bool WholeWord = true,
    bool Enabled = true,
    bool LoadedWholeWord = true,
    bool LoadedEnabled = true);

public sealed record SnippetDraftRow(
    string RowKey,
    DraftRowOrigin Origin,
    bool Touched,
    string? Phrase,
    string? Template,
    string? LoadedPhrase = null,
    string? LoadedTemplate = null,
    bool Enabled = true,
    bool LoadedEnabled = true);

public sealed record ProfileDraftRow(
    string RowKey,
    DraftRowOrigin Origin,
    bool Touched,
    string? Name,
    string? Apps,
    string? LoadedName = null,
    string? LoadedApps = null,
    string? WritingStyle = null,
    string? LoadedWritingStyle = null,
    NewlineInjectionMode? NewlineHandling = null,
    NewlineInjectionMode? LoadedNewlineHandling = null);

public sealed record DurationDraftField(
    SettingsPage Page,
    string ControlName,
    int Value,
    int Minimum,
    int Maximum);

public sealed record SettingsDraft(
    AppSettings Settings,
    IReadOnlyList<DictionaryDraftRow>? DictionaryRows = null,
    IReadOnlyList<SnippetDraftRow>? SnippetRows = null,
    IReadOnlyList<ProfileDraftRow>? ProfileRows = null,
    IReadOnlyList<DurationDraftField>? DurationFields = null);

public static class SettingsDraftValidator
{
    public const string DictionarySpokenEmptyMessage = "Type the words Scribe hears, or delete this row.";
    public const string SnippetTriggerEmptyMessage = "Type the words you'll say, or delete this snippet.";
    public const string SnippetTextEmptyMessage = "Type the text this snippet adds, or delete it.";
    public const string ProfileNameEmptyMessage = "Give this profile a name, or delete it.";
    public const string ProfileAppsEmptyMessage = "Add at least one app to this profile, or delete it.";
    public const string ShortcutsIdenticalMessage = "Both shortcuts use the same key. Choose a different key for one of them.";
    public const string FoundryEndpointInvalidMessage = "Enter the address of your Microsoft Foundry resource. It starts with https://.";
    public const string CustomEndpointInvalidMessage = "Enter the service's address, such as http://localhost:11434/v1.";

    /// <summary>An address that ends in the older Completions API's path, which Scribe does not use; the service says the same.</summary>
    public const string CustomEndpointOldCompletionsMessage =
        "This address ends in /completions, the older Completions API, which Scribe doesn't use. End it in " +
        "/chat/completions or /responses, or in the part before them, usually /v1.";
    public const string CustomModelEmptyMessage = "Enter the name of the model.";
    public const string LocalAppModelEmptyMessage = "Choose a model from the list.";
    public const string DeploymentEmptyMessage = "Enter the name of the model deployment.";
    public const string TenantEmptyMessage = "Enter the tenant ID.";
    public const string ClientIdEmptyMessage = "Enter the client ID.";
    public const string ClientSecretEmptyMessage = "Enter the client secret.";

    public static IReadOnlyList<ValidationIssue> Validate(SettingsDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(draft.Settings);

        var issues = new List<ValidationIssue>();
        ValidateDictionary(draft.DictionaryRows ?? []);
        ValidateSnippets(draft.SnippetRows ?? []);
        ValidateProfiles(draft.ProfileRows ?? []);
        ValidateShortcuts(draft.Settings);
        ValidateProvider(draft.Settings);
        ValidateDurations(draft.DurationFields ?? []);
        return issues;

        void Add(
            ValidationCode code,
            SettingsPage page,
            string control,
            string? rowKey,
            string message,
            ValidationSeverity severity = ValidationSeverity.Blocking) =>
            issues.Add(new ValidationIssue(code, page, control, rowKey, message, severity));

        void ValidateDictionary(IReadOnlyList<DictionaryDraftRow> rows)
        {
            var seen = new Dictionary<string, DictionaryDraftRow>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                if (IsPlaceholder(row))
                {
                    continue;
                }

                var pattern = Trim(row.Pattern);
                if (pattern.Length == 0)
                {
                    Add(
                        ValidationCode.DictionarySpokenEmpty,
                        SettingsPage.Dictionary,
                        "DictionaryPattern",
                        row.RowKey,
                        DictionarySpokenEmptyMessage,
                        SeverityFor(Unchanged(row)));

                    continue;
                }

                if (seen.TryGetValue(pattern, out var first))
                {
                    Add(
                        ValidationCode.DictionaryDuplicate,
                        SettingsPage.Dictionary,
                        "DictionaryPattern",
                        row.RowKey,
                        $"\"{pattern}\" is already in your dictionary. Keep one of the two rows.",
                        SeverityFor(Unchanged(first) && Unchanged(row)));
                }
                else
                {
                    seen.Add(pattern, row);
                }
            }
        }

        void ValidateSnippets(IReadOnlyList<SnippetDraftRow> rows)
        {
            var seen = new Dictionary<string, SnippetDraftRow>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                if (IsPlaceholder(row))
                {
                    continue;
                }

                var phrase = Trim(row.Phrase);
                var template = Trim(row.Template);
                var severity = SeverityFor(Unchanged(row));
                if (phrase.Length == 0)
                {
                    Add(
                        ValidationCode.SnippetTriggerEmpty,
                        SettingsPage.VoiceSnippets,
                        "SnippetPhrase",
                        row.RowKey,
                        SnippetTriggerEmptyMessage,
                        severity);

                    continue;
                }

                if (template.Length == 0)
                {
                    Add(
                        ValidationCode.SnippetTextEmpty,
                        SettingsPage.VoiceSnippets,
                        "SnippetTemplate",
                        row.RowKey,
                        SnippetTextEmptyMessage,
                        severity);
                }

                if (seen.TryGetValue(phrase, out var first))
                {
                    Add(
                        ValidationCode.SnippetDuplicate,
                        SettingsPage.VoiceSnippets,
                        "SnippetPhrase",
                        row.RowKey,
                        $"Another snippet already uses \"{phrase}\". Use different words for one of them.",
                        SeverityFor(Unchanged(first) && Unchanged(row)));
                }
                else
                {
                    seen.Add(phrase, row);
                }
            }
        }

        void ValidateProfiles(IReadOnlyList<ProfileDraftRow> rows)
        {
            foreach (var row in rows)
            {
                if (IsPlaceholder(row))
                {
                    continue;
                }

                var severity = SeverityFor(Unchanged(row));
                if (Trim(row.Name).Length == 0)
                {
                    Add(
                        ValidationCode.ProfileNameEmpty,
                        SettingsPage.AppProfiles,
                        "ProfileName",
                        row.RowKey,
                        ProfileNameEmptyMessage,
                        severity);
                }

                if (Trim(row.Apps).Length == 0)
                {
                    Add(
                        ValidationCode.ProfileAppsEmpty,
                        SettingsPage.AppProfiles,
                        "ProfileApps",
                        row.RowKey,
                        ProfileAppsEmptyMessage,
                        severity);
                }
            }
        }

        void ValidateShortcuts(AppSettings settings)
        {
            if (settings.DictationOnlyHotkey is { } second && HotkeyPhysicalBinding.Same(settings.Hotkey, second))
            {
                Add(ValidationCode.ShortcutsIdentical, SettingsPage.Dictation, "DictationOnlyHotkeyBox", null, ShortcutsIdenticalMessage);
            }
        }

        void ValidateProvider(AppSettings settings)
        {
            if (!settings.EnableAiCleanup)
            {
                return;
            }

            switch (settings.AiCleanupProvider)
            {
                case CleanupProvider.AzureFoundry:
                    ValidateAzure(settings);
                    break;
                case CleanupProvider.OpenAiCompatible:
                    if (!IsHttpOrHttps(settings.AiCleanupCustomEndpoint, requireHttps: false))
                    {
                        Add(ValidationCode.CustomEndpointInvalid, SettingsPage.AiCleanup, "CustomEndpointBox", null, CustomEndpointInvalidMessage);
                    }
                    else if (Cleanup.CustomServiceAddress.NamesOldCompletions(settings.AiCleanupCustomEndpoint))
                    {
                        Add(ValidationCode.CustomEndpointInvalid, SettingsPage.AiCleanup, "CustomEndpointBox", null, CustomEndpointOldCompletionsMessage);
                    }

                    if (string.IsNullOrWhiteSpace(settings.AiCleanupCustomModel))
                    {
                        Add(ValidationCode.CustomModelEmpty, SettingsPage.AiCleanup, "CustomModelBox", null, CustomModelEmptyMessage);
                    }

                    break;
            }
        }

        void ValidateAzure(AppSettings settings)
        {
            if (!IsHttpOrHttps(settings.AiCleanupAzureEndpoint, requireHttps: true))
            {
                Add(ValidationCode.FoundryEndpointInvalid, SettingsPage.AiCleanup, "AzureEndpointBox", null, FoundryEndpointInvalidMessage);
            }

            if (string.IsNullOrWhiteSpace(settings.AiCleanupAzureDeployment))
            {
                Add(ValidationCode.DeploymentEmpty, SettingsPage.AiCleanup, "AzureDeploymentBox", null, DeploymentEmptyMessage);
            }

            if (settings.AiCleanupAzureAuthMode != AzureAuthMode.ServicePrincipal ||
                !string.IsNullOrWhiteSpace(settings.AiCleanupAzureApiKey))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(settings.AiCleanupAzureTenantId))
            {
                Add(ValidationCode.TenantEmpty, SettingsPage.AiCleanup, "SpTenantBox", null, TenantEmptyMessage);
            }

            if (string.IsNullOrWhiteSpace(settings.AiCleanupAzureClientId))
            {
                Add(ValidationCode.ClientIdEmpty, SettingsPage.AiCleanup, "SpClientIdBox", null, ClientIdEmptyMessage);
            }

            if (string.IsNullOrWhiteSpace(settings.AiCleanupAzureClientSecret))
            {
                Add(ValidationCode.ClientSecretEmpty, SettingsPage.AiCleanup, "SpSecretBox", null, ClientSecretEmptyMessage);
            }
        }

        void ValidateDurations(IReadOnlyList<DurationDraftField> fields)
        {
            foreach (var field in fields)
            {
                if (field.Value < field.Minimum || field.Value > field.Maximum)
                {
                    Add(
                        ValidationCode.DurationOutOfRange,
                        field.Page,
                        field.ControlName,
                        null,
                        DurationOutOfRangeMessage(field.Minimum, field.Maximum));
                }
            }
        }
    }

    public static string DurationOutOfRangeMessage(int minimum, int maximum) =>
        $"Enter a number from {minimum} to {maximum}.";

    public static bool NeedsDictionaryRemovalConfirmation(DictionaryDraftRow row) =>
        !string.IsNullOrWhiteSpace(row.Pattern) &&
        string.IsNullOrWhiteSpace(row.Replacement) &&
        (row.Origin == DraftRowOrigin.New || !string.IsNullOrWhiteSpace(row.LoadedReplacement));

    public static string DictionaryRemovalTitle(string spoken) =>
        $"Leave \"{spoken}\" out of what you dictate?";

    /// <summary>
    /// Whether a row is an empty placeholder that Save drops without asking: a New row with every field blank, whether or
    /// not the user touched it. A row with any content, typed by the user or added by the app (Learn from history
    /// suggestions, imports, Add to dictionary), is never a placeholder: it is validated and saved or reported.
    /// </summary>
    public static bool IsPlaceholder(DictionaryDraftRow row) =>
        row.Origin == DraftRowOrigin.New && IsBlank(row.Pattern) && IsBlank(row.Replacement);

    /// <inheritdoc cref="IsPlaceholder(DictionaryDraftRow)"/>
    public static bool IsPlaceholder(SnippetDraftRow row) =>
        row.Origin == DraftRowOrigin.New && IsBlank(row.Phrase) && IsBlank(row.Template);

    /// <inheritdoc cref="IsPlaceholder(DictionaryDraftRow)"/>
    public static bool IsPlaceholder(ProfileDraftRow row) =>
        row.Origin == DraftRowOrigin.New &&
        IsBlank(row.Name) &&
        IsBlank(row.Apps) &&
        IsBlank(row.WritingStyle) &&
        row.NewlineHandling is null;

    private static bool IsBlank(string? value) => string.IsNullOrWhiteSpace(value);

    private static string Trim(string? value) => value?.Trim() ?? string.Empty;

    // An invalid row an older build could save is only a warning while it is exactly as it was loaded; a new row, or a
    // saved row edited in any stored field, blocks.
    private static ValidationSeverity SeverityFor(bool unchanged) =>
        unchanged ? ValidationSeverity.Warning : ValidationSeverity.Blocking;

    private static bool Unchanged(DictionaryDraftRow row) =>
        row.Origin == DraftRowOrigin.Saved &&
        IsSame(Trim(row.Pattern), Trim(row.LoadedPattern)) &&
        IsSame(Trim(row.Replacement), Trim(row.LoadedReplacement)) &&
        row.WholeWord == row.LoadedWholeWord &&
        row.Enabled == row.LoadedEnabled;

    /// <summary>
    /// A stored snippet row the user hasn't changed in any stored field (compared as validation compares them, trimmed).
    /// Such a row is only warned about when incomplete, so a Save must keep it exactly as stored.
    /// </summary>
    public static bool IsUnchanged(SnippetDraftRow row) => Unchanged(row);

    private static bool Unchanged(SnippetDraftRow row) =>
        row.Origin == DraftRowOrigin.Saved &&
        IsSame(Trim(row.Phrase), Trim(row.LoadedPhrase)) &&
        IsSame(Trim(row.Template), Trim(row.LoadedTemplate)) &&
        row.Enabled == row.LoadedEnabled;

    private static bool Unchanged(ProfileDraftRow row) =>
        row.Origin == DraftRowOrigin.Saved &&
        IsSame(Trim(row.Name), Trim(row.LoadedName)) &&
        IsSame(Trim(row.Apps), Trim(row.LoadedApps)) &&
        IsSame(Trim(row.WritingStyle), Trim(row.LoadedWritingStyle)) &&
        row.NewlineHandling == row.LoadedNewlineHandling;

    private static bool IsSame(string left, string right) =>
        string.Equals(left, right, StringComparison.Ordinal);

    private static bool IsHttpOrHttps(string? value, bool requireHttps)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return requireHttps
            ? uri.Scheme == Uri.UriSchemeHttps
            : uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
    }
}
