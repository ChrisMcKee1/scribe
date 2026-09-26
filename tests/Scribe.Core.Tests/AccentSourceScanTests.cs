namespace Scribe.Core.Tests;

public sealed class AccentSourceScanTests

{

    [Fact]

    public void Watched_windows_never_let_WPF_UI_update_accents()

    {

        var root = RepositoryRoot();

        var offenders = new List<string>();

        foreach (var file in SourceFiles(Path.Combine(root, "src", "Scribe.App")))

        {

            var relative = Path.GetRelativePath(root, file);

            var lines = File.ReadAllLines(file);

            for (var i = 0; i < lines.Length; i++)

            {

                if (lines[i].Contains("SystemThemeWatcher.Watch(", StringComparison.Ordinal) &&

                    !lines[i].Contains("updateAccents: false", StringComparison.Ordinal))

                {

                    offenders.Add($"{relative}:{i + 1}: {lines[i].Trim()}");

                }

            }

        }

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));

    }

    [Fact]

    public void Only_accent_resources_applies_accent_colours_directly()

    {

        var root = RepositoryRoot();

        var offenders = new List<string>();

        foreach (var file in SourceFiles(Path.Combine(root, "src", "Scribe.App")))

        {

            var relative = Path.GetRelativePath(root, file);

            var source = File.ReadAllText(file);

            if (source.Contains("ApplySystemAccent(", StringComparison.Ordinal))

            {

                offenders.Add($"{relative}: calls ApplySystemAccent");

            }

            if (!relative.Equals(Path.Combine("src", "Scribe.App", "Infrastructure", "AccentContrastResources.cs"), StringComparison.OrdinalIgnoreCase) &&

                source.Contains("ApplicationAccentColorManager.Apply(", StringComparison.Ordinal))

            {

                offenders.Add($"{relative}: calls ApplicationAccentColorManager.Apply");

            }

        }

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));

    }

    [Fact]

    public void Theme_apply_calls_that_take_a_theme_do_not_update_accents()

    {

        var root = RepositoryRoot();

        var offenders = new List<string>();

        foreach (var file in SourceFiles(Path.Combine(root, "src", "Scribe.App")))

        {

            var relative = Path.GetRelativePath(root, file);

            var lines = File.ReadAllLines(file);

            for (var i = 0; i < lines.Length; i++)

            {

                var line = lines[i];

                if (!line.Contains("ApplicationThemeManager.Apply(theme", StringComparison.Ordinal))
                {
                    continue;
                }

                if (relative.Equals(Path.Combine("src", "Scribe.App", "App.xaml.cs"), StringComparison.OrdinalIgnoreCase))

                {

                    // T2 sets updateAccent: false here (PD03).

                    continue;

                }

                if (!line.Contains("updateAccent: false", StringComparison.Ordinal))

                {

                    offenders.Add($"{relative}:{i + 1}: {line.Trim()}");

                }

            }

        }

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));

    }

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

    private static IEnumerable<string> SourceFiles(string folder) =>

        Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)

            .Where(file => !IsBuildOutput(file));

    private static bool IsBuildOutput(string file)

    {

        var separator = Path.DirectorySeparatorChar;

        return file.Contains($"{separator}obj{separator}", StringComparison.OrdinalIgnoreCase) ||

            file.Contains($"{separator}bin{separator}", StringComparison.OrdinalIgnoreCase);

    }

}

