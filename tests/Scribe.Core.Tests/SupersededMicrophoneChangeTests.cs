using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.Settings;
using Scribe.Core.Tests.Concurrency;

namespace Scribe.Core.Tests;

/// <summary>
/// The microphone between the tray and a whole-document Settings save, held to the rules the AI cleanup switch follows
/// (<see cref="SupersededOutsideChangeTests"/>): the newest intent wins, ordered by when the user made it, and a save
/// never writes over a stored microphone its window neither showed nor changed. Each setting keeps its own record of
/// intent, so a save that accounted for one never swallows a tray change to the other.
/// </summary>
public sealed class SupersededMicrophoneChangeTests : IDisposable
{
    private static readonly MicrophoneSelection Yeti = new("yeti", "Blue Yeti");
    private static readonly MicrophoneSelection Usb = new("usb", "USB Microphone");

    private readonly TempDatabaseFolder _folder = new();
    private readonly ScribeDatabase _database;
    private readonly SettingsRepository _settings;

    public SupersededMicrophoneChangeTests()
    {
        _database = _folder.Open();
        _settings = new SettingsRepository(_database);
        _settings.Save(new AppSettings { EnableAiCleanup = false });
    }

    public void Dispose()
    {
        _database.Dispose();
        _folder.Dispose();
    }

    [Fact]
    public void A_tray_microphone_change_no_save_has_accounted_for_is_written()
    {
        var revision = ExternalSwitchSync.NextRevision();
        _settings.SaveBundle(new AppSettings(), null, null, new ExternalIntents(0, Microphone: revision - 1));

        var stored = _settings.Update(Yeti.ApplyTo, ExternalSetting.Microphone, revision, out var superseded);

        Assert.False(superseded);
        Assert.Equal(Yeti, MicrophoneSelection.From(stored));
        Assert.Equal(Yeti, MicrophoneSelection.From(_settings.Load()));
    }

    [Fact]
    public void A_save_whose_microphone_intent_accounted_for_the_change_supersedes_it()
    {
        // The user picked Yeti in the tray, then picked USB in the window and saved before the tray's write landed.
        var tray = ExternalSwitchSync.NextRevision();
        var window = new AppSettings { HistoryRetentionDays = 30 };
        Usb.ApplyTo(window);
        _settings.SaveBundle(window, null, null, new ExternalIntents(0, Microphone: ExternalSwitchSync.NextRevision()));

        var stored = _settings.Update(Yeti.ApplyTo, ExternalSetting.Microphone, tray, out var superseded);

        Assert.True(superseded);
        Assert.Equal(Usb, MicrophoneSelection.From(stored));
        Assert.Equal(Usb, MicrophoneSelection.From(_settings.Load()));
    }

    [Fact]
    public void An_ai_cleanup_intent_never_supersedes_a_tray_microphone_change()
    {
        // The window's save carried a newer intent for the AI switch only; it never saw the tray's microphone.
        var tray = ExternalSwitchSync.NextRevision();
        _settings.SaveBundle(
            new AppSettings { EnableAiCleanup = true }, null, null,
            new ExternalIntents(AiCleanup: ExternalSwitchSync.NextRevision(), Microphone: 0));

        _settings.Update(Yeti.ApplyTo, ExternalSetting.Microphone, tray, out var superseded);

        Assert.False(superseded);
        Assert.Equal(Yeti, MicrophoneSelection.From(_settings.Load()));
        Assert.True(_settings.Load().EnableAiCleanup);
    }

    [Fact]
    public void A_microphone_intent_never_supersedes_a_tray_ai_cleanup_change()
    {
        var tray = ExternalSwitchSync.NextRevision();
        var window = new AppSettings();
        Usb.ApplyTo(window);
        _settings.SaveBundle(window, null, null, new ExternalIntents(AiCleanup: 0, Microphone: ExternalSwitchSync.NextRevision()));

        _settings.Update(s => s.EnableAiCleanup = true, tray, out var superseded);

        Assert.False(superseded);
        Assert.True(_settings.Load().EnableAiCleanup);
    }

    [Fact]
    public void A_save_with_no_microphone_intent_keeps_the_stored_microphone_and_hands_it_back()
    {
        // A tray change committed before word of it reached a window opened just before: the window still shows the old
        // choice, and its save must not put it back.
        _settings.Update(Yeti.ApplyTo, ExternalSetting.Microphone, ExternalSwitchSync.NextRevision(), out _);
        var window = new AppSettings { HistoryRetentionDays = 30 };

        _settings.SaveBundle(window, null, null, new ExternalIntents(0, Microphone: 0));

        var stored = _settings.Load();
        Assert.Equal(Yeti, MicrophoneSelection.From(stored));
        Assert.Equal(30, stored.HistoryRetentionDays);
        Assert.Equal(Yeti, MicrophoneSelection.From(window));
    }

    [Fact]
    public void A_save_with_a_microphone_intent_writes_the_windows_microphone()
    {
        _settings.Update(Yeti.ApplyTo, ExternalSetting.Microphone, ExternalSwitchSync.NextRevision(), out _);
        var window = new AppSettings();
        Usb.ApplyTo(window);

        _settings.SaveBundle(window, null, null, new ExternalIntents(0, Microphone: ExternalSwitchSync.NextRevision()));

        Assert.Equal(Usb, MicrophoneSelection.From(_settings.Load()));
    }

    [Fact]
    public void Going_back_to_the_windows_default_in_the_window_is_a_choice_like_any_other()
    {
        _settings.Update(Yeti.ApplyTo, ExternalSetting.Microphone, ExternalSwitchSync.NextRevision(), out _);

        _settings.SaveBundle(new AppSettings(), null, null, new ExternalIntents(0, Microphone: ExternalSwitchSync.NextRevision()));

        Assert.Equal(MicrophoneSelection.WindowsDefault, MicrophoneSelection.From(_settings.Load()));
    }

    [Fact]
    public void A_save_that_does_not_track_the_microphone_writes_it_as_given()
    {
        // Every caller of the older overload, which knew nothing of a tray microphone.
        _settings.Update(Yeti.ApplyTo, ExternalSetting.Microphone, ExternalSwitchSync.NextRevision(), out _);
        var window = new AppSettings();
        Usb.ApplyTo(window);

        _settings.SaveBundle(window, null, null, aiCleanupIntent: 0);

        Assert.Equal(Usb, MicrophoneSelection.From(_settings.Load()));
    }

    [Fact]
    public async Task The_lane_reports_a_microphone_change_a_save_accounted_for_as_superseded()
    {
        var lane = new SettingsWriteLane(_settings, callback => callback());
        var tray = ExternalSwitchSync.NextRevision();
        var window = new AppSettings();
        Usb.ApplyTo(window);
        _settings.SaveBundle(window, null, null, new ExternalIntents(0, Microphone: ExternalSwitchSync.NextRevision()));
        var result = new TaskCompletionSource<(AppSettings Stored, bool Superseded)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(lane.Submit(
            Yeti.ApplyTo,
            ExternalSetting.Microphone,
            tray,
            (stored, superseded) => result.TrySetResult((stored, superseded)),
            error => result.TrySetException(error)));

        var (stored, superseded) = await result.Task.WaitAsync(BlockedThreads.SafetyTimeout);
        Assert.True(superseded);
        Assert.Equal(Usb, MicrophoneSelection.From(stored));
    }
}
