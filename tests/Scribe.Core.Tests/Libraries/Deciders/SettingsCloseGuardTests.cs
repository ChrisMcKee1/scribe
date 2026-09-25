using Scribe.Core.Settings;

namespace Scribe.Core.Tests.Libraries.Deciders;

/// <summary>
/// D-8: the close guard for every trigger, naming only the tracked sections, Keep editing the default, restart,
/// sign-out and shutdown never waiting; the Escape order; and Ctrl+S captured as a hotkey not saving.
/// </summary>
public sealed class SettingsCloseGuardTests
{
    [Theory]
    [InlineData(CloseTrigger.CancelButton)]
    [InlineData(CloseTrigger.Escape)]
    [InlineData(CloseTrigger.AltF4)]
    [InlineData(CloseTrigger.CloseButton)]
    [InlineData(CloseTrigger.TrayQuit)]
    public void D8_a_close_the_user_starts_asks_when_a_tracked_section_is_unsaved_with_keep_editing_focused(CloseTrigger trigger)
    {
        var decision = SettingsCloseGuard.Decide(UnsavedSections.Libraries | UnsavedSections.Dictionary, trigger);

        Assert.True(decision.Ask);
        Assert.Equal("You have unsaved changes to Word packs and Dictionary.", decision.Prompt);
        Assert.Equal([CloseChoice.Save, CloseChoice.DiscardChanges, CloseChoice.KeepEditing], decision.Choices);
        Assert.Equal(CloseChoice.KeepEditing, decision.DefaultChoice);

        var clean = SettingsCloseGuard.Decide(UnsavedSections.None, trigger);
        Assert.False(clean.Ask);
        Assert.Null(clean.Prompt);
        Assert.Empty(clean.Choices);
    }

    [Theory]
    [InlineData(CloseTrigger.UpdateRestart)]
    [InlineData(CloseTrigger.SignOut)]
    [InlineData(CloseTrigger.Shutdown)]
    public void D8_an_update_restart_sign_out_or_shutdown_never_waits(CloseTrigger trigger)
    {
        var decision = SettingsCloseGuard.Decide(UnsavedSections.Libraries | UnsavedSections.Dictionary | UnsavedSections.Snippets, trigger);

        Assert.False(decision.Ask);
        Assert.Null(decision.DefaultChoice);
    }

    [Fact]
    public void D8_the_prompt_names_only_the_sections_it_tracks()
    {
        Assert.Equal("You have unsaved changes to Word packs.", SettingsCloseGuard.Prompt(UnsavedSections.Libraries));
        Assert.Equal("You have unsaved changes to Snippets.", SettingsCloseGuard.Prompt(UnsavedSections.Snippets));
        Assert.Equal(
            "You have unsaved changes to Word packs, Dictionary and Snippets.",
            SettingsCloseGuard.Prompt(UnsavedSections.Snippets | UnsavedSections.Libraries | UnsavedSections.Dictionary));
        Assert.Equal(string.Empty, SettingsCloseGuard.Prompt(UnsavedSections.None));

        // A flag the guard does not track (a future section) is never named and never asks on its own.
        var untracked = (UnsavedSections)64;
        Assert.False(SettingsCloseGuard.Decide(untracked, CloseTrigger.CancelButton).Ask);
        Assert.Equal("You have unsaved changes to Dictionary.",
            SettingsCloseGuard.Decide(untracked | UnsavedSections.Dictionary, CloseTrigger.CancelButton).Prompt);
    }

    [Fact]
    public void D8_escape_cancels_capture_then_composition_or_edit_then_a_menu_then_a_search_then_asks_the_guard()
    {
        var everything = new EscapeState(HotkeyCapture: true, ImeComposing: true, EditingCell: true, MenuOpen: true, SearchFocused: true, SearchHasText: true);

        Assert.Equal(EscapeAction.CancelHotkeyCapture, SettingsCloseGuard.NextEscape(everything));
        Assert.Equal(EscapeAction.CancelComposition, SettingsCloseGuard.NextEscape(everything with { HotkeyCapture = false }));
        Assert.Equal(EscapeAction.CancelEdit, SettingsCloseGuard.NextEscape(everything with { HotkeyCapture = false, ImeComposing = false }));
        Assert.Equal(EscapeAction.CloseMenu,
            SettingsCloseGuard.NextEscape(everything with { HotkeyCapture = false, ImeComposing = false, EditingCell = false }));
        Assert.Equal(EscapeAction.ClearSearch,
            SettingsCloseGuard.NextEscape(new EscapeState(false, false, false, false, SearchFocused: true, SearchHasText: true)));
        Assert.Equal(EscapeAction.CloseGuard,
            SettingsCloseGuard.NextEscape(new EscapeState(false, false, false, false, SearchFocused: true, SearchHasText: false)));
        Assert.Equal(EscapeAction.CloseGuard,
            SettingsCloseGuard.NextEscape(new EscapeState(false, false, false, false, SearchFocused: false, SearchHasText: true)));
    }

    [Fact]
    public void D8_ctrl_s_captured_as_a_hotkey_does_not_save_and_the_libraries_commands_stay_on_their_page()
    {
        var capturing = new AcceleratorState(HotkeyCapture: true, ImeComposing: false, OnLibrariesPage: true);
        var composing = new AcceleratorState(HotkeyCapture: false, ImeComposing: true, OnLibrariesPage: true);
        var libraries = new AcceleratorState(HotkeyCapture: false, ImeComposing: false, OnLibrariesPage: true);
        var elsewhere = libraries with { OnLibrariesPage = false };

        foreach (var command in Enum.GetValues<SettingsAccelerator>())
        {
            Assert.False(SettingsCloseGuard.CanRunAccelerator(command, capturing));
            Assert.False(SettingsCloseGuard.CanRunAccelerator(command, composing));
            Assert.True(SettingsCloseGuard.CanRunAccelerator(command, libraries));
        }

        Assert.True(SettingsCloseGuard.CanRunAccelerator(SettingsAccelerator.Save, elsewhere));
        Assert.False(SettingsCloseGuard.CanRunAccelerator(SettingsAccelerator.Find, elsewhere));
        Assert.False(SettingsCloseGuard.CanRunAccelerator(SettingsAccelerator.AddTerm, elsewhere));
        Assert.False(SettingsCloseGuard.CanRunAccelerator(SettingsAccelerator.NewLibrary, elsewhere));
    }
}
