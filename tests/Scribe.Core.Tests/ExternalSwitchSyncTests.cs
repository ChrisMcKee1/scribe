using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

/// <summary>
/// The tray's AI cleanup switch against an open Settings window, including the orderings that used to lose a choice:
/// the tray change arrives while the window records a hotkey, and the window's next save wrote its stale switch; and
/// word that the tray's change was stored arrives after the user has set the switch the other way.
/// </summary>
public sealed class ExternalSwitchSyncTests
{
    [Fact]
    public void An_outside_change_the_window_can_show_is_shown_at_once()
    {
        var sync = new ExternalSwitchSync();

        Assert.True(sync.TryAdopt(true, ExternalSwitchSync.NextRevision(), canShowNow: true, out var showNow));
        Assert.True(showNow);
        Assert.False(sync.HasWaitingChange);
        Assert.Null(sync.Release());
    }

    [Fact]
    public void A_change_that_arrives_during_a_hotkey_capture_is_saved_and_shown_when_the_capture_ends()
    {
        // The window shows AI cleanup off; the tray turns it on while a hotkey is being recorded.
        var sync = new ExternalSwitchSync();
        const bool Shown = false;

        Assert.True(sync.TryAdopt(true, ExternalSwitchSync.NextRevision(), canShowNow: false, out var showNow));
        Assert.False(showNow);

        // A save before the switch catches up writes the tray's value, not the stale switch.
        Assert.True(sync.ForSave(Shown));

        // The capture ends: the switch shows it, once.
        Assert.True(sync.Release());
        Assert.Null(sync.Release());
        Assert.True(sync.ForSave(shown: true));
    }

    [Fact]
    public void The_latest_outside_change_wins()
    {
        var sync = new ExternalSwitchSync();

        sync.TryAdopt(true, ExternalSwitchSync.NextRevision(), canShowNow: false, out _);
        sync.TryAdopt(false, ExternalSwitchSync.NextRevision(), canShowNow: false, out _);

        Assert.False(sync.ForSave(shown: true));
        Assert.False(sync.Release());
    }

    [Fact]
    public void A_change_shown_at_once_discards_one_still_waiting()
    {
        var sync = new ExternalSwitchSync();

        sync.TryAdopt(true, ExternalSwitchSync.NextRevision(), canShowNow: false, out _);
        Assert.True(sync.TryAdopt(false, ExternalSwitchSync.NextRevision(), canShowNow: true, out var showNow));
        Assert.True(showNow);

        Assert.Null(sync.Release());
        Assert.False(sync.ForSave(shown: false));
    }

    [Fact]
    public void The_users_own_change_is_newer_than_an_outside_change_still_waiting()
    {
        var sync = new ExternalSwitchSync();

        sync.TryAdopt(true, ExternalSwitchSync.NextRevision(), canShowNow: false, out _);
        sync.UserChanged();

        Assert.False(sync.HasWaitingChange);
        Assert.False(sync.ForSave(shown: false));
        Assert.Null(sync.Release());
    }

    [Fact]
    public void Word_that_a_tray_change_ended_never_undoes_the_users_later_click()
    {
        // The tray turns AI cleanup on during a hotkey capture, the capture ends and shows it, the user turns it off,
        // and only then does word arrive that the tray's change was stored.
        var sync = new ExternalSwitchSync();
        var tray = ExternalSwitchSync.NextRevision();
        Assert.True(sync.TryAdopt(true, tray, canShowNow: false, out _));
        Assert.True(sync.Release());
        sync.UserChanged();

        Assert.False(sync.TryAdopt(true, tray, canShowNow: true, out var showNow));
        Assert.False(showNow);
        Assert.False(sync.ForSave(shown: false));

        // Nor does word that it failed, which puts back what dictation was using.
        Assert.False(sync.TryAdopt(true, tray, canShowNow: true, out _));
        Assert.False(sync.ForSave(shown: false));
    }

    [Fact]
    public void Word_of_an_older_tray_change_never_undoes_a_newer_one()
    {
        var sync = new ExternalSwitchSync();
        var on = ExternalSwitchSync.NextRevision();
        var off = ExternalSwitchSync.NextRevision();
        sync.TryAdopt(true, on, canShowNow: true, out _);
        sync.TryAdopt(false, off, canShowNow: true, out _);

        // The first write lands first, so word of it arrives while the second is still on its way.
        Assert.False(sync.TryAdopt(true, on, canShowNow: true, out _));
        Assert.False(sync.ForSave(shown: false));

        // The second's own word still gets through.
        Assert.True(sync.TryAdopt(false, off, canShowNow: true, out var showNow));
        Assert.True(showNow);
    }

    [Fact]
    public void Word_of_a_tray_change_reaches_a_window_that_opened_before_its_write_landed()
    {
        // The tray's click came before this window opened, so the document the window loaded may predate the write.
        var tray = ExternalSwitchSync.NextRevision();
        var sync = new ExternalSwitchSync();

        Assert.True(sync.TryAdopt(true, tray, canShowNow: true, out var showNow));
        Assert.True(showNow);
    }

    [Fact]
    public void A_click_in_a_window_opened_after_the_tray_change_is_newer_than_word_of_it()
    {
        var tray = ExternalSwitchSync.NextRevision();
        var sync = new ExternalSwitchSync();
        sync.UserChanged();

        Assert.False(sync.TryAdopt(true, tray, canShowNow: true, out _));
    }

    [Fact]
    public void After_a_save_word_of_an_earlier_change_is_the_stored_truth_and_is_shown()
    {
        // The user turns the switch off and saves before the tray's earlier write lands. That write then reaches the
        // database last, and word of it carries the settings as stored: leaving the window on its own value would show
        // something the settings no longer say.
        var sync = new ExternalSwitchSync();
        var tray = ExternalSwitchSync.NextRevision();
        sync.TryAdopt(true, tray, canShowNow: true, out _);
        sync.UserChanged();
        sync.Saved();

        Assert.True(sync.TryAdopt(true, tray, canShowNow: true, out var showNow));
        Assert.True(showNow);

        // A later click is again newer than anything stored before it.
        sync.UserChanged();
        Assert.False(sync.TryAdopt(true, tray, canShowNow: true, out _));
    }

    [Fact]
    public void Revisions_only_grow()
    {
        var first = ExternalSwitchSync.NextRevision();

        Assert.True(ExternalSwitchSync.NextRevision() > first);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void With_nothing_waiting_a_save_writes_what_the_switch_shows(bool shown)
    {
        Assert.Equal(shown, new ExternalSwitchSync().ForSave(shown));
    }
}