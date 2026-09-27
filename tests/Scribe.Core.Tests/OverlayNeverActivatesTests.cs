using System.Text.RegularExpressions;

namespace Scribe.Core.Tests;

/// <summary>
/// The overlay pill never activates itself. Its first show used to call Window.Activate(), which "Attempts to activate the
/// application window by bringing it to the foreground and setting the input focus to it" (WinUI implements it as
/// ShowWindow with SW_SHOW and SetActiveWindow), and WS_EX_NOACTIVATE does not stop an explicit activation ("To activate
/// the window, use the SetActiveWindow or SetForegroundWindow function"): the user's log recorded
/// OverlayWindow.Activated state=CodeActivated during the first dictation after every overlay launch. The overlay has no
/// reference to Scribe.Core, so no test here can load it; its source is pinned instead, comments aside.
/// </summary>
public class OverlayNeverActivatesTests
{
    [Fact]
    public void The_overlay_shows_its_window_without_activating_it()
    {
        foreach (var file in Directory.EnumerateFiles(OverlaySourceFolder(), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var source = StripComments(File.ReadAllText(file));
            var name = Path.GetFileName(file);
            Assert.DoesNotMatch(new Regex(@"(?<![\w.])Activate\(\)"), source);
            Assert.DoesNotMatch(new Regex(@"\.Activate\(\)"), source);
            Assert.False(source.Contains("SetForegroundWindow", StringComparison.Ordinal), $"{name} sets the foreground window.");
            Assert.False(source.Contains("SetActiveWindow", StringComparison.Ordinal), $"{name} activates a window.");
            Assert.False(source.Contains("ShowWindow", StringComparison.Ordinal), $"{name} calls ShowWindow, whose SW_SHOW activates.");

            // AppWindow.Show() "Shows the window and activates it"; Show(true) too.
            Assert.DoesNotMatch(new Regex(@"\.Show\(\s*\)"), source);
            Assert.DoesNotMatch(new Regex(@"\.Show\(\s*(activateWindow:\s*)?true\s*\)"), source);
        }

        var window = StripComments(File.ReadAllText(Path.Combine(OverlaySourceFolder(), "OverlayWindow.xaml.cs")));
        Assert.Contains("_appWindow.Show(activateWindow: false);", window, StringComparison.Ordinal);
    }

    private static string StripComments(string source) =>
        Regex.Replace(source, @"//[^\n]*", string.Empty);

    private static string OverlaySourceFolder()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return Path.Combine(root.FullName, "src", "Scribe.Overlay");
    }
}
