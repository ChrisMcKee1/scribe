using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

public sealed class SettingsSectionLoadTests
{
    [Fact]
    public void Section_that_has_not_loaded_is_never_an_edited_empty_section()
    {
        var section = new SettingsSectionLoad();

        // The grid is empty before its rows arrive. Taking that for "the user deleted everything"
        // would make Save wipe the stored dictionary.
        Assert.Equal(SettingsSectionState.Unloaded, section.State);
        Assert.False(section.HasChanges(string.Empty));
        Assert.False(section.HasChanges("7|azure|Azure|True|True"));
        Assert.Null(section.Snapshot);

        Assert.True(section.TryBegin(string.Empty, out _));
        Assert.Equal(SettingsSectionState.Loading, section.State);
        Assert.False(section.HasChanges(string.Empty));
        Assert.False(section.HasChanges("anything"));
    }

    [Fact]
    public void Loaded_empty_section_still_detects_real_edits()
    {
        var section = new SettingsSectionLoad();
        Assert.True(section.TryBegin(string.Empty, out var ticket));
        Assert.True(section.Publish(ticket, string.Empty));

        Assert.True(section.IsLoaded);
        Assert.False(section.HasChanges(string.Empty));
        Assert.True(section.HasChanges("0|azure|Azure|True|True"));
    }

    [Fact]
    public void Failed_load_stays_out_of_save_and_can_be_retried()
    {
        var section = new SettingsSectionLoad();
        Assert.True(section.TryBegin(string.Empty, out var ticket));

        Assert.True(section.Fail(ticket));
        Assert.Equal(SettingsSectionState.Failed, section.State);
        Assert.False(section.HasChanges("anything"));

        Assert.True(section.TryBegin(string.Empty, out var retry));
        Assert.True(section.Publish(retry, "a"));
        Assert.True(section.IsLoaded);
    }

    [Fact]
    public void Late_load_after_a_write_around_the_grid_is_discarded_and_the_reload_wins()
    {
        var section = new SettingsSectionLoad();
        Assert.True(section.TryBegin(string.Empty, out var stale));

        // A quick add wrote to storage while the read was running, so that read may predate it.
        Assert.True(section.Invalidate());
        Assert.False(section.CanPublish(stale));

        Assert.True(section.TryBegin(string.Empty, out var fresh));
        Assert.False(section.Publish(stale, "old"));
        Assert.False(section.Fail(stale));
        Assert.Equal(SettingsSectionState.Loading, section.State);

        Assert.True(section.Publish(fresh, "new"));
        Assert.Equal("new", section.Snapshot);
    }

    [Fact]
    public void Late_load_never_replaces_edits_made_after_the_section_loaded()
    {
        var section = new SettingsSectionLoad();
        Assert.True(section.TryBegin(string.Empty, out var first));
        Assert.True(section.Publish(first, "a"));

        // The user edited the rows; a refresh would overwrite that work.
        Assert.False(section.TryBegin("a+edit", out _));
        Assert.True(section.IsLoaded);
        Assert.Equal("a", section.Snapshot);
        Assert.True(section.HasChanges("a+edit"));

        // A write around a loaded grid is merged into the rows by the caller, not reloaded.
        Assert.False(section.Invalidate());
        Assert.True(section.IsLoaded);

        // A stale completion from before the load cannot sneak in either.
        Assert.False(section.Publish(first, "late"));
        Assert.Equal("a", section.Snapshot);
    }

    [Fact]
    public void Unchanged_loaded_section_can_be_refreshed_and_only_the_newest_read_publishes()
    {
        var section = new SettingsSectionLoad();
        Assert.True(section.TryBegin(null, out var first));
        Assert.True(section.TryBegin(null, out var second));

        Assert.False(section.Publish(first, "first"));
        Assert.True(section.Publish(second, "second"));

        Assert.True(section.TryBegin("second", out var refresh));
        Assert.True(section.Publish(refresh, "third"));
        Assert.Equal("third", section.Snapshot);
    }

    [Fact]
    public void Closing_during_a_load_stops_it_publishing_to_the_closed_window()
    {
        var section = new SettingsSectionLoad();
        Assert.True(section.TryBegin(string.Empty, out var ticket));

        section.Close();

        Assert.True(section.IsClosed);
        Assert.False(section.CanPublish(ticket));
        Assert.False(section.Publish(ticket, "rows"));
        Assert.False(section.Fail(ticket));
        Assert.False(section.TryBegin(string.Empty, out _));
        Assert.False(section.Invalidate());
        Assert.False(section.IsLoaded);
    }

    [Fact]
    public void Write_that_lands_during_a_refresh_of_a_read_only_section_restarts_the_refresh()
    {
        var section = new SettingsSectionLoad();
        Assert.True(section.TryBegin(null, out var first));
        Assert.True(section.Publish(first, string.Empty));

        // With no refresh running, the rows on screen already show the write, so nothing restarts.
        Assert.False(section.Invalidate());
        Assert.True(section.IsLoaded);

        // A history refresh read the rows, then a rating write landed before it published. Showing
        // it now would put the old rating back.
        Assert.True(section.TryBegin(null, out var refresh));
        Assert.True(section.Invalidate());
        Assert.False(section.CanPublish(refresh));
        Assert.False(section.Publish(refresh, string.Empty));

        Assert.True(section.TryBegin(null, out var fresh));
        Assert.True(section.Publish(fresh, string.Empty));
        Assert.True(section.IsLoaded);
    }

    [Fact]
    public void Save_moves_the_snapshot_only_once_the_section_has_loaded()
    {
        var section = new SettingsSectionLoad();
        section.MarkSaved("ignored");
        Assert.Null(section.Snapshot);

        Assert.True(section.TryBegin(string.Empty, out var ticket));
        Assert.True(section.Publish(ticket, "a"));
        section.MarkSaved("b");

        Assert.Equal("b", section.Snapshot);
        Assert.False(section.HasChanges("b"));
        Assert.True(section.HasChanges("a"));
    }
}
