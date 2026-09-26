using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

public sealed class AppExitCloseGuardTests
{
    [Theory]
    [InlineData(false, false, AppExitCloseStep.Proceed)]
    [InlineData(true, false, AppExitCloseStep.Settings)]
    [InlineData(false, true, AppExitCloseStep.QuickAdd)]
    [InlineData(true, true, AppExitCloseStep.Settings)]
    public void First_orders_settings_before_quick_add(bool settingsUnsaved, bool quickAddUnsaved, AppExitCloseStep expected)
    {
        Assert.Equal(expected, AppExitCloseGuard.First(new AppExitCloseState(settingsUnsaved, quickAddUnsaved)));
    }

    [Theory]
    [InlineData(AppExitCloseStepResult.Saved)]
    [InlineData(AppExitCloseStepResult.Discarded)]
    public void Settings_success_continues_to_quick_add_when_it_has_a_savable_draft(AppExitCloseStepResult result)
    {
        var state = new AppExitCloseState(SettingsUnsaved: true, QuickAddUnsaved: true);

        Assert.Equal(AppExitCloseStep.QuickAdd, AppExitCloseGuard.Next(AppExitCloseStep.Settings, result, state));
    }

    [Theory]
    [InlineData(AppExitCloseStepResult.Saved)]
    [InlineData(AppExitCloseStepResult.Discarded)]
    public void Settings_success_proceeds_when_quick_add_is_clean(AppExitCloseStepResult result)
    {
        var state = new AppExitCloseState(SettingsUnsaved: true, QuickAddUnsaved: false);

        Assert.Equal(AppExitCloseStep.Proceed, AppExitCloseGuard.Next(AppExitCloseStep.Settings, result, state));
    }

    [Theory]
    [InlineData(AppExitCloseStepResult.Saved)]
    [InlineData(AppExitCloseStepResult.Discarded)]
    public void Quick_add_success_proceeds(AppExitCloseStepResult result)
    {
        var state = new AppExitCloseState(SettingsUnsaved: false, QuickAddUnsaved: true);

        Assert.Equal(AppExitCloseStep.Proceed, AppExitCloseGuard.Next(AppExitCloseStep.QuickAdd, result, state));
    }

    [Theory]
    [InlineData(AppExitCloseStep.Settings, AppExitCloseStepResult.KeepEditing)]
    [InlineData(AppExitCloseStep.Settings, AppExitCloseStepResult.SaveFailed)]
    [InlineData(AppExitCloseStep.QuickAdd, AppExitCloseStepResult.KeepEditing)]
    [InlineData(AppExitCloseStep.QuickAdd, AppExitCloseStepResult.SaveFailed)]
    public void Keep_editing_or_a_failed_save_cancels_the_exit(AppExitCloseStep completed, AppExitCloseStepResult result)
    {
        var state = new AppExitCloseState(SettingsUnsaved: true, QuickAddUnsaved: true);

        Assert.Equal(AppExitCloseStep.Canceled, AppExitCloseGuard.Next(completed, result, state));
    }
}
