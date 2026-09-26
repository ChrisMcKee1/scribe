namespace Scribe.Core.Settings;

public enum SettingsSavePreflightStep
{
    ReadyToSave,
    ShowValidation,
    ConfirmRemovalRules,
    ConfirmRetention,
}

public sealed record SettingsSavePreflightDecision(
    SettingsSavePreflightStep Step,
    ValidationIssue? ValidationIssue = null,
    IReadOnlyList<string>? RemovalRules = null,
    HistoryRetentionConfirmation? RetentionConfirmation = null);

public static class SettingsSavePreflight
{
    public static SettingsSavePreflightDecision Decide(
        IReadOnlyList<ValidationIssue> issues,
        IReadOnlyList<string> removalRules,
        HistoryRetentionConfirmation? retentionConfirmation)
    {
        ArgumentNullException.ThrowIfNull(issues);
        ArgumentNullException.ThrowIfNull(removalRules);

        if (issues.FirstOrDefault(issue => issue.Severity == ValidationSeverity.Blocking) is { } blocking)
        {
            return new SettingsSavePreflightDecision(SettingsSavePreflightStep.ShowValidation, ValidationIssue: blocking);
        }

        if (removalRules.Count > 0)
        {
            return new SettingsSavePreflightDecision(SettingsSavePreflightStep.ConfirmRemovalRules, RemovalRules: removalRules);
        }

        return retentionConfirmation is null
            ? new SettingsSavePreflightDecision(SettingsSavePreflightStep.ReadyToSave)
            : new SettingsSavePreflightDecision(SettingsSavePreflightStep.ConfirmRetention, RetentionConfirmation: retentionConfirmation);
    }
}
