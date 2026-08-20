using Scribe.Core.Infrastructure;

namespace Scribe.Core.Tests;

/// <summary>
/// Covers the move off AppData write virtualization. A Store build up to 0.3.10 created ScribeData
/// inside its package LocalCache, so once the manifest excludes that folder from virtualization the
/// app starts reading the real path and would otherwise look like a fresh install to every existing
/// Store user.
/// </summary>
public class PackagedDataMigrationTests : IDisposable
{
    private readonly string _temp = Path.Combine(
        Path.GetTempPath(), "scribe-pkg-test-" + Guid.NewGuid().ToString("N"));

    public PackagedDataMigrationTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Virtualized_path_matches_the_folder_Windows_actually_redirects_writes_into()
    {
        // Verified against a real Store install: a folder created under %LOCALAPPDATA% from inside
        // the package appears here and nowhere else.
        var composed = WindowsPackageIdentity.ComposeVirtualizedLocalAppData(
            @"C:\Users\someone\AppData\Local", "53984VeteranApps.ScribeAI_e3jkm6dfkwwbm");

        Assert.Equal(
            @"C:\Users\someone\AppData\Local\Packages\53984VeteranApps.ScribeAI_e3jkm6dfkwwbm\LocalCache\Local",
            composed);
    }

    [Fact]
    public void An_unpackaged_process_has_no_virtualized_root()
    {
        // The test host is unpackaged, so this is also the guarantee that the migration is inert on
        // the direct-download build rather than probing a path that means nothing there.
        Assert.Null(WindowsPackageIdentity.TryGetPackageFamilyName());
        Assert.Null(WindowsPackageIdentity.TryGetVirtualizedLocalAppData());
        Assert.Null(new AppPaths(Path.Combine(_temp, "root")).VirtualizedRootDir);
    }

    [Fact]
    public void Effective_paths_match_the_real_ones_when_nothing_is_redirected()
    {
        // The unpackaged case, which is every direct-download install and this test host. The two
        // families of path must be identical here, or the About page would start showing a second
        // location to users who do not have one.
        var paths = new AppPaths(Path.Combine(_temp, "root"));
        paths.EnsureCreated();

        Assert.False(paths.WritesAreVirtualized);
        Assert.Equal(paths.RootDir, paths.EffectiveRootDir);
        Assert.Equal(paths.LogsDir, paths.EffectiveLogsDir);
        Assert.Equal(paths.DatabasePath, paths.EffectiveDatabasePath);
    }

    [Fact]
    public void Effective_paths_are_usable_before_the_directories_are_created()
    {
        // The probe runs in EnsureCreated, but the About page and the session banner can be built
        // from an AppPaths that has not been through it. An empty root would render as a bare
        // "logs" folder and send a user somewhere that does not exist.
        var paths = new AppPaths(Path.Combine(_temp, "unmade"));

        Assert.Equal(paths.RootDir, paths.EffectiveRootDir);
        Assert.Equal(paths.LogsDir, paths.EffectiveLogsDir);
    }

    [Fact]
    public void The_probe_leaves_nothing_behind_in_the_data_folder()
    {
        // It works by writing a marker and looking for it. A marker that survives would show up in
        // the user's data folder, and one per launch would accumulate forever.
        var root = Path.Combine(_temp, "probe-residue");
        var paths = new AppPaths(root);
        paths.EnsureCreated();
        paths.EnsureCreated();

        Assert.Empty(Directory.GetFiles(root, ".scribe-virtualization-probe-*"));
    }

    [Fact]
    public void Libraries_are_copied_forward_from_the_old_root()
    {
        var oldLibraries = Path.Combine(_temp, "old", "libraries");
        var newLibraries = Path.Combine(_temp, "new", "libraries");
        Directory.CreateDirectory(oldLibraries);
        File.WriteAllText(Path.Combine(oldLibraries, "team-terms.csv"), "term,replacement");

        AppPaths.TryMigrateLibraries(oldLibraries, newLibraries);

        Assert.True(File.Exists(Path.Combine(newLibraries, "team-terms.csv")));
    }

    [Fact]
    public void A_library_the_user_has_since_edited_is_never_overwritten()
    {
        var oldLibraries = Path.Combine(_temp, "old", "libraries");
        var newLibraries = Path.Combine(_temp, "new", "libraries");
        Directory.CreateDirectory(oldLibraries);
        Directory.CreateDirectory(newLibraries);
        File.WriteAllText(Path.Combine(oldLibraries, "team-terms.csv"), "stale");
        File.WriteAllText(Path.Combine(newLibraries, "team-terms.csv"), "current");

        AppPaths.TryMigrateLibraries(oldLibraries, newLibraries);

        Assert.Equal("current", File.ReadAllText(Path.Combine(newLibraries, "team-terms.csv")));
    }

    [Fact]
    public void Migration_is_a_no_op_when_the_old_root_is_absent()
    {
        var newLibraries = Path.Combine(_temp, "new", "libraries");

        AppPaths.TryMigrateLibraries(Path.Combine(_temp, "missing", "libraries"), newLibraries);

        Assert.False(Directory.Exists(newLibraries));
    }

    [Fact]
    public void Migration_ignores_files_that_are_not_libraries()
    {
        // The old root is a whole data folder, not a library folder, on some layouts. Only CSVs are
        // libraries; sweeping anything else in would copy a database or a key store by accident.
        var oldLibraries = Path.Combine(_temp, "old", "libraries");
        var newLibraries = Path.Combine(_temp, "new", "libraries");
        Directory.CreateDirectory(oldLibraries);
        File.WriteAllText(Path.Combine(oldLibraries, "terms.csv"), "term");
        File.WriteAllText(Path.Combine(oldLibraries, "scribe.db"), "secrets");

        AppPaths.TryMigrateLibraries(oldLibraries, newLibraries);

        Assert.True(File.Exists(Path.Combine(newLibraries, "terms.csv")));
        Assert.False(File.Exists(Path.Combine(newLibraries, "scribe.db")));
    }
}
