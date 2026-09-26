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

    [Fact]
    public void Settings_discard_rechecks_quick_add_after_the_settings_prompt_resolves()
    {
        var recomputedAfterSettings = new AppExitCloseState(SettingsUnsaved: false, QuickAddUnsaved: true);

        Assert.Equal(
            AppExitCloseStep.QuickAdd,
            AppExitCloseGuard.Next(AppExitCloseStep.Settings, AppExitCloseStepResult.Discarded, recomputedAfterSettings));
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

    [Fact]
    public void App_source_coalesces_repeated_exit_requests_before_showing_ui()
    {
        var source = File.ReadAllText(FindRepoFile("src", "Scribe.App", "App.xaml.cs"));

        Assert.Contains("private Task? _appExitOperation;", source, StringComparison.Ordinal);
        Assert.Contains("if (_appExitOperation is { IsCompleted: false } existing)", source, StringComparison.Ordinal);
        Assert.Contains("return existing;", source, StringComparison.Ordinal);

        // Reserved before the operation runs: its prompts are modal and pump messages, so a request made during one must
        // find the reservation already there.
        var reserve = source.IndexOf("_appExitOperation = reservation.Task;", StringComparison.Ordinal);
        var run = source.IndexOf("_ = RunReservedAsync();", StringComparison.Ordinal);
        Assert.True(reserve >= 0 && run > reserve, "The exit is reserved before its operation starts.");
    }

    [Fact]
    public void About_s_update_judges_Add_to_dictionary_against_the_stored_dictionary_after_a_discard()
    {
        // With Discard chosen, Settings stays open to own the update's UI; its draft must no longer block a correction.
        var app = File.ReadAllText(FindRepoFile("src", "Scribe.App", "App.xaml.cs"));
        Assert.Contains("_settingsWindow is { IsDraftDiscardedForAppExit: false } window ? window : null;", app, StringComparison.Ordinal);
        Assert.Contains("liveSettings.IsDraftDiscardedForAppExit = false;", app, StringComparison.Ordinal);

        var close = File.ReadAllText(FindRepoFile("src", "Scribe.App", "Settings", "SettingsWindow.AppClose.cs"));
        Assert.Contains("IsDraftDiscardedForAppExit = true;", close, StringComparison.Ordinal);

        // A Save and close that finished during the wait leaves no owner for the update's UI.
        var wait = close[close.IndexOf("while (_saveInProgress)", StringComparison.Ordinal)..];
        wait = wait[..wait.IndexOf("CommitPendingGridEdits();", StringComparison.Ordinal)];
        Assert.Contains("return SettingsUpdateRestartGuardResult.Canceled;", wait, StringComparison.Ordinal);
    }

    [Fact]
    public void App_source_routes_active_settings_saves_through_the_close_entry()
    {
        var source = File.ReadAllText(FindRepoFile("src", "Scribe.App", "App.xaml.cs"));

        Assert.Contains("includeSettingsSave && _settingsWindow?.HasAppCloseSaveInProgress == true", source, StringComparison.Ordinal);
        Assert.Contains("await _settingsWindow.RequestAppCloseAsync(trigger)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_source_lets_update_restart_use_the_full_dirty_tracker()
    {
        var source = File.ReadAllText(FindRepoFile("src", "Scribe.App", "Settings", "SettingsWindow.Closing.cs"));

        Assert.Contains("trigger is CloseTrigger.SignOut or CloseTrigger.Shutdown", source, StringComparison.Ordinal);
        Assert.DoesNotContain("trigger is CloseTrigger.UpdateRestart or CloseTrigger.SignOut or CloseTrigger.Shutdown", source, StringComparison.Ordinal);
    }

    private static string FindRepoFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not find repository file.", Path.Combine(parts));
    }
}
