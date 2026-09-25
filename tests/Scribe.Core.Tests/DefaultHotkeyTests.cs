using System.Text.Json;
using Scribe.Core.Hotkeys;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

/// <summary>
/// The shipped hotkeys are a first-run default: a new install gets hold Page Down for dictation with AI cleanup and
/// hold Page Up for dictation only, while an install that already exists keeps what its settings hold, including no
/// dictation-only key, and a session whose saved settings could not be used keeps the Right Ctrl every earlier release
/// shipped. Moving someone's push-to-talk key on upgrade would break the key they press dozens of times a day and take
/// Page Up and Page Down away from all their other apps.
/// </summary>
public sealed class DefaultHotkeyTests
{
    private const uint PageDown = 0x22; // VK_NEXT
    private const uint PageUp = 0x21; // VK_PRIOR
    private const uint RightCtrl = 0xA3; // VK_RCONTROL
    private const uint LeftCtrl = 0xA2;
    private const uint DownArrow = 0x28;

    [Fact]
    public void The_shipped_defaults_are_page_down_and_page_up_held_and_swallowed()
    {
        AssertHeldAndSwallowed(HotkeyBinding.DefaultDictation, PageDown, "Page Down");
        AssertHeldAndSwallowed(HotkeyBinding.DefaultDictationOnly, PageUp, "Page Up");
        AssertHeldAndSwallowed(HotkeyBinding.Legacy, RightCtrl, "Right Ctrl");
    }

    [Fact]
    public void A_fresh_install_starts_with_page_down_and_page_up()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var repo = new SettingsRepository(db);

        var loaded = repo.Load();

        Assert.False(repo.LastLoadFailed);
        Assert.Equal(HotkeyBinding.DefaultDictation, loaded.Hotkey);
        Assert.Equal(HotkeyBinding.DefaultDictationOnly, loaded.DictationOnlyHotkey);
    }

    [Fact]
    public void The_first_document_a_fresh_install_writes_holds_page_down_and_page_up()
    {
        // The welcome records its flag through Update before anything else saves, so this is the document a new
        // install's next start reads.
        using var db = ScribeDatabase.CreateInMemory();
        var repo = new SettingsRepository(db);

        repo.Update(stored => stored.HasCompletedFirstRun = true);
        var reloaded = new SettingsRepository(db).Load();

        Assert.NotNull(repo.Get("app_settings"));
        Assert.True(reloaded.HasCompletedFirstRun);
        Assert.Equal(HotkeyBinding.DefaultDictation, reloaded.Hotkey);
        Assert.Equal(HotkeyBinding.DefaultDictationOnly, reloaded.DictationOnlyHotkey);
    }

    [Theory]
    // A 0.4.3 document on the defaults of its day: Right Ctrl and no dictation-only key, both written out.
    [InlineData("right-ctrl", "null")]
    // Keys the document does not carry at all come back as what that install had: Right Ctrl, and one hotkey.
    [InlineData(null, null)]
    [InlineData("right-ctrl", null)]
    [InlineData(null, "f9")]
    // Custom keys on both triggers stay exactly as stored.
    [InlineData("f8", "f9")]
    public void An_existing_document_keeps_its_hotkeys_with_or_without_each_key(string? hotkey, string? dictationOnly)
    {
        var fields = new List<string> { "\"showOverlay\":false", "\"hasCompletedFirstRun\":true" };
        if (hotkey is not null)
        {
            fields.Add($"\"hotkey\":{Json(Stored(hotkey))}");
        }

        if (dictationOnly is not null)
        {
            fields.Add($"\"dictationOnlyHotkey\":{(Stored(dictationOnly) is { } binding ? Json(binding) : "null")}");
        }

        using var db = ScribeDatabase.CreateInMemory();
        var repo = new SettingsRepository(db);
        repo.Set("app_settings", "{" + string.Join(",", fields) + "}");

        var loaded = repo.Load();

        Assert.False(repo.LastLoadFailed);
        Assert.False(loaded.ShowOverlay); // the stored document was read, not replaced
        Assert.Equal(hotkey is null ? HotkeyBinding.Legacy : Stored(hotkey), loaded.Hotkey);
        Assert.Equal(dictationOnly is null ? null : Stored(dictationOnly), loaded.DictationOnlyHotkey);
    }

    [Fact]
    public void An_existing_document_whose_hotkey_is_null_keeps_right_ctrl()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var repo = new SettingsRepository(db);
        repo.Set("app_settings", """{"hotkey":null,"dictationOnlyHotkey":null}""");

        var loaded = repo.Load();

        Assert.False(repo.LastLoadFailed);
        Assert.Equal(HotkeyBinding.Legacy, loaded.Hotkey);
        Assert.Null(loaded.DictationOnlyHotkey);
    }

    [Fact]
    public void An_unreadable_document_runs_on_right_ctrl_and_one_hotkey()
    {
        // Not a first run: this install's document is still there (and kept as the recovery copy), it just cannot be
        // read, and the key it most likely held is the one every earlier release shipped.
        using var db = ScribeDatabase.CreateInMemory();
        var repo = new SettingsRepository(db);
        repo.Set("app_settings", "{\"hotkey\":{\"virtualKey\":163, not json");

        var loaded = repo.Load();

        Assert.True(repo.LastLoadFailed);
        Assert.Equal(HotkeyBinding.Legacy, loaded.Hotkey);
        Assert.Null(loaded.DictationOnlyHotkey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t ")]
    public void A_blank_stored_document_is_unreadable_not_a_first_run(string blank)
    {
        // Something wrote this row, so the install has run before: an empty or white-space document gets what any
        // unreadable one gets (the recovery copy, the failed-load report and the legacy hotkeys), never the new
        // defaults, and the row itself is left as it was.
        using var db = ScribeDatabase.CreateInMemory();
        var repo = new SettingsRepository(db);
        repo.Set("app_settings", blank);

        var loaded = repo.Load();

        Assert.True(repo.LastLoadFailed);
        Assert.Equal(HotkeyBinding.Legacy, loaded.Hotkey);
        Assert.Null(loaded.DictationOnlyHotkey);
        Assert.Equal(blank, repo.Get("app_settings"));
        Assert.Null(repo.Get("app_settings_recovery")); // nothing to recover, so the write-once slot stays free
        Assert.True(SettingsRepository.StartsWithoutSavedSettings(new SettingsRepository(db), db));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_stored_document_refuses_partial_updates_until_a_save_replaces_it(string blank)
    {
        using var db = ScribeDatabase.CreateInMemory();
        var repo = new SettingsRepository(db);
        repo.Set("app_settings", blank);

        // The welcome's flag and a tray change are partial updates: neither may stand defaults in for the document.
        Assert.Throws<InvalidOperationException>(() => repo.Update(stored => stored.HasCompletedFirstRun = true));
        Assert.Throws<InvalidOperationException>(
            () => repo.Update(stored => stored.EnableAiCleanup = true, ExternalSwitchSync.NextRevision(), out _));
        Assert.True(repo.LastLoadFailed);
        Assert.Equal(blank, repo.Get("app_settings"));
        Assert.Null(repo.Get("app_settings_recovery"));

        // Saving what the session showed, as the Settings window does, is the explicit choice that ends it.
        repo.SaveBundle(repo.Load(), dictionaryEntries: null, snippets: null);

        Assert.False(repo.LastLoadFailed);
        var reread = new SettingsRepository(db);
        var saved = reread.Load();
        Assert.False(reread.LastLoadFailed);
        Assert.Equal(HotkeyBinding.Legacy, saved.Hotkey);
        Assert.Null(saved.DictationOnlyHotkey);
        Assert.True(reread.Update(stored => stored.HasCompletedFirstRun = true).HasCompletedFirstRun);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_blank_document_leaves_the_recovery_slot_to_the_next_real_one(bool throughUpdate)
    {
        // The copy is written once and never overwritten, so a blank document taking it would lose the unreadable
        // document that comes after it. Through Load (startup, Settings) and through a refused Update alike.
        using var db = ScribeDatabase.CreateInMemory();
        var repo = new SettingsRepository(db);
        void Read()
        {
            if (throughUpdate)
            {
                Assert.Throws<InvalidOperationException>(() => repo.Update(stored => stored.HasCompletedFirstRun = true));
            }
            else
            {
                repo.Load();
            }
        }

        repo.Set("app_settings", "  ");
        Read();
        Assert.Null(repo.Get("app_settings_recovery"));

        repo.Set("app_settings", "{\"hotkey\":{\"virtualKey\":34, not json");
        Read();
        Assert.Equal("{\"hotkey\":{\"virtualKey\":34, not json", repo.Get("app_settings_recovery"));

        // Once a document with content holds the slot, neither a later one nor a blank one replaces it.
        repo.Set("app_settings", "{\"second\": not json");
        Read();
        repo.Set("app_settings", "");
        Read();
        Assert.Equal("{\"hotkey\":{\"virtualKey\":34, not json", repo.Get("app_settings_recovery"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_blank_recovery_copy_gives_way_to_a_document_with_content(bool throughUpdate)
    {
        using var db = ScribeDatabase.CreateInMemory();
        var repo = new SettingsRepository(db);
        repo.Set("app_settings_recovery", " \t ");
        repo.Set("app_settings", "{\"unreadable");

        if (throughUpdate)
        {
            Assert.Throws<InvalidOperationException>(() => repo.Update(stored => stored.HasCompletedFirstRun = true));
        }
        else
        {
            repo.Load();
        }

        Assert.Equal("{\"unreadable", repo.Get("app_settings_recovery"));
    }

    [Fact]
    public void A_document_a_repair_recorded_as_lost_runs_on_right_ctrl_and_one_hotkey()
    {
        // The start after a repair that lost the document: no document, but the recorded loss says this is no first run.
        using var db = ScribeDatabase.CreateInMemory();
        var repo = new SettingsRepository(db);
        repo.Set(SettingsRepository.LostMarkerKey, "lost");

        var loaded = repo.Load();

        Assert.True(repo.LastLoadFailed);
        Assert.Equal(HotkeyBinding.Legacy, loaded.Hotkey);
        Assert.Null(loaded.DictationOnlyHotkey);
    }

    [Fact]
    public void Settings_that_could_not_be_used_differ_from_a_first_run_only_in_the_hotkeys()
    {
        var fresh = AppSettings.CreateDefault();
        var existing = AppSettings.CreateForExistingInstall();

        Assert.Equal(HotkeyBinding.Legacy, existing.Hotkey);
        Assert.Null(existing.DictationOnlyHotkey);

        existing.Hotkey = fresh.Hotkey;
        existing.DictationOnlyHotkey = fresh.DictationOnlyHotkey;
        Assert.Equal(JsonSerializer.Serialize(fresh), JsonSerializer.Serialize(existing));
    }

    [Fact]
    public void Restore_moves_right_ctrl_to_page_down_and_adds_page_up()
    {
        var restored = DefaultHotkeyRestore.Restore(HotkeyBinding.Legacy, null, HotkeyBinding.Legacy, null);

        Assert.True(restored.Changed);
        Assert.True(restored.SaveNeeded);
        Assert.Equal(HotkeyBinding.DefaultDictation, restored.Dictation);
        Assert.Equal(HotkeyBinding.DefaultDictationOnly, restored.DictationOnly);
        Assert.Equal(
            "Hotkeys set to the defaults: hold Page Down for dictation with AI cleanup and hold Page Up for dictation " +
            "only. Save to apply them.",
            restored.Message);
    }

    [Fact]
    public void Restore_replaces_toggles_chords_and_unswallowed_keys_with_the_held_defaults()
    {
        var chordToggle = new HotkeyBinding(
            RightCtrl, KeyModifiers.None, HotkeyMode.Toggle, Suppress: true, "Right Ctrl+Right Shift",
            SecondaryVirtualKey: 0xA1, SuppressChordMembers: true);
        var loose = new HotkeyBinding(0x78, KeyModifiers.Control, HotkeyMode.Toggle, Suppress: false, "Ctrl+F9");

        var restored = DefaultHotkeyRestore.Restore(chordToggle, loose, chordToggle, loose);

        Assert.True(restored.Changed);
        AssertHeldAndSwallowed(restored.Dictation, PageDown, "Page Down");
        AssertHeldAndSwallowed(restored.DictationOnly, PageUp, "Page Up");
    }

    [Fact]
    public void A_second_press_before_saving_still_asks_for_the_save()
    {
        // A double click, or pressing again to be sure: the page already shows the defaults, the saved settings do not,
        // and the notice that replaces the first one must not read as if nothing were left to do.
        var first = DefaultHotkeyRestore.Restore(HotkeyBinding.Legacy, null, HotkeyBinding.Legacy, null);
        var second = DefaultHotkeyRestore.Restore(first.Dictation, first.DictationOnly, HotkeyBinding.Legacy, null);

        Assert.False(second.Changed);
        Assert.True(second.SaveNeeded);
        Assert.Equal(
            "The hotkeys already show the defaults: hold Page Down for dictation with AI cleanup and hold Page Up for " +
            "dictation only. Save to apply them.",
            second.Message);
    }

    [Fact]
    public void Restore_on_the_saved_defaults_changes_nothing_and_says_so()
    {
        var restored = DefaultHotkeyRestore.Restore(
            HotkeyBinding.DefaultDictation, HotkeyBinding.DefaultDictationOnly,
            HotkeyBinding.DefaultDictation, HotkeyBinding.DefaultDictationOnly);

        Assert.False(restored.Changed);
        Assert.False(restored.SaveNeeded);
        Assert.Equal(HotkeyBinding.DefaultDictation, restored.Dictation);
        Assert.Equal(HotkeyBinding.DefaultDictationOnly, restored.DictationOnly);
        Assert.StartsWith("Your hotkeys already match the defaults", restored.Message);
        Assert.DoesNotContain("Save", restored.Message);
    }

    [Fact]
    public void Restoring_edits_back_to_the_saved_defaults_needs_no_save()
    {
        // The page was edited away from saved defaults and then restored: it matches what is saved again.
        var restored = DefaultHotkeyRestore.Restore(
            HotkeyBinding.Legacy, null, HotkeyBinding.DefaultDictation, HotkeyBinding.DefaultDictationOnly);

        Assert.True(restored.Changed);
        Assert.False(restored.SaveNeeded);
        Assert.EndsWith("They are already saved.", restored.Message);
    }

    [Fact]
    public void Default_keys_stored_under_an_old_alias_already_are_the_defaults()
    {
        // The name never changed what a key does, so a stored "Next" is still Page Down, on the page and when saved.
        var next = HotkeyBinding.DefaultDictation with { DisplayName = "Next" };
        var prior = HotkeyBinding.DefaultDictationOnly with { DisplayName = "Prior" };
        var restored = DefaultHotkeyRestore.Restore(next, prior, next, prior);

        Assert.False(restored.Changed);
        Assert.False(restored.SaveNeeded);
        Assert.Equal("Page Down", restored.Dictation.DisplayName);
        Assert.Equal("Page Up", restored.DictationOnly.DisplayName);
    }

    [Theory]
    [InlineData(HotkeyMode.Toggle, true, HotkeyMode.Hold)]
    [InlineData(HotkeyMode.Hold, false, HotkeyMode.Hold)]
    [InlineData(HotkeyMode.Hold, true, HotkeyMode.Toggle)]
    public void The_default_keys_pressed_another_way_are_not_the_defaults(
        HotkeyMode dictationMode, bool dictationSuppressed, HotkeyMode dictationOnlyMode)
    {
        var dictation = HotkeyBinding.DefaultDictation with { Mode = dictationMode, Suppress = dictationSuppressed };
        var dictationOnly = HotkeyBinding.DefaultDictationOnly with { Mode = dictationOnlyMode };
        var restored = DefaultHotkeyRestore.Restore(dictation, dictationOnly, dictation, dictationOnly);

        Assert.True(restored.Changed);
        Assert.True(restored.SaveNeeded);
        Assert.Equal(HotkeyBinding.DefaultDictation, restored.Dictation);
        Assert.Equal(HotkeyBinding.DefaultDictationOnly, restored.DictationOnly);
    }

    [Fact]
    public void The_restore_hint_names_the_defaults_and_what_binding_them_costs()
    {
        var hint = DefaultHotkeyRestore.Hint;

        Assert.Contains("hold Page Down for dictation with AI cleanup and hold Page Up for dictation only", hint);
        Assert.Contains("Page Down and Page Up pressed on their own no longer reach other apps", hint);
        Assert.Contains("a presentation remote stops changing slides", hint);
        Assert.Contains("With Ctrl, Shift, Alt, Win or the Narrator key held they work in other apps as before", hint);
        Assert.Contains("Pause dictation from the tray icon to use them for a while", hint);
        Assert.Contains("choose other keys here if you present", hint);
        Assert.Contains("Fn with the Down and Up arrows", hint);
        Assert.Contains("the keypad's Page Down and Page Up with Num Lock off also work", hint);
    }

    [Fact]
    public void Page_down_and_page_up_start_their_own_dictations_and_never_reach_other_apps()
    {
        // Through the engine the hook runs, not just the settings: each default fires its own trigger, its whole
        // keystroke is swallowed, and a key neither binds passes straight through.
        using var h = new HotkeyEngineHarness(HotkeyBinding.DefaultDictation, HotkeyBinding.DefaultDictationOnly);

        Assert.True(h.Down(PageDown).Suppress);
        Assert.True(h.Down(PageDown).Suppress); // autorepeat
        Assert.True(h.Up(PageDown).Suppress);
        Assert.True(h.Down(PageUp).Suppress);
        Assert.True(h.Up(PageUp).Suppress);
        Assert.False(h.Down(DownArrow).Suppress);
        Assert.False(h.Up(DownArrow).Suppress);

        var transitions = h.TakeTransitions().Select(t => (t.Transition, t.Trigger)).ToList();
        var expected = new List<(HotkeyTransition, HotkeyTrigger)>
        {
            (HotkeyTransition.Activated, HotkeyTrigger.Standard),
            (HotkeyTransition.Deactivated, HotkeyTrigger.Standard),
            (HotkeyTransition.Activated, HotkeyTrigger.DictationOnly),
            (HotkeyTransition.Deactivated, HotkeyTrigger.DictationOnly),
        };
        Assert.Equal(expected, transitions);
    }

    [Fact]
    public void Ctrl_page_down_reaches_the_app_and_starts_nothing()
    {
        // Ctrl+Page Down switches tabs in browsers, editors and Excel, so a binding of the bare key must not take it:
        // Ctrl and Page Down both reach the app, nothing starts, and a bare press still dictates afterwards.
        // HotkeyModifierTests covers every other modifier and the Narrator keys.
        var state = new ChordStateMachine(HotkeyBinding.DefaultDictation);

        Assert.False(state.Process(LeftCtrl, isDown: true).ShouldSuppress);
        var down = state.Process(PageDown, isDown: true);
        var up = state.Process(PageDown, isDown: false);
        Assert.False(state.Process(LeftCtrl, isDown: false).ShouldSuppress);

        Assert.Equal(HotkeyTransition.None, down.Transition);
        Assert.False(down.ShouldSuppress);
        Assert.Equal(HotkeyTransition.None, up.Transition);
        Assert.False(up.ShouldSuppress);
        Assert.Equal(HotkeyTransition.Activated, state.Process(PageDown, isDown: true).Transition);
    }

    private static void AssertHeldAndSwallowed(HotkeyBinding binding, uint virtualKey, string name)
    {
        Assert.Equal(virtualKey, binding.VirtualKey);
        Assert.Null(binding.SecondaryVirtualKey);
        Assert.Equal(KeyModifiers.None, binding.Modifiers);
        Assert.Equal(HotkeyMode.Hold, binding.Mode);
        Assert.True(binding.Suppress);
        Assert.False(binding.SuppressChordMembers);
        Assert.Equal(name, binding.DisplayName);
        Assert.Equal(name, HotkeyText.Describe(binding));
    }

    // The bindings the document tests store, by name; "null" is a key written out as null.
    private static HotkeyBinding? Stored(string name) => name switch
    {
        "right-ctrl" => HotkeyBinding.Legacy,
        "f8" => new HotkeyBinding(0x77, KeyModifiers.None, HotkeyMode.Toggle, Suppress: true, "F8"),
        "f9" => new HotkeyBinding(0x78, KeyModifiers.None, HotkeyMode.Hold, Suppress: true, "F9"),
        "null" => null,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    // A hotkey the way the settings document stores it.
    private static string Json(HotkeyBinding? binding) =>
        binding is null
            ? "null"
            : $$"""{"virtualKey":{{binding.VirtualKey}},"modifiers":"{{binding.Modifiers}}","mode":"{{binding.Mode}}","suppress":{{(binding.Suppress ? "true" : "false")}},"displayName":"{{binding.DisplayName}}","secondaryVirtualKey":null,"suppressChordMembers":false}""";
}
