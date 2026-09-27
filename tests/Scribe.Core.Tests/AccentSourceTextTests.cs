using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

public sealed class AccentSourceTextTests
{
    [Fact]
    public void The_description_names_every_scribe_window_and_what_keeps_scribe_s_colors()
    {
        // The coordinator's accuracy points: Welcome and the dictionary cleanup review follow the setting too, so the
        // description says "Scribe's windows" rather than naming two of them, and in a contrast theme the pill and the tray
        // icon take system colors, so they "keep Scribe's colors" rather than "always use Scribe blue".
        Assert.Equal(
            "Scribe's windows and the tray menu use your Windows accent color instead of Scribe blue. " +
            "The recording indicator and the tray icon keep Scribe's colors.",
            AccentSourceText.Description(contrastTheme: false));
    }

    [Fact]
    public void A_contrast_theme_adds_that_windows_chooses_every_color()
    {
        var description = AccentSourceText.Description(contrastTheme: true);

        Assert.StartsWith(AccentSourceText.Description(contrastTheme: false), description, StringComparison.Ordinal);
        Assert.EndsWith(" While a contrast theme is on, Windows chooses every color.", description, StringComparison.Ordinal);
    }

    [Fact]
    public void The_window_shows_the_core_text_and_follows_the_contrast_theme()
    {
        var settings = Path.Combine(RepositoryRoot(), "src", "Scribe.App", "Settings");
        var xaml = File.ReadAllText(Path.Combine(settings, "SettingsWindow.xaml"));
        var code = string.Concat(Directory.EnumerateFiles(settings, "SettingsWindow*.cs").Select(File.ReadAllText));

        Assert.DoesNotContain("always use Scribe blue", xaml, StringComparison.Ordinal);
        Assert.Contains($"Text=\"{AccentSourceText.Title}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AccentSourceDescription.Text = AccentSourceText.Description(SystemParameters.HighContrast);", code, StringComparison.Ordinal);
        Assert.Contains("nameof(SystemParameters.HighContrast)", code, StringComparison.Ordinal);
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
