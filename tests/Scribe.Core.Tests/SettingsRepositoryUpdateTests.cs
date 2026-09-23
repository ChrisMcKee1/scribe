using Scribe.Core.Models;
using Scribe.Core.Persistence;

namespace Scribe.Core.Tests;

/// <summary>
/// <see cref="SettingsRepository.Update"/> is the atomic read-modify-write for writers that change a
/// few fields of the stored settings from different threads (the Settings window's Start with
/// Windows write on a worker, the tray AI toggle on the UI thread). A plain load, edit and save from
/// two threads lets the later save silently drop the earlier one's field.
/// </summary>
public sealed class SettingsRepositoryUpdateTests : IDisposable
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    private readonly TempDatabaseFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task Two_concurrent_updates_of_different_fields_both_survive()
    {
        using var db = _folder.Open();
        var repository = new SettingsRepository(db);
        repository.Save(AppSettings.CreateDefault());
        using var firstInside = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        var secondMutated = 0;

        // The first writer has read the document and is part way through its change...
        var startup = Task.Run(() => repository.Update(settings =>
        {
            settings.LaunchOnLogin = true;
            firstInside.Set();
            Assert.True(releaseFirst.Wait(Generous));
        }));
        Assert.True(firstInside.Wait(Generous));

        // ...when the second arrives. It must not read the document until the first has written it.
        var aiToggle = Task.Run(() => repository.Update(settings =>
        {
            Interlocked.Increment(ref secondMutated);
            settings.EnableAiCleanup = true;
        }));
        Assert.Equal(0, Volatile.Read(ref secondMutated));

        releaseFirst.Set();
        await Task.WhenAll(startup, aiToggle).WaitAsync(Generous);

        var saved = repository.Load();
        Assert.True(saved.LaunchOnLogin);
        Assert.True(saved.EnableAiCleanup);
        Assert.True((await aiToggle).LaunchOnLogin); // the second read the first one's write
    }

    [Fact]
    public void Protected_secrets_are_handled_exactly_as_load_and_save_handle_them()
    {
        using var db = _folder.Open();
        var repository = new SettingsRepository(db);
        var settings = AppSettings.CreateDefault();
        settings.AiCleanupAzureApiKey = "update-test-key";
        repository.Save(settings);

        var updated = repository.Update(s => s.EnableAiCleanup = true);

        Assert.Equal("update-test-key", updated.AiCleanupAzureApiKey);
        Assert.Equal("update-test-key", repository.Load().AiCleanupAzureApiKey);
        Assert.DoesNotContain("update-test-key", repository.Get("app_settings")); // still encrypted at rest
    }

    [Fact]
    public async Task Update_never_waits_for_the_storage_maintenance_write_gate()
    {
        using var db = _folder.Open();
        var repository = new SettingsRepository(db);
        using var holding = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var maintenance = new Thread(() =>
        {
            using (db.EnterWriteScope())
            {
                holding.Set();
                release.Wait(Generous);
            }
        });
        maintenance.Start();
        Assert.True(holding.Wait(Generous));

        try
        {
            await Task.Run(() => repository.Update(s => s.EnableAiCleanup = true)).WaitAsync(Generous);
            Assert.Equal(0, db.WaitingWriters);
        }
        finally
        {
            release.Set();
            Assert.True(maintenance.Join(Generous));
        }

        Assert.True(repository.Load().EnableAiCleanup);
    }

    [Fact]
    public void An_unreadable_document_is_kept_as_a_recovery_copy_and_the_change_is_refused()
    {
        using var db = _folder.Open();
        var repository = new SettingsRepository(db);
        repository.Set("app_settings", "{ not json");

        // Defaults plus one field written over it would discard everything else the user saved.
        Assert.Throws<InvalidOperationException>(() => repository.Update(s => s.LaunchOnLogin = true));

        Assert.True(repository.LastLoadFailed);
        Assert.Equal("{ not json", repository.Get("app_settings"));
        Assert.Equal("{ not json", repository.Get("app_settings_recovery"));

        // The first recovery copy is never overwritten by a later one.
        repository.Set("app_settings", "{ also not json");
        Assert.Throws<InvalidOperationException>(() => repository.Update(s => s.EnableAiCleanup = true));
        Assert.Equal("{ also not json", repository.Get("app_settings"));
        Assert.Equal("{ not json", repository.Get("app_settings_recovery"));

        // Once a readable document is saved again, updates go through and the flag clears.
        repository.Save(AppSettings.CreateDefault());
        Assert.True(repository.Update(s => s.LaunchOnLogin = true).LaunchOnLogin);
        Assert.False(repository.LastLoadFailed);
    }

    [Fact]
    public void A_mutation_that_throws_saves_nothing()
    {
        using var db = _folder.Open();
        var repository = new SettingsRepository(db);
        repository.Save(AppSettings.CreateDefault());

        Assert.Throws<InvalidOperationException>(() => repository.Update(s =>
        {
            s.LaunchOnLogin = true;
            throw new InvalidOperationException("caller bug");
        }));

        Assert.False(repository.Load().LaunchOnLogin);

        // The lock and the transaction were released: the next update goes through.
        Assert.True(repository.Update(s => s.LaunchOnLogin = true).LaunchOnLogin);
    }

    [Fact]
    public void An_update_from_inside_its_own_callback_fails_at_once_instead_of_hanging()
    {
        using var db = _folder.Open();
        var repository = new SettingsRepository(db);
        repository.Save(AppSettings.CreateDefault());

        var nested = Assert.Throws<InvalidOperationException>(() =>
            repository.Update(outer => repository.Update(inner => inner.EnableAiCleanup = true)));

        Assert.Contains("inside its own mutate callback", nested.Message);
        Assert.False(repository.Load().EnableAiCleanup);
    }
}
