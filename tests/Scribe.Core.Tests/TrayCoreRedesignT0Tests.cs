using Scribe.Core.Cleanup;
using Scribe.Core.Hotkeys;
using Scribe.Core.Lifecycle;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;
using Scribe.Core.TextInjection;
using Scribe.Core.Tray;

namespace Scribe.Core.Tests;

public sealed class TrayCoreRedesignT0Tests
{
    [Fact]
    public void Tray_menu_lists_redesigned_items_in_order()
    {
        var menu = TrayMenu.Build(new TrayMenuState(
            UpdateReady: true,
            HasRecentDictation: false,
            AiCleanup: new TrayAiCleanupItem(TrayAiCleanupKind.SetUp, "Set up AI cleanup...", false, true),
            DictationPaused: true,
            RecentDictationPreviews: ["One", "Two"]));

        Assert.Equal(
            ["Restart to update Scribe", "", "Settings", "", "Add to dictionary...", "Copy last dictation", "Copy a recent dictation", "", "Microphone", "Set up AI cleanup...", "Pause dictation", "", "Quit Scribe"],
            menu.Items.Select(i => i.Label));
        Assert.True(menu.Items.Single(i => i.Label == "Settings").IsDefault);
        Assert.False(menu.Items.Single(i => i.Label == "Copy last dictation").Enabled);
        Assert.True(menu.Items.Single(i => i.Label == "Pause dictation").IsChecked);
    }

    [Fact]
    public void Tray_ai_cleanup_describes_setup_and_not_ready_states()
    {
        var settings = AppSettings.CreateDefault();
        settings.EnableAiCleanup = true;
        var setup = TrayAiCleanup.Describe(settings, setupComplete: false, CleanupStatus.Disabled, settingsRecovered: false);
        var unavailable = TrayAiCleanup.Describe(settings, setupComplete: true, CleanupStatus.Unavailable, settingsRecovered: false);

        Assert.Equal("Set up AI cleanup...", setup.Label);
        Assert.False(setup.IsCheckItem);
        Assert.Equal("AI cleanup (not ready)", unavailable.Label);
        Assert.True(unavailable.Checked);
    }

    [Fact]
    public void Tooltip_uses_state_text_condition_priority_and_cap()
    {
        var tooltip = TrayToolTip.Compose(TrayState.Ready, new string('x', 100), HotkeyMode.Toggle, TrayCondition.UpdateReady, "0.4.5");

        Assert.StartsWith("Scribe: ready. Press ", tooltip);
        Assert.Contains("start and stop", tooltip);
        Assert.True(tooltip.Length <= TrayToolTip.MaxLength);
        Assert.Equal("Scribe: listening...", TrayToolTip.Compose(TrayState.Recording, "Page Down", HotkeyMode.Hold));
    }

    [Fact]
    public void Notice_delivery_pins_recording_warning_flags()
    {
        var delivery = TrayNoticeDelivery.For(TrayNoticeKind.RecordingWarning);
        var notice = DictationProblemText.Describe(DictationProblem.MicrophoneMuted);

        Assert.Equal(TrayNotificationIcon.Warning, delivery.Icon);
        Assert.True(delivery.Silent);
        Assert.False(delivery.RespectQuietTime);
        Assert.Equal("Your microphone is muted", notice.Title);
        Assert.Equal("Microphone muted", notice.PillText);
    }

    [Fact]
    public void Dictation_problem_from_legacy_maps_current_strings()
    {
        Assert.Equal(DictationProblem.TooQuick, DictationProblemText.FromLegacy("that was too quick, hold the key while you speak"));
        Assert.Equal(DictationProblem.NothingRecognized, DictationProblemText.FromLegacy("nothing was recognised, try again"));
        var press = DictationProblemText.Describe(DictationProblem.TooQuick, HotkeyMode.Toggle, "Page Down");
        Assert.Equal("That was too quick. Press Page Down, speak, then press it again.", press.Body);
    }

    [Fact]
    public void Feedback_policy_routes_problem_and_ai_cleanup_episode()
    {
        var policy = new TrayFeedbackPolicy();

        Assert.Equal(TrayFeedbackChannel.Pill, policy.Decide(TrayFeedbackEvent.DictationProblem, recordingIndicatorOn: true).Channel);
        Assert.Equal(TrayFeedbackChannel.Notice, policy.Decide(TrayFeedbackEvent.DictationProblem, recordingIndicatorOn: false).Channel);
        Assert.Equal(TrayFeedbackChannel.Notice, policy.Decide(TrayFeedbackEvent.AiCleanupFailure).Channel);
        Assert.Equal(TrayFeedbackChannel.None, policy.Decide(TrayFeedbackEvent.AiCleanupFailure).Channel);
        policy.Decide(TrayFeedbackEvent.AiCleanupSuccess);
        Assert.Equal(TrayFeedbackChannel.Notice, policy.Decide(TrayFeedbackEvent.AiCleanupFailure).Channel);
    }

    [Fact]
    public void Prompts_and_launch_decisions_use_redesign_texts()
    {
        var settingsPrompt = SettingsClosePrompt.For(CloseTrigger.RestartToUpdate);
        var quickAddPrompt = QuickAddClosePrompt.ForUnsavedWord();

        Assert.Equal("Save changes before restarting?", settingsPrompt.Title);
        Assert.Equal("Keep editing", settingsPrompt.DefaultButton);
        Assert.Equal("Save this word before closing?", quickAddPrompt.Title);
        Assert.Equal(SecondLaunchAction.OpenSettingsInRunningInstance, SecondLaunch.Decide(signalRaised: true));
        Assert.Equal(SecondLaunchAction.ShowAlreadyRunningNotice, SecondLaunch.Decide(signalRaised: false));
        Assert.False(FirstRunWelcome.ShouldShow(hasCompletedFirstRun: false, settingsRecovered: true));
    }

    [Fact]
    public void Quick_add_plan_covers_settings_block_pack_override_and_references_failure()
    {
        var library = new DictionaryLibrary("ai", "AI", "Built-in", null, true, [new DictionaryEntry(0, "cloud pilot", "Copilot")]);
        var vocabulary = QuickAddVocabulary.Compose([], ["draft"], [library], ["ai"]);

        var blocked = QuickDictionaryAdd.Build(new QuickDictionaryAdd.QuickAddRequest("draft", "Draft", false, true), vocabulary);
        var pack = QuickDictionaryAdd.Build(new QuickDictionaryAdd.QuickAddRequest("cloud pilot", "GitHub Copilot", false, true, "ask cloud pilot", true), vocabulary);
        var failed = QuickDictionaryAdd.Build(new QuickDictionaryAdd.QuickAddRequest("cloud", "Copilot", false, true, ReferencesAvailable: false), vocabulary);

        Assert.Equal(QuickDictionaryAdd.PlanKind.BlockedBySettings, blocked.Kind);
        Assert.Equal("Show in Settings", ActionText(blocked.Action));
        Assert.Equal(QuickDictionaryAdd.PlanKind.OverridesWordPack, pack.Kind);
        Assert.Contains("The \"AI\" word pack writes \"cloud pilot\" as \"Copilot\"", pack.Message);
        Assert.Equal("Couldn't check your dictionary. Try again.", failed.Message);
    }

    [Fact]
    public void Quick_add_helpers_pin_keyboard_hint_selection_and_announcements()
    {
        var state = new ChipKeyboardState(1, 1, QuickDictionaryAdd.WordRange.None, [0, 0, 1]);
        var result = ChipKeyboard.Apply(state, ChipKey.Right, shift: true, wordCount: 3);
        var prior = new QuickDictionaryAdd.Plan(QuickDictionaryAdd.PlanKind.Pending, null, "pending");
        var next = new QuickDictionaryAdd.Plan(QuickDictionaryAdd.PlanKind.Create, DictionaryEntry.New("a", "b"), "create");

        Assert.Equal(new QuickDictionaryAdd.WordRange(1, 2), result.State.Selection);
        Assert.Equal("Arrow keys move between words. Space selects a word. Shift and an arrow key select several.", QuickAddHint.For(result.State.Selection, keyboardFocusInWords: true, hasDictation: true));
        Assert.Equal(QuickAddAnnouncementTiming.Delayed, QuickAddAnnouncement.ShouldAnnounce(prior, next));
        Assert.Equal(QuickDictionaryAdd.WordRange.None, QuickAddSelection.Reconcile("typed", "selected", result.State.Selection));
        Assert.Equal(TimeSpan.FromMilliseconds(700), QuickAddAnnouncement.AnnouncementDelay);
    }

    [Fact]
    public void Microphone_tray_choices_can_hide_default_suffix()
    {
        var devices = new[]
        {
            new AudioDevice("1", "Realtek", IsDefault: true),
            new AudioDevice("2", "Jabra", IsDefault: false),
        };

        var tray = MicrophoneChoices.Build(devices, MicrophoneSelection.WindowsDefault, markWindowsDefault: false);
        var settings = MicrophoneChoices.Build(devices, MicrophoneSelection.WindowsDefault);

        Assert.Contains(tray.Choices, choice => choice.Label == "Realtek");
        Assert.DoesNotContain(tray.Choices, choice => choice.Label.EndsWith(MicrophoneChoices.DefaultSuffix, StringComparison.Ordinal));
        Assert.Contains(settings.Choices, choice => choice.Label == "Realtek" + MicrophoneChoices.DefaultSuffix);
    }

    [Fact]
    public void Last_transcript_store_clear_and_forget_remove_history_copies()
    {
        var store = new LastTranscriptStore();
        store.Set("one");
        store.Set("two");
        store.Set("one");

        Assert.True(store.Forget("one"));
        Assert.Equal(["two"], store.GetRecent());
        store.Clear();
        Assert.Empty(store.GetRecent());
    }

    [Fact]
    public void Clipboard_privacy_formats_are_the_three_windows_markers()
    {
        Assert.Equal(
            ["ExcludeClipboardContentFromMonitorProcessing", "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard"],
            ClipboardPrivacyFormats.Names);
    }

    [Fact]
    public void Startup_and_core_notices_have_redesigned_text()
    {
        Assert.Equal("Scribe is already running", StartupNotices.AlreadyRunning().Title);
        Assert.Equal("Scribe couldn't start", StartupFailureNotice.Title);
        Assert.Equal("AI files removed", FoundryStorageReclaimNotice.Title);
        Assert.Equal("Scribe repaired its data", DatabaseRepairNotice.Title);
        Assert.Equal("AI cleanup also receives your words and word pack words as vocabulary.", CleanupDisclosure.AddToDictionaryVocabularyLine);
        Assert.StartsWith("Scribe couldn't use your saved settings", SavedSettingsNotice.AtStartup);
    }

    [Fact]
    public void Dictionary_draft_diff_reports_new_edited_and_deleted_spoken_forms()
    {
        var loaded = new[] { new DictionaryEntry(1, "same", "Same"), new DictionaryEntry(2, "old", "Old"), new DictionaryEntry(3, "gone", "Gone") };
        var staged = new[] { new DictionaryEntry(1, " same ", "Same"), new DictionaryEntry(2, "old", "New"), new DictionaryEntry(4, "new", "New") };

        Assert.Equal(["gone", "new", "old"], DictionaryDraftDiff.ChangedSpokenForms(loaded, staged).Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Hotkey_sentence_name_adds_article_for_mouse_buttons()
    {
        var mouse = new HotkeyBinding(MouseButtons.Middle, KeyModifiers.None, HotkeyMode.Hold, Suppress: true, "Middle mouse button");
        Assert.Equal("the middle mouse button", HotkeyText.SentenceName(mouse));
    }

    private static string ActionText(QuickDictionaryAdd.PlanAction action) => action switch
    {
        QuickDictionaryAdd.PlanAction.ShowInSettings => "Show in Settings",
        QuickDictionaryAdd.PlanAction.FixInstead => "Fix instead",
        _ => string.Empty,
    };
}
