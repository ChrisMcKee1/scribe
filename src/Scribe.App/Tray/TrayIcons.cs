using System.Drawing;
using System.IO;

namespace Scribe.App.Tray;

/// <summary>
/// Supplies the Scribe brand mark and its state variants as native <see cref="Icon"/>s for
/// H.NotifyIcon.
/// </summary>
/// <remarks>
/// Every call hands back a <b>fresh</b> icon, and the caller owns it. H.NotifyIcon disposes the
/// icon it replaces, so a single long-lived instance per state is destroyed the first time that
/// state is replaced; assigning it again reads a zeroed handle and throws
/// <see cref="ObjectDisposedException"/> from inside the tray update. That exception escapes the
/// dictation state notification and stops every later subscriber, which is what left the recording
/// pill frozen on its last state while dictation itself kept working.
/// </remarks>
internal static class TrayIcons
{
    private static readonly byte[]? IdleData = ReadResource("scribe.ico");
    private static readonly byte[]? RecordingData = ReadResource("scribe-recording.ico");
    private static readonly byte[]? ProcessingData = ReadResource("scribe-processing.ico");
    private static readonly byte[]? PausedData = ReadResource("scribe-paused.ico");

    /// <summary>Neutral idle icon (ready to dictate).</summary>
    public static Icon CreateIdle() => Create(IdleData);

    /// <summary>Recording icon (capture in progress).</summary>
    public static Icon CreateRecording() => Create(RecordingData);

    /// <summary>Processing icon (transcribing / injecting).</summary>
    public static Icon CreateProcessing() => Create(ProcessingData);

    /// <summary>Paused icon with the waveform muted to slate.</summary>
    public static Icon CreatePaused() => Create(PausedData);

    // Read once into memory so a state change never touches the assembly manifest again.
    private static byte[]? ReadResource(string fileName)
    {
        try
        {
            var resourceName = $"Scribe.App.Assets.{fileName}";
            using var stream = typeof(TrayIcons).Assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                return null;
            }

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
        catch
        {
            // A missing or unreadable resource degrades to the framework icon rather than
            // preventing the tray from being created at all.
            return null;
        }
    }

    private static Icon Create(byte[]? data)
    {
        if (data is not null)
        {
            try
            {
                using var stream = new MemoryStream(data);
                // The 64 px frame scales cleanly on high-DPI taskbars.
                return new Icon(stream, 64, 64);
            }
            catch
            {
                // Fall through to the framework icon.
            }
        }

        // A damaged resource must not prevent the tray app from starting. This icon is always
        // available from the framework and keeps the application controllable so it can quit.
        return (Icon)SystemIcons.Application.Clone();
    }
}
