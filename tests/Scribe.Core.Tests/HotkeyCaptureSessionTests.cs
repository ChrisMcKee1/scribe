using Scribe.Core.Hotkeys;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

/// <summary>
/// Settings' Set records keys and the middle, Back and Forward mouse buttons by the same rules: up to two inputs in the
/// order they go down, set once every one is up again. The left and right buttons are refused and keep their meaning;
/// the window only maps each event to its code and shows what the capture decides, and marks the event handled when the
/// capture says so (a side button's release left unhandled becomes a Back or Forward command in WPF).
/// </summary>
public sealed class HotkeyCaptureSessionTests
{
    private const uint Middle = MouseButtons.Middle;
    private const uint Back = MouseButtons.Back;
    private const uint Forward = MouseButtons.Forward;
    private const uint LeftCtrl = 0xA2;
    private const uint Escape = 0x1B;
    private const uint F9 = 0x78;
    private const uint PageDown = 0x22;

    [Theory]
    [InlineData(Middle, "Middle mouse button")]
    [InlineData(Back, "Mouse Back (button 4)")]
    [InlineData(Forward, "Mouse Forward (button 5)")]
    public void A_mouse_button_on_its_own_becomes_the_hotkey(uint button, string name)
    {
        var capture = new HotkeyCaptureSession();

        var press = capture.Press(button);
        Assert.Equal(HotkeyCaptureOutcome.Recorded, press.Outcome);
        Assert.True(press.Handled);
        Assert.Equal(name + "  (add another key or mouse button, or release)", press.Text);

        var release = capture.Release(button, HotkeyMode.Toggle);
        Assert.Equal(HotkeyCaptureOutcome.Completed, release.Outcome);
        Assert.True(release.Handled);
        Assert.Null(release.Message);
        var binding = Assert.IsType<HotkeyBinding>(release.Binding);
        Assert.Equal(button, binding.VirtualKey);
        Assert.Null(binding.SecondaryVirtualKey);
        Assert.Equal(KeyModifiers.None, binding.Modifiers);
        Assert.Equal(HotkeyMode.Toggle, binding.Mode);
        Assert.True(binding.Suppress);
        Assert.Equal(name, binding.DisplayName); // what 0.4.3 and 0.4.2 show, since they show the stored name
        Assert.Equal(name, HotkeyText.Describe(binding));
    }

    [Fact]
    public void A_key_then_a_mouse_button_become_a_chord_in_that_order()
    {
        var capture = new HotkeyCaptureSession();

        Assert.Equal("Left Ctrl  (add another key or mouse button, or release)", capture.Press(LeftCtrl).Text);
        Assert.Equal("Left Ctrl+Mouse Back (button 4)  (release to set)", capture.Press(Back).Text);
        Assert.Equal(HotkeyCaptureOutcome.Unchanged, capture.Release(Back, HotkeyMode.Hold).Outcome); // Ctrl still down
        var done = capture.Release(LeftCtrl, HotkeyMode.Hold);

        Assert.Equal(HotkeyCaptureOutcome.Completed, done.Outcome);
        Assert.Null(done.Message); // the key first: the button never reaches the app
        var binding = done.Binding!;
        Assert.Equal(LeftCtrl, binding.VirtualKey);
        Assert.Equal(Back, binding.SecondaryVirtualKey);
        Assert.True(binding.SuppressChordMembers);
        Assert.Equal("Left Ctrl+Mouse Back (button 4)", binding.DisplayName);
        Assert.Equal("Left Ctrl+Mouse Back (button 4)", HotkeyText.Describe(binding));
    }

    [Fact]
    public void A_mouse_button_recorded_before_its_key_says_which_to_press_first()
    {
        var capture = new HotkeyCaptureSession();
        capture.Press(Middle);
        capture.Press(LeftCtrl);
        capture.Release(Middle, HotkeyMode.Hold);
        var done = capture.Release(LeftCtrl, HotkeyMode.Hold);

        Assert.Equal(HotkeyCaptureOutcome.Completed, done.Outcome);
        Assert.Equal(
            "Press Left Ctrl before Middle mouse button when you use this chord: a mouse button pressed first still " +
            "reaches the app under the pointer.",
            done.Message);
        Assert.Equal(Middle, done.Binding!.VirtualKey);
        Assert.Equal(LeftCtrl, done.Binding.SecondaryVirtualKey);
    }

    [Fact]
    public void Two_mouse_buttons_make_a_chord_and_the_warning_says_the_first_reaches_the_app()
    {
        var capture = new HotkeyCaptureSession();
        capture.Press(Back);
        capture.Press(Forward);
        capture.Release(Forward, HotkeyMode.Hold);
        var done = capture.Release(Back, HotkeyMode.Hold);

        Assert.Equal("Mouse Back (button 4)+Mouse Forward (button 5)", done.Binding!.DisplayName);
        Assert.StartsWith("Whichever mouse button of this chord you press first still reaches the app", done.Message);
    }

    [Theory]
    [InlineData(MouseButtons.Left)]
    [InlineData(MouseButtons.Right)]
    public void The_left_and_right_buttons_are_refused_and_keep_their_meaning(uint button)
    {
        var capture = new HotkeyCaptureSession();

        var press = capture.Press(button);
        Assert.Equal(HotkeyCaptureOutcome.Refused, press.Outcome);
        Assert.False(press.Handled); // so the click still reaches Cancel, the mode box or anything else
        Assert.Equal(HotkeyCaptureSession.RefusedMessage, press.Message);
        var release = capture.Release(button, HotkeyMode.Hold);
        Assert.Equal(HotkeyCaptureOutcome.Unchanged, release.Outcome);
        Assert.False(release.Handled);
        Assert.Empty(capture.Recorded);

        // Nothing was recorded: the next input is the first.
        Assert.Equal(HotkeyCaptureOutcome.Recorded, capture.Press(Back).Outcome);
        Assert.Equal(Back, capture.Release(Back, HotkeyMode.Hold).Binding!.VirtualKey);
    }

    [Fact]
    public void A_left_click_between_a_key_and_its_release_changes_nothing()
    {
        var capture = new HotkeyCaptureSession();
        capture.Press(F9);
        capture.Press(MouseButtons.Left);
        capture.Release(MouseButtons.Left, HotkeyMode.Hold);

        var done = capture.Release(F9, HotkeyMode.Hold);
        Assert.Equal(F9, done.Binding!.VirtualKey);
        Assert.Null(done.Binding.SecondaryVirtualKey);
    }

    [Fact]
    public void Escape_cancels_and_a_third_input_is_refused_as_too_many()
    {
        var cancelled = new HotkeyCaptureSession();
        cancelled.Press(Back);
        Assert.Equal(HotkeyCaptureOutcome.Cancelled, cancelled.Press(Escape).Outcome);

        var capture = new HotkeyCaptureSession();
        capture.Press(LeftCtrl);
        capture.Press(Back);
        var third = capture.Press(Forward);
        Assert.Equal(HotkeyCaptureOutcome.TooMany, third.Outcome);
        Assert.True(third.Handled);
        Assert.Equal(HotkeyCaptureSession.TooManyMessage, third.Message);

        Assert.Equal(HotkeyCaptureOutcome.Unchanged, capture.Release(Forward, HotkeyMode.Hold).Outcome);
        Assert.Equal(HotkeyCaptureOutcome.Unchanged, capture.Release(Back, HotkeyMode.Hold).Outcome);
        var done = capture.Release(LeftCtrl, HotkeyMode.Hold);
        Assert.Equal(LeftCtrl, done.Binding!.VirtualKey);
        Assert.Equal(Back, done.Binding.SecondaryVirtualKey);
    }

    [Fact]
    public void A_release_never_seen_going_down_and_a_repeat_change_nothing()
    {
        // The key that pressed Set, or a button already held when capture began, comes up first.
        var capture = new HotkeyCaptureSession();
        var orphan = capture.Release(Back, HotkeyMode.Hold);
        Assert.Equal(HotkeyCaptureOutcome.Unchanged, orphan.Outcome);
        Assert.True(orphan.Handled); // a side button's release is still kept from WPF, which would go Back

        capture.Press(PageDown);
        Assert.Equal(HotkeyCaptureOutcome.Unchanged, capture.Press(PageDown).Outcome); // autorepeat
        Assert.Equal(HotkeyCaptureOutcome.Unchanged, capture.Press(0).Outcome); // a key WPF maps to no code
        Assert.Equal(new[] { PageDown }, capture.Recorded);
        Assert.Equal(PageDown, capture.Release(PageDown, HotkeyMode.Hold).Binding!.VirtualKey);
    }

    [Fact]
    public void Keys_are_captured_exactly_as_before_named_by_code_and_layout()
    {
        string? Layout(uint key) => key == 0xBA ? ";" : null;
        var capture = new HotkeyCaptureSession(Layout);

        Assert.Equal("Page Down  (add another key or mouse button, or release)", capture.Press(PageDown).Text);
        Assert.Equal("Page Down+;  (release to set)", capture.Press(0xBA).Text);
        capture.Release(0xBA, HotkeyMode.Hold);
        var done = capture.Release(PageDown, HotkeyMode.Hold);

        Assert.Equal(
            new HotkeyBinding(
                PageDown, KeyModifiers.None, HotkeyMode.Hold, Suppress: true, "Page Down+;",
                SecondaryVirtualKey: 0xBA, SuppressChordMembers: true),
            done.Binding);
    }

    [Fact]
    public void Build_takes_one_or_two_inputs()
    {
        Assert.Throws<ArgumentException>(() => HotkeyCaptureSession.Build([], HotkeyMode.Hold));
        Assert.Throws<ArgumentException>(() => HotkeyCaptureSession.Build([F9, Back, Forward], HotkeyMode.Hold));
        Assert.Equal(
            new HotkeyBinding(Back, KeyModifiers.None, HotkeyMode.Hold, Suppress: true, "Mouse Back (button 4)"),
            HotkeyCaptureSession.Build([Back], HotkeyMode.Hold));
    }

    [Fact]
    public void A_mouse_button_binding_is_stored_in_the_existing_settings_and_read_back_unchanged()
    {
        // No new settings field and no schema change: the button is the binding's virtual-key code, which every build
        // since the chords has stored, so 0.4.3 reads this document as it is and shows the stored name.
        using var db = ScribeDatabase.CreateInMemory();
        var repository = new SettingsRepository(db);
        var settings = AppSettings.CreateDefault();
        settings.Hotkey = HotkeyCaptureSession.Build([LeftCtrl, Back], HotkeyMode.Hold);
        settings.DictationOnlyHotkey = HotkeyCaptureSession.Build([Middle], HotkeyMode.Toggle);
        repository.Save(settings);

        var loaded = new SettingsRepository(db).Load();

        Assert.Equal(settings.Hotkey, loaded.Hotkey);
        Assert.Equal(settings.DictationOnlyHotkey, loaded.DictationOnlyHotkey);
        using var document = System.Text.Json.JsonDocument.Parse(repository.Get("app_settings")!);
        var hotkey = document.RootElement.GetProperty("hotkey");
        Assert.Equal(LeftCtrl, hotkey.GetProperty("virtualKey").GetUInt32());
        Assert.Equal(Back, hotkey.GetProperty("secondaryVirtualKey").GetUInt32());
        Assert.Equal("Left Ctrl+Mouse Back (button 4)", hotkey.GetProperty("displayName").GetString());
        Assert.Equal(Middle, document.RootElement.GetProperty("dictationOnlyHotkey").GetProperty("virtualKey").GetUInt32());
    }

    [Fact]
    public void The_welcome_names_a_mouse_button_the_way_settings_does()
    {
        var settings = AppSettings.CreateDefault();
        settings.Hotkey = HotkeyCaptureSession.Build([Back], HotkeyMode.Hold);
        settings.DictationOnlyHotkey = HotkeyCaptureSession.Build([LeftCtrl, Middle], HotkeyMode.Toggle);

        var (title, body) = HotkeyText.Gesture(settings);

        Assert.Equal("Hold, speak, release", title);
        Assert.StartsWith("Hold Mouse Back (button 4) and start talking.", body);
        Assert.Contains("Press Left Ctrl+Middle mouse button instead to dictate without AI cleanup.", body);
        Assert.DoesNotContain("Fn", body);
    }

    [Fact]
    public void Everything_the_capture_says_is_dash_free_and_the_hint_covers_the_other_buttons()
    {
        var texts = new[]
        {
            HotkeyCaptureSession.Prompt, HotkeyCaptureSession.TooManyMessage, HotkeyCaptureSession.RefusedMessage,
            HotkeyCaptureSession.MouseButtonsHint, KeyNames.Of(Middle)!, KeyNames.Of(Back)!, KeyNames.Of(Forward)!,
        };
        Assert.All(texts, text =>
        {
            Assert.DoesNotContain('\u2014', text);
            Assert.DoesNotContain('\u2013', text);
        });

        Assert.Contains("F13 to F24", HotkeyCaptureSession.MouseButtonsHint);
        Assert.Contains("Left and right clicks can't be used", HotkeyCaptureSession.MouseButtonsHint);
        Assert.Contains("press the key first", HotkeyCaptureSession.MouseButtonsHint);
        Assert.Contains("Ctrl, Shift, Alt, Win or the Narrator key", HotkeyCaptureSession.MouseButtonsHint);
    }

    [Fact]
    public void The_settings_window_maps_mouse_buttons_into_the_capture_and_keeps_side_buttons_from_wpf()
    {
        // The window has no test harness, so its wiring is pinned by source: the Preview mouse events of the whole window
        // go through the Core capture, a handled step marks the event handled, and the title bar's buttons come in as
        // window messages.
        var root = RepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "Scribe.App", "Settings", "SettingsWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "src", "Scribe.App", "Settings", "SettingsWindow.xaml.cs"));
        var windowTag = xaml[..xaml.IndexOf('>', xaml.IndexOf("<ui:FluentWindow", StringComparison.Ordinal))];

        Assert.Contains("PreviewMouseDown=\"HotkeyCapture_PreviewMouseDown\"", windowTag);
        Assert.Contains("PreviewMouseUp=\"HotkeyCapture_PreviewMouseUp\"", windowTag);
        Assert.Contains("_capture.Press(HotkeyCapture.VirtualKeyOf(e.ChangedButton))", code);
        Assert.Contains("_capture.Release(HotkeyCapture.VirtualKeyOf(e.ChangedButton), ActiveSelectedMode)", code);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(code, @"if \(step\.Handled\)\s*\{\s*e\.Handled = true;").Count);
        Assert.Contains("AddHook(CaptureNonClientMouseButtons)", code);
        Assert.Contains("MouseButtonsHintText.Text = HotkeyCaptureSession.MouseButtonsHint;", code);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Scribe.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
