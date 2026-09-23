using System.Diagnostics;
using System.Runtime.InteropServices;
using Scribe.Core.Cleanup;
using Scribe.Core.Infrastructure;

namespace Scribe.Core.Tests;

/// <summary>
/// The Foundry Local storage janitor deletes multi-gigabyte downloads from the user's profile, so its
/// refusals are the point. Everything here runs against throwaway temp directories.
/// </summary>
public sealed class FoundryStorageJanitorTests
{
    private static FoundryStorageJanitor Janitor() => new(new PhysicalFoundryStorageFileSystem());

    private static string[] Tombstones(string parent, string name) =>
        Directory.Exists(parent)
            ? Directory.GetDirectories(parent, "." + name + FoundryStorageJanitor.TombstoneMarker + "*")
            : [];

    [Fact]
    public void Deletes_the_whole_target_and_reports_the_size()
    {
        using var temp = new TempDirectory();
        var root = temp.Combine(".Scribe");
        temp.WriteFile(@".Scribe\ep\cuda-ep\onnxruntime_providers_cuda.dll", 4096);
        temp.WriteFile(@".Scribe\ep\webgpu-ep\webgpu.dll", 1024);
        var log = temp.WriteFile(@".Scribe\logs\foundry.log", 10);

        var result = Janitor().ReclaimDirectory(root, Path.Combine(root, "ep"), CancellationToken.None);

        Assert.False(result.Refused);
        Assert.Equal(2, result.FilesDeleted);
        Assert.Equal(4096 + 1024, result.BytesDeleted);
        Assert.Equal(0, result.FilesDeferred);
        Assert.False(Directory.Exists(Path.Combine(root, "ep")));
        Assert.Empty(Tombstones(root, "ep"));
        Assert.True(File.Exists(log), "Only the target directory is touched.");
        Assert.True(Directory.Exists(root), "The root itself is never deleted.");
    }

    [Theory]
    [InlineData(@"outside")]
    [InlineData(@".Scribe\..\outside")]
    [InlineData(@".Scribe\ep\..\..\outside")]
    [InlineData(@".ScribeX\ep")]
    public void Refuses_a_target_outside_the_root(string relativeTarget)
    {
        using var temp = new TempDirectory();
        var root = temp.Combine(".Scribe");
        Directory.CreateDirectory(root);
        var victim = temp.WriteFile(@"outside\keep.bin", 64);
        temp.WriteFile(@".ScribeX\ep\keep.bin", 64);

        var whole = Janitor().ReclaimDirectory(root, temp.Combine(relativeTarget), CancellationToken.None);
        var byFile = Janitor().DeleteContents(root, temp.Combine(relativeTarget), CancellationToken.None);

        Assert.True(whole.Refused);
        Assert.True(byFile.Refused);
        Assert.Equal(0, whole.FilesDeleted + byFile.FilesDeleted);
        Assert.True(File.Exists(victim));
        Assert.True(File.Exists(temp.Combine(@".ScribeX\ep\keep.bin")), "A sibling that shares the prefix is outside.");
        Assert.True(Directory.Exists(temp.Combine("outside")), "Nothing outside is ever renamed either.");
    }

    [Fact]
    public void Refuses_the_root_itself_and_relative_paths()
    {
        using var temp = new TempDirectory();
        var root = temp.Combine(".Scribe");
        var file = temp.WriteFile(@".Scribe\cache\models\m.onnx", 16);

        Assert.True(Janitor().ReclaimDirectory(root, root, CancellationToken.None).Refused);
        Assert.True(Janitor().ReclaimDirectory(@".Scribe", @".Scribe\ep", CancellationToken.None).Refused);
        Assert.True(Janitor().ReclaimDirectory(root, @"cache\models", CancellationToken.None).Refused);
        Assert.True(Janitor().DeleteContents(root, root, CancellationToken.None).Refused);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void A_missing_directory_is_the_healthy_case_not_a_refusal()
    {
        using var temp = new TempDirectory();

        var missingRoot = Janitor().ReclaimDirectory(temp.Combine(".Scribe"), temp.Combine(@".Scribe\ep"), CancellationToken.None);
        Directory.CreateDirectory(temp.Combine(".Scribe"));
        var missingTarget = Janitor().ReclaimDirectory(temp.Combine(".Scribe"), temp.Combine(@".Scribe\ep"), CancellationToken.None);
        var missingParent = Janitor().ReclaimDirectory(temp.Combine(".Scribe"), temp.Combine(@".Scribe\cache\models"), CancellationToken.None);

        Assert.Equal(default, missingRoot);
        Assert.Equal(default, missingTarget);
        Assert.Equal(default, missingParent);
    }

    [Fact]
    public void A_file_in_use_leaves_the_whole_directory_for_a_later_attempt()
    {
        using var temp = new TempDirectory();
        var root = temp.Combine(".Scribe");
        var ep = Path.Combine(root, "ep");
        var locked = temp.WriteFile(@".Scribe\ep\cuda-ep\loaded.dll", 128);
        var free = temp.WriteFile(@".Scribe\ep\cuda-ep\free.dll", 256);

        // Delete sharing too: Windows still refuses to rename a directory with any open file inside.
        FoundryStorageReclaimResult first;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            first = Janitor().ReclaimDirectory(root, ep, CancellationToken.None);
        }

        Assert.Equal(1, first.FilesDeferred);
        Assert.Equal(0, first.FilesDeleted);
        Assert.True(File.Exists(locked), "A file in use is never forced.");
        Assert.True(File.Exists(free), "All or nothing: no half-deleted runtime is left behind.");
        Assert.Empty(Tombstones(root, "ep"));

        var second = Janitor().ReclaimDirectory(root, ep, CancellationToken.None);

        Assert.Equal(2, second.FilesDeleted);
        Assert.Equal(0, second.FilesDeferred);
        Assert.False(Directory.Exists(ep));
        Assert.Empty(Tombstones(root, "ep"));
    }

    [Fact]
    public void File_by_file_deletion_leaves_only_the_file_in_use()
    {
        using var temp = new TempDirectory();
        var root = temp.Combine(".Scribe");
        var locked = temp.WriteFile(@".Scribe\ep\cuda-ep\loaded.dll", 128);
        temp.WriteFile(@".Scribe\ep\cuda-ep\free.dll", 256);

        FoundryStorageReclaimResult result;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = Janitor().DeleteContents(root, Path.Combine(root, "ep"), CancellationToken.None);
        }

        Assert.Equal(1, result.FilesDeferred);
        Assert.Equal(1, result.FilesDeleted);
        Assert.Equal(256, result.BytesDeleted);
        Assert.True(File.Exists(locked));
    }

    [Fact]
    public void A_directory_holding_a_native_library_loaded_here_is_left_whole()
    {
        // Measured on Windows: a mapped image holds no open handle, so the rename alone would succeed
        // and split the runtime in half (the loaded DLL then refuses deletion). The janitor must see it.
        using var temp = new TempDirectory();
        var root = temp.Combine(".Scribe");
        var ep = Path.Combine(root, "ep");
        var dll = temp.Combine(@".Scribe\ep\cuda-ep\probe.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(dll)!);
        File.Copy(Path.Combine(Environment.SystemDirectory, "version.dll"), dll);
        var sibling = temp.WriteFile(@".Scribe\ep\cuda-ep\other.bin", 32);

        var handle = NativeLibrary.Load(dll);
        FoundryStorageReclaimResult result;
        try
        {
            result = Janitor().ReclaimDirectory(root, ep, CancellationToken.None);
        }
        finally
        {
            NativeLibrary.Free(handle);
        }

        Assert.Equal(1, result.FilesDeferred);
        Assert.Equal(0, result.FilesDeleted);
        Assert.True(File.Exists(dll));
        Assert.True(File.Exists(sibling), "Nothing next to a loaded library is deleted either.");
        Assert.Empty(Tombstones(root, "ep"));
    }

    [Fact]
    public void A_module_loaded_in_this_process_stops_the_rename_before_it_happens()
    {
        var root = @"C:\fake\.Scribe";
        var target = @"C:\fake\.Scribe\ep";
        var fs = new FakeFileSystem { LoadedModules = [@"C:\Windows\System32\kernel32.dll", @"C:\fake\.Scribe\ep\cuda-ep\cuda.dll"] };
        fs.AddDirectory(root);
        fs.AddDirectory(target);
        fs.AddFile(@"C:\fake\.Scribe\ep\a.dll", 10);

        var result = new FoundryStorageJanitor(fs).ReclaimDirectory(root, target, CancellationToken.None);

        Assert.Equal(1, result.FilesDeferred);
        Assert.Empty(fs.Moves);
        Assert.Empty(fs.DeletedFiles);
    }

    [Fact]
    public void An_unlisted_module_table_does_not_block_reclaim()
    {
        // Reclaim only reaches these directories when no Foundry Local runtime exists in this
        // process, so a module list that cannot be read is not a reason to keep gigabytes forever.
        var root = @"C:\fake\.Scribe";
        var target = @"C:\fake\.Scribe\ep";
        var fs = new FakeFileSystem { LoadedModules = null };
        fs.AddDirectory(root);
        fs.AddDirectory(target);

        new FoundryStorageJanitor(fs).ReclaimDirectory(root, target, CancellationToken.None);

        var move = Assert.Single(fs.Moves);
        Assert.Equal(target, move.Source);
        Assert.StartsWith(@"C:\fake\.Scribe\.ep" + FoundryStorageJanitor.TombstoneMarker, move.Destination);
    }

    [Fact]
    public void Leftover_tombstones_are_deleted_by_the_next_pass_and_nothing_else_is_mistaken_for_one()
    {
        using var temp = new TempDirectory();
        var root = temp.Combine(".Scribe");
        var leftover = FoundryStorageJanitor.NewTombstoneName("ep");
        temp.WriteFile($@".Scribe\{leftover}\cuda-ep\half-deleted.dll", 100);
        var notOurs = temp.WriteFile(@".Scribe\.ep.reclaim-notaguid\keep.bin", 5);
        var otherTarget = temp.WriteFile($@".Scribe\{FoundryStorageJanitor.NewTombstoneName("logs")}\keep.log", 5);

        // The target itself is already gone; the leftover is still found and finished.
        var result = Janitor().ReclaimDirectory(root, Path.Combine(root, "ep"), CancellationToken.None);

        Assert.Equal(1, result.FilesDeleted);
        Assert.Equal(100, result.BytesDeleted);
        Assert.False(Directory.Exists(Path.Combine(root, leftover)));
        Assert.True(File.Exists(notOurs), "Only the exact .{name}.reclaim-{guid} shape is a tombstone.");
        Assert.True(File.Exists(otherTarget), "A tombstone of another directory belongs to that directory's pass.");
    }

    [Fact]
    public void A_leftover_tombstone_that_is_a_junction_is_skipped_not_followed()
    {
        using var temp = new TempDirectory();
        var root = temp.Combine(".Scribe");
        Directory.CreateDirectory(root);
        var elsewhere = temp.WriteFile(@"elsewhere\important.bin", 64);
        var link = Path.Combine(root, FoundryStorageJanitor.NewTombstoneName("ep"));
        if (!TryCreateJunction(link, temp.Combine("elsewhere")))
        {
            return;
        }

        var result = Janitor().ReclaimDirectory(root, Path.Combine(root, "ep"), CancellationToken.None);

        Assert.Equal(1, result.ReparsePointsSkipped);
        Assert.Equal(0, result.FilesDeleted);
        Assert.True(File.Exists(elsewhere));
        Assert.True(Directory.Exists(link));
    }

    [Fact]
    public void A_read_only_file_is_deleted()
    {
        using var temp = new TempDirectory();
        var root = temp.Combine(".Scribe");
        var file = temp.WriteFile(@".Scribe\cache\models\m\weights.onnx", 32);
        File.SetAttributes(file, FileAttributes.ReadOnly);

        var result = Janitor().ReclaimDirectory(root, Path.Combine(root, "cache", "models"), CancellationToken.None);

        Assert.Equal(1, result.FilesDeleted);
        Assert.False(File.Exists(file));
        Assert.Empty(Tombstones(Path.Combine(root, "cache"), "models"));
    }

    [Fact]
    public void A_cancelled_pass_stops_before_renaming_or_deleting()
    {
        using var temp = new TempDirectory();
        var root = temp.Combine(".Scribe");
        var file = temp.WriteFile(@".Scribe\ep\a.dll", 32);

        var result = Janitor().ReclaimDirectory(root, Path.Combine(root, "ep"), new CancellationToken(canceled: true));

        Assert.Equal(0, result.FilesDeleted);
        Assert.True(File.Exists(file));
        Assert.Empty(Tombstones(root, "ep"));
    }

    [Fact]
    public void A_junction_inside_the_target_is_neither_followed_nor_deleted()
    {
        using var temp = new TempDirectory();
        var root = temp.Combine(".Scribe");
        temp.WriteFile(@".Scribe\ep\own.dll", 8);
        var elsewhere = temp.WriteFile(@"elsewhere\important.bin", 64);
        var link = temp.Combine(@".Scribe\ep\moved-cache");
        if (!TryCreateJunction(link, temp.Combine("elsewhere")))
        {
            return; // No junction support here; the fake file system test below still pins the rule.
        }

        var result = Janitor().ReclaimDirectory(root, Path.Combine(root, "ep"), CancellationToken.None);

        Assert.Equal(1, result.FilesDeleted);
        Assert.Equal(1, result.ReparsePointsSkipped);
        Assert.True(File.Exists(elsewhere), "A junction's target is never reached.");

        // The link moved with its directory and stays, skipped, in the tombstone a later pass retries.
        var tombstone = Assert.Single(Tombstones(root, "ep"));
        Assert.True(new DirectoryInfo(Path.Combine(tombstone, "moved-cache")).Attributes.HasFlag(FileAttributes.ReparsePoint));

        var again = Janitor().ReclaimDirectory(root, Path.Combine(root, "ep"), CancellationToken.None);
        Assert.Equal(1, again.ReparsePointsSkipped);
        Assert.True(File.Exists(elsewhere));
    }

    [Fact]
    public void A_junction_on_the_path_to_the_target_refuses_the_whole_pass()
    {
        using var temp = new TempDirectory();
        var root = temp.Combine(".Scribe");
        Directory.CreateDirectory(root);
        var elsewhere = temp.WriteFile(@"moved-models\models\weights.onnx", 64);
        if (!TryCreateJunction(Path.Combine(root, "cache"), temp.Combine("moved-models")))
        {
            return;
        }

        // A user who moved the model cache to another drive with a junction must not have that
        // drive's contents deleted, or renamed.
        var result = Janitor().ReclaimDirectory(root, Path.Combine(root, "cache", "models"), CancellationToken.None);

        Assert.True(result.Refused);
        Assert.True(File.Exists(elsewhere));
    }

    [Fact]
    public void A_target_that_is_itself_a_junction_is_refused()
    {
        using var temp = new TempDirectory();
        var root = temp.Combine(".Scribe");
        Directory.CreateDirectory(root);
        var elsewhere = temp.WriteFile(@"moved-ep\cuda.dll", 64);
        if (!TryCreateJunction(Path.Combine(root, "ep"), temp.Combine("moved-ep")))
        {
            return;
        }

        var result = Janitor().ReclaimDirectory(root, Path.Combine(root, "ep"), CancellationToken.None);

        Assert.True(result.Refused);
        Assert.True(File.Exists(elsewhere));
        Assert.Empty(Tombstones(root, "ep"));
    }

    [Fact]
    public void A_root_that_is_a_junction_is_refused()
    {
        using var temp = new TempDirectory();
        var elsewhere = temp.WriteFile(@"real-profile-dir\ep\keep.dll", 64);
        var root = temp.Combine(".Scribe");
        if (!TryCreateJunction(root, temp.Combine("real-profile-dir")))
        {
            return;
        }

        var result = Janitor().ReclaimDirectory(root, Path.Combine(root, "ep"), CancellationToken.None);

        Assert.True(result.Refused);
        Assert.True(File.Exists(elsewhere));
    }

    [Fact]
    public void Reparse_points_and_escaping_entries_are_refused_by_the_walk_itself()
    {
        // Defence in depth against a file system that reports something odd: an enumerator that hands
        // back an entry outside the root, and a reparse point, must both be left alone.
        var root = @"C:\fake\.Scribe";
        var target = @"C:\fake\.Scribe\ep";
        var fs = new FakeFileSystem();
        fs.AddDirectory(root);
        fs.AddDirectory(target);
        fs.AddFile(@"C:\fake\.Scribe\ep\a.dll", 10);
        fs.AddDirectory(@"C:\fake\.Scribe\ep\link", reparse: true);
        fs.AddChild(target, new FoundryStorageEntry(@"C:\Windows\System32\kernel32.dll", false, false, 100, false));

        var result = new FoundryStorageJanitor(fs).DeleteContents(root, target, CancellationToken.None);

        Assert.Equal([@"C:\fake\.Scribe\ep\a.dll"], fs.DeletedFiles);
        Assert.Equal(1, result.ReparsePointsSkipped);
        Assert.True(result.Refused, "The escaping entry is reported, not deleted.");
    }

    [Fact]
    public void Measuring_counts_only_real_files_inside_the_root()
    {
        using var temp = new TempDirectory();
        var root = temp.Combine(".Scribe");
        temp.WriteFile(@".Scribe\cache\models\m\a.onnx", 100);
        temp.WriteFile(@".Scribe\cache\models\m\b.onnx", 50);
        temp.WriteFile(@"outside\c.onnx", 1000);

        Assert.Equal(150, Janitor().MeasureBytes(root, Path.Combine(root, "cache", "models", "m")));
        Assert.Equal(100, Janitor().MeasureBytes(root, Path.Combine(root, "cache", "models", "m", "a.onnx")));
        Assert.Equal(0, Janitor().MeasureBytes(root, temp.Combine("outside")));
        Assert.Equal(0, Janitor().MeasureBytes(root, "relative"));
    }

    [Fact]
    public void A_cache_holding_only_the_sdk_catalog_index_holds_no_model()
    {
        // The real layout on a PC that browsed the catalog but never downloaded a model: one loose
        // foundry.modelinfo.json in cache\models and nothing else.
        using var temp = new TempDirectory();
        var root = temp.Combine(".Scribe");
        var cache = Path.Combine(root, "cache", "models");

        Assert.Equal(FoundryModelCache.Empty, Janitor().InspectModelCache(root, cache));

        temp.WriteFile(@".Scribe\cache\models\foundry.modelinfo.json", 2048);
        Directory.CreateDirectory(Path.Combine(cache, "Microsoft", "removed-model"));

        Assert.Equal(FoundryModelCache.Empty, Janitor().InspectModelCache(root, cache));
    }

    [Fact]
    public void A_model_folder_holding_a_file_is_a_cached_model()
    {
        using var temp = new TempDirectory();
        var root = temp.Combine(".Scribe");
        temp.WriteFile(@".Scribe\cache\models\foundry.modelinfo.json", 2048);
        temp.WriteFile(@".Scribe\cache\models\Microsoft\qwen3-1.7b-generic-cpu-1\v1\model.onnx", 64);

        Assert.Equal(FoundryModelCache.HasModels, Janitor().InspectModelCache(root, Path.Combine(root, "cache", "models")));
    }

    [Fact]
    public void A_cache_that_cannot_be_reasoned_about_is_unknown_never_empty()
    {
        using var temp = new TempDirectory();
        var root = temp.Combine(".Scribe");
        var cache = Path.Combine(root, "cache", "models");
        Directory.CreateDirectory(cache);
        temp.WriteFile(@"moved\model.onnx", 64);

        Assert.Equal(FoundryModelCache.Unknown, Janitor().InspectModelCache(root, @"cache\models"));
        Assert.Equal(FoundryModelCache.Unknown, Janitor().InspectModelCache(root, temp.Combine("moved")));
        if (TryCreateJunction(Path.Combine(cache, "Microsoft"), temp.Combine("moved")))
        {
            // A model folder moved elsewhere with a junction may well hold the selected model.
            Assert.Equal(FoundryModelCache.Unknown, Janitor().InspectModelCache(root, cache));
        }
    }

    private static bool TryCreateJunction(string link, string target)
    {
        // mklink /J needs no elevation, unlike a symbolic link.
        try
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
            {
                return false;
            }

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 && new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private sealed class FakeFileSystem : IFoundryStorageFileSystem
    {
        private readonly Dictionary<string, FoundryStorageEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<FoundryStorageEntry>> _children = new(StringComparer.OrdinalIgnoreCase);

        public List<string> DeletedFiles { get; } = [];

        public List<(string Source, string Destination)> Moves { get; } = [];

        public IReadOnlyList<string>? LoadedModules { get; set; } = [];

        public void AddDirectory(string path, bool reparse = false) => Add(new FoundryStorageEntry(path, true, reparse, 0, false));

        public void AddFile(string path, long length) => Add(new FoundryStorageEntry(path, false, false, length, false));

        public void AddChild(string directory, FoundryStorageEntry entry) => ChildrenOf(directory).Add(entry);

        public FoundryStorageEntry? GetEntry(string path) => _entries.TryGetValue(path, out var entry) ? entry : null;

        public IEnumerable<FoundryStorageEntry> EnumerateEntries(string directory) => ChildrenOf(directory);

        public void DeleteFile(string path, bool clearReadOnly) => DeletedFiles.Add(path);

        public void DeleteEmptyDirectory(string path) => throw new IOException("not empty");

        // Recorded only: nothing here needs the tree to actually move.
        public void MoveDirectory(string source, string destination) => Moves.Add((source, destination));

        public IReadOnlyList<string>? LoadedModulePaths() => LoadedModules;

        private void Add(FoundryStorageEntry entry)
        {
            _entries[entry.Path] = entry;
            var parent = Path.GetDirectoryName(entry.Path);
            if (parent is not null && _entries.ContainsKey(parent))
            {
                ChildrenOf(parent).Add(entry);
            }
        }

        private List<FoundryStorageEntry> ChildrenOf(string directory)
        {
            if (!_children.TryGetValue(directory, out var list))
            {
                list = [];
                _children[directory] = list;
            }

            return list;
        }
    }
}

/// <summary>The Foundry Local directory layout and the persisted "keep only the selected model" marker.</summary>
public sealed class FoundryLocalStorageTests
{
    [Fact]
    public void The_layout_is_the_one_the_sdk_documents_for_app_name_scribe()
    {
        // The normal profile: {home}/.{AppName}, then {appdata}/cache/models, and the observed ep folder.
        var appData = FoundryLocalStorage.ResolveAppDataDir(isolatedRoot: false, @"C:\data", @"C:\Users\someone")!;
        var storage = new FoundryLocalStorage(
            appData, @"C:\data\" + FoundryLocalStorage.MarkerFileName, new FoundryStorageJanitor(new PhysicalFoundryStorageFileSystem()));

        Assert.Equal(@"C:\Users\someone\.Scribe", storage.AppDataDir);
        Assert.Equal(@"C:\Users\someone\.Scribe\cache\models", storage.ModelCacheDir);
        Assert.Equal(@"C:\Users\someone\.Scribe\ep", storage.ExecutionProviderDir);
        Assert.NotEqual(@"C:\Users\someone\.foundry", storage.AppDataDir);
    }

    [Fact]
    public void A_relative_data_directory_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new FoundryLocalStorage(
            ".Scribe", @"C:\marker.json", new FoundryStorageJanitor(new PhysicalFoundryStorageFileSystem())));
    }

    [Fact]
    public void The_marker_round_trips_and_an_unreadable_one_deletes_nothing()
    {
        using var temp = new TempDirectory();
        var storage = new FoundryLocalStorage(
            temp.Combine(".Scribe"), temp.Combine("state", "marker.json"),
            new FoundryStorageJanitor(new PhysicalFoundryStorageFileSystem()));

        Assert.False(storage.ReadKeepOnlySelected());
        Assert.True(storage.WriteKeepOnlySelected(true));
        Assert.True(storage.ReadKeepOnlySelected());
        Assert.True(storage.WriteKeepOnlySelected(false));
        Assert.False(File.Exists(storage.MarkerPath));

        File.WriteAllText(storage.MarkerPath, "{ not json");
        Assert.False(storage.ReadKeepOnlySelected());
        Assert.Empty(Directory.GetFiles(temp.Combine("state"), "*.tmp"));
    }

    [Fact]
    public void Logged_paths_fold_the_user_profile_away()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.Equal(@"%USERPROFILE%\.Scribe\ep", FoundryLocalStorage.DisplayPath(Path.Combine(home, ".Scribe", "ep")));
        Assert.Equal("%USERPROFILE%", FoundryLocalStorage.DisplayPath(home));

        // A sibling profile that shares the prefix is not this user's profile.
        Assert.Equal("(outside the user profile)", FoundryLocalStorage.DisplayPath(home + "X\\.Scribe"));
        Assert.Equal("(outside the user profile)", FoundryLocalStorage.DisplayPath(@"D:\elsewhere\.Scribe"));
    }
}
