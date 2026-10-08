namespace Scribe.Core.Tests;

/// <summary>
/// Try dictation shares the footer's dirty check and Save's draft, including the word pack workspace rather than its
/// downgrade projection. A separate comparison of all enabled packs with that projection made an unchanged window dirty.
/// </summary>
public sealed class TryDictationDraftCoverageTests
{
    [Fact]
    public void Try_dictation_and_the_footer_compare_the_draft_Save_actually_stores()
    {
        var root = RepositoryRoot();
        var window = string.Join("\n", Directory.EnumerateFiles(Path.Combine(root, "src", "Scribe.App", "Settings"), "SettingsWindow*.cs")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(File.ReadAllText));
        var tryDictation = File.ReadAllText(Path.Combine(root, "src", "Scribe.App", "Settings", "SettingsWindow.TryDictation.cs"));
        var save = Body(window, "private async Task<bool> TrySaveAsync()");
        var captured = Body(window, "private AppSettings CaptureDraftSettings()");
        Assert.Contains("var changes = currentChanges ?? ComputeCurrentChanges();", tryDictation, StringComparison.Ordinal);
        Assert.DoesNotContain("TryDictationDraft(", tryDictation, StringComparison.Ordinal);
        Assert.DoesNotContain("CollectEnabledLibraryIds()", captured, StringComparison.Ordinal);
        var comparison = Body(window, "private SettingsChangeSet ComputeCurrentChanges()");
        Assert.Contains("var draft = CaptureDraftSettings();", comparison, StringComparison.Ordinal);
        Assert.Contains("_wordPackWorkspace?.HasUnsavedChanges == true", comparison, StringComparison.Ordinal);

        var copy = save.LastIndexOf("CopySettings(preflight.Settings, _settings);", StringComparison.Ordinal);
        var store = save.IndexOf("await _wordPackSaveProtocol.SaveAsync(", StringComparison.Ordinal);
        Assert.True(copy >= 0 && copy < store, "Save must store the captured preflight, not later control values.");
        Assert.DoesNotMatch(@"_settings\.(?!LaunchOnLogin\b)\w+\s*=(?![=>])", save[copy..store]);
        Assert.Contains("var settings = CaptureDraftSettings();",
            Body(window, "private async Task<SavePreflightInput?> PrepareSavePreflightAsync()"), StringComparison.Ordinal);
        var copier = Body(window, "private static void CopySettings(");
        Assert.Contains("var copy = source.Clone();", copier, StringComparison.Ordinal);
        Assert.Contains("property.CanRead && property.CanWrite && property.Name != nameof(AppSettings.LaunchOnLogin)", copier, StringComparison.Ordinal);
        Assert.Contains("property.SetValue(target, property.GetValue(copy));", copier, StringComparison.Ordinal);
    }

    [Fact]
    public void Try_dictation_tracks_live_dirty_changes_and_shows_only_the_shortcuts_in_use()
    {
        var settings = Path.Combine(RepositoryRoot(), "src", "Scribe.App", "Settings");
        var tryDictation = File.ReadAllText(Path.Combine(settings, "SettingsWindow.TryDictation.cs"));
        var footer = File.ReadAllText(Path.Combine(settings, "SettingsWindow.Footer.cs"));
        Assert.Contains("UpdateTryDictationPage(_currentChanges);", Body(footer, "private void RefreshFooterNow()"), StringComparison.Ordinal);
        var page = Body(tryDictation, "private void UpdateTryDictationPageCore(");
        Assert.Contains("var primary = _committedSettings.Hotkey;", page, StringComparison.Ordinal);
        Assert.Contains("_committedSettings.DictationOnlyHotkey", page, StringComparison.Ordinal);
        Assert.DoesNotContain("_pendingBinding", page, StringComparison.Ordinal);
        Assert.DoesNotContain("_pendingDictationOnlyBinding", page, StringComparison.Ordinal);
        Assert.Contains("TryDictationUnsavedHost.Visibility = changes.IsDirty", page, StringComparison.Ordinal);
        Assert.DoesNotContain("TryDictationRestartBar.Visibility", page, StringComparison.Ordinal);
    }

    private static string Body(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{signature} was not found.");
        var open = source.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            depth += source[i] switch { '{' => 1, '}' => -1, _ => 0 };
            if (depth == 0)
            {
                return source[open..(i + 1)];
            }
        }

        throw new InvalidOperationException($"{signature} has no end.");
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
}
