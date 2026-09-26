using Scribe.Core.Models;
using Scribe.Core.Persistence;

namespace Scribe.Core.Tests;

public sealed class AccentSourceSettingsRepositoryTests : IDisposable
{
    private readonly TempDatabaseFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Theory]
    [InlineData(AccentSource.Scribe)]
    [InlineData(AccentSource.Windows)]
    public void Accent_source_round_trips_as_a_name(AccentSource source)
    {
        using var db = _folder.Open();
        var repository = new SettingsRepository(db);
        var settings = AppSettings.CreateDefault();
        settings.AccentSource = source;

        repository.Save(settings);

        Assert.Equal(source, repository.Load().AccentSource);
        Assert.Contains($"\"accentSource\":\"{source}\"", repository.Get("app_settings"));
    }

    [Fact]
    public void Missing_or_unknown_accent_source_reads_as_scribe_without_failing_load()
    {
        using var db = _folder.Open();
        var repository = new SettingsRepository(db);

        repository.Set("app_settings", "{}");
        Assert.Equal(AccentSource.Scribe, repository.Load().AccentSource);
        Assert.False(repository.LastLoadFailed);

        repository.Set("app_settings", "{\"accentSource\":\"Purple\"}");
        Assert.Equal(AccentSource.Scribe, repository.Load().AccentSource);
        Assert.False(repository.LastLoadFailed);

        repository.Set("app_settings", "{\"accentSource\":7}");
        Assert.Equal(AccentSource.Scribe, repository.Load().AccentSource);
        Assert.False(repository.LastLoadFailed);

        repository.Set("app_settings", "{\"accentSource\":null}");
        Assert.Equal(AccentSource.Scribe, repository.Load().AccentSource);
        Assert.False(repository.LastLoadFailed);
    }

    [Fact]
    public void Stored_windows_source_survives_an_unrelated_update()
    {
        using var db = _folder.Open();
        var repository = new SettingsRepository(db);
        var settings = AppSettings.CreateDefault();
        settings.AccentSource = AccentSource.Windows;
        repository.Save(settings);

        repository.Update(s => s.HistoryRetentionDays = 30);

        var loaded = repository.Load();
        Assert.Equal(AccentSource.Windows, loaded.AccentSource);
        Assert.Equal(30, loaded.HistoryRetentionDays);
    }
}
