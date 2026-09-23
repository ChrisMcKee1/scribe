using System.Security.AccessControl;
using System.Security.Principal;
using Scribe.Core.Infrastructure;

namespace Scribe.Core.Tests;

/// <summary>
/// SCRIBE_DATA_DIR runs Scribe on a data folder of its own. These drive the value through the
/// internal overloads rather than the real environment variable, which every other test in the
/// process would otherwise see.
/// </summary>
public sealed class IsolatedDataFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "scribe-isolated-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Isolated_data_folder_becomes_the_root_and_is_marked_isolated()
    {
        var paths = AppPaths.CreateForStartup(rootOverride: null, fallbackRootOverride: null, dataDirOverride: _root);

        Assert.Equal(_root, paths.RootDir);
        Assert.True(paths.IsIsolatedRoot);
        Assert.False(paths.IsFallbackRoot);
        Assert.Null(paths.LegacyRootDir);
        Assert.Null(paths.VirtualizedRootDir);

        // The normal installation's fallback folder is none of this profile's business.
        Assert.Null(paths.OrphanedFallbackRootDir);
        Assert.True(Directory.Exists(paths.LogsDir));
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public void Isolated_data_folder_that_cannot_be_created_stops_startup_instead_of_falling_back()
    {
        File.WriteAllText(_root, "a file where the folder should be");

        var failure = Assert.Throws<IsolatedDataFolderException>(
            () => AppPaths.CreateForStartup(rootOverride: null, fallbackRootOverride: null, dataDirOverride: _root));

        Assert.Equal(_root, failure.RootDir);
        Assert.Contains(_root, failure.Message);
        Assert.Contains(AppPaths.DataDirVariable, failure.Message);
        Assert.NotNull(failure.InnerException);
    }

    [Fact]
    public void Isolated_data_folder_that_exists_but_cannot_be_written_stops_startup()
    {
        // Creating the folders succeeds because they already exist; only a write shows the problem.
        Directory.CreateDirectory(Path.Combine(_root, AppPaths.LogsFolderName));
        Directory.CreateDirectory(Path.Combine(_root, AppPaths.LibrariesFolderName));
        var denyCreateFiles = new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!, FileSystemRights.CreateFiles, AccessControlType.Deny);
        var folder = new DirectoryInfo(_root);
        var acl = folder.GetAccessControl();
        acl.AddAccessRule(denyCreateFiles);
        folder.SetAccessControl(acl);

        try
        {
            var failure = Assert.Throws<IsolatedDataFolderException>(
                () => AppPaths.CreateForStartup(rootOverride: null, fallbackRootOverride: null, dataDirOverride: _root));

            Assert.IsAssignableFrom<UnauthorizedAccessException>(failure.InnerException);
        }
        finally
        {
            acl = folder.GetAccessControl();
            acl.RemoveAccessRule(denyCreateFiles);
            folder.SetAccessControl(acl);
        }
    }

    [Fact]
    public void An_isolated_root_uses_a_fallback_only_when_the_caller_names_one()
    {
        File.WriteAllText(_root, "a file where the folder should be");
        var fallback = _root + "-fallback";

        try
        {
            var paths = AppPaths.CreateForStartup(rootOverride: null, fallbackRootOverride: fallback, dataDirOverride: _root);

            Assert.Equal(fallback, paths.RootDir);
            Assert.True(paths.IsFallbackRoot);
            Assert.True(paths.IsIsolatedRoot);
        }
        finally
        {
            TryDelete(fallback);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Without_a_data_folder_setting_the_normal_root_is_used(string? dataDir)
    {
        var paths = new AppPaths(rootOverride: null, dataDirOverride: dataDir);

        Assert.False(paths.IsIsolatedRoot);
        Assert.EndsWith(Path.DirectorySeparatorChar + AppPaths.AppFolderName, paths.RootDir);
    }

    public void Dispose() => TryDelete(_root);

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Temp cleanup is best effort.
        }
    }
}
