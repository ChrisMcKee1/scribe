using System.Runtime.InteropServices;
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

    public string Message => Error ?? State switch
    {
        StartupTaskState.DisabledByUser =>
            "Disabled in Windows. Turn on Scribe AI in Windows Settings > Apps > Startup.",
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
/// For packaged apps, Windows user and policy overrides win over the saved preference.
/// </summary>
public sealed class StartupRegistration
{
    // Must match the windows.startupTask declaration in build/pack-msix.ps1.
    public const string TaskId = "ScribeStartup";

    private readonly IStartupRegistrationBackend _backend;
    private readonly ILogger<StartupRegistration> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public StartupRegistration(ILogger<StartupRegistration> log)
        : this(CreateBackend(WindowsPackageIdentity.IsPackaged()), log)
    {
    }

    internal StartupRegistration(IStartupRegistrationBackend backend, ILogger<StartupRegistration> log)
    {
        _backend = backend;
        _log = log;
    }

    public bool IsPackaged => _backend.IsPackaged;

    internal static IStartupRegistrationBackend CreateBackend(bool isPackaged) =>
        isPackaged ? new PackagedStartupBackend() : new RunKeyStartupBackend();

    public Task<StartupRegistrationStatus> GetStatusAsync() => ExecuteAsync(null, synchronize: false);

    public Task<StartupRegistrationStatus> SetEnabledAsync(bool enabled) =>
        ExecuteAsync(enabled, synchronize: false);

    public Task<StartupRegistrationStatus> SyncAsync(bool enabled) =>
        ExecuteAsync(enabled, synchronize: true);

    private async Task<StartupRegistrationStatus> ExecuteAsync(bool? enabled, bool synchronize)
    {
        await _gate.WaitAsync();
        try
        {
            var state = await _backend.GetStateAsync();

            // A newly declared package task starts Disabled. Migrate an existing opt-in on first
            // launch, but do not undo changes made in Windows Settings or by an administrator.
            var applyPreference = !synchronize || !IsPackaged ||
                (enabled == true && state == StartupTaskState.Disabled);
            if (enabled is { } requested && applyPreference)
            {
                if (requested && (state == StartupTaskState.Disabled ||
                    (!IsPackaged && state == StartupTaskState.Enabled)))
                {
                    await _backend.EnableAsync();
                    state = await _backend.GetStateAsync();
                }
                else if (!requested && state == StartupTaskState.Enabled)
                {
                    await _backend.DisableAsync();
                    state = await _backend.GetStateAsync();
                }
            }

            _log.LogInformation(
                "Startup registration: packaged={Packaged}, requested={Requested}, state={State}.",
                IsPackaged, enabled, state);

            var status = new StartupRegistrationStatus(state);
            if (enabled is { } desired && applyPreference && !status.Matches(desired))
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
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException
            or SecurityException or InvalidOperationException or ArgumentException)
        {
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

internal sealed class RunKeyStartupBackend : IStartupRegistrationBackend
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Scribe";

    public bool IsPackaged => false;

    public Task<StartupTaskState> GetStateAsync()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var enabled = key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
        return Task.FromResult(enabled ? StartupTaskState.Enabled : StartupTaskState.Disabled);
    }

    public Task EnableAsync()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
        {
            throw new InvalidOperationException("The running executable path is unavailable.");
        }

        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        key.SetValue(ValueName, $"\"{exe}\"");
        return Task.CompletedTask;
    }

    public Task DisableAsync()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
        return Task.CompletedTask;
    }
}
