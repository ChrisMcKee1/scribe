using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

/// <summary>
/// The window's side of a setting changed from outside it, for a value choice such as the microphone: the same rules the
/// AI cleanup switch follows (<see cref="ExternalSwitchSyncTests"/>), which now delegates to it.
/// </summary>
public sealed class ExternalChoiceSyncTests
{
    private static readonly MicrophoneSelection Yeti = new("yeti", "Blue Yeti");
    private static readonly MicrophoneSelection Usb = new("usb", "USB Microphone");

    [Fact]
    public void An_outside_change_the_window_can_show_is_shown_at_once_and_counts_as_its_intent()
    {
        var sync = new ExternalChoiceSync<MicrophoneSelection>();
        var revision = ExternalSwitchSync.NextRevision();

        Assert.True(sync.TryAdopt(Yeti, revision, canShowNow: true, out var showNow));

        Assert.True(showNow);
        Assert.Equal(revision, sync.NewestRevision);
        Assert.Null(sync.Release());
    }

    [Fact]
    public void A_change_that_arrives_while_the_list_is_open_is_saved_and_shown_when_it_closes()
    {
        var sync = new ExternalChoiceSync<MicrophoneSelection>();

        Assert.True(sync.TryAdopt(Yeti, ExternalSwitchSync.NextRevision(), canShowNow: false, out var showNow));
        Assert.False(showNow);

        // A save before the picker catches up writes the tray's choice, not what the picker still shows.
        Assert.Equal(Yeti, sync.ForSave(MicrophoneSelection.WindowsDefault));
        Assert.Equal(Yeti, sync.Release());
        Assert.Null(sync.Release());
    }

    [Fact]
    public void Word_of_an_older_outside_change_never_replaces_the_users_newer_choice()
    {
        var sync = new ExternalChoiceSync<MicrophoneSelection>();
        var trayRevision = ExternalSwitchSync.NextRevision();
        sync.TryAdopt(Yeti, trayRevision, canShowNow: true, out _);

        sync.UserChanged(); // the user then picks another microphone in the window

        Assert.False(sync.TryAdopt(Yeti, trayRevision, canShowNow: true, out _)); // the tray's "it was stored"
        Assert.Equal(Usb, sync.ForSave(Usb));
        Assert.True(sync.NewestRevision > trayRevision);
    }

    [Fact]
    public void A_save_forgets_the_intent_and_the_next_word_is_taken_again()
    {
        var sync = new ExternalChoiceSync<MicrophoneSelection>();
        var revision = ExternalSwitchSync.NextRevision();
        sync.UserChanged();

        sync.Saved();

        Assert.Equal(0, sync.NewestRevision);
        Assert.True(sync.TryAdopt(Yeti, revision, canShowNow: true, out _));
    }

    [Fact]
    public void The_latest_outside_change_wins()
    {
        var sync = new ExternalChoiceSync<MicrophoneSelection>();

        sync.TryAdopt(Yeti, ExternalSwitchSync.NextRevision(), canShowNow: false, out _);
        sync.TryAdopt(Usb, ExternalSwitchSync.NextRevision(), canShowNow: false, out _);

        Assert.Equal(Usb, sync.ForSave(MicrophoneSelection.WindowsDefault));
    }
}
