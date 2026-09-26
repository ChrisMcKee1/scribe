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

    [Theory]
    [InlineData("{}", AccentSource.Scribe)]
    [InlineData("{\"accentSource\":\"Purple\"}", AccentSource.Scribe)]
    [InlineData("{\"accentSource\":\"1\"}", AccentSource.Scribe)]
    [InlineData("{\"accentSource\":\"0\"}", AccentSource.Scribe)]
    [InlineData("{\"accentSource\":\"Scribe, Windows\"}", AccentSource.Scribe)]
    [InlineData("{\"accentSource\":\"windows\"}", AccentSource.Windows)]
    [InlineData("{\"accentSource\":\"WINDOWS\"}", AccentSource.Windows)]
    [InlineData("{\"accentSource\":1}", AccentSource.Scribe)]
    [InlineData("{\"accentSource\":null}", AccentSource.Scribe)]
    [InlineData("{\"accentSource\":{}}", AccentSource.Scribe)]
    [InlineData("{\"accentSource\":[]}", AccentSource.Scribe)]
    public void Missing_or_unknown_accent_source_reads_tolerantly_without_failing_load(string json, AccentSource expected)
    {
        using var db = _folder.Open();
        var repository = new SettingsRepository(db);

        repository.Set("app_settings", json);

        Assert.Equal(expected, repository.Load().AccentSource);
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
