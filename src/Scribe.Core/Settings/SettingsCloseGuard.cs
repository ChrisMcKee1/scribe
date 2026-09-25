namespace Scribe.Core.Settings;

/// <summary>
/// The Settings sections whose unsaved changes the close guard tracks (plan 3.9); nothing else is named. The prompt names
/// each by the title its page shows, which is not always the member's name (see <see cref="SettingsCloseGuard.Prompt"/>).
/// </summary>
[Flags]
public enum UnsavedSections
{
    /// <summary>Nothing tracked is unsaved.</summary>
    None = 0,

    /// <summary>The word packs (the Word packs tab of the Dictionary page); named "Word packs".</summary>
    Libraries = 1,

    /// <summary>The personal dictionary; named "Dictionary".</summary>
    Dictionary = 2,

    /// <summary>The voice snippets; named "Voice snippets".</summary>
    Snippets = 4,
}

/// <summary>What is asking the Settings window to close.</summary>
public enum CloseTrigger
{
    /// <summary>The Cancel button.</summary>
    CancelButton,

    /// <summary>An Escape no control consumed (<see cref="SettingsCloseGuard.NextEscape"/> returned the guard).</summary>
    Escape,

    /// <summary>Alt+F4.</summary>
    AltF4,

    /// <summary>The title bar's close button.</summary>
    CloseButton,

    /// <summary>Quit from the tray.</summary>
    TrayQuit,

    /// <summary>Scribe restarting to install an update.</summary>
    UpdateRestart,

    /// <summary>The user signing out of Windows.</summary>
    SignOut,

    /// <summary>Windows shutting down or restarting.</summary>
    Shutdown,
}

/// <summary>A button of the close prompt.</summary>
public enum CloseChoice
{
    /// <summary>Save, then close.</summary>
    Save,

    /// <summary>Discard changes: close without saving.</summary>
    DiscardChanges,

    /// <summary>Keep editing: stay open with nothing changed; the default.</summary>
    KeepEditing,
}

/// <summary>What closing the window should do.</summary>
/// <param name="Ask">Ask first with <paramref name="Prompt"/>; false means close now.</param>
/// <param name="Prompt">"You have unsaved changes to Word packs and Dictionary.", naming only the tracked sections; null when not asking.</param>
/// <param name="Choices">The prompt's buttons in order: Save, Discard changes, Keep editing; empty when not asking.</param>
/// <param name="DefaultChoice">The focused button, Keep editing, so a stray Enter commits nothing half-typed; null when not asking.</param>
public sealed record CloseDecision(bool Ask, string? Prompt, IReadOnlyList<CloseChoice> Choices, CloseChoice? DefaultChoice);

/// <summary>What an Escape does, in the order the window tries them.</summary>
public enum EscapeAction
{
    /// <summary>Stop capturing a hotkey, which owns the keyboard while it runs.</summary>
    CancelHotkeyCapture,

    /// <summary>Cancel the IME composition; the IME takes the key.</summary>
    CancelComposition,

    /// <summary>Cancel the cell edit, putting the cell's value back.</summary>
    CancelEdit,

    /// <summary>Close the open menu.</summary>
    CloseMenu,

    /// <summary>Clear the focused, non-empty search box.</summary>
    ClearSearch,

    /// <summary>Nothing consumed it: the close guard decides (<see cref="CloseTrigger.Escape"/>).</summary>
    CloseGuard,
}

/// <summary>What the window knows when Escape is pressed.</summary>
/// <param name="HotkeyCapture">A hotkey box is capturing: every key is the hotkey being recorded.</param>
/// <param name="ImeComposing">An editor holds an IME composition.</param>
/// <param name="EditingCell">A grid cell is being edited.</param>
/// <param name="MenuOpen">A menu or a flyout is open.</param>
/// <param name="SearchFocused">The search box has the focus.</param>
/// <param name="SearchHasText">The search box holds text.</param>
public readonly record struct EscapeState(
    bool HotkeyCapture, bool ImeComposing, bool EditingCell, bool MenuOpen, bool SearchFocused, bool SearchHasText);

/// <summary>
/// The Settings window's keyboard commands (plan 3.9, S6). The libraries page is the Word packs tab of the Dictionary page
/// (the maintainer's product decision and the Settings redesign); the identifiers keep "library".
/// </summary>
public enum SettingsAccelerator
{
    /// <summary>Ctrl+S, Save, on any page.</summary>
    Save,

    /// <summary>Ctrl+F, Search all word packs, on the Word packs tab.</summary>
    Find,

    /// <summary>Ctrl+N, Add term, on the Word packs tab.</summary>
    AddTerm,

    /// <summary>Ctrl+Shift+N, New word pack, on the Word packs tab.</summary>
    NewLibrary,
}

/// <summary>What the window knows when an accelerator is pressed.</summary>
/// <param name="HotkeyCapture">A hotkey box is capturing: every key is the hotkey being recorded.</param>
/// <param name="ImeComposing">An editor holds an IME composition.</param>
/// <param name="OnLibrariesPage">The Word packs tab is the one shown (the libraries page, by the identifier's name).</param>
public readonly record struct AcceleratorState(bool HotkeyCapture, bool ImeComposing, bool OnLibrariesPage);

/// <summary>
/// The Settings window's close guard, Escape order and accelerator gate (plan 3.9, review findings S4, S6, R16, UX-02).
/// </summary>
/// <remarks>
/// Every close the user starts (Cancel, an unconsumed Escape, Alt+F4, the close button, the tray's Quit) asks when a
/// tracked section has unsaved changes, with Keep editing focused, because the prompt is usually reached by one Escape
/// too many and Enter must not commit half-typed rows. A close Windows or an update starts never waits on a prompt.
/// Profiles and general settings are not tracked yet: that needs a baseline of the settings document free of side
/// effects, which is a later change. Pure.
/// </remarks>
public static class SettingsCloseGuard
{
    private static readonly IReadOnlyList<CloseChoice> PromptChoices =
        [CloseChoice.Save, CloseChoice.DiscardChanges, CloseChoice.KeepEditing];

    /// <summary>Whether to ask before closing, and with what.</summary>
    public static CloseDecision Decide(UnsavedSections sections, CloseTrigger trigger)
    {
        var tracked = sections & (UnsavedSections.Libraries | UnsavedSections.Dictionary | UnsavedSections.Snippets);
        if (tracked == UnsavedSections.None || trigger is CloseTrigger.UpdateRestart or CloseTrigger.SignOut or CloseTrigger.Shutdown)
        {
            return new CloseDecision(false, null, [], null);
        }

        return new CloseDecision(true, Prompt(tracked), PromptChoices, CloseChoice.KeepEditing);
    }

    /// <summary>
    /// "You have unsaved changes to Word packs, Dictionary and Voice snippets.", naming only the sections given, in that
    /// order, each by the title its page shows (<see cref="UnsavedSections.Libraries"/> as Word packs,
    /// <see cref="UnsavedSections.Snippets"/> as Voice snippets); empty for none.
    /// </summary>
    public static string Prompt(UnsavedSections sections)
    {
        var names = new List<string>(3);
        if (sections.HasFlag(UnsavedSections.Libraries))
        {
            names.Add("Word packs");
        }

        if (sections.HasFlag(UnsavedSections.Dictionary))
        {
            names.Add("Dictionary");
        }

        if (sections.HasFlag(UnsavedSections.Snippets))
        {
            names.Add("Voice snippets");
        }

        return names.Count switch
        {
            0 => string.Empty,
            1 => $"You have unsaved changes to {names[0]}.",
            _ => $"You have unsaved changes to {string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}.",
        };
    }

    /// <summary>
    /// What Escape does: stop a hotkey capture, then cancel an IME composition or a cell edit, close an open menu, clear
    /// a focused non-empty search, and only then ask the close guard.
    /// </summary>
    public static EscapeAction NextEscape(EscapeState state) =>
        state.HotkeyCapture ? EscapeAction.CancelHotkeyCapture
        : state.ImeComposing ? EscapeAction.CancelComposition
        : state.EditingCell ? EscapeAction.CancelEdit
        : state.MenuOpen ? EscapeAction.CloseMenu
        : state.SearchFocused && state.SearchHasText ? EscapeAction.ClearSearch
        : EscapeAction.CloseGuard;

    /// <summary>
    /// Whether an accelerator may run: never while a hotkey capture owns the keyboard (Ctrl+S captured as a hotkey must
    /// not save) or an IME composes, and the Libraries commands only on their page.
    /// </summary>
    public static bool CanRunAccelerator(SettingsAccelerator command, AcceleratorState state) =>
        !state.HotkeyCapture
        && !state.ImeComposing
        && (command == SettingsAccelerator.Save || state.OnLibrariesPage);
}
