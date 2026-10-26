using Scribe.Core.Persistence;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

/// <summary>
/// App-aware formatting is on for every install: earlier builds saved it off for everyone, so it is turned on once, at
/// the first start of a build with this change, and the user's later choice stands.
/// </summary>
public sealed class AppAwareFormattingRolloutTests
{
    [Fact]
    public void A_stored_off_from_an_earlier_build_is_turned_on_once_and_a_later_off_stands()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var repository = new SettingsRepository(db);
        repository.Set(SettingsRepository.SettingsKey, """{"appAwareFormattingEnabled":false,"decodeThreads":3}""");

        Assert.True(AppAwareFormattingRollout.Apply(repository));

        var stored = repository.Load();
        Assert.True(stored.AppAwareFormattingEnabled);
        Assert.True(stored.HasTurnedOnAppAwareFormatting);
        Assert.Equal(3, stored.DecodeThreads);

        repository.Update(settings => settings.AppAwareFormattingEnabled = false);
        Assert.False(AppAwareFormattingRollout.Apply(repository));
        Assert.False(repository.Load().AppAwareFormattingEnabled);
    }

    [Fact]
    public void A_setting_already_on_only_records_the_gate()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var repository = new SettingsRepository(db);
        repository.Set(SettingsRepository.SettingsKey, """{"appAwareFormattingEnabled":true}""");

        Assert.False(AppAwareFormattingRollout.Apply(repository));

        var stored = repository.Load();
        Assert.True(stored.AppAwareFormattingEnabled);
        Assert.True(stored.HasTurnedOnAppAwareFormatting);
    }

    [Fact]
    public void A_first_run_starts_on_and_records_the_gate()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var repository = new SettingsRepository(db);

        Assert.False(AppAwareFormattingRollout.Apply(repository));

        var stored = repository.Load();
        Assert.True(stored.AppAwareFormattingEnabled);
        Assert.True(stored.HasTurnedOnAppAwareFormatting);
    }

    [Fact]
    public void An_unreadable_document_is_never_written_over()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var repository = new SettingsRepository(db);
        const string unreadable = "{ not json";
        repository.Set(SettingsRepository.SettingsKey, unreadable);

        Assert.False(AppAwareFormattingRollout.Apply(repository));

        Assert.Equal(unreadable, repository.Get(SettingsRepository.SettingsKey));
    }
}
