using Scribe.Core.Cleanup;
using Scribe.Core.Diagnostics;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;
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
    [InlineData("olk", "New Outlook")]
    [InlineData("unknown-app.exe", "unknown-app")]
    public void App_display_names_are_friendly_when_known(string process, string expected) =>
        Assert.Equal(expected, AppDisplayName.For(process));

    [Theory]
    [InlineData("OUTLOOK", "outlook", "Outlook")]
    [InlineData("olk", "new-outlook", "New Outlook")]
    [InlineData("ms-teams", "teams", "Teams")]
    [InlineData("Teams", "teams", "Teams")]
    [InlineData("WindowsTerminal", "terminal", "Terminal")]
    [InlineData("wt", "terminal", "Terminal")]
    public void App_display_groups_keep_distinct_program_groups(string process, string key, string display)
    {
        Assert.Equal(key, AppDisplayName.GroupKeyFor(process));
        Assert.Equal(display, AppDisplayName.For(process));
    }

    [Fact]
    public void Ai_cleanup_off_rows_cover_complete_incomplete_and_empty_setup()
    {
        var saved = AppSettings.CreateDefault();
        var draft = saved.Clone();
        draft.EnableAiCleanup = false;
        var empty = AiCleanupPageState.Describe(saved, draft, CleanupStatus.Disabled);
        Assert.False(empty.ShowProviderSetup);
        Assert.Equal("Off. Scribe types what it hears, with your dictionary and snippets.", empty.StatusLine);
        Assert.Equal("Turn on AI cleanup to choose where it runs and set your writing style.", empty.OffHelperText);

        saved.AiCleanupProvider = CleanupProvider.AzureFoundry;
        saved.AiCleanupAzureEndpoint = "https://example.test";
        saved.AiCleanupAzureDeployment = "gpt-4o";
        var completeDraft = saved.Clone();
        completeDraft.EnableAiCleanup = false;
        var complete = AiCleanupPageState.Describe(
            saved,
            completeDraft,
            CleanupStatus.Ready,
            savedSetupState: AiCleanupPageState.SavedSetupState(saved),
            providerSummary: AiCleanupPageState.ProviderSetupSummary(saved));
        Assert.Equal("Set up to use Microsoft Foundry (gpt-4o).", complete.OffHelperText);

        saved.AiCleanupAzureEndpoint = null;
        saved.EnableAiCleanup = true;
        var incompleteDraft = saved.Clone();
        incompleteDraft.EnableAiCleanup = false;
        var incomplete = AiCleanupPageState.Describe(
            saved,
            incompleteDraft,
            CleanupStatus.Disabled,
            savedSetupState: AiCleanupSetupState.Incomplete,
            providerSummary: "Microsoft Foundry");
        Assert.Equal("Partly set up for Microsoft Foundry. Turn on AI cleanup to finish setting it up.", incomplete.StatusLine);
    }

    [Fact]
    public void Ai_cleanup_setup_summary_and_completeness_are_core_owned()
    {
        var settings = AppSettings.CreateDefault();
        settings.AiCleanupProvider = CleanupProvider.AzureFoundry;
        settings.AiCleanupAzureEndpoint = "https://example.test";
        settings.AiCleanupAzureDeployment = "gpt-4o";

        Assert.True(AiCleanupPageState.HasProviderConfiguration(settings, CleanupProvider.AzureFoundry));
        Assert.Equal(AiCleanupSetupState.Complete, AiCleanupPageState.SavedSetupState(settings));
        Assert.Equal("Microsoft Foundry (gpt-4o)", AiCleanupPageState.ProviderSetupSummary(settings));

        settings.AiCleanupAzureDeployment = null;
        Assert.False(AiCleanupPageState.HasProviderConfiguration(settings, CleanupProvider.AzureFoundry));
        Assert.Equal(AiCleanupSetupState.NothingConfigured, AiCleanupPageState.SavedSetupState(settings));
    }

    [Theory]
    [InlineData(CleanupStatus.Disabled, "On, but not set up yet. Until it's ready, Scribe types what it hears.", AiCleanupStatusKind.Warning, "Set up")]
    [InlineData(CleanupStatus.Initializing, "On. Getting ready...", AiCleanupStatusKind.Busy, null)]
    [InlineData(CleanupStatus.Downloading, "On. Getting ready...", AiCleanupStatusKind.Busy, null)]
    [InlineData(CleanupStatus.Ready, "On. Using Qwen3 1.7B on this PC.", AiCleanupStatusKind.Success, "Free memory")]
    [InlineData(CleanupStatus.Unavailable, "On, but not ready. Until it's ready, Scribe types what it hears.", AiCleanupStatusKind.Error, "Try again")]
    public void Ai_cleanup_foundry_state_table(CleanupStatus status, string line, AiCleanupStatusKind kind, string? action)
    {
        var saved = AppSettings.CreateDefault();
        saved.EnableAiCleanup = true;
        saved.AiCleanupProvider = CleanupProvider.FoundryLocal;
        saved.AiCleanupModel = "qwen3-1.7b";
        var setup = FoundryLocalSetup.Describe(
            FoundryLocalSetup.FromCleanupStatus(status, runtimeReady: status != CleanupStatus.Disabled, modelCached: status == CleanupStatus.Ready, modelLoaded: status == CleanupStatus.Ready),
            "Qwen3 1.7B",
            "about 1.3 GB");
        var description = AiCleanupPageState.Describe(
            saved,
            saved.Clone(),
            status,
            foundrySetup: setup,
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
    [InlineData(FoundryLocalSetupStage.CachedUnloaded, AiCleanupStatusKind.Info, "Load", false)]
    [InlineData(FoundryLocalSetupStage.Checking, AiCleanupStatusKind.Info, null, false)]
    [InlineData(FoundryLocalSetupStage.DownloadingOrLoading, AiCleanupStatusKind.Busy, null, false)]
    [InlineData(FoundryLocalSetupStage.Loaded, AiCleanupStatusKind.Success, "Free memory", true)]
    [InlineData(FoundryLocalSetupStage.Failed, AiCleanupStatusKind.Error, "Try again", false)]
    [InlineData(FoundryLocalSetupStage.ModelFailed, AiCleanupStatusKind.Error, "Try again", false)]
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
        Assert.Equal(stage, state.Stage);
    }

    [Theory]
    [InlineData(CleanupStatus.Disabled, true, false, false, FoundryLocalSetupStage.RuntimeReady)]
    [InlineData(CleanupStatus.Disabled, true, true, false, FoundryLocalSetupStage.CachedUnloaded)]
    [InlineData(CleanupStatus.Disabled, true, true, null, FoundryLocalSetupStage.Checking)]
    [InlineData(CleanupStatus.Disabled, true, true, true, FoundryLocalSetupStage.Loaded)]
    [InlineData(CleanupStatus.Unavailable, true, true, false, FoundryLocalSetupStage.CachedUnloaded)]
    [InlineData(CleanupStatus.Unavailable, true, false, null, FoundryLocalSetupStage.ModelFailed)]
    [InlineData(CleanupStatus.Unavailable, true, false, false, FoundryLocalSetupStage.ModelFailed)]
    [InlineData(CleanupStatus.Unavailable, false, false, null, FoundryLocalSetupStage.Failed)]
    [InlineData(CleanupStatus.Unavailable, false, true, null, FoundryLocalSetupStage.Failed)]
    [InlineData(CleanupStatus.Unavailable, true, true, null, FoundryLocalSetupStage.Checking)]
    [InlineData(CleanupStatus.Downloading, true, true, false, FoundryLocalSetupStage.DownloadingOrLoading)]
    [InlineData(CleanupStatus.Downloading, true, true, null, FoundryLocalSetupStage.DownloadingOrLoading)]
    [InlineData(CleanupStatus.Initializing, false, false, null, FoundryLocalSetupStage.SettingUp)]
    [InlineData(CleanupStatus.Ready, true, true, null, FoundryLocalSetupStage.Loaded)]
    [InlineData(CleanupStatus.Disabled, false, false, false, FoundryLocalSetupStage.NotSetUp)]
    public void Foundry_setup_distinguishes_cached_and_unknown_residency(
        CleanupStatus status,
        bool runtimeReady,
        bool modelCached,
        bool? modelLoaded,
        FoundryLocalSetupStage expected)
    {
        Assert.Equal(expected, FoundryLocalSetup.FromCleanupStatus(status, runtimeReady, modelCached, modelLoaded));
    }

    [Fact]
    public void Foundry_model_choices_use_catalog_facts_only()
    {
        var choices = FoundryModelChoices.Build(
            "phi-4-mini",
            CleanupModelCatalog.Curated,
            [
                new FoundryModelOption("qwen2.5-1.5b", Cached: true, Loaded: false),
                new FoundryModelOption("qwen3-1.7b", Cached: true, Loaded: false),
            ]);

        Assert.Contains(choices, choice =>
            choice.Alias == "qwen2.5-1.5b" &&
            choice.Label == "Qwen2.5 1.5B, about 1.5 GB, downloaded" &&
            choice.Hint == "About 1.5 GB. Quick on any PC, with or without a graphics card.");
        Assert.Contains(choices, choice =>
            choice.Alias == "qwen3-1.7b" &&
            choice.Label == "Qwen3 1.7B, about 1.3 GB, downloaded" &&
            choice.Hint == "About 1.3 GB. Scribe's default before version 0.5.2.");
        Assert.Contains(choices, choice =>
            choice.Alias == "phi-4-mini" &&
            choice.Label == "Phi-4 Mini, about 3.7 GB" &&
            choice.IsSelected);

        // Settings recommends no model: the benchmark ranks them (docs/local-model-benchmark.md).
        Assert.DoesNotContain(choices, choice =>
            choice.Label.Contains("recommend", StringComparison.OrdinalIgnoreCase) ||
            choice.Hint.Contains("recommend", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_model_that_left_the_curated_list_stays_selectable_from_the_catalog()
    {
        // Mistral NeMo 12B and Phi-4 were curated until 0.5.1; a saved choice of either keeps working.
        var choices = FoundryModelChoices.Build(
            "mistral-nemo-12b-instruct",
            CleanupModelCatalog.Curated,
            [new FoundryModelOption("mistral-nemo-12b-instruct", Cached: true, Loaded: true)]);

        var selected = Assert.Single(choices, choice => choice.IsSelected);
        Assert.Equal("mistral-nemo-12b-instruct", selected.Alias);
        Assert.Equal("mistral-nemo-12b-instruct (downloaded)", selected.Label);
        Assert.True(selected.IsLoaded);
    }

    [Fact]
    public void Foundry_model_choices_keep_a_saved_alias_outside_the_catalog()
    {
        var choices = FoundryModelChoices.Build("team-custom-model", CleanupModelCatalog.Curated, []);

        var selected = Assert.Single(choices, choice => choice.IsSelected);
        Assert.Equal("team-custom-model", selected.Alias);
        Assert.Equal("team-custom-model", selected.Label);
        Assert.Equal("Custom Foundry Local model.", selected.Hint);
    }

    [Theory]
    [InlineData(FoundryLocalSetupStage.NotSetUp, AiCleanupStatusKind.Warning, "Set up")]
    [InlineData(FoundryLocalSetupStage.RuntimeReady, AiCleanupStatusKind.Info, "Download and load")]
    [InlineData(FoundryLocalSetupStage.CachedUnloaded, AiCleanupStatusKind.Info, "Load")]
    [InlineData(FoundryLocalSetupStage.ModelFailed, AiCleanupStatusKind.Error, "Try again")]
    public void Foundry_setup_rows_distinguish_setup_download_cache_and_failure(FoundryLocalSetupStage stage, AiCleanupStatusKind kind, string action)
    {
        var setup = FoundryLocalSetup.Describe(stage, "Qwen3 1.7B", "about 1.3 GB");

        Assert.Equal(kind, setup.Kind);
        Assert.Equal(action, setup.ActionText);
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
    public void Snippet_list_text_shows_off_state_and_new_placeholder()
    {
        Assert.Equal(new SnippetListItemText("New snippet", null), SnippetListText.Describe(" ", enabled: true));
        Assert.Equal(new SnippetListItemText("insert signature", "Off"), SnippetListText.Describe(" insert signature ", enabled: false));
    }

    [Fact]
    public void Profile_list_text_and_chips_hide_process_jargon_until_needed()
    {
        var description = ProfileListText.Describe(" Email ", "OUTLOOK.exe, ms-teams, Teams, olk");

        Assert.Equal("Email", description.Primary);
        Assert.Equal("Outlook, Teams, New Outlook", description.Secondary);

        var chips = ProfileAppChips.FromProgramNames("OUTLOOK.exe, ms-teams, Teams, olk");
        Assert.Collection(
            chips,
            chip =>
            {
                Assert.Equal("outlook", chip.GroupKey);
                Assert.Equal("Outlook", chip.DisplayName);
                Assert.Equal(["OUTLOOK"], chip.ProgramNames);
                Assert.Equal("Remove Outlook", chip.RemoveName);
            },
            chip =>
            {
                Assert.Equal("teams", chip.GroupKey);
                Assert.Equal("Teams", chip.DisplayName);
                Assert.Equal(["ms-teams", "Teams"], chip.ProgramNames);
            },
            chip =>
            {
                Assert.Equal("new-outlook", chip.GroupKey);
                Assert.Equal("New Outlook", chip.DisplayName);
                Assert.Equal(["olk"], chip.ProgramNames);
            });
        Assert.Equal("OUTLOOK, ms-teams, Teams, olk", ProfileAppChips.ToProgramNames(chips));
        Assert.Equal("OUTLOOK, olk", ProfileAppChips.RemoveGroup("OUTLOOK, ms-teams, Teams, olk", "teams"));
    }

    [Fact]
    public void App_picker_options_put_running_apps_first_and_deduplicate_selected_apps()
    {
        var options = AppPickerOptions.Build(
            [
                new AppPickerCandidate("WINWORD.exe", "Word", IsRunning: true),
                new AppPickerCandidate("OUTLOOK", "Outlook", IsRunning: true),
            ],
            [
                new RecentApp("OUTLOOK", "Outlook", 5),
                new RecentApp("slack", "Slack", 3),
            ],
            selectedApps: ["WINWORD"]);

        Assert.Collection(
            options,
            option =>
            {
                Assert.Equal("OUTLOOK", option.ProcessName);
                Assert.Equal("Outlook (OUTLOOK)", option.Label);
                Assert.True(option.IsRunning);
                Assert.Equal(5, option.RecentDictations);
            },
            option =>
            {
                Assert.Equal("slack", option.ProcessName);
                Assert.Equal("Slack (slack)", option.Label);
                Assert.False(option.IsRunning);
                Assert.Equal(3, option.RecentDictations);
            });
    }

    [Fact]
    public void Usage_period_state_keeps_old_period_while_loading_or_failed()
    {
        Assert.Equal("Counting your dictations...", UsagePeriodState.Describe(null, UsagePeriod.Last30Days, false).StatusText);
        Assert.Equal("Usage isn't available right now.", UsagePeriodState.Describe(null, UsagePeriod.Last30Days, true).StatusText);
        Assert.Equal("Showing the last 7 days. Loading the last 30 days...", UsagePeriodState.Describe(UsagePeriod.Last7Days, UsagePeriod.Last30Days, false).StatusText);
        Assert.Equal("All kept history", UsagePeriodState.Label(UsagePeriod.AllKeptHistory));
        var failed = UsagePeriodState.Describe(UsagePeriod.Last90Days, null, true);
        Assert.Equal("Showing the last 90 days. Usage isn't available right now.", failed.StatusText);
        Assert.True(failed.ShowRetry);
    }

    [Fact]
    public void Usage_coverage_line_avoids_retained_jargon()
    {
        Assert.Null(UsagePeriodState.CoverageLine("Last 30 days", 0, capped: false, limit: 5000));
        Assert.Equal("Last 30 days: 1 dictation.", UsagePeriodState.CoverageLine("Last 30 days", 1, capped: false, limit: 5000));
        Assert.Equal("Last 30 days: 48 dictations.", UsagePeriodState.CoverageLine("Last 30 days", 48, capped: false, limit: 5000));
        Assert.Equal("All kept history: based on your latest 5,000 dictations.", UsagePeriodState.CoverageLine("All kept history", 5000, capped: true, limit: 5000));
    }

    [Theory]
    [InlineData(0, "0 s")]
    [InlineData(24.5, "25 s")]
    [InlineData(59.9, "60 s")]
    [InlineData(60, "1 min")]
    [InlineData(546, "9.1 min")]
    [InlineData(4320, "1.2 hr")]
    public void Usage_durations_match_tile_copy(double seconds, string expected) =>
        Assert.Equal(expected, UsagePeriodState.FormatDuration(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Usage_trend_axis_labels_match_granularity()
    {
        var daily = UsagePeriodState.AxisLabels(
            [
                new(new DateOnly(2026, 9, 1), 1, 10),
                new(new DateOnly(2026, 9, 2), 2, 20),
            ],
            UsageAnalyzer.TrendGranularity.Daily);
        Assert.Equal(["Sep 1", "Sep 2", "Sep 2"], daily);

        var weekly = UsagePeriodState.AxisLabels(
            [
                new(new DateOnly(2026, 9, 1), 1, 10),
                new(new DateOnly(2026, 9, 8), 2, 20),
                new(new DateOnly(2026, 9, 15), 3, 30),
            ],
            UsageAnalyzer.TrendGranularity.Weekly);
        Assert.Equal(["Week of Sep 1", "Week of Sep 8", "Week of Sep 15"], weekly);
        Assert.Equal(["Sep 1", "Sep 1", "Sep 1"], UsagePeriodState.AxisLabels([new(new DateOnly(2026, 9, 1), 1, 10)], UsageAnalyzer.TrendGranularity.Daily));
    }

    [Fact]
    public void Notices_for_text_changes_profiles_and_usage_insights_are_core_owned()
    {
        Assert.Equal("Your dictionary and snippets are turned off, so Scribe saves these changes but doesn't use them.", TextChangesNotice.Describe(false, aiCleanupEnabled: false).Message);
        Assert.Equal("Your dictionary and snippets are turned off, so Scribe doesn't replace any words with them. AI cleanup still receives your vocabulary when this is off.", TextChangesNotice.Describe(false, aiCleanupEnabled: true).Message);
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
        Assert.Contains("Foundry Local on this PC", UsageInsightAvailability.Describe(true, true, CleanupProvider.FoundryLocal).Description, StringComparison.Ordinal);
        Assert.Contains("your AI service", UsageInsightAvailability.Describe(true, true, CleanupProvider.OpenAiCompatible).Description, StringComparison.Ordinal);
        Assert.Equal("Couldn't get a summary. Try again.", UsageSummaryText.Exception);
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
    public void Try_dictation_processing_time_excludes_recording()
    {
        var elapsed = TryDictationTiming.ProcessingDuration(
            TimeSpan.FromMilliseconds(12),
            TimeSpan.FromMilliseconds(240),
            TimeSpan.FromMilliseconds(820),
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(35));

        Assert.Equal(TimeSpan.FromMilliseconds(1108), elapsed);
        Assert.Equal("13 times faster than real time", TryDictationTiming.SpeedLabel(0.08));
    }

    [Fact]
    public void Try_dictation_change_lines_use_plain_sources()
    {
        var lines = TryDictationChangeList.Describe([
            new TextReplacement(0, 4, "dot net", ".NET", TextReplacementKind.Dictionary),
            new TextReplacement(5, 4, "sig", "Best regards", TextReplacementKind.Snippet),
        ], aiCleanupChanged: true);

        Assert.Equal([
            "\"dot net\" became \".NET\" (your dictionary or a word pack)",
            "\"sig\" became \"Best regards\" (snippet)",
            "AI cleanup rewrote the text.",
        ], lines);
    }

    [Fact]
    public void Try_dictation_result_view_hides_empty_sections_for_failures()
    {
        var noSpeech = TryDictationResultView.For(new TryDictationResultViewInput(
            TryDictationReportClassifier.StageSpeechRecognition,
            TryDictationReportClassifier.NoSpeechRecognized,
            null,
            null,
            0,
            true,
            "AI cleanup: on, Qwen3 on this PC."));
        Assert.False(noSpeech.ShowHeard);
        Assert.False(noSpeech.ShowTyped);
        Assert.False(noSpeech.ShowChanges);
        Assert.True(noSpeech.ShowTimingDetails);
        Assert.Equal(TryDictationSummaryTone.Information, TryDictationSummary.ToneFor(noSpeech.Summary));

        var microphone = TryDictationResultView.For(new TryDictationResultViewInput(
            TryDictationReportClassifier.StageAudioCapture,
            "The microphone produced no audio.",
            null,
            null,
            0,
            true,
            "AI cleanup: on, Qwen3 on this PC."));
        Assert.False(microphone.ShowHeard);
        Assert.Equal(TryDictationSummaryAction.OpenSoundSettings, microphone.Action);

        var typing = TryDictationResultView.For(new TryDictationResultViewInput(
            TryDictationReportClassifier.StageTextInsertion,
            "Text could not be inserted.",
            "hello",
            false,
            0.4,
            true,
            "AI cleanup: on, Qwen3 on this PC."));
        Assert.True(typing.ShowHeard);
        Assert.False(typing.ShowTyped);
        Assert.False(typing.ShowChanges);
        Assert.Equal(FailureStage.TextInsertion, typing.Summary.StoppedAt);
    }

    [Fact]
    public void Try_dictation_result_view_shows_all_sections_for_success_and_cleanup_cautions()
    {
        var success = TryDictationResultView.For(new TryDictationResultViewInput(
            null,
            null,
            "raw",
            true,
            1.1,
            true,
            "AI cleanup: on, Qwen3 on this PC."));
        Assert.True(success.Summary.Success);
        Assert.True(success.ShowHeard);
        Assert.True(success.ShowTyped);
        Assert.True(success.ShowChanges);

        var notReady = TryDictationResultView.For(new TryDictationResultViewInput(
            null,
            null,
            "raw",
            true,
            1.1,
            true,
            "AI cleanup: on, Qwen3 on this PC.",
            CleanupNotReady: true));
        Assert.True(notReady.ShowTyped);
        Assert.True(notReady.Summary.CleanupNotReady);
        Assert.Equal(TryDictationSummaryAction.OpenAiCleanup, notReady.Action);
    }

    [Fact]
    public void Try_dictation_report_classifier_matches_exact_controller_text()
    {
        Assert.True(TryDictationReportClassifier.IsNoSpeech(
            TryDictationReportClassifier.StageVoiceActivityDetection,
            TryDictationReportClassifier.NoSpeechDetected));
        Assert.True(TryDictationReportClassifier.IsNoSpeech(
            TryDictationReportClassifier.StageSpeechRecognition,
            TryDictationReportClassifier.NoSpeechRecognized));
        Assert.False(TryDictationReportClassifier.IsNoSpeech(
            TryDictationReportClassifier.StageVoiceActivityDetection,
            TryDictationReportClassifier.SilenceTrimmingFailed));
        Assert.False(TryDictationReportClassifier.IsNoSpeech(
            TryDictationReportClassifier.StageSpeechRecognition,
            TryDictationReportClassifier.SpeechRecognitionFailed));
        Assert.False(TryDictationReportClassifier.IsNoSpeech(
            TryDictationReportClassifier.StageSpeechRecognition,
            "The speech recognizer crashed."));
        Assert.Equal(TryDictationReportClassifier.SilenceTrimmingFailed, TryDictationReportClassifier.FailureReasonForStage(TryDictationReportClassifier.StageVoiceActivityDetection));
        Assert.Equal(TryDictationReportClassifier.SpeechRecognitionFailed, TryDictationReportClassifier.FailureReasonForStage(TryDictationReportClassifier.StageSpeechRecognition));
        Assert.Equal(FailureStage.TextInsertion, TryDictationReportClassifier.StageFrom(TryDictationReportClassifier.StageTextInsertion));
        Assert.True(TryDictationReportClassifier.IsMicrophoneProblem(TryDictationReportClassifier.StageAudioCapture));
    }

    [Fact]
    public void Try_dictation_timing_details_use_plain_words()
    {
        Assert.Equal("Off in settings", TryDictationTimingDetail.Cleanup(null, enabled: false));
        Assert.Equal("Didn't finish", TryDictationTimingDetail.Cleanup(new CleanupResult("raw", CleanupOutcome.Failed), enabled: true));
        Assert.Equal("Not ready", TryDictationTimingDetail.Cleanup(CleanupResult.Skip("raw", "Still loading"), enabled: true));
        Assert.Equal("Skipped", TryDictationTimingDetail.Cleanup(CleanupResult.Skip("raw"), enabled: true));
        Assert.Equal("No changes", TryDictationTimingDetail.ChangeCount(0));
        Assert.Equal("1 change", TryDictationTimingDetail.ChangeCount(1));
        Assert.Equal("2 changes", TryDictationTimingDetail.ChangeCount(2));
    }

    [Fact]
    public void Dictation_controller_pipeline_stage_strings_stay_in_sync_with_try_dictation_classifier()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Scribe.App", "Dictation", "DictationController.cs"));
        var calls = ExtractReportFailCalls(source);

        // The two no-speech reports, each exactly once, with the classifier's own texts.
        Assert.Single(calls, call =>
            call.StageLiteral == TryDictationReportClassifier.StageVoiceActivityDetection &&
            call.ReasonLiteral == TryDictationReportClassifier.NoSpeechDetected);
        Assert.Single(calls, call =>
            call.StageLiteral == TryDictationReportClassifier.StageSpeechRecognition &&
            call.ReasonLiteral == TryDictationReportClassifier.NoSpeechRecognized);

        // Every stage the controller can report, literally or through currentStage on its exception path, is one the
        // classifier names, so no failure falls through to the page's empty state; and the classifier names no stage
        // the controller never reports.
        var stages = calls.Where(call => call.StageLiteral is not null).Select(call => call.StageLiteral!)
            .Concat(ExtractCurrentStageAssignments(source))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        Assert.All(stages, stage => Assert.NotNull(TryDictationReportClassifier.StageFrom(stage)));
        Assert.DoesNotContain(null, ReadCurrentStageAssignments(source));
        string[] classified =
        [
            TryDictationReportClassifier.StageAudioCapture,
            TryDictationReportClassifier.StageVoiceActivityDetection,
            TryDictationReportClassifier.StageSpeechRecognition,
            TryDictationReportClassifier.StageAiCleanup,
            TryDictationReportClassifier.StageDictionaryAndSnippets,
            TryDictationReportClassifier.StageTextInsertion,
        ];
        Assert.Equal(classified.OrderBy(s => s, StringComparer.Ordinal), stages.OrderBy(s => s, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("// report.Fail(\"Voice activity detection\", \"No speech was detected.\");")]
    [InlineData("/* report.Fail(\"Voice activity detection\", \"No speech was detected.\"); */")]
    [InlineData("var text = \"\"\"report.Fail(\"Voice activity detection\", \"No speech was detected.\");\"\"\";")]
    [InlineData("var text = @\"report.Fail(\"\"Voice activity detection\"\", \"\"No speech was detected.\"\");\";")]
    [InlineData("var text = $\"{\"report.Fail(\"} and {\"x\"}\";")]
    [InlineData("var quote = '\"'; var text = \"report.Fail(\";")]
    public void Report_fail_scan_finds_no_call_in_comments_or_strings(string source)
    {
        Assert.Empty(ExtractReportFailCalls(source));
        Assert.Empty(ExtractCurrentStageAssignments(source.Replace("report.Fail(", "currentStage = ", StringComparison.Ordinal)));
    }

    [Fact]
    public void Report_fail_scan_never_takes_an_interpolated_string_for_an_exact_text()
    {
        var calls = ExtractReportFailCalls("report.Fail(\"Speech recognition\", $\"No speech was recognized.{1}\");");
        var call = Assert.Single(calls);
        Assert.Equal(TryDictationReportClassifier.StageSpeechRecognition, call.StageLiteral);
        Assert.Null(call.ReasonLiteral);

        Assert.Null(Assert.Single(ExtractReportFailCalls("report.Fail($\"Speech recognition\", \"x\");")).StageLiteral);
        Assert.Equal([null], ReadCurrentStageAssignments("currentStage = $\"Speech recognition{1}\";"));
        Assert.Equal([null], ReadCurrentStageAssignments("currentStage = StageFor(step);"));
        Assert.Empty(ExtractCurrentStageAssignments("currentStage = $\"Speech recognition{1}\";"));
    }

    [Fact]
    public void Report_fail_scan_bounds_each_call_and_reads_stage_assignments()
    {
        const string source = """
            report.Fail("Text insertion", injection.Error ?? "Text could not be inserted.");
            report.Fail(stage, "The capture contained only silence.");
            if (currentStage == "Audio capture") { currentStage = "Decode"; }
            """;

        var calls = ExtractReportFailCalls(source);

        Assert.Equal(2, calls.Count);
        Assert.Equal("Text insertion", calls[0].StageLiteral);
        Assert.Null(calls[0].ReasonLiteral);
        Assert.Null(calls[1].StageLiteral);
        Assert.Equal("The capture contained only silence.", calls[1].ReasonLiteral);
        Assert.Equal(["Decode"], ExtractCurrentStageAssignments(source));
        Assert.Null(TryDictationReportClassifier.StageFrom("Decode"));
    }

    [Fact]
    public void Settings_window_tracks_committed_settings_for_try_dictation()
    {
        var settingsDir = Path.Combine(RepositoryRoot(), "src", "Scribe.App", "Settings");
        var source = string.Join("\n", Directory.EnumerateFiles(settingsDir, "SettingsWindow*.cs")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(File.ReadAllText));
        var tryDictation = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Scribe.App", "Settings", "SettingsWindow.TryDictation.cs"));

        Assert.Contains("_committedSettings = _settings.Clone();", source, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(source, "_committedSettings = _settings.Clone();"));
        Assert.Contains("var changes = currentChanges ?? ComputeCurrentChanges();", tryDictation, StringComparison.Ordinal);
        Assert.Contains("_committedSettings.AiCleanupProvider", tryDictation, StringComparison.Ordinal);

        // A tray change confirmed as stored moves the baseline; the optimistic word of it, which may still fail, doesn't.
        Assert.Equal(1, CountOccurrences(source, "_committedSettings = stored.Clone();"));
        var app = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Scribe.App", "App.xaml.cs"));
        Assert.Equal(2, CountOccurrences(app, "_settingsWindow?.AdoptStoredSettings(stored);"));
    }

    [Fact]
    public void Try_dictation_draft_keeps_multiline_writing_style_clean()
    {
        var saved = AppSettings.CreateDefault();
        saved.AiCleanupWritingStyle = "First rule.\r\nSecond rule.";
        var draft = saved.Clone();
        draft.AiCleanupWritingStyle = "First rule.\r\nSecond rule.";

        Assert.False(SettingsChangeTracker.Compare(saved, draft).IsDirty);
    }

    private sealed record FailCall(string? StageLiteral, string? ReasonLiteral);

    private sealed record SourceToken(string Text, bool IsString, bool IsPlainLiteral = false);

    // The controller's report.Fail calls as the compiler sees them: each call's arguments bounded by its own parentheses,
    // and an argument that is exactly one string literal read as that literal, any other expression as null.
    private static IReadOnlyList<FailCall> ExtractReportFailCalls(string source)
    {
        var tokens = Tokenize(source);
        var calls = new List<FailCall>();
        for (var t = 0; t + 3 < tokens.Count; t++)
        {
            if (!IsCode(tokens[t], "report") || !IsCode(tokens[t + 1], ".") || !IsCode(tokens[t + 2], "Fail") ||
                !IsCode(tokens[t + 3], "("))
            {
                continue;
            }

            var arguments = new List<List<SourceToken>> { new() };
            var depth = 0;
            var u = t + 4;
            for (; u < tokens.Count; u++)
            {
                var token = tokens[u];
                if (IsCode(token, ")") && depth == 0)
                {
                    break;
                }

                if (IsCode(token, ",") && depth == 0)
                {
                    arguments.Add([]);
                    continue;
                }

                if (!token.IsString && token.Text is "(" or "[" or "{")
                {
                    depth++;
                }
                else if (!token.IsString && token.Text is ")" or "]" or "}")
                {
                    depth--;
                }

                arguments[^1].Add(token);
            }

            calls.Add(new FailCall(Literal(arguments, 0), Literal(arguments, 1)));
            t = u;
        }

        return calls;
    }

    // Every literal assigned to currentStage, the stage the controller's exception path reports.
    private static IReadOnlyList<string> ExtractCurrentStageAssignments(string source) =>
        [.. ReadCurrentStageAssignments(source).Where(stage => stage is not null).Select(stage => stage!)];

    // Every assignment to currentStage: its plain literal, an approved classifier constant, or null for anything else
    // (an interpolated string, a variable, a call), which the contract can't check and so rejects.
    private static IReadOnlyList<string?> ReadCurrentStageAssignments(string source)
    {
        var tokens = Tokenize(source);
        var stages = new List<string?>();
        for (var t = 0; t + 2 < tokens.Count; t++)
        {
            if (!IsCode(tokens[t], "currentStage") || !IsCode(tokens[t + 1], "=") || IsCode(tokens[t + 2], "="))
            {
                continue;
            }

            var end = t + 2;
            while (end < tokens.Count && !IsCode(tokens[end], ";"))
            {
                end++;
            }

            stages.Add(ConstantValue(tokens.GetRange(t + 2, end - (t + 2))));
        }

        return stages;
    }

    private static bool IsCode(SourceToken token, string text) =>
        !token.IsString && string.Equals(token.Text, text, StringComparison.Ordinal);

    private static string? Literal(List<List<SourceToken>> arguments, int index) =>
        index < arguments.Count ? ConstantValue(arguments[index]) : null;

    private static string? ConstantValue(IReadOnlyList<SourceToken> tokens)
    {
        if (tokens is [{ IsPlainLiteral: true } only])
        {
            return only.Text;
        }

        if (tokens.Count == 3 &&
            IsCode(tokens[0], "TryDictationReportClassifier") &&
            IsCode(tokens[1], ".") &&
            !tokens[2].IsString)
        {
            return tokens[2].Text switch
            {
                nameof(TryDictationReportClassifier.StageAudioCapture) => TryDictationReportClassifier.StageAudioCapture,
                nameof(TryDictationReportClassifier.StageVoiceActivityDetection) => TryDictationReportClassifier.StageVoiceActivityDetection,
                nameof(TryDictationReportClassifier.StageSpeechRecognition) => TryDictationReportClassifier.StageSpeechRecognition,
                nameof(TryDictationReportClassifier.StageAiCleanup) => TryDictationReportClassifier.StageAiCleanup,
                nameof(TryDictationReportClassifier.StageDictionaryAndSnippets) => TryDictationReportClassifier.StageDictionaryAndSnippets,
                nameof(TryDictationReportClassifier.StageTextInsertion) => TryDictationReportClassifier.StageTextInsertion,
                nameof(TryDictationReportClassifier.NoSpeechDetected) => TryDictationReportClassifier.NoSpeechDetected,
                nameof(TryDictationReportClassifier.NoSpeechRecognized) => TryDictationReportClassifier.NoSpeechRecognized,
                nameof(TryDictationReportClassifier.SilenceTrimmingFailed) => TryDictationReportClassifier.SilenceTrimmingFailed,
                nameof(TryDictationReportClassifier.SpeechRecognitionFailed) => TryDictationReportClassifier.SpeechRecognitionFailed,
                nameof(TryDictationReportClassifier.AudioCaptureFailed) => TryDictationReportClassifier.AudioCaptureFailed,
                nameof(TryDictationReportClassifier.SilentCapture) => TryDictationReportClassifier.SilentCapture,
                nameof(TryDictationReportClassifier.AiCleanupFailed) => TryDictationReportClassifier.AiCleanupFailed,
                nameof(TryDictationReportClassifier.DictionaryAndSnippetsFailed) => TryDictationReportClassifier.DictionaryAndSnippetsFailed,
                nameof(TryDictationReportClassifier.TextInsertionFailed) => TryDictationReportClassifier.TextInsertionFailed,
                _ => null,
            };
        }

        if (tokens is [{ Text: "TryDictationSummary", IsString: false }, { Text: ".", IsString: false }, { Text: nameof(TryDictationSummary.EmptyTextReason), IsString: false }])
        {
            return TryDictationSummary.EmptyTextReason;
        }

        return null;
    }

    // Enough of a C# lexer for these scans: comments are dropped, string literals of every form (regular, verbatim,
    // interpolated, raw) and char literals are single tokens, never code, and everything else is an identifier or one
    // punctuation character.
    private static List<SourceToken> Tokenize(string source)
    {
        var tokens = new List<SourceToken>();
        var i = 0;
        while (i < source.Length)
        {
            var c = source[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] is not ('\r' or '\n'))
                {
                    i++;
                }
            }
            else if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? source.Length : end + 2;
            }
            else if (TryReadString(source, ref i, out var value, out var interpolated))
            {
                // An interpolated string's value is only known at run time, so it is never an exact text.
                tokens.Add(new SourceToken(value, IsString: true, IsPlainLiteral: !interpolated));
            }
            else if (c == '\'')
            {
                i = SkipCharLiteral(source, i);
                tokens.Add(new SourceToken("'", IsString: false));
            }
            else if (char.IsLetterOrDigit(c) || c == '_')
            {
                var start = i;
                while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '_'))
                {
                    i++;
                }

                tokens.Add(new SourceToken(source[start..i], IsString: false));
            }
            else
            {
                tokens.Add(new SourceToken(c.ToString(), IsString: false));
                i++;
            }
        }

        return tokens;
    }

    // Reads a string literal at i, of any form, and moves i past it. An interpolation hole's expression is skipped, so a
    // string inside it never ends the outer one; only plain literals are compared, so the hole's text is not kept.
    private static bool TryReadString(string source, ref int i, out string value, out bool interpolated)
    {
        var j = i;
        var dollars = 0;
        var verbatim = false;
        while (j < source.Length && source[j] is '$' or '@')
        {
            dollars += source[j] == '$' ? 1 : 0;
            verbatim |= source[j] == '@';
            j++;
        }

        value = string.Empty;
        interpolated = dollars > 0;
        if (j >= source.Length || source[j] != '"')
        {
            return false;
        }

        var quotes = 0;
        while (j + quotes < source.Length && source[j + quotes] == '"')
        {
            quotes++;
        }

        if (quotes >= 3)
        {
            var fence = new string('"', quotes);
            var contentStart = j + quotes;
            var end = source.IndexOf(fence, contentStart, StringComparison.Ordinal);
            value = end < 0 ? source[contentStart..] : source[contentStart..end];
            i = end < 0 ? source.Length : end + quotes;
            return true;
        }

        if (quotes == 2)
        {
            i = j + 2;
            return true;
        }

        var builder = new System.Text.StringBuilder();
        var k = j + 1;
        while (k < source.Length)
        {
            var ch = source[k];
            if (dollars > 0 && ch == '{')
            {
                if (k + 1 < source.Length && source[k + 1] == '{')
                {
                    builder.Append('{');
                    k += 2;
                    continue;
                }

                k = SkipInterpolationHole(source, k + 1);
                continue;
            }

            if (verbatim && ch == '"')
            {
                if (k + 1 < source.Length && source[k + 1] == '"')
                {
                    builder.Append('"');
                    k += 2;
                    continue;
                }

                break;
            }

            if (!verbatim && ch == '\\' && k + 1 < source.Length)
            {
                builder.Append(source[k + 1]);
                k += 2;
                continue;
            }

            if (!verbatim && ch == '"')
            {
                break;
            }

            builder.Append(ch);
            k++;
        }

        value = builder.ToString();
        i = Math.Min(source.Length, k + 1);
        return true;
    }

    private static int SkipInterpolationHole(string source, int k)
    {
        var depth = 1;
        while (k < source.Length && depth > 0)
        {
            var ch = source[k];
            if (ch == '{')
            {
                depth++;
                k++;
            }
            else if (ch == '}')
            {
                depth--;
                k++;
            }
            else if (ch == '\'')
            {
                k = SkipCharLiteral(source, k);
            }
            else if (TryReadString(source, ref k, out _, out _))
            {
                // A string inside the hole is skipped whole, so its quotes never end the outer string.
            }
            else
            {
                k++;
            }
        }

        return k;
    }

    private static int SkipCharLiteral(string source, int k)
    {
        k++;
        while (k < source.Length && source[k] != '\'')
        {
            k += source[k] == '\\' ? 2 : 1;
        }

        return Math.Min(source.Length, k + 1);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Scribe.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not find the repository root.");
    }

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

    [Fact]
    public void Ai_cleanup_draft_state_does_not_use_saved_live_status()
    {
        var saved = AppSettings.CreateDefault();
        saved.EnableAiCleanup = true;
        saved.AiCleanupProvider = CleanupProvider.AzureFoundry;
        saved.AiCleanupAzureEndpoint = "https://old.example";
        saved.AiCleanupAzureDeployment = "old";

        var draft = saved.Clone();
        draft.AiCleanupProvider = CleanupProvider.FoundryLocal;

        var state = AiCleanupPageState.Describe(
            saved,
            draft,
            CleanupStatus.Ready,
            foundrySetup: FoundryLocalSetup.Describe(FoundryLocalSetupStage.CachedUnloaded, "Qwen3 1.7B"),
            modelName: "Qwen3 1.7B");

        Assert.Equal("Save to start AI cleanup.", state.StatusLine);
        Assert.Equal("Load", state.StatusRow!.ActionText);
        Assert.DoesNotContain("Using", state.StatusLine, StringComparison.Ordinal);
    }

    [Fact]
    public void Ai_cleanup_newly_enabled_complete_setup_says_save_first()
    {
        var saved = AppSettings.CreateDefault();
        saved.EnableAiCleanup = false;
        saved.AiCleanupProvider = CleanupProvider.AzureFoundry;
        saved.AiCleanupAzureEndpoint = "https://example.test";
        saved.AiCleanupAzureDeployment = "cleanup";

        var draft = saved.Clone();
        draft.EnableAiCleanup = true;

        var state = AiCleanupPageState.Describe(
            saved,
            draft,
            CleanupStatus.Ready,
            azureSetup: new(AzureSetupResult.ApiKeyVerified, ApiKeySelected: true));

        Assert.Equal("Save to start AI cleanup.", state.StatusLine);
        Assert.Equal("Connected.", state.StatusRow!.Text);
        Assert.Equal("Test connection", state.StatusRow.ActionText);
    }

    [Fact]
    public void Ai_cleanup_edited_remote_details_do_not_show_saved_live_status()
    {
        var saved = AppSettings.CreateDefault();
        saved.EnableAiCleanup = true;
        saved.AiCleanupProvider = CleanupProvider.OpenAiCompatible;
        saved.AiCleanupCustomEndpoint = "http://localhost:11434/v1";
        saved.AiCleanupCustomModel = "qwen3:4b";

        var draft = saved.Clone();
        draft.AiCleanupCustomModel = "llama3";

        var state = AiCleanupPageState.Describe(
            saved,
            draft,
            CleanupStatus.Ready,
            customSetup: new(CustomEndpointTestResult.NotTested));

        Assert.Equal("Save to start AI cleanup.", state.StatusLine);
        Assert.Equal("Not tested yet.", state.StatusRow!.Text);
    }

    [Fact]
    public void Ai_cleanup_switch_away_edit_other_provider_and_switch_back_stays_saved_active()
    {
        var saved = AppSettings.CreateDefault();
        saved.EnableAiCleanup = true;
        saved.AiCleanupProvider = CleanupProvider.AzureFoundry;
        saved.AiCleanupAzureEndpoint = "https://example.test";
        saved.AiCleanupAzureDeployment = "cleanup";
        saved.AiCleanupModel = "qwen3-1.7b";

        var draft = saved.Clone();
        draft.AiCleanupProvider = CleanupProvider.FoundryLocal;
        draft.AiCleanupModel = "phi-4";
        draft.AiCleanupProvider = CleanupProvider.AzureFoundry;

        var state = AiCleanupPageState.Describe(
            saved,
            draft,
            CleanupStatus.Ready,
            azureSetup: new(AzureSetupResult.ApiKeyVerified, ApiKeySelected: true));

        Assert.Equal("On. AI cleanup is ready.", state.StatusLine);
        Assert.NotEqual("Save to start AI cleanup.", state.StatusLine);
    }

    public static TheoryData<AzureSetupResult, AiCleanupStatusKind, string, string?, bool, string?> AzureRows => new()
    {
        { AzureSetupResult.NotChecked, AiCleanupStatusKind.Info, "Not checked yet.", "Check sign-in", true, null },
        { AzureSetupResult.CheckingSignIn, AiCleanupStatusKind.Busy, "Checking your Azure sign-in...", null, false, null },
        { AzureSetupResult.CliMissing, AiCleanupStatusKind.Warning, "Azure CLI isn't installed. Scribe uses it to sign you in and find your models.", "Install Azure CLI", true, "Use an API key instead" },
        { AzureSetupResult.NotSignedIn, AiCleanupStatusKind.Info, "Not signed in to Azure.", "Sign in", true, "Use an API key instead" },
        { AzureSetupResult.SigningIn, AiCleanupStatusKind.Busy, "Finish signing in in your browser.", null, false, null },
        { AzureSetupResult.SignedIn, AiCleanupStatusKind.Success, "Signed in.", "Refresh models", true, null },
        { AzureSetupResult.ListingModels, AiCleanupStatusKind.Busy, "Finding your models...", null, false, null },
        { AzureSetupResult.ListingFailed, AiCleanupStatusKind.Error, "Couldn't list your models. no access", "Try again", true, null },
        { AzureSetupResult.Verifying, AiCleanupStatusKind.Busy, "Testing the connection...", null, false, null },
        { AzureSetupResult.ApiKeyIncomplete, AiCleanupStatusKind.Info, "Fill in the details above, then choose Test connection.", "Test connection", false, null },
        { AzureSetupResult.ApiKeyComplete, AiCleanupStatusKind.Info, "Not tested yet.", "Test connection", true, null },
        { AzureSetupResult.ApiKeyVerified, AiCleanupStatusKind.Success, "Connected.", "Test connection", true, null },
        { AzureSetupResult.ApiKeyVerificationFailed, AiCleanupStatusKind.Error, "Azure denied access. Check the resource key and its access settings. (403)", "Try again", true, null },
        { AzureSetupResult.ApiKeyVerifyAgain, AiCleanupStatusKind.Info, "Changed since the last test. Choose Test connection.", "Test connection", true, null },
        { AzureSetupResult.ServicePrincipalIncomplete, AiCleanupStatusKind.Info, "Fill in the details above, then choose Test connection.", "Test connection", false, null },
        { AzureSetupResult.ServicePrincipalComplete, AiCleanupStatusKind.Info, "Not tested yet.", "Test connection", true, null },
        { AzureSetupResult.ServicePrincipalVerified, AiCleanupStatusKind.Success, "Connected.", "Test connection", true, null },
        { AzureSetupResult.ServicePrincipalVerificationFailed, AiCleanupStatusKind.Error, "Azure denied access. Check the app registration and resource role. (403)", "Try again", true, null },
        { AzureSetupResult.ServicePrincipalVerifyAgain, AiCleanupStatusKind.Info, "Changed since the last test. Choose Test connection.", "Test connection", true, null },
    };

    [Theory]
    [MemberData(nameof(AzureRows))]
    public void Ai_cleanup_azure_rows_have_provider_specific_actions(
        AzureSetupResult result,
        AiCleanupStatusKind kind,
        string text,
        string? action,
        bool actionEnabled,
        string? secondary)
    {
        var reason = result switch
        {
            AzureSetupResult.ApiKeyVerificationFailed => "Azure denied access. Check the resource key and its access settings. (403)",
            AzureSetupResult.ServicePrincipalVerificationFailed => "Azure denied access. Check the app registration and resource role. (403)",
            _ => "no access",
        };
        var state = ActiveAzure(new AzureAiSetupState(result, SafeReason: reason));

        Assert.Equal(kind, state.StatusRow!.Kind);
        Assert.Equal(text, state.StatusRow.Text);
        Assert.Equal(action, state.StatusRow.ActionText);
        Assert.Equal(actionEnabled, state.StatusRow.ActionEnabled);
        Assert.Equal(secondary, state.StatusRow.SecondaryActionText);
    }

    public static TheoryData<CopilotSetupResult, AiCleanupStatusKind, string, string?, string?> CopilotRows => new()
    {
        { CopilotSetupResult.NotChecked, AiCleanupStatusKind.Busy, "Looking for GitHub Copilot...", null, null },
        { CopilotSetupResult.ToolNotFound, AiCleanupStatusKind.Warning, "GitHub Copilot isn't installed on this PC.", "Install", "Check again" },
        { CopilotSetupResult.Outdated, AiCleanupStatusKind.Warning, "GitHub Copilot needs an update.", "Update", "Check again" },
        { CopilotSetupResult.Installing, AiCleanupStatusKind.Info, "The installer is open. Finish it, then choose Check again.", "Check again", null },
        { CopilotSetupResult.Installed, AiCleanupStatusKind.Success, "GitHub Copilot is installed.", "Sign in", null },
        { CopilotSetupResult.SignedIn, AiCleanupStatusKind.Success, "GitHub Copilot is installed.", null, null },
        { CopilotSetupResult.ModelsListed, AiCleanupStatusKind.Success, "GitHub Copilot is installed.", null, null },
    };

    [Theory]
    [MemberData(nameof(CopilotRows))]
    public void Ai_cleanup_copilot_rows_have_provider_specific_actions(
        CopilotSetupResult result,
        AiCleanupStatusKind kind,
        string text,
        string? action,
        string? secondary)
    {
        var state = ActiveCopilot(new CopilotSetupState(result));

        Assert.Equal(kind, state.StatusRow!.Kind);
        Assert.Equal(text, state.StatusRow.Text);
        Assert.Equal(action, state.StatusRow.ActionText);
        Assert.Equal(secondary, state.StatusRow.SecondaryActionText);
    }

    [Theory]
    [InlineData(CustomEndpointTestResult.NotTested, AiCleanupStatusKind.Info, "Not tested yet.", "Test connection")]
    [InlineData(CustomEndpointTestResult.Testing, AiCleanupStatusKind.Busy, "Testing the connection...", null)]
    [InlineData(CustomEndpointTestResult.Connected, AiCleanupStatusKind.Success, "Connected. qwen answered.", "Test connection")]
    [InlineData(CustomEndpointTestResult.Failed, AiCleanupStatusKind.Error, "Connection refused.", "Try again")]
    public void Ai_cleanup_custom_rows_have_provider_specific_actions(
        CustomEndpointTestResult result,
        AiCleanupStatusKind kind,
        string text,
        string? action)
    {
        var state = ActiveCustom(new CustomEndpointSetupState(result, "qwen", "Connection refused."));

        Assert.Equal(kind, state.StatusRow!.Kind);
        Assert.Equal(text, state.StatusRow.Text);
        Assert.Equal(action, state.StatusRow.ActionText);
    }

    [Fact]
    public void Ai_cleanup_custom_test_connection_is_disabled_until_required_fields_are_valid()
    {
        var state = ActiveCustom(new CustomEndpointSetupState(CustomEndpointTestResult.NotTested, CanTest: false));

        Assert.Equal("Test connection", state.StatusRow!.ActionText);
        Assert.False(state.StatusRow.ActionEnabled);
    }

    public static TheoryData<CleanupProvider, CleanupStatus, string?, string> RemoteStatusLines => new()
    {
        { CleanupProvider.AzureFoundry, CleanupStatus.Ready, null, "On. AI cleanup is ready." },
        { CleanupProvider.AzureFoundry, CleanupStatus.Initializing, null, "On. Getting ready..." },
        { CleanupProvider.AzureFoundry, CleanupStatus.Unavailable, null, "On, but not ready. Until it's ready, Scribe types what it hears." },
        { CleanupProvider.AzureFoundry, CleanupStatus.Unavailable, "Not signed in", "On, but not ready: Not signed in. Until it's ready, Scribe types what it hears." },
        { CleanupProvider.AzureFoundry, CleanupStatus.Disabled, null, "On, but not set up yet. Until it's ready, Scribe types what it hears." },
        { CleanupProvider.GitHubCopilot, CleanupStatus.Ready, null, "On. AI cleanup is ready." },
        { CleanupProvider.GitHubCopilot, CleanupStatus.Initializing, null, "On. Getting ready..." },
        { CleanupProvider.GitHubCopilot, CleanupStatus.Unavailable, null, "On, but not ready: GitHub Copilot isn't installed." },
        { CleanupProvider.GitHubCopilot, CleanupStatus.Disabled, null, "On, but not set up yet. Until it's ready, Scribe types what it hears." },
        { CleanupProvider.OpenAiCompatible, CleanupStatus.Unavailable, null, "On, but not ready. Until it's ready, Scribe types what it hears." },
        { CleanupProvider.OpenAiCompatible, CleanupStatus.Ready, null, "On. AI cleanup is ready." },
        { CleanupProvider.OpenAiCompatible, CleanupStatus.Initializing, null, "On. Getting ready..." },
        { CleanupProvider.OpenAiCompatible, CleanupStatus.Unavailable, "Connection refused", "On, but not ready: Connection refused. Until it's ready, Scribe types what it hears." },

        // The service's reasons end their own sentences, and the line must not add a second period after one.
        { CleanupProvider.OpenAiCompatible, CleanupStatus.Unavailable, "Couldn't reach the AI service. Check your network and the service's address.", "On, but not ready: Couldn't reach the AI service. Check your network and the service's address. Until it's ready, Scribe types what it hears." },
        { CleanupProvider.AzureFoundry, CleanupStatus.Unavailable, "AI cleanup couldn't start. ", "On, but not ready: AI cleanup couldn't start. Until it's ready, Scribe types what it hears." },
        { CleanupProvider.AzureFoundry, CleanupStatus.Unavailable, "Is the deployment name right?", "On, but not ready: Is the deployment name right? Until it's ready, Scribe types what it hears." },
        { CleanupProvider.OpenAiCompatible, CleanupStatus.Disabled, null, "On, but not set up yet. Until it's ready, Scribe types what it hears." },
    };

    [Theory]
    [MemberData(nameof(RemoteStatusLines))]
    public void Saved_active_remote_status_lines_follow_live_status(
        CleanupProvider provider,
        CleanupStatus status,
        string? reason,
        string expected)
    {
        var state = provider switch
        {
            CleanupProvider.AzureFoundry => ActiveAzure(new AzureAiSetupState(AzureSetupResult.ApiKeyVerified, ApiKeySelected: true), status, reason),
            CleanupProvider.GitHubCopilot => ActiveCopilot(new CopilotSetupState(CopilotSetupResult.Installing), status, reason),
            CleanupProvider.OpenAiCompatible => ActiveCustom(new CustomEndpointSetupState(CustomEndpointTestResult.Connected, "qwen"), status, reason),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
        };

        Assert.Equal(expected, state.StatusLine);
    }

    [Theory]
    [InlineData("Azure rejected the API key (401). Check that the key belongs to this resource.")]
    [InlineData("Azure could not find the deployment 'gpt-4o' (404). Check the endpoint and exact deployment name.")]
    [InlineData("Azure is throttling requests (429). The deployment is reachable but over quota. Wait and retry.")]
    [InlineData("Azure returned a server error (500). This is usually transient; try again shortly.")]
    [InlineData("The service principal secret was rejected. Check the secret's Value.")]
    [InlineData("The service principal secret has expired. Create a new secret and paste its Value.")]
    [InlineData("The app registration was not found. Check the application ID and tenant ID.")]
    public void Azure_verification_failures_are_typed_errors_not_message_parsing(string reason)
    {
        var outcome = AzureVerificationOutcome.Failed(reason);

        var apiRow = ActiveAzure(new AzureAiSetupState(outcome.ToApiKeyResult(complete: true), ApiKeySelected: true, SafeReason: outcome.SafeMessage)).StatusRow!;
        Assert.Equal(AiCleanupStatusKind.Error, apiRow.Kind);
        Assert.Equal(reason, apiRow.Text);

        var spRow = ActiveAzure(new AzureAiSetupState(outcome.ToServicePrincipalResult(complete: true), AuthMode: AzureAuthMode.ServicePrincipal, SafeReason: outcome.SafeMessage)).StatusRow!;
        Assert.Equal(AiCleanupStatusKind.Error, spRow.Kind);
        Assert.Equal(reason, spRow.Text);
    }

    [Fact]
    public void Azure_verification_changed_since_maps_to_verify_again()
    {
        var outcome = AzureVerificationOutcome.ChangedSince;

        Assert.Equal(AzureSetupResult.ApiKeyVerifyAgain, outcome.ToApiKeyResult(complete: true));
        Assert.Equal(AzureSetupResult.ServicePrincipalVerifyAgain, outcome.ToServicePrincipalResult(complete: true));
        Assert.Equal(AzureSetupResult.ApiKeyIncomplete, outcome.ToApiKeyResult(complete: false));
    }

    [Fact]
    public void Reapplying_the_same_off_page_projection_keeps_configured_provider_summary()
    {
        var saved = AppSettings.CreateDefault();
        saved.AiCleanupProvider = CleanupProvider.OpenAiCompatible;
        saved.AiCleanupCustomEndpoint = "http://localhost:11434/v1";
        saved.AiCleanupCustomModel = "synthetic-model";
        var draft = saved.Clone();
        draft.EnableAiCleanup = false;
        var setup = AiCleanupPageState.SavedSetupState(saved);
        var summary = AiCleanupPageState.ProviderSetupSummary(saved);

        var first = AiCleanupPageState.Describe(saved, draft, CleanupStatus.Ready, savedSetupState: setup, providerSummary: summary);
        var second = AiCleanupPageState.Describe(saved, draft, CleanupStatus.Ready, savedSetupState: setup, providerSummary: summary);

        Assert.Equal("Set up to use On this PC (Ollama, synthetic-model).", first.OffHelperText);
        Assert.Equal(first, second);

        // Anything but Ollama's or LM Studio's own address stays another AI service.
        saved.AiCleanupCustomEndpoint = "https://ai.example.invalid/v1";
        Assert.Equal("Another AI service (synthetic-model)", AiCleanupPageState.ProviderSetupSummary(saved));
        saved.AiCleanupCustomEndpoint = "http://localhost:1234/v1";
        Assert.Equal("On this PC (LM Studio, synthetic-model)", AiCleanupPageState.ProviderSetupSummary(saved));

        // An app's address saved with a key is shown, and summed up, as another AI service.
        saved.AiCleanupCustomApiKey = "lm-studio-token";
        Assert.Equal("Another AI service (synthetic-model)", AiCleanupPageState.ProviderSetupSummary(saved));
    }
    private static AiCleanupPageDescription ActiveAzure(AzureAiSetupState setup, CleanupStatus status = CleanupStatus.Ready, string? safeReason = null)
    {
        var saved = AppSettings.CreateDefault();
        saved.EnableAiCleanup = true;
        saved.AiCleanupProvider = CleanupProvider.AzureFoundry;
        saved.AiCleanupAzureEndpoint = "https://example.test";
        saved.AiCleanupAzureDeployment = "cleanup";
        var draft = saved.Clone();
        return AiCleanupPageState.Describe(saved, draft, status, azureSetup: setup, safeReason: safeReason);
    }

    private static AiCleanupPageDescription ActiveCopilot(CopilotSetupState setup, CleanupStatus status = CleanupStatus.Ready, string? safeReason = null)
    {
        var saved = AppSettings.CreateDefault();
        saved.EnableAiCleanup = true;
        saved.AiCleanupProvider = CleanupProvider.GitHubCopilot;
        var draft = saved.Clone();
        return AiCleanupPageState.Describe(saved, draft, status, copilotSetup: setup, safeReason: safeReason);
    }

    private static AiCleanupPageDescription ActiveCustom(CustomEndpointSetupState setup, CleanupStatus status = CleanupStatus.Ready, string? safeReason = null)
    {
        var saved = AppSettings.CreateDefault();
        saved.EnableAiCleanup = true;
        saved.AiCleanupProvider = CleanupProvider.OpenAiCompatible;
        saved.AiCleanupCustomEndpoint = "http://localhost:11434/v1";
        saved.AiCleanupCustomModel = "qwen";
        var draft = saved.Clone();
        return AiCleanupPageState.Describe(saved, draft, status, customSetup: setup, safeReason: safeReason);
    }

    [Fact]
    public void A_cached_model_loads_and_unloads_through_the_expected_page_states()
    {
        // Load pressed: the service reports Downloading while it loads the cached model, then Ready; Unload publishes
        // Unavailable with the model still on disk (TextCleanupService.DecideResidentChange).
        var steps = new (CleanupStatus Status, bool? Loaded, FoundryLocalSetupStage Stage, string Line, string? Action)[]
        {
            (CleanupStatus.Disabled, false, FoundryLocalSetupStage.CachedUnloaded, "On. Getting ready...", "Load"),
            (CleanupStatus.Downloading, false, FoundryLocalSetupStage.DownloadingOrLoading, "On. Getting ready...", null),
            (CleanupStatus.Ready, true, FoundryLocalSetupStage.Loaded, "On. Using Qwen3 1.7B on this PC.", "Free memory"),
            (CleanupStatus.Unavailable, false, FoundryLocalSetupStage.CachedUnloaded, "On. Getting ready...", "Load"),
        };

        foreach (var (status, loaded, stage, line, action) in steps)
        {
            var mapped = FoundryLocalSetup.FromCleanupStatus(status, runtimeReady: true, modelCached: true, modelLoaded: loaded);
            Assert.Equal(stage, mapped);

            var page = ActiveFoundry(FoundryLocalSetup.Describe(mapped, "Qwen3 1.7B", "about 1.3 GB"), status);
            Assert.Equal(line, page.StatusLine);
            Assert.Equal(action, page.StatusRow!.ActionText);
        }
    }

    [Fact]
    public void Runtime_ready_without_a_model_reads_getting_ready_on_the_top_card()
    {
        var page = ActiveFoundry(FoundryLocalSetup.Describe(FoundryLocalSetupStage.RuntimeReady, "Qwen3 1.7B", "about 1.3 GB"), CleanupStatus.Disabled);

        Assert.Equal("On. Getting ready...", page.StatusLine);
        Assert.Equal("Ready to download Qwen3 1.7B (about 1.3 GB).", page.StatusRow!.Text);
        Assert.Equal("Download and load", page.StatusRow.ActionText);
    }

    [Fact]
    public void A_model_that_failed_after_the_runtime_came_up_is_not_a_runtime_failure()
    {
        var page = ActiveFoundry(FoundryLocalSetup.Describe(FoundryLocalSetupStage.ModelFailed, "Qwen3 1.7B"), CleanupStatus.Unavailable);

        Assert.Equal("On, but not ready. Until it's ready, Scribe types what it hears.", page.StatusLine);
        Assert.Equal("Couldn't download or load Qwen3 1.7B. Try again, or choose another model.", page.StatusRow!.Text);
        Assert.DoesNotContain("runtime", page.StatusRow.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_failed_model_list_does_not_stop_a_running_deployment()
    {
        var page = ActiveAzure(new AzureAiSetupState(AzureSetupResult.ListingFailed, SafeReason: "Azure denied access."), CleanupStatus.Ready);

        Assert.Equal("On. AI cleanup is ready.", page.StatusLine);
        Assert.Equal(AiCleanupStatusKind.Error, page.StatusRow!.Kind);
        Assert.Equal("Couldn't list your models. Azure denied access.", page.StatusRow.Text);
    }

    [Fact]
    public void A_known_setup_problem_explains_an_unavailable_service_and_a_reason_explains_the_rest()
    {
        Assert.Equal(
            "On, but not ready: Azure CLI isn't installed.",
            ActiveAzure(new AzureAiSetupState(AzureSetupResult.CliMissing), CleanupStatus.Unavailable).StatusLine);
        Assert.Equal(
            "On, but not ready: you're not signed in to Azure.",
            ActiveAzure(new AzureAiSetupState(AzureSetupResult.NotSignedIn), CleanupStatus.Unavailable, "ignored when the cause is known").StatusLine);
        Assert.Equal(
            "On. AI cleanup is ready.",
            ActiveAzure(new AzureAiSetupState(AzureSetupResult.CliMissing), CleanupStatus.Ready).StatusLine);
        Assert.Equal(
            "On, but not ready: Copilot didn't answer. Until it's ready, Scribe types what it hears.",
            ActiveCopilot(new CopilotSetupState(CopilotSetupResult.ModelsListed), CleanupStatus.Unavailable, "Copilot didn't answer").StatusLine);
        Assert.Equal(
            "On. AI cleanup is ready.",
            ActiveCopilot(new CopilotSetupState(CopilotSetupResult.ModelsListed), CleanupStatus.Ready).StatusLine);
    }

    private static AiCleanupPageDescription ActiveFoundry(FoundryLocalSetupDescription setup, CleanupStatus status)
    {
        var saved = AppSettings.CreateDefault();
        saved.EnableAiCleanup = true;
        saved.AiCleanupProvider = CleanupProvider.FoundryLocal;
        var draft = saved.Clone();
        return AiCleanupPageState.Describe(saved, draft, status, foundrySetup: setup, modelName: "Qwen3 1.7B");
    }
}
