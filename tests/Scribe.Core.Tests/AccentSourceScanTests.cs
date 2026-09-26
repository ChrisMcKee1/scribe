namespace Scribe.Core.Tests;

// The accent resolver in AccentContrastResources is the only thing allowed to choose the accent (Signal On, PD01 to
// PD03). WPF-UI updates the accent itself wherever a watched window or a theme application is allowed to, which would
// replace Scribe blue with the Windows accent behind the resolver's back.
public sealed class AccentSourceScanTests
{
    // The one known exception until T2 sets updateAccent: false in App.xaml.cs (PD03).
    private const string KnownAppThemeApply = "ApplicationThemeManager.Apply(theme, updateAccent: true);";

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
        var resolver = Path.Combine("src", "Scribe.App", "Infrastructure", "AccentContrastResources.cs");
        var offenders = new List<string>();
        foreach (var file in SourceFiles(Path.Combine(root, "src", "Scribe.App")))
        {
            var relative = Path.GetRelativePath(root, file);
            var source = File.ReadAllText(file);
            if (source.Contains("ApplySystemAccent(", StringComparison.Ordinal))
            {
                offenders.Add($"{relative}: calls ApplySystemAccent");
            }

            if (!relative.Equals(resolver, StringComparison.OrdinalIgnoreCase) &&
                source.Contains("ApplicationAccentColorManager.Apply(", StringComparison.Ordinal))
            {
                offenders.Add($"{relative}: calls ApplicationAccentColorManager.Apply");
            }
        }

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void Theme_applications_never_update_accents_except_the_one_T2_removes()
    {
        var root = RepositoryRoot();
        var app = Path.Combine("src", "Scribe.App", "App.xaml.cs");
        var offenders = new List<string>();
        foreach (var file in SourceFiles(Path.Combine(root, "src", "Scribe.App")))
        {
            var relative = Path.GetRelativePath(root, file);
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                var appliesTheme = line.Contains("ApplicationThemeManager.Apply(theme", StringComparison.Ordinal) ||
                    line.Contains("ApplicationThemeManager.Apply(ApplicationTheme.", StringComparison.Ordinal) ||
                    line.Contains("ApplySystemTheme(", StringComparison.Ordinal);
                if (!appliesTheme || line.Contains("updateAccent: false", StringComparison.Ordinal))
                {
                    continue;
                }

                if (relative.Equals(app, StringComparison.OrdinalIgnoreCase) &&
                    line.Equals(KnownAppThemeApply, StringComparison.Ordinal))
                {
                    continue;
                }

                offenders.Add($"{relative}:{i + 1}: {line}");
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