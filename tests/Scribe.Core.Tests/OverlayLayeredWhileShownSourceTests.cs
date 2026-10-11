using System.Text.RegularExpressions;

namespace Scribe.Core.Tests;

/// <summary>
/// The pill's window is layered only while it is on screen. Win+Shift+S was bisected (0.3.12) to the overlay window having
/// been shown: never created and created but never shown both worked. Since 0.5.0 the helper stays running hidden all
/// session, so whatever a shown window keeps lasts until Scribe quits. WS_EX_LAYERED is what makes WS_EX_TRANSPARENT pass
/// clicks through, so it goes on before every show, and every hide after a show takes it off, which Using Layered Windows
/// says lets the system "free up some memory associated with layering and redirection". The overlay has no reference to
/// Scribe.Core, so no test here can load it; its source is pinned instead, comments aside.
/// </summary>
public sealed class OverlayLayeredWhileShownSourceTests
{
    [Fact]
    public void Every_hide_after_a_show_takes_the_layered_style_off()
    {
        var window = StripComments(File.ReadAllText(OverlayFile("OverlayWindow.xaml.cs")));
        var hides = Regex.Count(window, @"_appWindow\.Hide\(\);");
        var hidesThenUnlayered = Regex.Count(window, @"_appWindow\.Hide\(\);\s*SetLayered\(false\);");

        // The constructor's hide is the one that follows no show: the window keeps the style there, as the bisection's
        // created-but-never-shown case did.
        var constructor = Body(window, "public OverlayWindow()");
        Assert.Contains("_appWindow.Hide();", constructor, StringComparison.Ordinal);
        Assert.DoesNotContain("SetLayered(", constructor, StringComparison.Ordinal);

        Assert.True(hides >= 3, "The overlay's hides were not found.");
        Assert.Equal(hides - 1, hidesThenUnlayered);
    }

    [Fact]
    public void Every_show_puts_the_layered_style_back_first()
    {
        var window = StripComments(File.ReadAllText(OverlayFile("OverlayWindow.xaml.cs")));
        var show = Body(window, "private void EnsureShown()");

        var layered = show.IndexOf("SetLayered(true);", StringComparison.Ordinal);
        var shown = show.IndexOf("_appWindow.Show(activateWindow: false);", StringComparison.Ordinal);
        Assert.True(layered >= 0, "EnsureShown does not put the layered style back.");
        Assert.True(shown > layered, "The window is shown before it is layered again, so it would not be click-through.");
        Assert.Equal(1, Regex.Count(window, @"_appWindow\.Show\("));
    }

    [Fact]
    public void Only_the_layered_style_changes_after_the_window_is_built()
    {
        // Two writes of the extended style: the constructor's, and SetLayered's, which changes WS_EX_LAYERED alone, so
        // WS_EX_TRANSPARENT, WS_EX_TOOLWINDOW and WS_EX_NOACTIVATE stay as the constructor set them.
        var overlay = Directory
            .EnumerateFiles(Path.Combine(RepositoryRoot(), "src", "Scribe.Overlay"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.EndsWith($"Interop{Path.DirectorySeparatorChar}NativeMethods.cs", StringComparison.Ordinal))
            .Sum(f => Regex.Count(StripComments(File.ReadAllText(f)), @"SetWindowLongPtr\("));
        Assert.Equal(2, overlay);

        var window = StripComments(File.ReadAllText(OverlayFile("OverlayWindow.xaml.cs")));
        Assert.Contains("SetWindowLongPtr(", Body(window, "private void ApplyExtendedStyles()"), StringComparison.Ordinal);

        var setLayered = Body(window, "private void SetLayered(bool onScreen)");
        Assert.Contains("SetWindowLongPtr(", setLayered, StringComparison.Ordinal);
        Assert.Contains("ex | _layeredStyle", setLayered, StringComparison.Ordinal);
        Assert.Contains("ex & ~_layeredStyle", setLayered, StringComparison.Ordinal);
        Assert.Contains("_keepLayeredWhileHidden", setLayered, StringComparison.Ordinal);

        // A hide with no show before it (a HIDE right after launch) leaves the never-shown window as the bisection found it.
        Assert.Contains("!_shownOnce", setLayered, StringComparison.Ordinal);
    }

    private static string Body(string code, string signature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' was not found in the overlay.");
        var open = code.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < code.Length; i++)
        {
            depth += code[i] == '{' ? 1 : code[i] == '}' ? -1 : 0;
            if (depth == 0)
            {
                return code[start..(i + 1)];
            }
        }

        throw new InvalidOperationException($"'{signature}' has no closing brace.");
    }

    private static string StripComments(string source) => Regex.Replace(source, @"//[^\n]*", string.Empty);

    private static string OverlayFile(string name) => Path.Combine(RepositoryRoot(), "src", "Scribe.Overlay", name);

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
