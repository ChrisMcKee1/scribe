using System.Text.RegularExpressions;

namespace Scribe.Core.Tests;

/// <summary>
/// <c>FindResource</c> throws when its key is missing, and the Settings window calls it from its constructor, so a key
/// removed from the XAML (the Phase 2 visual system dropped <c>SettingHint</c>) made Settings fail to open while every
/// build stayed green. Every literal key the app looks up with a throwing lookup must be declared in its own XAML.
/// </summary>
public sealed class ResourceKeySourceTests
{
    [Fact]
    public void Every_throwing_resource_lookup_names_a_key_the_app_declares()
    {
        var app = Path.Combine(RepositoryRoot(), "src", "Scribe.App");
        var declared = Directory.EnumerateFiles(app, "*.xaml", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(file))
            .SelectMany(file => Regex.Matches(File.ReadAllText(file), @"x:Key=""(?<key>[^""{}]+)""").Select(match => match.Groups["key"].Value))
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(declared);

        var lookups = Directory.EnumerateFiles(app, "*.cs", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(file))
            .SelectMany(file => Regex.Matches(File.ReadAllText(file), @"(?<![\w.]Try)(?<!Try)FindResource\(\s*""(?<key>[^""]+)""\s*\)")
                .Select(match => (File: Path.GetFileName(file), Key: match.Groups["key"].Value)))
            .ToList();

        Assert.All(lookups, lookup => Assert.True(
            declared.Contains(lookup.Key),
            $"{lookup.File} looks up \"{lookup.Key}\" with FindResource, which throws, and no XAML in Scribe.App declares it."));
    }

    private static bool IsBuildOutput(string file) =>
        file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
        file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return root.FullName;
    }
}
