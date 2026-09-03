using System.Globalization;
using Scribe.Core.Infrastructure;
using Scribe.Core.Models;

namespace Scribe.Core.Diagnostics;

/// <summary>
/// Identifies one run of the app. A tray process lives for days and its daily log rolls at
/// midnight, so the file a user hands over often contains no trace of how that session started.
/// Every session gets a short id that is repeated on the banner and stamped on each dictation, so
/// a log covering three restarts can still be read as three separate stories.
/// </summary>
/// <param name="Id">Short, human-quotable id (e.g. <c>a3f19c</c>).</param>
/// <param name="ProcessId">OS process id, to line the log up against Task Manager or a dump.</param>
/// <param name="StartedLocal">Local wall-clock start, with offset so timestamps can be compared.</param>
public sealed record SessionIdentity(string Id, int ProcessId, DateTimeOffset StartedLocal)
{
    /// <summary>Creates an identity for the current process.</summary>
    public static SessionIdentity ForCurrentProcess() => new(
        Guid.NewGuid().ToString("N")[..6],
        Environment.ProcessId,
        DateTimeOffset.Now);
}

/// <summary>
/// How this copy of Scribe was installed. It decides which update path runs, and it determines
/// whether Windows redirects the app's writes, which decides where the user's own log folder
/// physically is. A packaged build's data may sit in the package container rather than at the
/// %LOCALAPPDATA% path the app addresses, so this belongs at the top of every log: without it,
/// "look in ScribeData\logs" is advice that sends a Store user to an empty folder.
/// </summary>
public enum InstallChannel
{
    /// <summary>Run from a build output; no installer involved.</summary>
    Development,

    /// <summary>Velopack installer from GitHub Releases.</summary>
    DirectDownload,

    /// <summary>MSIX with Windows package identity (Microsoft Store or a sideload).</summary>
    Packaged,
}

/// <summary>
/// Composes the block of lines written once at the top of every session.
/// <para>
/// This exists because of a real support dead end: a user on 0.3.10 reported that dictation cut
/// out after a few seconds, and there was no way to get anywhere. Their log folder did not exist
/// where the app said it did, and even a log would not have said which build, which install
/// channel, which microphone, which model, or which settings were in play, because none of that
/// was ever written down. Everything here is chosen to answer a first support question without a
/// round trip.
/// </para>
/// <para>
/// <b>Nothing user-authored goes in.</b> No transcripts, no dictionary entries, no snippet bodies,
/// no API keys, no prompts. Settings appear as shapes and flags: a count, an enum name, a
/// "configured"/"not configured". PRIVACY.md is the contract and this class is where it is easiest
/// to break it, so add fields with that in mind.
/// </para>
/// </summary>
public static class SessionBanner
{
    /// <summary>Marks the start of a session so a reader can find it with one search.</summary>
    public const string StartMarker = "===== Scribe session start =====";

    /// <summary>
    /// Builds the banner. Every argument is optional so a caller can log what it has: a probe that
    /// failed (no audio devices, no model) must still leave a banner behind, because "the thing we
    /// could not detect" is usually the answer.
    /// </summary>
    public static IReadOnlyList<string> Compose(
        SessionIdentity session,
        string version,
        InstallChannel channel,
        AppPaths paths,
        AppSettings? settings = null,
        string? packageFamilyName = null,
        string? modelDescription = null,
        string? defaultAudioDevice = null,
        int? audioDeviceCount = null,
        string? computeCapability = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(paths);

        var lines = new List<string>
        {
            StartMarker,
            $"session={session.Id} pid={session.ProcessId} started={session.StartedLocal:yyyy-MM-dd HH:mm:ss zzz}",
            $"build: version={version} channel={channel} package={packageFamilyName ?? "none"}",
            "host: " + DescribeHost(),
        };

        if (computeCapability is { Length: > 0 })
        {
            lines.Add("compute: " + computeCapability);
        }

        lines.Add("paths: " + DescribePaths(paths));

        if (paths.WritesAreVirtualized)
        {
            // The single most useful line in the file when somebody says "that folder isn't there".
            lines.Add($"paths: writes are redirected by Windows; on disk these files are at {paths.EffectiveRootDir}");
        }
        else if (paths.VirtualizedRootDir is { } virtualized && Directory.Exists(virtualized))
        {
            // Not redirected now, but an earlier build of this install was, and its logs are still
            // in the package folder. Saying so saves the exact support round trip that prompted
            // this whole area of work.
            lines.Add($"paths: an older packaged build stored data at {virtualized}");
        }

        lines.Add("audio: " + DescribeAudio(settings, defaultAudioDevice, audioDeviceCount));
        lines.Add("model: " + (modelDescription ?? settings?.TranscriptionModelId ?? "unknown"));

        if (settings is not null)
        {
            lines.Add("hotkeys: " + DescribeHotkeys(settings));
            lines.Add("pipeline: " + DescribePipeline(settings));
            lines.Add("cleanup: " + DescribeCleanup(settings));
            lines.Add("injection: " + DescribeInjection(settings));
        }
        else
        {
            lines.Add("settings: unavailable (the settings store did not load)");
        }

        lines.Add($"logs: {LogRetentionPolicy.DefaultRetentionDays} day retention, " +
            $"{LogRetentionPolicy.DefaultDailyBudgetBytes / (1024 * 1024)} MB per day, " +
            $"{LogRetentionPolicy.DefaultTotalBudgetBytes / (1024 * 1024)} MB total");

        return lines;
    }

    private static string DescribeHost()
    {
        // Total RAM is not exposed by Environment; the GC's memory info reports what the process
        // sees, which is the number that matters for a model that fails to load.
        var totalRamGb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024 * 1024);
        return string.Create(CultureInfo.InvariantCulture,
            $"os={Environment.OSVersion.VersionString} osArch={System.Runtime.InteropServices.RuntimeInformation.OSArchitecture} " +
            $"procArch={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture} " +
            $"runtime={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription} " +
            $"cores={Environment.ProcessorCount} ram={totalRamGb:F1}GB " +
            $"elevated={IsElevated()} session={Environment.UserInteractive}");
    }

    private static string IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)
                .ToString();
        }
        catch
        {
            return "unknown";
        }
    }

    private static string DescribePaths(AppPaths paths) =>
        $"root={paths.EffectiveRootDir} logs={paths.EffectiveLogsDir} db={paths.EffectiveDatabasePath} " +
        $"virtualized={paths.WritesAreVirtualized} fallbackRoot={paths.IsFallbackRoot} " +
        $"dbSize={DescribeFileSize(paths.DatabasePath)}";

    private static string DescribeFileSize(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists
                ? string.Create(CultureInfo.InvariantCulture, $"{info.Length / (1024.0 * 1024):F1}MB")
                : "absent";
        }
        catch
        {
            return "unreadable";
        }
    }

    private static string DescribeAudio(AppSettings? settings, string? defaultDevice, int? deviceCount)
    {
        var selected = settings?.InputDeviceId is null
            ? "system default"
            : settings.InputDeviceName ?? "a saved device";
        return $"selected='{selected}' default='{defaultDevice ?? "unknown"}' inputs={deviceCount?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}";
    }

    private static string DescribeHotkeys(AppSettings settings)
    {
        var primary = Describe(settings.Hotkey);
        var secondary = settings.DictationOnlyHotkey is { } second
            ? Describe(second)
            : "none";
        return $"primary={primary} dictationOnly={secondary} autoStopOnSilence={settings.AutoStopOnSilence}";

        static string Describe(HotkeyBinding binding) =>
            $"'{binding.DisplayName ?? "custom"}'(vk=0x{binding.VirtualKey:X2} mods={binding.Modifiers} " +
            $"mode={binding.Mode} suppress={binding.Suppress} chord={binding.IsPhysicalChord})";
    }

    private static string DescribePipeline(AppSettings settings) =>
        $"vad={settings.UseVoiceActivityDetection} postProcess={settings.ApplyPostProcessing} " +
        $"decodeThreads={settings.DecodeThreads} libraries={settings.EnabledDictionaryLibraryIds.Count} " +
        $"profiles={settings.Profiles.Count} overlay={settings.ShowOverlay}@{settings.OverlayPosition} " +
        $"storeAudio={settings.StoreAudioHistory} launchOnLogin={settings.LaunchOnLogin}";

    private static string DescribeCleanup(AppSettings settings)
    {
        if (!settings.EnableAiCleanup)
        {
            return "off";
        }

        // Model and provider names are product identifiers, not user content. Endpoints and keys
        // are reported as presence only: an endpoint can carry a tenant or a resource name a user
        // would not expect to hand out with a log file.
        var target = settings.AiCleanupProvider switch
        {
            Cleanup.CleanupProvider.FoundryLocal => $"model={settings.AiCleanupModel}",
            Cleanup.CleanupProvider.AzureFoundry =>
                $"deployment={settings.AiCleanupAzureDeployment ?? "unset"} " +
                $"endpoint={Presence(settings.AiCleanupAzureEndpoint)} auth={settings.AiCleanupAzureAuthMode}",
            /*
             * Copilot needs its own arm rather than falling into the custom-endpoint one.
             *
             * That arm reported `model=unset endpoint=unset` for every Copilot session: it reads
             * AiCleanupCustomModel, which this provider never sets, and prints an endpoint field that
             * is meaningless for a provider with no endpoint. The banner exists because a log handed
             * over after the fact often has no other record of how the process started, and this is
             * the one provider whose model is free text, so it is the worst one to lose.
             */
            Cleanup.CleanupProvider.GitHubCopilot =>
                $"model={settings.AiCleanupCopilotModel ?? "account default"}",
            _ => $"model={settings.AiCleanupCustomModel ?? "unset"} endpoint={Presence(settings.AiCleanupCustomEndpoint)}",
        };

        return $"on provider={settings.AiCleanupProvider} {target} promptStyle={settings.AiCleanupPromptStyle} " +
            $"customPrompt={Presence(settings.AiCleanupLocalPrompt, settings.AiCleanupFrontierPrompt)} " +
            $"writingStyle={Presence(settings.AiCleanupWritingStyle)}";

        static string Presence(params string?[] values) =>
            values.Any(v => !string.IsNullOrWhiteSpace(v)) ? "configured" : "unset";
    }

    private static string DescribeInjection(AppSettings settings) =>
        $"method={settings.InjectionMethod} newlines={settings.NewlineHandling} " +
        $"shiftEnter={settings.ShiftEnterLineBreaks}";
}
