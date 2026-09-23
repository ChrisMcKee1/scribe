using System.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace Scribe.Core.Infrastructure;

public sealed record StartupRegistrationStatus(StartupTaskState? State, string? Error = null)
{
    public bool IsKnown => State.HasValue;
    public bool IsEnabled => State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
    public bool CanChange => State is StartupTaskState.Enabled or StartupTaskState.Disabled;

    // Neutral about the app's name: the Store build is listed in Windows as "Scribe AI" and the
    // direct download as "Scribe", and both appear on the same Startup page.
    public string Message => Error ?? State switch
    {
        StartupTaskState.DisabledByUser =>
            "Turned off in Windows. To start Scribe with Windows, turn it on in Windows Settings > Apps > Startup.",
        StartupTaskState.DisabledByPolicy =>
            "Startup is disabled by your organization. Contact your administrator to change it.",
        StartupTaskState.EnabledByPolicy =>
            "Startup is enabled by your organization and cannot be changed here.",
        null => "Could not read the Windows startup setting. Reopen Settings to try again.",
        _ => "Launch Scribe automatically when you sign in.",
    };

    public bool Matches(bool enabled) => IsKnown && Error is null && IsEnabled == enabled;
}

/// <summary>
/// Uses the package startup task for MSIX installs and the per-user Run key for unpackaged apps.
/// A choice the user made in Windows itself, or one an administrator enforces, always wins over
/// the saved preference.
/// </summary>
public sealed class StartupRegistration
{
    // Must match the windows.startupTask declaration in build/pack-msix.ps1.
    public const string TaskId = "ScribeStartup";

    /// <summary>Shown beside the Settings switch when <see cref="IsIsolated"/> is true.</summary>
    public const string IsolatedDataNote =
        "This instance uses an isolated data folder, so it does not change Start with Windows at launch. " +
        "Changing the switch here still changes it for Scribe on this PC.";

    private readonly IStartupRegistrationBackend _backend;
    private readonly ILogger<StartupRegistration> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public StartupRegistration(ILogger<StartupRegistration> log, AppPaths paths)
        : this(
            CreateBackend(WindowsPackageIdentity.IsPackaged(), log),
            log,
            (paths ?? throw new ArgumentNullException(nameof(paths))).IsIsolatedRoot)
    {
    }

    internal StartupRegistration(
        IStartupRegistrationBackend backend,
        ILogger<StartupRegistration> log,
        bool isolatedDataRoot = false)
    {
        _backend = backend;
        _log = log;
        IsIsolated = isolatedDataRoot;
    }

    public bool IsPackaged => _backend.IsPackaged;

    /// <summary>
    /// True when this process runs on an isolated data folder (<c>SCRIBE_DATA_DIR</c>).
    /// </summary>
    /// <remarks>
    /// Such an instance never reconciles at launch. Its saved preference belongs to a scratch
    /// profile, while the Run entry or package task belongs to the installed app: a direct-download
    /// build would otherwise delete the installed app's Run entry, or register a build that starts
    /// without its data folder. An explicit change from the Settings switch still applies.
    /// </remarks>
    public bool IsIsolated { get; }

    internal static IStartupRegistrationBackend CreateBackend(bool isPackaged, ILogger? log = null) =>
        isPackaged ? new PackagedStartupBackend() : new RunKeyStartupBackend(log);

    public Task<StartupRegistrationStatus> GetStatusAsync() => ExecuteAsync(null, synchronize: false);

    public Task<StartupRegistrationStatus> SetEnabledAsync(bool enabled) =>
        ExecuteAsync(enabled, synchronize: false);

    public Task<StartupRegistrationStatus> SyncAsync(bool enabled)
    {
        if (IsIsolated)
        {
            _log.LogInformation(
                "Startup registration not reconciled: this instance uses an isolated data folder.");
            return GetStatusAsync();
        }

        return ExecuteAsync(enabled, synchronize: true);
    }

    /// <summary>
    /// Decides what, if anything, to change in Windows for a request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Package startup task: the manifest declares it disabled, so a fresh install stays opt-in and
    /// an existing opt-in is migrated by enabling it at startup. Beyond that, startup never touches
    /// it, because Task Manager and Settings own the task once the user has used them.
    /// </para>
    /// <para>
    /// Run key: startup reconciles the key with the saved preference, which is how a moved install
    /// heals its path and a stale entry from an earlier install is cleared. The exception is an
    /// entry Windows records as turned off by the user: nothing written to the Run key changes that,
    /// and it is the user's decision, so it is never re-enabled here. Removing such an entry is still
    /// allowed, because that keeps Scribe off, which is what both sides asked for.
    /// </para>
    /// </remarks>
    internal static StartupChange Plan(bool isPackaged, bool synchronize, bool? requested, StartupTaskState state)
    {
        if (requested is not { } enable)
        {
            return StartupChange.None;
        }

        if (isPackaged)
        {
            if (enable)
            {
                return state == StartupTaskState.Disabled ? StartupChange.Enable : StartupChange.None;
            }

            return !synchronize && state == StartupTaskState.Enabled ? StartupChange.Disable : StartupChange.None;
        }

        if (enable)
        {
            return state is StartupTaskState.Disabled or StartupTaskState.Enabled
                ? StartupChange.Enable
                : StartupChange.None;
        }

        return state is StartupTaskState.Enabled or StartupTaskState.DisabledByUser
            ? StartupChange.Disable
            : StartupChange.None;
    }

    private async Task<StartupRegistrationStatus> ExecuteAsync(bool? requested, bool synchronize)
    {
        await _gate.WaitAsync();
        try
        {
            var before = await _backend.GetStateAsync();
            var change = Plan(IsPackaged, synchronize, requested, before);
            var state = before;
            if (change == StartupChange.Enable)
            {
                await _backend.EnableAsync();
                state = await _backend.GetStateAsync();
            }
            else if (change == StartupChange.Disable)
            {
                await _backend.DisableAsync();
                state = await _backend.GetStateAsync();
            }

            _log.Log(
                requested is null ? LogLevel.Debug : LogLevel.Information,
                "Startup registration: packaged={Packaged}, requested={Requested}, sync={Sync}, before={Before}, change={Change}, state={State}.",
                IsPackaged, requested, synchronize, before, change, state);

            var status = new StartupRegistrationStatus(state);

            // An explicit request always gets an answer, including "Windows' own setting blocks
            // this", so the user learns why nothing changed. A startup reconcile that deliberately
            // left Windows alone has nothing to report.
            if (requested is { } desired && (change != StartupChange.None || !synchronize) &&
                !status.Matches(desired))
            {
                _log.LogWarning("Windows did not accept the startup setting; state={State}.", state);
                return status with
                {
                    Error = status.CanChange
                        ? "Windows did not accept the Start with Windows change. Check Windows Settings > Apps > Startup."
                        : status.Message,
                };
            }

            return status;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Every failure crossing into WinRT or the registry becomes an unknown state instead of
            // escaping. An exception type outside a narrower filter used to leave the Settings
            // switch disabled on "Checking..." with nothing on screen to say why.
            _log.LogWarning(ex, "Could not access startup registration; packaged={Packaged}.", IsPackaged);
            return new StartupRegistrationStatus(null,
                "Could not access the Windows startup setting. Reopen Settings to try again. " +
                "If this continues, save diagnostics from About.");
        }
        finally
        {
            _gate.Release();
        }
    }
}

internal enum StartupChange
{
    None,
    Enable,
    Disable,
}

internal interface IStartupRegistrationBackend
{
    bool IsPackaged { get; }
    Task<StartupTaskState> GetStateAsync();
    Task EnableAsync();
    Task DisableAsync();
}

internal sealed class PackagedStartupBackend : IStartupRegistrationBackend
{
    public bool IsPackaged => true;

    public async Task<StartupTaskState> GetStateAsync() =>
        (await StartupTask.GetAsync(StartupRegistration.TaskId)).State;

    // A packaged desktop app gets no consent dialog from RequestEnableAsync, so this needs no
    // window, and a task the user disabled in Windows stays disabled.
    public async Task EnableAsync()
    {
        var task = await StartupTask.GetAsync(StartupRegistration.TaskId);
        await task.RequestEnableAsync();
    }

    public async Task DisableAsync()
    {
        var task = await StartupTask.GetAsync(StartupRegistration.TaskId);
        task.Disable();
    }
}

/// <summary>
/// Direct-download builds register through the per-user Run key. Windows keeps the user's Task
/// Manager or Settings choice for a Run entry separately (see <see cref="StartupApproval"/>), so
/// the Run value alone does not say whether Windows will launch it.
/// </summary>
internal sealed class RunKeyStartupBackend : IStartupRegistrationBackend
{
    internal const string ValueName = "Scribe";

    private readonly IRunKeyStore _runKey;
    private readonly IStartupApprovalReader _approval;
    private readonly Func<string?> _executablePath;
    private readonly ILogger? _log;

    public RunKeyStartupBackend(ILogger? log = null)
        : this(new RegistryRunKeyStore(), new RegistryStartupApprovalReader(), () => Environment.ProcessPath, log)
    {
    }

    internal RunKeyStartupBackend(
        IRunKeyStore runKey,
        IStartupApprovalReader approval,
        Func<string?> executablePath,
        ILogger? log = null)
    {
        _runKey = runKey;
        _approval = approval;
        _executablePath = executablePath;
        _log = log;
    }

    public bool IsPackaged => false;

    public Task<StartupTaskState> GetStateAsync()
    {
        if (string.IsNullOrWhiteSpace(_runKey.Read()))
        {
            // Windows lists no entry without a Run value, so a leftover "off" record cannot apply
            // yet. Reporting it would leave the user no way to put Scribe back on Windows' own list.
            return Task.FromResult(StartupTaskState.Disabled);
        }

        var approval = _approval.Read(ValueName);
        if (approval.Kind is StartupApprovalKind.Unrecognized or StartupApprovalKind.Unreadable)
        {
            _log?.LogDebug(
                "Startup approval record not understood (kind={Kind}, marker={Marker}, length={Length}); treating the Run entry as enabled.",
                approval.Kind, approval.Marker, approval.Length);
        }
        else if (approval.Kind is (StartupApprovalKind.Enabled or StartupApprovalKind.Disabled) &&
            !approval.IsReportedMarker)
        {
            _log?.LogDebug(
                "Startup approval record has an unusual status byte (marker={Marker}, length={Length}); reading its low bit as {Kind}.",
                approval.Marker, approval.Length, approval.Kind);
        }

        return Task.FromResult(approval.Kind == StartupApprovalKind.Disabled
            ? StartupTaskState.DisabledByUser
            : StartupTaskState.Enabled);
    }

    public Task EnableAsync()
    {
        var exe = _executablePath();
        if (string.IsNullOrWhiteSpace(exe))
        {
            throw new InvalidOperationException("The running executable path is unavailable.");
        }

        // Quoted so an install path with spaces still launches.
        _runKey.Write($"\"{exe}\"");
        return Task.CompletedTask;
    }

    public Task DisableAsync()
    {
        _runKey.Delete();
        return Task.CompletedTask;
    }
}

internal interface IRunKeyStore
{
    string? Read();
    void Write(string command);
    void Delete();
}

internal sealed class RegistryRunKeyStore : IRunKeyStore
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(RunKeyStartupBackend.ValueName) as string;
    }

    public void Write(string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        key.SetValue(RunKeyStartupBackend.ValueName, command);
    }

    public void Delete()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(RunKeyStartupBackend.ValueName, throwOnMissingValue: false);
    }
}

internal enum StartupApprovalKind
{
    NotRecorded,
    Enabled,
    Disabled,
    Unrecognized,
    Unreadable,
}

/// <summary>
/// Windows' own record of whether the user allows one Run key entry to start. Read only.
/// </summary>
/// <remarks>
/// <para>
/// Not documented by Microsoft; the layout below is community-reported only. Task Manager and
/// Settings > Apps > Startup store the user's choice for a Run entry as a binary value named like
/// the Run value, under <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run</c>,
/// and leave the Run key alone. The value is usually 12 bytes, a status byte followed by padding
/// and a timestamp of the change. The status byte has been reported as 0x02 or 0x06 for on and
/// 0x03 or 0x07 for off, which is to say its low bit means "turned off by the user", so the low bit
/// is what is read here. An empty or non-binary value is not understood and keeps the earlier
/// behavior of trusting the Run value.
/// </para>
/// <para>
/// Scribe never writes this key: it holds the user's own decision in Windows' UI, and the platform
/// applies the same rule to packaged startup tasks, where a user disable cannot be re-enabled
/// programmatically.
/// </para>
/// </remarks>
internal readonly record struct StartupApproval(StartupApprovalKind Kind, int Marker = -1, int Length = 0)
{
    private const byte TurnedOffByUserBit = 0x01;

    /// <summary>False for a status byte outside the reported 0x02, 0x03, 0x06 and 0x07.</summary>
    public bool IsReportedMarker => Marker is 0x02 or 0x03 or 0x06 or 0x07;

    public static StartupApproval Parse(object? value) => value switch
    {
        null => new(StartupApprovalKind.NotRecorded),
        byte[] { Length: 0 } => new(StartupApprovalKind.Unrecognized, -1, 0),
        byte[] bytes => new(
            (bytes[0] & TurnedOffByUserBit) != 0 ? StartupApprovalKind.Disabled : StartupApprovalKind.Enabled,
            bytes[0],
            bytes.Length),
        _ => new(StartupApprovalKind.Unrecognized),
    };
}

internal interface IStartupApprovalReader
{
    StartupApproval Read(string valueName);
}

internal sealed class RegistryStartupApprovalReader : IStartupApprovalReader
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public StartupApproval Read(string valueName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
            return StartupApproval.Parse(key?.GetValue(valueName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // An auxiliary signal: failing to read it must not break registration through the Run key.
            return new StartupApproval(StartupApprovalKind.Unreadable);
        }
    }
}
