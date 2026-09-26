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
    [InlineData(FoundryLocalSetupStage.CachedUnloaded, AiCleanupStatusKind.Info, "Load", false)]
    [InlineData(FoundryLocalSetupStage.Checking, AiCleanupStatusKind.Info, "Unload", false)]
    [InlineData(FoundryLocalSetupStage.DownloadingOrLoading, AiCleanupStatusKind.Busy, null, false)]
    [InlineData(FoundryLocalSetupStage.Loaded, AiCleanupStatusKind.Success, "Unload", true)]
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
    public void Snippet_list_text_shows_off_state_and_new_placeholder()
    {
        Assert.Equal(new SnippetListItemText("New snippet", null), SnippetListText.Describe(" ", enabled: true));
        Assert.Equal(new SnippetListItemText("insert signature", "Off"), SnippetListText.Describe(" insert signature ", enabled: false));
    }

    [Fact]
    public void Profile_list_text_and_chips_hide_process_jargon_until_needed()
    {
        var description = ProfileListText.Describe(" Email ", "OUTLOOK.exe, ms-teams, outlook");

        Assert.Equal("Email", description.Primary);
        Assert.Equal("Outlook, Teams", description.Secondary);

        var chips = ProfileAppChips.FromProgramNames("OUTLOOK.exe, ms-teams");
        Assert.Collection(
            chips,
            chip =>
            {
                Assert.Equal("OUTLOOK", chip.ProgramName);
                Assert.Equal("Outlook", chip.DisplayName);
                Assert.Equal("Remove Outlook", chip.RemoveName);
            },
            chip => Assert.Equal("Teams", chip.DisplayName));
        Assert.Equal("OUTLOOK, ms-teams", ProfileAppChips.ToProgramNames(chips));
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
        Assert.Equal("Showing Last 7 days. Loading Last 30 days...", UsagePeriodState.Describe(UsagePeriod.Last7Days, UsagePeriod.Last30Days, false).StatusText);
        var failed = UsagePeriodState.Describe(UsagePeriod.Last90Days, null, true);
        Assert.Equal("Showing Last 90 days. Usage isn't available right now.", failed.StatusText);
        Assert.True(failed.ShowRetry);
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
        Assert.Equal("Azure accepted the key.", state.StatusRow!.Text);
        Assert.Equal("Verify", state.StatusRow.ActionText);
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
        { AzureSetupResult.CliMissing, AiCleanupStatusKind.Warning, "Azure CLI isn't installed.", "Install Azure CLI", true, "Use an API key instead" },
        { AzureSetupResult.NotSignedIn, AiCleanupStatusKind.Info, "Not signed in to Azure.", "Sign in", true, "Use an API key instead" },
        { AzureSetupResult.SigningIn, AiCleanupStatusKind.Busy, "Finish signing in in your browser.", null, false, null },
        { AzureSetupResult.SignedIn, AiCleanupStatusKind.Success, "Signed in.", "Refresh models", true, null },
        { AzureSetupResult.ListingModels, AiCleanupStatusKind.Busy, "Finding your models...", null, false, null },
        { AzureSetupResult.ListingFailed, AiCleanupStatusKind.Error, "Couldn't list your models. no access", "Try again", true, null },
        { AzureSetupResult.ApiKeyIncomplete, AiCleanupStatusKind.Info, "Fill in the details above, then choose Verify.", "Verify", false, null },
        { AzureSetupResult.ApiKeyComplete, AiCleanupStatusKind.Info, "Fill in the details above, then choose Verify.", "Verify", true, null },
        { AzureSetupResult.ApiKeyVerified, AiCleanupStatusKind.Success, "Azure accepted the key.", "Verify", true, null },
        { AzureSetupResult.ApiKeyVerificationFailed, AiCleanupStatusKind.Error, "Azure denied access. Check the resource key and its access settings. (403)", "Verify", true, null },
        { AzureSetupResult.ApiKeyVerifyAgain, AiCleanupStatusKind.Info, "Verify again.", "Verify", true, null },
        { AzureSetupResult.ServicePrincipalIncomplete, AiCleanupStatusKind.Info, "Fill in the details above, then choose Verify.", "Verify", false, null },
        { AzureSetupResult.ServicePrincipalComplete, AiCleanupStatusKind.Info, "Fill in the details above, then choose Verify.", "Verify", true, null },
        { AzureSetupResult.ServicePrincipalVerified, AiCleanupStatusKind.Success, "Verified.", "Verify", true, null },
        { AzureSetupResult.ServicePrincipalVerificationFailed, AiCleanupStatusKind.Error, "Azure denied access. Check the app registration and resource role. (403)", "Verify", true, null },
        { AzureSetupResult.ServicePrincipalVerifyAgain, AiCleanupStatusKind.Info, "Verify again.", "Verify", true, null },
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
        { CopilotSetupResult.NotChecked, AiCleanupStatusKind.Info, "Not checked yet.", "Get models", null },
        { CopilotSetupResult.ToolNotFound, AiCleanupStatusKind.Warning, "GitHub Copilot isn't installed on this PC.", "Install", "Check again" },
        { CopilotSetupResult.Installing, AiCleanupStatusKind.Info, "The installer is open. Finish it, then choose Check again.", "Check again", null },
        { CopilotSetupResult.Installed, AiCleanupStatusKind.Success, "GitHub Copilot is installed.", "Sign in", "Get models" },
        { CopilotSetupResult.SignedIn, AiCleanupStatusKind.Success, "GitHub Copilot is installed.", "Get models", null },
        { CopilotSetupResult.ModelsListed, AiCleanupStatusKind.Success, "GitHub Copilot is ready.", "Get models", null },
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
    [InlineData(CustomEndpointTestResult.Testing, AiCleanupStatusKind.Busy, "Testing...", null)]
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
            (CleanupStatus.Ready, true, FoundryLocalSetupStage.Loaded, "On. Using Qwen3 1.7B on this PC.", "Unload"),
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