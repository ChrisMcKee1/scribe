using Scribe.Core.Cleanup;
using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

public sealed class SettingsDraftValidatorTests
{
    [Fact]
    public void Untouched_new_rows_are_dropped_silently()
    {
        var issues = SettingsDraftValidator.Validate(new SettingsDraft(
            AppSettings.CreateDefault(),
            DictionaryRows: [new("new", DraftRowOrigin.New, Touched: false, null, null)],
            SnippetRows: [new("snippet", DraftRowOrigin.New, Touched: false, null, null)],
            ProfileRows: [new("profile", DraftRowOrigin.New, Touched: false, null, null)]));

        Assert.Empty(issues);
    }

    [Fact]
    public void Half_entered_new_dictionary_row_reports_the_exact_message()
    {
        var issue = Single(new SettingsDraft(
            AppSettings.CreateDefault(),
            DictionaryRows: [new("new", DraftRowOrigin.New, Touched: true, "", "value")]));

        AssertIssue(issue, ValidationCode.DictionarySpokenEmpty, SettingsPage.Dictionary, "DictionaryPattern", "new",
            "Type the words Scribe hears, or delete this row.");
    }

    [Fact]
    public void Dictionary_duplicates_report_the_spoken_form()
    {
        var issue = Single(new SettingsDraft(
            AppSettings.CreateDefault(),
            DictionaryRows:
            [
                new("a", DraftRowOrigin.Saved, Touched: false, "dot net", ".NET"),
                new("b", DraftRowOrigin.New, Touched: true, " dot net ", ".NET"),
            ]));

        Assert.Equal(ValidationCode.DictionaryDuplicate, issue.Code);
        Assert.Equal("\"dot net\" is already in your dictionary. Keep one of the two rows.", issue.Message);
    }

    [Fact]
    public void Snippet_requires_trigger_and_text_and_checks_duplicates()
    {
        var issues = SettingsDraftValidator.Validate(new SettingsDraft(
            AppSettings.CreateDefault(),
            SnippetRows:
            [
                new("missing-trigger", DraftRowOrigin.New, Touched: true, "", "text"),
                new("missing-text", DraftRowOrigin.New, Touched: true, "sig", ""),
                new("a", DraftRowOrigin.Saved, Touched: false, "hello", "one"),
                new("b", DraftRowOrigin.New, Touched: true, "hello", "two"),
            ]));

        Assert.Contains(issues, issue => issue.Code == ValidationCode.SnippetTriggerEmpty && issue.Message == "Type the words you'll say, or delete this snippet.");
        Assert.Contains(issues, issue => issue.Code == ValidationCode.SnippetTextEmpty && issue.Message == "Type the text this snippet adds, or delete it.");
        Assert.Contains(issues, issue => issue.Code == ValidationCode.SnippetDuplicate && issue.Message == "Another snippet already uses \"hello\". Use different words for one of them.");
    }

    [Fact]
    public void Profile_requires_name_and_apps()
    {
        var issues = SettingsDraftValidator.Validate(new SettingsDraft(
            AppSettings.CreateDefault(),
            ProfileRows:
            [
                new("name", DraftRowOrigin.New, Touched: true, "", "OUTLOOK"),
                new("apps", DraftRowOrigin.New, Touched: true, "Email", ""),
            ]));

        Assert.Contains(issues, issue => issue.Code == ValidationCode.ProfileNameEmpty && issue.Message == "Give this profile a name, or delete it.");
        Assert.Contains(issues, issue => issue.Code == ValidationCode.ProfileAppsEmpty && issue.Message == "Add at least one app to this profile, or delete it.");
    }

    [Fact]
    public void Identical_shortcuts_are_invalid()
    {
        var settings = AppSettings.CreateDefault();
        settings.DictationOnlyHotkey = settings.Hotkey;

        var issue = Single(new SettingsDraft(settings));

        AssertIssue(issue, ValidationCode.ShortcutsIdentical, SettingsPage.Dictation, "DictationOnlyHotkeyBox", null,
            "Both shortcuts use the same key. Choose a different key for one of them.");
    }

    [Fact]
    public void Shortcut_conflict_uses_physical_input_not_mode_or_suppression()
    {
        var settings = AppSettings.CreateDefault();
        settings.Hotkey = new HotkeyBinding(0x70, KeyModifiers.Control, HotkeyMode.Hold, Suppress: true);
        settings.DictationOnlyHotkey = new HotkeyBinding(0x70, KeyModifiers.Control, HotkeyMode.Toggle, Suppress: false);

        var issue = Single(new SettingsDraft(settings));

        Assert.Equal(ValidationCode.ShortcutsIdentical, issue.Code);
    }

    [Fact]
    public void Mouse_button_shortcut_conflict_uses_the_same_physical_comparison()
    {
        var settings = AppSettings.CreateDefault();
        settings.Hotkey = new HotkeyBinding(0x04, KeyModifiers.None, HotkeyMode.Hold, Suppress: true, "Middle mouse button");
        settings.DictationOnlyHotkey = new HotkeyBinding(0x04, KeyModifiers.None, HotkeyMode.Toggle, Suppress: false, "Middle mouse button");
        Assert.Equal(ValidationCode.ShortcutsIdentical, Single(new SettingsDraft(settings)).Code);

        settings.DictationOnlyHotkey = new HotkeyBinding(0x05, KeyModifiers.None, HotkeyMode.Toggle, Suppress: false, "Back mouse button");
        Assert.Empty(SettingsDraftValidator.Validate(new SettingsDraft(settings)));
    }

    [Fact]
    public void Provider_incomplete_is_allowed_when_ai_cleanup_is_off()
    {
        var settings = AppSettings.CreateDefault();
        settings.EnableAiCleanup = false;
        settings.AiCleanupProvider = CleanupProvider.AzureFoundry;

        Assert.Empty(SettingsDraftValidator.Validate(new SettingsDraft(settings)));
    }

    [Fact]
    public void Azure_provider_validates_endpoint_deployment_and_service_principal_fields()
    {
        var settings = AppSettings.CreateDefault();
        settings.EnableAiCleanup = true;
        settings.AiCleanupProvider = CleanupProvider.AzureFoundry;
        settings.AiCleanupAzureEndpoint = "http://example.test";
        settings.AiCleanupAzureAuthMode = AzureAuthMode.ServicePrincipal;

        var issues = SettingsDraftValidator.Validate(new SettingsDraft(settings));

        Assert.Contains(issues, issue => issue.Code == ValidationCode.FoundryEndpointInvalid && issue.Message == "Enter the address of your Microsoft Foundry resource. It starts with https://.");
        Assert.Contains(issues, issue => issue.Code == ValidationCode.DeploymentEmpty && issue.Message == "Enter the name of the model deployment.");
        Assert.Contains(issues, issue => issue.Code == ValidationCode.TenantEmpty && issue.Message == "Enter the tenant ID.");
        Assert.Contains(issues, issue => issue.Code == ValidationCode.ClientIdEmpty && issue.Message == "Enter the client ID.");
        Assert.Contains(issues, issue => issue.Code == ValidationCode.ClientSecretEmpty && issue.Message == "Enter the client secret.");
    }

    [Fact]
    public void Custom_provider_validates_server_address_and_model_name()
    {
        var settings = AppSettings.CreateDefault();
        settings.EnableAiCleanup = true;
        settings.AiCleanupProvider = CleanupProvider.OpenAiCompatible;
        settings.AiCleanupCustomEndpoint = "not a url";

        var issues = SettingsDraftValidator.Validate(new SettingsDraft(settings));

        Assert.Contains(issues, issue => issue.Code == ValidationCode.CustomEndpointInvalid && issue.Message == "Enter the service's address, such as http://localhost:11434/v1.");
        Assert.Contains(issues, issue => issue.Code == ValidationCode.DeploymentEmpty && issue.Message == "Enter the name of the model deployment.");
    }

    [Fact]
    public void Duration_fields_use_the_exact_range_message()
    {
        var issue = Single(new SettingsDraft(
            AppSettings.CreateDefault(),
            DurationFields: [new(SettingsPage.History, "HistoryRetentionCustomBox", 3651, 1, 3650)]));

        AssertIssue(issue, ValidationCode.DurationOutOfRange, SettingsPage.History, "HistoryRetentionCustomBox", null,
            "Enter a number from 1 to 3650.");
    }

    [Fact]
    public void Cleared_saved_replacement_is_a_review_not_a_validation_issue()
    {
        var row = new DictionaryDraftRow(
            "saved",
            DraftRowOrigin.Saved,
            Touched: true,
            Pattern: "um",
            Replacement: "",
            LoadedPattern: "um",
            LoadedReplacement: "UM");

        Assert.Empty(SettingsDraftValidator.Validate(new SettingsDraft(AppSettings.CreateDefault(), DictionaryRows: [row])));
        Assert.True(SettingsDraftValidator.NeedsDictionaryRemovalConfirmation(row));
        Assert.Equal("Leave \"um\" out of what you dictate?", SettingsDraftValidator.DictionaryRemovalTitle("um"));
    }

    [Fact]
    public void New_removal_rule_needs_confirmation_but_placeholder_and_existing_removal_do_not()
    {
        Assert.True(SettingsDraftValidator.NeedsDictionaryRemovalConfirmation(
            new DictionaryDraftRow("new", DraftRowOrigin.New, Touched: true, Pattern: "um", Replacement: "")));
        Assert.False(SettingsDraftValidator.NeedsDictionaryRemovalConfirmation(
            new DictionaryDraftRow("placeholder", DraftRowOrigin.New, Touched: false, Pattern: "", Replacement: "")));
        Assert.False(SettingsDraftValidator.NeedsDictionaryRemovalConfirmation(
            new DictionaryDraftRow("saved", DraftRowOrigin.Saved, Touched: true, Pattern: "um", Replacement: "", LoadedReplacement: "")));
    }

    [Fact]
    public void Unchanged_legacy_invalid_rows_warn_instead_of_blocking()
    {
        var issues = SettingsDraftValidator.Validate(new SettingsDraft(
            AppSettings.CreateDefault(),
            SnippetRows: [new("snippet", DraftRowOrigin.Saved, Touched: false, "legacy", "", LoadedPhrase: "legacy", LoadedTemplate: "")],
            ProfileRows:
            [
                new("name-only", DraftRowOrigin.Saved, Touched: false, "Legacy", "", LoadedName: "Legacy", LoadedApps: ""),
                new("apps-only", DraftRowOrigin.Saved, Touched: false, "", "OUTLOOK", LoadedName: "", LoadedApps: "OUTLOOK"),
            ]));

        Assert.All(issues, issue => Assert.Equal(ValidationSeverity.Warning, issue.Severity));
        Assert.Contains(issues, issue => issue.Code == ValidationCode.SnippetTextEmpty);
        Assert.Contains(issues, issue => issue.Code == ValidationCode.ProfileAppsEmpty);
        Assert.Contains(issues, issue => issue.Code == ValidationCode.ProfileNameEmpty);
    }

    [Fact]
    public void Edited_legacy_invalid_rows_block_save()
    {
        var issue = Single(new SettingsDraft(
            AppSettings.CreateDefault(),
            SnippetRows: [new("snippet", DraftRowOrigin.Saved, Touched: true, "changed", "", LoadedPhrase: "legacy", LoadedTemplate: "")]));

        Assert.Equal(ValidationSeverity.Blocking, issue.Severity);
        Assert.Equal(ValidationCode.SnippetTextEmpty, issue.Code);
    }

    [Fact]
    public void First_issue_stays_first_for_focus()
    {
        var issues = SettingsDraftValidator.Validate(new SettingsDraft(
            AppSettings.CreateDefault(),
            DictionaryRows: [new("first", DraftRowOrigin.New, Touched: true, "", "value")],
            SnippetRows: [new("second", DraftRowOrigin.New, Touched: true, "", "value")]));

        Assert.Equal("first", issues[0].RowKey);
        Assert.Equal(SettingsPage.Dictionary, issues[0].Page);
    }

    private static ValidationIssue Single(SettingsDraft draft) =>
        Assert.Single(SettingsDraftValidator.Validate(draft));

    private static void AssertIssue(
        ValidationIssue issue,
        ValidationCode code,
        SettingsPage page,
        string control,
        string? rowKey,
        string message)
    {
        Assert.Equal(code, issue.Code);
        Assert.Equal(page, issue.Page);
        Assert.Equal(control, issue.ControlName);
        Assert.Equal(rowKey, issue.RowKey);
        Assert.Equal(message, issue.Message);
    }
}
