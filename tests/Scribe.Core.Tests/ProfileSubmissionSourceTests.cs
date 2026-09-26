namespace Scribe.Core.Tests;

/// <summary>
/// A Save marks as saved only what it stored. The word pack Save awaits its preparation between reading the rows and
/// committing, so the profile rows' saved baseline must come from what was read before that wait, like the snippets'
/// submission (review of 7b722fe: clearing a profile's apps during the wait was recorded as saved, and the next Save then
/// treated the incomplete profile as unchanged legacy data). Pinned by source because the window has no tests of its own.
/// </summary>
public sealed class ProfileSubmissionSourceTests
{
    [Fact]
    public void The_save_marks_the_profile_rows_it_submitted_not_the_live_rows()
    {
        var settings = Path.Combine(RepositoryRoot(), "src", "Scribe.App", "Settings");
        var window = File.ReadAllText(Path.Combine(settings, "SettingsWindow.xaml.cs"));
        var profiles = File.ReadAllText(Path.Combine(settings, "SettingsWindow.Profiles.cs"));

        var save = Body(window, "private async Task<bool> TrySaveAsync()");
        var captured = save.IndexOf("var profileSubmission = CaptureProfileSubmission();", StringComparison.Ordinal);
        var awaited = save.IndexOf("await _wordPackSaveProtocol.SaveAsync(", StringComparison.Ordinal);
        Assert.True(captured > 0 && awaited > captured, "The profile submission must be read before the word pack Save awaits.");
        Assert.Contains("profileSubmission: profileSubmission", save, StringComparison.Ordinal);

        var request = Body(window, "private WordPackSaveProtocolRequest BuildWordPackSaveRequest(");
        Assert.Contains("MarkProfileRowsSaved(profileSubmission ?? []);", request, StringComparison.Ordinal);

        var mark = Body(profiles, "private void MarkProfileRowsSaved(");
        foreach (var field in new[] { "Name", "Processes", "WritingStyle", "NewlineHandling" })
        {
            Assert.Contains($"row.Loaded{field} = submitted.{field};", mark, StringComparison.Ordinal);
            Assert.DoesNotContain($"row.Loaded{field} = row.{field};", mark, StringComparison.Ordinal);
        }
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
