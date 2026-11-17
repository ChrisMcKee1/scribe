using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

/// <summary>
/// The document one Settings Save stores, frozen when its request is built (<see cref="SaveCandidate"/>): what the store
/// keeps for a setting the window holds no newer intent for goes back into the editing document, and a newer choice the
/// window took while the Save waited does not.
/// </summary>
public sealed class SaveCandidateTests : IDisposable
{
    private static readonly MicrophoneSelection Yeti = new("yeti", "Blue Yeti");
    private static readonly MicrophoneSelection Usb = new("usb", "USB Microphone");

    private readonly TempDatabaseFolder _folder = new();
    private readonly ScribeDatabase _database;
    private readonly SettingsRepository _settings;

    public SaveCandidateTests()
    {
        _database = _folder.Open();
        _settings = new SettingsRepository(_database);
        _settings.Save(new AppSettings { EnableAiCleanup = true });
    }

    public void Dispose()
    {
        _database.Dispose();
        _folder.Dispose();
    }

    [Fact]
    public void Freezing_copies_the_editing_document_so_a_later_edit_never_reaches_what_the_save_stores()
    {
        var editing = new AppSettings { EnableAiCleanup = true, HistoryRetentionDays = 30 };
        Usb.ApplyTo(editing);
        var candidate = SaveCandidate.Freeze(editing, new ExternalIntents(7, 8));

        editing.EnableAiCleanup = false;
        editing.HistoryRetentionDays = 0;
        Yeti.ApplyTo(editing);

        Assert.NotSame(editing, candidate.Document);
        Assert.True(candidate.Document.EnableAiCleanup);
        Assert.Equal(30, candidate.Document.HistoryRetentionDays);
        Assert.Equal(Usb, MicrophoneSelection.From(candidate.Document));
        Assert.Equal(new ExternalIntents(7, 8), candidate.Intents);
    }

    [Fact]
    public void Settle_takes_what_the_store_kept_for_a_setting_the_window_holds_no_newer_intent_for()
    {
        // The tray changed both settings and those writes committed before word of them reached a window that was opened
        // earlier: it shows the old choices and holds no intent for either, so the Save keeps what is stored.
        _settings.Update(s => s.EnableAiCleanup = false, ExternalSwitchSync.NextRevision(), out _);
        _settings.Update(Yeti.ApplyTo, ExternalSetting.Microphone, ExternalSwitchSync.NextRevision(), out _);
        var editing = new AppSettings { EnableAiCleanup = true, HistoryRetentionDays = 30 };
        Usb.ApplyTo(editing);
        var aiCleanup = new ExternalSwitchSync();
        var microphone = new ExternalChoiceSync<MicrophoneSelection>();
        var candidate = SaveCandidate.Freeze(editing, new ExternalIntents(aiCleanup.NewestRevision, microphone.NewestRevision));

        _settings.SaveBundle(candidate.Document, null, null, candidate.Intents, null);
        candidate.Settle(editing, aiCleanup, microphone);

        var stored = _settings.Load();
        Assert.False(stored.EnableAiCleanup);
        Assert.Equal(Yeti, MicrophoneSelection.From(stored));
        Assert.Equal(30, stored.HistoryRetentionDays);
        Assert.False(editing.EnableAiCleanup);
        Assert.Equal(Yeti, MicrophoneSelection.From(editing));
    }

    [Fact]
    public void Settle_leaves_the_editing_document_alone_for_a_setting_the_window_holds_a_newer_intent_for()
    {
        // The window holds its own intent for both settings when the Save freezes its document: AI cleanup off, Usb.
        var editing = new AppSettings { EnableAiCleanup = false };
        Usb.ApplyTo(editing);
        var aiCleanup = new ExternalSwitchSync();
        var microphone = new ExternalChoiceSync<MicrophoneSelection>();
        aiCleanup.UserChanged();
        microphone.UserChanged();
        var candidate = SaveCandidate.Freeze(editing, new ExternalIntents(aiCleanup.NewestRevision, microphone.NewestRevision));

        // While the Save waits, the tray's newer choices reach the window, which takes them into its document at once.
        var newerAi = ExternalSwitchSync.NextRevision();
        Assert.True(aiCleanup.TryAdopt(true, newerAi, canShowNow: true, out _));
        editing.EnableAiCleanup = true;
        var newerMicrophone = ExternalSwitchSync.NextRevision();
        Assert.True(microphone.TryAdopt(Yeti, newerMicrophone, canShowNow: true, out _));
        Yeti.ApplyTo(editing);

        _settings.SaveBundle(candidate.Document, null, null, candidate.Intents, null);
        candidate.Settle(editing, aiCleanup, microphone);

        // The Save stored the choices it captured, and the newer ones stay the window's own until their writes settle them.
        var stored = _settings.Load();
        Assert.False(stored.EnableAiCleanup);
        Assert.Equal(Usb, MicrophoneSelection.From(stored));
        Assert.True(editing.EnableAiCleanup);
        Assert.Equal(Yeti, MicrophoneSelection.From(editing));
        Assert.Equal(newerAi, aiCleanup.NewestRevision);
        Assert.Equal(newerMicrophone, microphone.NewestRevision);
    }

    [Fact]
    public void A_preflight_save_freezes_the_preflights_choices_for_what_the_tray_can_change_and_the_editing_documents_for_the_rest()
    {
        var preflight = new AppSettings { EnableAiCleanup = false, HistoryRetentionDays = 30 };
        Usb.ApplyTo(preflight);
        var editing = preflight.Clone();
        editing.EnableAiCleanup = true;
        Yeti.ApplyTo(editing);
        editing.HistoryRetentionDays = 0;

        var candidate = SaveCandidate.FreezePreflight(preflight, editing, new ExternalIntents(5, 6));

        Assert.NotSame(editing, candidate.Document);
        Assert.False(candidate.Document.EnableAiCleanup);
        Assert.Equal(Usb, MicrophoneSelection.From(candidate.Document));
        Assert.Equal(0, candidate.Document.HistoryRetentionDays);
        Assert.Equal(new ExternalIntents(5, 6), candidate.Intents);

        // Neither document the candidate was made from is touched.
        Assert.True(editing.EnableAiCleanup);
        Assert.Equal(Yeti, MicrophoneSelection.From(editing));
        Assert.False(preflight.EnableAiCleanup);
        Assert.Equal(Usb, MicrophoneSelection.From(preflight));
    }

    [Fact]
    public void A_newer_choice_adopted_before_the_freeze_stays_the_editing_documents_and_the_save_stores_the_preflights_under_its_intents()
    {
        // The window holds an intent for both settings when the Save's preflight captures them: AI cleanup off, Usb.
        var editing = new AppSettings { EnableAiCleanup = false };
        Usb.ApplyTo(editing);
        var aiCleanup = new ExternalSwitchSync();
        var microphone = new ExternalChoiceSync<MicrophoneSelection>();
        aiCleanup.UserChanged();
        microphone.UserChanged();
        var preflight = editing.Clone();
        var intents = new ExternalIntents(aiCleanup.NewestRevision, microphone.NewestRevision);

        // The Save waits for a confirmation, and the tray's newer choices are adopted meanwhile. The window's copy of the
        // preflight skips what the tray can change (SaveCandidate.IsExternal), so they are still in the editing document
        // when the Save freezes.
        var newerAi = ExternalSwitchSync.NextRevision();
        Assert.True(aiCleanup.TryAdopt(true, newerAi, canShowNow: true, out _));
        editing.EnableAiCleanup = true;
        var newerMicrophone = ExternalSwitchSync.NextRevision();
        Assert.True(microphone.TryAdopt(Yeti, newerMicrophone, canShowNow: true, out _));
        Yeti.ApplyTo(editing);
        var candidate = SaveCandidate.FreezePreflight(preflight, editing, intents);

        _settings.SaveBundle(candidate.Document, null, null, candidate.Intents, null);
        candidate.Settle(editing, aiCleanup, microphone);

        // The Save stored what its preflight captured, under the intents that captured it, and never a tray choice whose own
        // write may still fail; the editing document keeps the newer choices until those writes settle them.
        var stored = _settings.Load();
        Assert.False(stored.EnableAiCleanup);
        Assert.Equal(Usb, MicrophoneSelection.From(stored));
        Assert.True(editing.EnableAiCleanup);
        Assert.Equal(Yeti, MicrophoneSelection.From(editing));
        Assert.Equal(newerAi, aiCleanup.NewestRevision);
        Assert.Equal(newerMicrophone, microphone.NewestRevision);
    }

    [Fact]
    public void IsExternal_names_exactly_the_settings_the_tray_can_change()
    {
        var external = typeof(AppSettings).GetProperties()
            .Where(property => SaveCandidate.IsExternal(property.Name))
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal([nameof(AppSettings.EnableAiCleanup), nameof(AppSettings.InputDeviceId), nameof(AppSettings.InputDeviceName)], external);

        // A new setting the tray can change needs its properties here, in FreezePreflight and in Settle, or a Save's
        // preflight puts a stale value of it back into the editing document.
        Assert.Equal(2, Enum.GetValues<ExternalSetting>().Length);
        Assert.False(SaveCandidate.IsExternal(nameof(AppSettings.LaunchOnLogin)));
        Assert.False(SaveCandidate.IsExternal(nameof(AppSettings.HistoryRetentionDays)));
    }
}
