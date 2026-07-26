using System.Drawing;

namespace Scribe.App.Tray;

/// <summary>
/// Loads the Scribe brand mark and its state variants as native <see cref="Icon"/>s for
/// H.NotifyIcon. The resources include a 64 px frame so Windows can scale them cleanly on
/// high-DPI taskbars.
/// </summary>
internal static class TrayIcons
{
    /// <summary>Neutral idle icon (ready to dictate).</summary>
    public static Icon Idle { get; } = Load("scribe.ico");

    /// <summary>Recording icon (capture in progress).</summary>
    public static Icon Recording { get; } = Load("scribe-recording.ico");

    /// <summary>Processing icon (transcribing / injecting).</summary>
    public static Icon Processing { get; } = Load("scribe-processing.ico");

    /// <summary>Paused icon with the waveform muted to slate.</summary>
    public static Icon Paused { get; } = Load("scribe-paused.ico");

    private static Icon Load(string fileName)
    {
        try
        {
            var resourceName = $"Scribe.App.Assets.{fileName}";
            using var stream = typeof(TrayIcons).Assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded tray icon '{resourceName}' was not found.");
            using var source = new Icon(stream, 64, 64);
            return (Icon)source.Clone();
        }
        catch
        {
            // A damaged resource must not prevent the tray app from starting. This icon is always
            // available from the framework and keeps the application controllable so it can quit.
            return (Icon)SystemIcons.Application.Clone();
        }
    }
}
