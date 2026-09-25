using Scribe.Core.Cleanup;
using Scribe.Core.Models;
using Scribe.Core.Settings;
using Xunit;

namespace Scribe.Core.Tests;

public sealed class SettingsPhaseThreeToSevenRulesTests
{
    [Theory]
    [InlineData(HotkeyMode.Hold, null, false, SilenceStopApplies.None, ShortcutRules.SilenceStopDisabled)]
    [InlineData(HotkeyMode.Hold, HotkeyMode.Hold, false, SilenceStopApplies.None, ShortcutRules.SilenceStopDisabled)]
    [InlineData(HotkeyMode.Hold, HotkeyMode.Toggle, true, SilenceStopApplies.Secondary, ShortcutRules.SilenceStopSecondary)]
    [InlineData(HotkeyMode.Toggle, null, true, SilenceStopApplies.Primary, ShortcutRules.SilenceStopPrimary)]
    [InlineData(HotkeyMode.Toggle, HotkeyMode.Hold, true, SilenceStopApplies.Primary, ShortcutRules.SilenceStopPrimary)]
    [InlineData(HotkeyMode.Toggle, HotkeyMode.Toggle, true, SilenceStopApplies.Both, ShortcutRules.SilenceStopPrimary)]
    public void Silence_stop_follows_toggle_modes(
        HotkeyMode primary,
        HotkeyMode? secondary,
        bool enabled,
        SilenceStopApplies applies,
        string description)
    {
        var state = ShortcutRules.SilenceStop(primary, secondary);

        Assert.Equal(enabled, state.IsEnabled);
        Assert.Equal(applies, state.Applies);
        Assert.Equal(description, state.Description);
    }

    [Fact]
    public void First_run_hint_shows_only_after_empty_history_load()
    {
        var shown = FirstRunHint.ShouldShow(
            SettingsLoadState.Loaded,
            historyCount: 0,
            dismissed: false,
            "Click in any text box, hold Page Down and speak. Let go to type.");

        Assert.True(shown.Show);
        Assert.Equal("Ready to dictate", shown.Title);
        Assert.Equal("Try it here", shown.ActionText);
        Assert.False(FirstRunHint.ShouldShow(SettingsLoadState.Loading, 0, false, "x").Show);
        Assert.False(FirstRunHint.ShouldShow(SettingsLoadState.Loaded, 1, false, "x").Show);
        Assert.False(FirstRunHint.ShouldShow(SettingsLoadState.Loaded, 0, true, "x").Show);
    }

    [Theory]
    [InlineData("OUTLOOK.EXE", "Outlook")]
    [InlineData("ms-teams", "Teams")]
    [InlineData("WINWORD", "Word")]
    [InlineData("olk", "Outlook")]
    [InlineData("unknown-app.exe", "unknown-app")]
    public void App_display_names_are_friendly_when_known(string process, string expected) =>
        Assert.Equal(expected, AppDisplayName.For(process));

    [Fact]
    public void Ai_cleanup_off_rows_cover_complete_incomplete_and_empty_setup()
    {
        var empty = AiCleanupPageState.Describe(
            false,
            CleanupProvider.FoundryLocal,
            CleanupProvider.FoundryLocal,
            CleanupStatus.Disabled,
            draftComplete: false);
        Assert.False(empty.ShowProviderSetup);
        Assert.Equal("Off. Scribe types what it hears, with your dictionary and snippets.", empty.StatusLine);
        Assert.Equal("Turn on AI cleanup to choose where it runs and set your writing style.", empty.OffHelperText);

        var complete = AiCleanupPageState.Describe(
            false,
            CleanupProvider.AzureFoundry,
            CleanupProvider.AzureFoundry,
            CleanupStatus.Ready,
            draftComplete: true,
            AiCleanupSetupState.Complete,
            "Microsoft Foundry (gpt-4o)");
        Assert.Equal("Set up to use Microsoft Foundry (gpt-4o).", complete.OffHelperText);

        var incomplete = AiCleanupPageState.Describe(
            false,
            CleanupProvider.AzureFoundry,
            CleanupProvider.AzureFoundry,
            CleanupStatus.Disabled,
            draftComplete: false,
            AiCleanupSetupState.Incomplete);
        Assert.Equal("Partly set up for Microsoft Foundry. Turn on AI cleanup to finish setting it up.", incomplete.StatusLine);
    }

    [Theory]
    [InlineData(CleanupStatus.Disabled, "On, but not set up yet. Until it's ready, Scribe types what it hears.", AiCleanupStatusKind.Warning, "Set up")]
    [InlineData(CleanupStatus.Initializing, "On. Getting ready...", AiCleanupStatusKind.Busy, null)]
    [InlineData(CleanupStatus.Downloading, "On. Getting ready...", AiCleanupStatusKind.Busy, null)]
    [InlineData(CleanupStatus.Ready, "On. Using Qwen3 1.7B on this PC.", AiCleanupStatusKind.Success, "Unload")]
    [InlineData(CleanupStatus.Unavailable, "On, but not ready. Until it's ready, Scribe types what it hears.", AiCleanupStatusKind.Error, "Try again")]
    public void Ai_cleanup_foundry_state_table(CleanupStatus status, string line, AiCleanupStatusKind kind, string? action)
    {
        var description = AiCleanupPageState.Describe(
            true,
            CleanupProvider.FoundryLocal,
            CleanupProvider.FoundryLocal,
            status,
            draftComplete: true,
            modelName: "Qwen3 1.7B");

        Assert.True(description.ShowProviderSetup);
        Assert.Equal(line, description.StatusLine);
        Assert.NotNull(description.StatusRow);
        Assert.Equal(kind, description.StatusRow!.Kind);
        Assert.Equal(action, description.StatusRow.ActionText);
    }

    [Theory]
    [InlineData(FoundryLocalSetupStage.NotSetUp, AiCleanupStatusKind.Warning, "Set up", false)]
    [InlineData(FoundryLocalSetupStage.SettingUp, AiCleanupStatusKind.Busy, null, false)]
    [InlineData(FoundryLocalSetupStage.RuntimeReady, AiCleanupStatusKind.Info, "Download and load", false)]
    [InlineData(FoundryLocalSetupStage.DownloadingOrLoading, AiCleanupStatusKind.Busy, null, false)]
    [InlineData(FoundryLocalSetupStage.Loaded, AiCleanupStatusKind.Success, "Unload", true)]
    [InlineData(FoundryLocalSetupStage.Failed, AiCleanupStatusKind.Error, "Try again", false)]
    public void Foundry_setup_describes_one_next_action(
        FoundryLocalSetupStage stage,
        AiCleanupStatusKind kind,
        string? action,
        bool canUnload)
    {
        var state = FoundryLocalSetup.Describe(stage, "Qwen3 1.7B", "about 1.3 GB");

        Assert.Equal(kind, state.Kind);
        Assert.Equal(action, state.ActionText);
        Assert.Equal(canUnload, state.CanUnload);
    }

    [Fact]
    public void Foundry_model_choices_use_catalog_facts_only()
    {
        var choices = FoundryModelChoices.Build(
            "mistral-nemo-12b-instruct",
            CleanupModelCatalog.Curated,
            [new FoundryModelOption("qwen3-1.7b", Cached: true, Loaded: false)]);

        Assert.Contains(choices, choice =>
            choice.Alias == "qwen3-1.7b" &&
            choice.Label == "Qwen3 1.7B, about 1.3 GB, recommended, downloaded" &&
            choice.Hint == "About 1.3 GB. Scribe's recommended default.");
        Assert.Contains(choices, choice =>
            choice.Alias == "mistral-nemo-12b-instruct" &&
            choice.Label == "Mistral NeMo 12B, about 7 GB, large download" &&
            choice.IsSelected);
    }

    [Fact]
    public void Text_filter_is_case_and_accent_insensitive()
    {
        Assert.True(TextFilter.Matches("resume", "Résumé for Contoso"));
        Assert.True(TextFilter.Matches("TEAMS", "Microsoft Teams"));
        Assert.False(TextFilter.Matches("slack", "Microsoft Teams"));
    }

    [Fact]
    public void Program_names_strip_exe_and_deduplicate()
    {
        Assert.Equal(["OUTLOOK", "ms-teams"], ProgramNames.Normalize([" OUTLOOK.exe ", "outlook", "ms-teams.exe"]));
    }

    [Fact]
    public void Recent_apps_count_history_by_normalized_program()
    {
        var recent = RecentApps.From(
            [
                Entry("OUTLOOK.exe"),
                Entry("outlook"),
                Entry("WINWORD"),
            ]);

        Assert.Collection(
            recent,
            app =>
            {
                Assert.Equal("OUTLOOK", app.ProcessName);
                Assert.Equal("Outlook", app.DisplayName);
                Assert.Equal(2, app.DictationCount);
            },
            app =>
            {
                Assert.Equal("WINWORD", app.ProcessName);
                Assert.Equal("Word", app.DisplayName);
                Assert.Equal(1, app.DictationCount);
            });
    }

    [Fact]
    public void Usage_period_state_keeps_old_period_while_loading_or_failed()
    {
        Assert.Equal("Showing Last 7 days. Loading Last 30 days...", UsagePeriodState.Describe(UsagePeriod.Last7Days, UsagePeriod.Last30Days, false).StatusText);
        var failed = UsagePeriodState.Describe(UsagePeriod.Last90Days, null, true);
        Assert.Equal("Showing Last 90 days. Usage isn't available right now.", failed.StatusText);
        Assert.True(failed.ShowRetry);
    }

    [Fact]
    public void Notices_for_text_changes_profiles_and_usage_insights_are_core_owned()
    {
        Assert.Equal(TextChangesNotice.Message, TextChangesNotice.Describe(false).Message);
        Assert.False(TextChangesNotice.Describe(true).Show);

        var profile = ProfileRules.Describe(aiCleanupEnabled: false, profileCount: 2);
        Assert.True(profile.ShowAiCleanupNotice);
        Assert.True(profile.ShowFirstMatchHint);
        Assert.Equal("Writing styles are used only when AI cleanup is on.", profile.NoticeText);

        var insight = UsageInsightAvailability.Describe(true, cleanupReady: false, CleanupProvider.GitHubCopilot);
        Assert.True(insight.IsVisible);
        Assert.False(insight.IsEnabled);
        Assert.Equal("AI cleanup isn't ready yet.", insight.DisabledReason);
        Assert.Contains("GitHub Copilot", insight.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Try_dictation_sample_uses_dictionary_spoken_form_but_not_private_text()
    {
        var samples = TryDictationSample.For([DictionaryEntry.New("dot net", ".NET")]);

        Assert.Equal("Not sure what to say? Try: \"This is a test of Scribe on my PC.\"", samples[0]);
        Assert.Equal("Or try: \"Please book a meeting about dot net.\"", samples[1]);
        Assert.DoesNotContain(".NET", samples[1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(InjectionMethod.UnicodeType, false, "Typed as keystrokes")]
    [InlineData(InjectionMethod.ClipboardPaste, true, "Pasted, then a space")]
    public void Injection_method_labels_hide_internal_codes(InjectionMethod method, bool space, string expected) =>
        Assert.Equal(expected, InjectionMethodLabel.Describe(method, space));

    [Fact]
    public void Advanced_defaults_count_changed_settings_by_section_and_reset_only_advanced_fields()
    {
        var settings = AppSettings.CreateDefault();
        settings.InputDeviceName = "Keep me";
        settings.DecodeThreads = 4;
        settings.InjectionMethod = InjectionMethod.ClipboardPaste;
        settings.ShiftEnterLineBreaks = false;

        Assert.Equal(1, AdvancedDefaults.ChangedCount(AdvancedSection.SpeechRecognition, settings));
        Assert.Equal(2, AdvancedDefaults.ChangedCount(AdvancedSection.TypingIntoApps, settings));
        Assert.Equal("2 changed", AdvancedDefaults.SectionHeader(AdvancedSection.TypingIntoApps, settings));

        AdvancedDefaults.ApplyTo(settings);

        Assert.Equal("Keep me", settings.InputDeviceName);
        Assert.Equal(0, settings.DecodeThreads);
        Assert.Equal(InjectionMethod.UnicodeType, settings.InjectionMethod);
        Assert.True(settings.ShiftEnterLineBreaks);
    }

    private static HistoryEntry Entry(string app) =>
        new(0, DateTimeOffset.UtcNow, "text", 1000, 100, TargetApp: app);
}
