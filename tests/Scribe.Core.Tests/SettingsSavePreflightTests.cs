using Scribe.Core.Settings;

using Scribe.Core.Models;

namespace Scribe.Core.Tests;

public sealed class SettingsSavePreflightTests

{
    [Fact]

    public void Blocking_validation_precedes_destructive_confirmations()

    {

        var issue = new ValidationIssue(

            ValidationCode.ShortcutsIdentical,

            SettingsPage.Dictation,

            "Hotkey",

            null,

            SettingsDraftValidator.ShortcutsIdenticalMessage);

        var retention = HistoryRetentionChange.Describe(90, 7);

        var decision = SettingsSavePreflight.Decide([issue], ["delete me"], retention);

        Assert.Equal(SettingsSavePreflightStep.ShowValidation, decision.Step);

        Assert.Same(issue, decision.ValidationIssue);

    }

    [Fact]

    public void Removal_rules_precede_retention_confirmation()

    {

        var retention = HistoryRetentionChange.Describe(90, 7);

        var decision = SettingsSavePreflight.Decide([], ["delete me"], retention);

        Assert.Equal(SettingsSavePreflightStep.ConfirmRemovalRules, decision.Step);

        Assert.Equal(["delete me"], decision.RemovalRules);

    }

    [Fact]

    public void Retention_confirmation_is_last_before_save()

    {

        var retention = HistoryRetentionChange.Describe(90, 7)!;

        var decision = SettingsSavePreflight.Decide([], [], retention);

        Assert.Equal(SettingsSavePreflightStep.ConfirmRetention, decision.Step);

        Assert.Same(retention, decision.RetentionConfirmation);

    }

    [Fact]

    public void Clean_preflight_is_ready_to_save()

    {

        var decision = SettingsSavePreflight.Decide([], [], null);

        Assert.Equal(SettingsSavePreflightStep.ReadyToSave, decision.Step);

    }

    [Fact]
    public void Fractional_custom_duration_is_blocking_before_save()
    {
        var draft = new SettingsDraft(
            AppSettings.CreateDefault(),
            DurationFields: [new DurationDraftField(SettingsPage.History, "HistoryRetentionCustomBox", int.MinValue, 1, 3650)]);

        var issue = Assert.Single(SettingsDraftValidator.Validate(draft));

        Assert.Equal(ValidationCode.DurationOutOfRange, issue.Code);
        Assert.Equal("Enter a number from 1 to 3650.", issue.Message);
    }
}
