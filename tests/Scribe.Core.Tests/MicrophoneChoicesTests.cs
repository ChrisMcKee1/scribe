using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

/// <summary>
/// The microphone picker Settings and the tray both show. The first entry follows Windows and names the device that means
/// right now; every microphone follows by name with the Windows default marked; and a saved microphone that is away stays
/// listed and chosen, so opening the picker never changes the choice by itself.
/// </summary>
public sealed class MicrophoneChoicesTests
{
    private static readonly AudioDevice Insta = new("insta", "Microphone (6- Insta360 Link 2 Pro)", IsDefault: true);
    private static readonly AudioDevice Elgato = new("elgato", "Mic In (Elgato Wave Neo)", IsDefault: false, IsCommunicationsDefault: true);
    private static readonly AudioDevice ChatMix = new("chat", "Chat Mix (Elgato Virtual Audio)", IsDefault: false);

    [Fact]
    public void The_windows_default_comes_first_and_names_the_device_it_means_now()
    {
        var menu = MicrophoneChoices.Build([Elgato, Insta, ChatMix], MicrophoneSelection.WindowsDefault);

        var first = menu.Choices[0];
        Assert.Equal(MicrophoneChoiceKind.WindowsDefault, first.Kind);
        Assert.Equal("Windows default: Microphone (6- Insta360 Link 2 Pro)", first.Label);
        Assert.Equal(MicrophoneSelection.WindowsDefault, first.Selection);
        Assert.Equal(0, menu.SelectedIndex);
    }

    [Fact]
    public void Microphones_follow_by_name_with_the_windows_default_marked()
    {
        var menu = MicrophoneChoices.Build([Insta, Elgato, ChatMix], MicrophoneSelection.WindowsDefault);

        Assert.Equal(
            [
                "Windows default: Microphone (6- Insta360 Link 2 Pro)",
                "Chat Mix (Elgato Virtual Audio)",
                "Mic In (Elgato Wave Neo)",
                "Microphone (6- Insta360 Link 2 Pro) (default)",
            ],
            menu.Choices.Select(choice => choice.Label));
        Assert.All(menu.Choices.Skip(1), choice => Assert.Equal(MicrophoneChoiceKind.Device, choice.Kind));

        // Choosing a device saves its name without the mark, so a later "Unavailable:" entry reads right.
        var insta = menu.Choices[3];
        Assert.Equal(new MicrophoneSelection("insta", "Microphone (6- Insta360 Link 2 Pro)"), insta.Selection);
    }

    [Fact]
    public void The_windows_default_names_the_new_device_as_soon_as_windows_moves_it()
    {
        var before = MicrophoneChoices.Build([Insta, Elgato], MicrophoneSelection.WindowsDefault);
        var after = MicrophoneChoices.Build(
            [Insta with { IsDefault = false }, Elgato with { IsDefault = true }], MicrophoneSelection.WindowsDefault);

        Assert.Equal("Windows default: Microphone (6- Insta360 Link 2 Pro)", before.Choices[0].Label);
        Assert.Equal("Windows default: Mic In (Elgato Wave Neo)", after.Choices[0].Label);
        Assert.Equal(0, after.SelectedIndex);
    }

    [Fact]
    public void A_chosen_microphone_that_is_there_is_the_selected_entry()
    {
        var menu = MicrophoneChoices.Build([Insta, Elgato], new MicrophoneSelection("elgato", "Mic In (Elgato Wave Neo)"));

        Assert.Equal("Mic In (Elgato Wave Neo)", menu.Selected.Label);
        Assert.Equal(MicrophoneChoiceKind.Device, menu.Selected.Kind);
    }

    [Fact]
    public void A_chosen_microphone_that_is_away_stays_listed_chosen_and_named()
    {
        var saved = new MicrophoneSelection("yeti", "Blue Yeti");

        var menu = MicrophoneChoices.Build([Insta, Elgato], saved);

        Assert.Equal(MicrophoneChoiceKind.Unavailable, menu.Selected.Kind);
        Assert.Equal("Unavailable: Blue Yeti", menu.Selected.Label);
        Assert.Equal(saved, menu.Selected.Selection);
        Assert.Equal(menu.Choices.Count - 1, menu.SelectedIndex);
    }

    [Fact]
    public void An_away_microphone_saved_without_a_name_still_reads_as_something()
    {
        var menu = MicrophoneChoices.Build([Insta], new MicrophoneSelection("yeti", null));

        Assert.Equal("Unavailable: saved microphone", menu.Selected.Label);
    }

    [Fact]
    public void With_no_microphone_at_all_only_the_windows_default_remains_and_says_so()
    {
        var menu = MicrophoneChoices.Build([], MicrophoneSelection.WindowsDefault);

        var only = Assert.Single(menu.Choices);
        Assert.Equal("Windows default (no microphone found)", only.Label);
    }

    [Fact]
    public void A_blank_saved_id_is_the_windows_default()
    {
        var menu = MicrophoneChoices.Build([Insta], new MicrophoneSelection("  ", "stale name"));

        Assert.Equal(0, menu.SelectedIndex);
        Assert.Equal(MicrophoneSelection.WindowsDefault, MicrophoneSelection.Normalize("  ", "stale name"));
    }

    [Fact]
    public void An_entry_reads_out_its_label()
    {
        // The picker draws Label, and a screen reader announces ToString.
        var menu = MicrophoneChoices.Build([Insta], MicrophoneSelection.WindowsDefault);

        Assert.All(menu.Choices, choice => Assert.Equal(choice.Label, choice.ToString()));
    }

    [Fact]
    public void A_selection_round_trips_through_the_settings()
    {
        var settings = AppSettings.CreateDefault();

        new MicrophoneSelection("yeti", "Blue Yeti").ApplyTo(settings);
        Assert.Equal(("yeti", "Blue Yeti"), (settings.InputDeviceId, settings.InputDeviceName));
        Assert.Equal(new MicrophoneSelection("yeti", "Blue Yeti"), MicrophoneSelection.From(settings));

        MicrophoneSelection.WindowsDefault.ApplyTo(settings);
        Assert.Null(settings.InputDeviceId);
        Assert.Null(settings.InputDeviceName);
    }

    [Fact]
    public void A_short_description_names_the_choice()
    {
        Assert.Equal("the Windows default microphone", MicrophoneChoices.Describe(MicrophoneSelection.WindowsDefault));
        Assert.Equal("Blue Yeti", MicrophoneChoices.Describe(new MicrophoneSelection("yeti", "Blue Yeti")));
        Assert.Equal("the saved microphone", MicrophoneChoices.Describe(new MicrophoneSelection("yeti", null)));
    }
}
