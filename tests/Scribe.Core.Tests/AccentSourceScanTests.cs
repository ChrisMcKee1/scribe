namespace Scribe.Core.Tests;

// The accent resolver in AccentContrastResources is the only thing allowed to choose the accent (Signal On, PD01 to
// PD03). WPF-UI updates the accent itself wherever a watched window or a theme application is allowed to, which would
// replace Scribe blue with the Windows accent behind the resolver's back.
public sealed class AccentSourceScanTests
{
    [Fact]
    public void Watched_windows_never_let_WPF_UI_update_accents()
    {
        var root = RepositoryRoot();
        var offenders = RepositoryViolations(root)
            .Where(v => v.Call.StartsWith("SystemThemeWatcher.Watch(", StringComparison.Ordinal))
            .Select(v => $"{v.RelativePath}: {v.Call}")
            .ToList();

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
    public void Theme_applications_never_update_accents()
    {
        var root = RepositoryRoot();
        var offenders = RepositoryViolations(root)
            .Where(v => !v.Call.StartsWith("SystemThemeWatcher.Watch(", StringComparison.Ordinal))
            .Select(v => $"{v.RelativePath}: {v.Call}")
            .ToList();

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    [Theory]
    [InlineData("ApplicationThemeManager.Apply(currentTheme);")]
    [InlineData("ApplicationThemeManager.Apply(applicationTheme: ApplicationTheme.Dark);")]
    [InlineData("ApplicationThemeManager.Apply(\r\n    currentTheme,\r\n    WindowBackdropType.Mica);")]
    [InlineData("SystemThemeWatcher.Watch (this);")]
    [InlineData("ApplicationThemeManager.ApplySystemTheme();")]
    [InlineData("ApplicationThemeManager.ApplySystemTheme(true);")]
    public void Detector_flags_accent_bypasses(string source)
    {
        Assert.NotEmpty(FindAccentPolicyViolations(source));
    }

    [Theory]
    [InlineData("ApplicationThemeManager.Apply(_menu);")]
    [InlineData("ApplicationThemeManager.Apply(this);")]
    [InlineData("ApplicationThemeManager.Apply(frame);")]
    [InlineData("ApplicationThemeManager.Apply(\r\n    currentTheme,\r\n    WindowBackdropType.Mica,\r\n    updateAccent: false);")]
    [InlineData("SystemThemeWatcher.Watch (this, WindowBackdropType.Mica, updateAccents: false);")]
    [InlineData("ApplicationThemeManager.ApplySystemTheme(false);")]
    [InlineData("ApplicationThemeManager.ApplySystemTheme(updateAccent: false);")]
    public void Detector_allows_compliant_calls(string source)
    {
        Assert.Empty(FindAccentPolicyViolations(source));
    }

    private static IReadOnlyList<(string RelativePath, string Call)> RepositoryViolations(string root)
    {
        var offenders = new List<(string RelativePath, string Call)>();
        foreach (var file in SourceFiles(Path.Combine(root, "src", "Scribe.App")))
        {
            var relative = Path.GetRelativePath(root, file);
            foreach (var call in FindAccentPolicyViolations(File.ReadAllText(file), relative))
            {
                offenders.Add((relative, call));
            }
        }

        return offenders;
    }

    private static IReadOnlyList<string> FindAccentPolicyViolations(string source, string relativePath = "")
    {
        var compact = RemoveWhitespaceAndComments(source);
        var offenders = new List<string>();
        Scan("SystemThemeWatcher.Watch(", call =>
        {
            if (!call.Args.Contains("updateAccents:false", StringComparison.Ordinal))
            {
                offenders.Add(call.Full);
            }
        });
        Scan("ApplicationThemeManager.ApplySystemTheme(", call =>
        {
            if (call.Args != "false" && !call.Args.Contains("updateAccent:false", StringComparison.Ordinal))
            {
                offenders.Add(call.Full);
            }
        });
        Scan("ApplicationThemeManager.Apply(", call =>
        {
            if (IsElementApplyOverload(call.Args))
            {
                return;
            }

            if (!call.Args.Contains("updateAccent:false", StringComparison.Ordinal))
            {
                offenders.Add(call.Full);
            }
        });
        return offenders;

        void Scan(string prefix, Action<(string Full, string Args)> inspect)
        {
            var index = compact.IndexOf(prefix, StringComparison.Ordinal);
            while (index >= 0)
            {
                var argsStart = index + prefix.Length;
                if (TryReadArguments(compact, argsStart, out var args, out var end))
                {
                    inspect((compact[index..(end + 1)], args));
                    index = end + 1;
                }
                else
                {
                    index += prefix.Length;
                }

                index = index <= compact.Length
                    ? compact.IndexOf(prefix, index, StringComparison.Ordinal)
                    : -1;
            }
        }
    }

    private static bool IsElementApplyOverload(string args) =>
        !ContainsTopLevelComma(args) &&
        !args.Contains("Theme", StringComparison.Ordinal) &&
        !args.Contains("theme", StringComparison.Ordinal);

    private static bool ContainsTopLevelComma(string text)
    {
        var depth = 0;
        foreach (var ch in text)
        {
            if (ch == '(')
            {
                depth++;
            }
            else if (ch == ')')
            {
                depth--;
            }
            else if (ch == ',' && depth == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadArguments(string text, int argsStart, out string args, out int closeIndex)
    {
        var depth = 1;
        for (var i = argsStart; i < text.Length; i++)
        {
            if (text[i] == '(')
            {
                depth++;
            }
            else if (text[i] == ')' && --depth == 0)
            {
                args = text[argsStart..i];
                closeIndex = i;
                return true;
            }
        }

        args = string.Empty;
        closeIndex = -1;
        return false;
    }

    private static string RemoveWhitespaceAndComments(string source)
    {
        var result = new System.Text.StringBuilder(source.Length);
        for (var i = 0; i < source.Length; i++)
        {
            if (char.IsWhiteSpace(source[i]))
            {
                continue;
            }

            if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                i += 2;
                while (i < source.Length && source[i] != '\r' && source[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && (source[i] != '*' || source[i + 1] != '/'))
                {
                    i++;
                }

                i++;
                continue;
            }

            result.Append(source[i]);
        }

        return result.ToString();
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