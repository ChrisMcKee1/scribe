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
    DeploymentEmpty,
    TenantEmpty,
    ClientIdEmpty,
    ClientSecretEmpty,
    DurationOutOfRange,
}

public sealed record ValidationIssue(
    ValidationCode Code,
    SettingsPage Page,
    string ControlName,
    string? RowKey,
    string Message);

public sealed record DictionaryDraftRow(
    string RowKey,
    DraftRowOrigin Origin,
    bool Touched,
    string? Pattern,
    string? Replacement,
    string? LoadedPattern = null,
    string? LoadedReplacement = null,
    bool WholeWord = true,
    bool Enabled = true);

public sealed record SnippetDraftRow(
    string RowKey,
    DraftRowOrigin Origin,
    bool Touched,
    string? Phrase,
    string? Template,
    string? LoadedPhrase = null,
    string? LoadedTemplate = null,
    bool Enabled = true);

public sealed record ProfileDraftRow(
    string RowKey,
    DraftRowOrigin Origin,
    bool Touched,
    string? Name,
    string? Apps,
    string? LoadedName = null,
    string? LoadedApps = null);

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

        void Add(ValidationCode code, SettingsPage page, string control, string? rowKey, string message) =>
            issues.Add(new ValidationIssue(code, page, control, rowKey, message));

        void ValidateDictionary(IReadOnlyList<DictionaryDraftRow> rows)
        {
            var seen = new Dictionary<string, DictionaryDraftRow>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                if (ShouldIgnoreUntouchedNewRow(row.Origin, row.Touched, row.Pattern, row.Replacement))
                {
                    continue;
                }

                var pattern = Trim(row.Pattern);
                if (pattern.Length == 0)
                {
                    if (row.Touched || row.Origin == DraftRowOrigin.New)
                    {
                        Add(ValidationCode.DictionarySpokenEmpty, SettingsPage.Dictionary, "DictionaryPattern", row.RowKey, DictionarySpokenEmptyMessage);
                    }

                    continue;
                }

                if (seen.TryGetValue(pattern, out var first))
                {
                    if (row.Touched || first.Touched || row.Origin == DraftRowOrigin.New || first.Origin == DraftRowOrigin.New)
                    {
                        Add(
                            ValidationCode.DictionaryDuplicate,
                            SettingsPage.Dictionary,
                            "DictionaryPattern",
                            row.RowKey,
                            $"\"{pattern}\" is already in your dictionary. Keep one of the two rows.");
                    }
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
                if (ShouldIgnoreUntouchedNewRow(row.Origin, row.Touched, row.Phrase, row.Template))
                {
                    continue;
                }

                var phrase = Trim(row.Phrase);
                var template = Trim(row.Template);
                if (phrase.Length == 0)
                {
                    if (row.Touched || template.Length > 0)
                    {
                        Add(ValidationCode.SnippetTriggerEmpty, SettingsPage.VoiceSnippets, "SnippetPhrase", row.RowKey, SnippetTriggerEmptyMessage);
                    }

                    continue;
                }

                if (template.Length == 0)
                {
                    if (row.Touched || phrase.Length > 0)
                    {
                        Add(ValidationCode.SnippetTextEmpty, SettingsPage.VoiceSnippets, "SnippetTemplate", row.RowKey, SnippetTextEmptyMessage);
                    }
                }

                if (seen.TryGetValue(phrase, out var first))
                {
                    if (row.Touched || first.Touched || row.Origin == DraftRowOrigin.New || first.Origin == DraftRowOrigin.New)
                    {
                        Add(
                            ValidationCode.SnippetDuplicate,
                            SettingsPage.VoiceSnippets,
                            "SnippetPhrase",
                            row.RowKey,
                            $"Another snippet already uses \"{phrase}\". Use different words for one of them.");
                    }
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
                if (ShouldIgnoreUntouchedNewRow(row.Origin, row.Touched, row.Name, row.Apps))
                {
                    continue;
                }

                var name = Trim(row.Name);
                var apps = Trim(row.Apps);
                if (name.Length == 0 && (row.Touched || apps.Length > 0))
                {
                    Add(ValidationCode.ProfileNameEmpty, SettingsPage.AppProfiles, "ProfileName", row.RowKey, ProfileNameEmptyMessage);
                }

                if (apps.Length == 0 && (row.Touched || name.Length > 0))
                {
                    Add(ValidationCode.ProfileAppsEmpty, SettingsPage.AppProfiles, "ProfileApps", row.RowKey, ProfileAppsEmptyMessage);
                }
            }
        }

        void ValidateShortcuts(AppSettings settings)
        {
            if (settings.DictationOnlyHotkey is { } second && settings.Hotkey.SameKeysAndBehavior(second))
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

                    if (string.IsNullOrWhiteSpace(settings.AiCleanupCustomModel))
                    {
                        Add(ValidationCode.DeploymentEmpty, SettingsPage.AiCleanup, "CustomModelBox", null, DeploymentEmptyMessage);
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
        row.Origin == DraftRowOrigin.Saved &&
        row.Touched &&
        !string.IsNullOrWhiteSpace(row.LoadedReplacement) &&
        string.IsNullOrWhiteSpace(row.Replacement);

    public static string DictionaryRemovalTitle(string spoken) =>
        $"Leave \"{spoken}\" out of what you dictate?";

    private static bool ShouldIgnoreUntouchedNewRow(
        DraftRowOrigin origin,
        bool touched,
        string? first,
        string? second) =>
        origin == DraftRowOrigin.New &&
        !touched &&
        string.IsNullOrWhiteSpace(first) &&
        string.IsNullOrWhiteSpace(second);

    private static string Trim(string? value) => value?.Trim() ?? string.Empty;

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
