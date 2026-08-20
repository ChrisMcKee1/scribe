using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using Scribe.Core.Audio;
using Scribe.Core.Diagnostics;
using Scribe.Core.Infrastructure;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.Transcription;

namespace Scribe.App.Infrastructure;

/// <summary>
/// Owns this run's identity and gathers the environment facts that go at the top of the log and
/// into an exported diagnostics bundle.
/// <para>
/// Registered as a singleton so the session id written in the startup banner is the same one the
/// dictation pipeline stamps on each capture, and so the Settings page can rebuild the same report
/// on demand without duplicating any of the collection logic.
/// </para>
/// </summary>
public sealed class SessionDiagnostics(
    AppPaths paths,
    ISettingsRepository settings,
    IAudioCaptureService audio,
    ModelLocator models,
    ILogger<SessionDiagnostics> log)
{
    /// <summary>Identity of this run, quoted in the banner and on every dictation.</summary>
    public SessionIdentity Session { get; } = SessionIdentity.ForCurrentProcess();

    /// <summary>How this copy was installed, which decides the update path and the data location.</summary>
    public static InstallChannel Channel { get; } = DetectChannel();

    /// <summary>Writes the session banner to the log. Never throws.</summary>
    public void WriteBanner(ILogger target)
    {
        try
        {
            foreach (var line in Compose())
            {
                // One line per entry rather than one multi-line message: the log is read with grep
                // as often as with an editor, and a wrapped block breaks every line-oriented tool.
                target.LogInformation("{BannerLine}", line);
            }
        }
        catch (Exception ex)
        {
            // A banner is the least important thing in the process. It must never cost a startup.
            log.LogWarning(ex, "Could not write the session banner.");
        }
    }

    /// <summary>
    /// The plain-text report placed in an exported bundle: the same banner the log opens with,
    /// plus what the log folder currently holds and what the file is safe to do with.
    /// </summary>
    public string ComposeReport()
    {
        var builder = new StringBuilder();
        builder.AppendLine("Scribe diagnostics bundle");
        builder.AppendLine($"Exported {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine();
        builder.AppendLine("This bundle contains Scribe's diagnostic log files and the summary below.");
        builder.AppendLine("It does NOT contain your dictations, your dictionary, your snippets, your");
        builder.AppendLine("settings file, or any saved API key. Those live in scribe.db, which is never");
        builder.AppendLine("included. The logs do record the name of the app you dictated into, your");
        builder.AppendLine("audio device name, and error details, so read them before posting publicly.");
        builder.AppendLine();

        foreach (var line in Compose())
        {
            builder.AppendLine(line);
        }

        builder.AppendLine();
        builder.AppendLine("--- log folder ---");
        builder.AppendLine(DiagnosticsBundle.DescribeLogInventory(paths.LogsDir, DateOnly.FromDateTime(DateTime.Now)));

        return builder.ToString();
    }

    private IReadOnlyList<string> Compose()
    {
        // Read once. Every consumer below wants the same snapshot, and Load() is a database round
        // trip that would otherwise run three times and log three separate warnings for one fault.
        var current = TryLoadSettings();

        return SessionBanner.Compose(
            Session,
            UpdateService.RunningVersion,
            Channel,
            paths,
            current,
            WindowsPackageIdentity.TryGetPackageFamilyName(),
            DescribeModel(current),
            TryGetDefaultDeviceName(out var deviceCount),
            deviceCount,
            TryDescribeCapability());
    }

    private AppSettings? TryLoadSettings()
    {
        try
        {
            // LastLoadFailed matters more than an exception here: the repository substitutes
            // defaults on a failed load, and reporting those as if they were the user's settings
            // would send support chasing a configuration the user never had.
            var loaded = settings.Load();
            return settings.LastLoadFailed ? null : loaded;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not read settings for the session banner.");
            return null;
        }
    }

    private string? TryGetDefaultDeviceName(out int? deviceCount)
    {
        deviceCount = null;
        try
        {
            var devices = audio.GetInputDevices();
            deviceCount = devices.Count;
            return devices.FirstOrDefault(d => d.IsDefault)?.Name;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not enumerate audio inputs for the session banner.");
            return null;
        }
    }

    private string? DescribeModel(AppSettings? current)
    {
        try
        {
            var configured = current?.TranscriptionModelId ?? TranscriptionModelCatalog.DefaultId;
            var model = TranscriptionModelCatalog.Resolve(configured);
            var set = models.Resolve();

            // "Which model is selected" and "is that model actually on disk" are different
            // questions, and a mismatch between them is a whole class of first-run failure.
            var missing = set.AsrComplete ? "none" : string.Join(",", set.MissingAsrFiles());
            return $"id={model.Id} name='{model.DisplayName}' dir={set.Directory} " +
                $"complete={set.AsrComplete} missing={missing} vad={set.VadAvailable}";
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not resolve the transcription model for the session banner.");
            return null;
        }
    }

    private static string? TryDescribeCapability()
    {
        try
        {
            return ComputeCapabilityReport.Detect().Describe();
        }
        catch
        {
            return null;
        }
    }

    private static InstallChannel DetectChannel()
    {
        if (WindowsPackageIdentity.IsPackaged())
        {
            return InstallChannel.Packaged;
        }

        // Velopack installs sit under a versioned "current" folder next to an Update.exe. Probing
        // the layout avoids constructing an UpdateManager (which reaches for a source) just to
        // answer a question the banner needs before anything else has started.
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var parent = Directory.GetParent(baseDir.TrimEnd(Path.DirectorySeparatorChar))?.FullName;
            return parent is not null && File.Exists(Path.Combine(parent, "Update.exe"))
                ? InstallChannel.DirectDownload
                : InstallChannel.Development;
        }
        catch
        {
            return InstallChannel.Development;
        }
    }
}
