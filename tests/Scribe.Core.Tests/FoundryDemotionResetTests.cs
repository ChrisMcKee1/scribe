using Scribe.Core.Cleanup;
using Scribe.Core.Infrastructure;
using Scribe.Core.Persistence;
using Xunit;

namespace Scribe.Core.Tests;

public class FoundryDemotionResetTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "scribe-demotion-reset-" + Guid.NewGuid().ToString("N"));

    private AppPaths Paths()
    {
        Directory.CreateDirectory(_root);
        return AppPaths.CreateForStartup(_root);
    }

    private string MarkerPath => Path.Combine(_root, FoundryDemotionReset.FileName);

    private void WriteMarkers() =>
        File.WriteAllText(MarkerPath, """{"qwen3-1.7b":"qwen3-1.7b-generic-cpu:2"}""");

    [Fact]
    public void Clears_markers_once_and_records_that_it_ran()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var settings = new SettingsRepository(db);
        var paths = Paths();
        WriteMarkers();

        Assert.True(FoundryDemotionReset.Apply(settings, paths));
        Assert.False(File.Exists(MarkerPath));
        Assert.True(settings.Load().HasResetFoundryDemotions);
    }

    [Fact]
    public void Does_not_clear_a_demotion_relearned_after_the_reset_ran()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var settings = new SettingsRepository(db);
        var paths = Paths();
        WriteMarkers();

        Assert.True(FoundryDemotionReset.Apply(settings, paths));

        // The probe path legitimately re-demotes a model whose GPU build really is broken. Running
        // again must leave that alone, otherwise the machine is re-probed on every launch.
        WriteMarkers();
        Assert.False(FoundryDemotionReset.Apply(settings, paths));
        Assert.True(File.Exists(MarkerPath));
    }

    [Fact]
    public void Records_the_reset_even_when_no_markers_exist()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var settings = new SettingsRepository(db);
        var paths = Paths();

        // Returns false (nothing was cleared) but must still record the flag, or a demotion written
        // later would be wiped by a reset that had simply never had anything to do.
        Assert.False(FoundryDemotionReset.Apply(settings, paths));
        Assert.True(settings.Load().HasResetFoundryDemotions);
    }

    [Fact]
    public void Skips_when_settings_could_not_be_loaded()
    {
        var settings = new FailedLoadSettingsRepository();
        var paths = Paths();
        WriteMarkers();

        // A failed load reports every flag as unset, so proceeding would clear the markers on every
        // launch and keep undoing demotions the probe path had just relearned.
        Assert.False(FoundryDemotionReset.Apply(settings, paths));
        Assert.True(File.Exists(MarkerPath));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory must never fail a test run.
        }

        GC.SuppressFinalize(this);
    }

    private sealed class FailedLoadSettingsRepository : ISettingsRepository
    {
        public bool LastLoadFailed => true;

        public Models.AppSettings Load() => Models.AppSettings.CreateDefault();

        public void Save(Models.AppSettings settings) =>
            throw new InvalidOperationException("Save must not be reached when the load failed.");

        public void SaveBundle(
            Models.AppSettings settings,
            IReadOnlyList<Models.DictionaryEntry>? dictionaryEntries,
            IReadOnlyList<Models.Snippet>? snippets) =>
            throw new InvalidOperationException("SaveBundle must not be reached when the load failed.");

        public string? Get(string key) => null;

        public void Set(string key, string value) =>
            throw new InvalidOperationException("Set must not be reached when the load failed.");
    }
}
