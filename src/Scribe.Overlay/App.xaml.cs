using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Scribe.Overlay.Ipc;
using Scribe.Overlay.Logging;

namespace Scribe.Overlay;

/// <summary>
/// WinUI 3 application host for the standalone recording pill. Normally launched by the Scribe (WPF)
/// engine with <c>--pipe &lt;name&gt;</c>: the window starts hidden and is driven entirely over a named
/// pipe. Without a pipe argument it falls back to a standalone dev mode driven by the
/// <c>SCRIBE_OVERLAY_STATE</c> env var, which is how Phase 0 transparency was proven. Either way the
/// full window lifecycle is captured in the shared Scribe log so we can prove the surface behaves.
/// </summary>
public partial class App : Application
{
    private OverlayWindow? _window;
    private OverlayIpcServer? _ipcServer;

    public App()
    {
        OverlayLog.Write("App.ctor enter (process start)");
        InitializeComponent();

        // Surface any unhandled XAML-thread exception into the log instead of dying silently —
        // a silent exit is exactly how the old overlay hid its failures.
        UnhandledException += (_, e) =>
        {
            OverlayLog.Error($"App.UnhandledException handled={e.Handled} msg={e.Message}", e.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            OverlayLog.Error("AppDomain.UnhandledException", e.ExceptionObject as Exception);
        };
        OverlayLog.Write("App.ctor exit");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        OverlayLog.Write("App.OnLaunched enter");
        try
        {
            _window = new OverlayWindow();
            OverlayLog.Write("App.OnLaunched window constructed");

            // Arm the parent watchdog before anything else: if the engine that spawned us dies in the
            // window before the pipe ever connects (pipe EOF can't fire pre-connection), this is what
            // still tells us to exit. The engine-side job object is the primary guard; this is the backup.
            var parentPid = ResolveParentPid();
            if (parentPid > 0)
            {
                StartParentWatchdog(parentPid);
            }

            var pipeName = ResolvePipeName();
            if (!string.IsNullOrWhiteSpace(pipeName))
            {
                StartIpc(pipeName!);
            }
            else
            {
                StartStandalone();
            }
        }
        catch (Exception ex)
        {
            OverlayLog.Error("App.OnLaunched failed", ex);
            throw;
        }
    }

    /// <summary>IPC mode: window stays hidden; the engine drives every state over the pipe.</summary>
    private void StartIpc(string pipeName)
    {
        _ipcServer = new OverlayIpcServer(pipeName, _window!, OnPipeDisconnected);
        _ipcServer.Start();
        OverlayLog.Write($"App.OnLaunched exit (IPC mode, pipe='{pipeName}', window hidden)");
    }

    /// <summary>Standalone dev mode: pick an initial state from SCRIBE_OVERLAY_STATE (Listening default).</summary>
    private void StartStandalone()
    {
        var initial = OverlayState.Listening;
        var requested = Environment.GetEnvironmentVariable("SCRIBE_OVERLAY_STATE");
        if (!string.IsNullOrWhiteSpace(requested) && Enum.TryParse<OverlayState>(requested, true, out var parsed))
        {
            initial = parsed;
        }

        _window!.ShowState(initial);
        OverlayLog.Write($"App.OnLaunched exit (standalone, initial state={initial})");
    }

    /// <summary>The engine closed the pipe (it exited or crashed) — exit so we never orphan ourselves.</summary>
    private void OnPipeDisconnected()
    {
        OverlayLog.Write("App.OnPipeDisconnected — exiting overlay process");
        var queue = _window?.DispatcherQueue;
        if (queue is not null)
        {
            queue.TryEnqueue(Exit);
        }
        else
        {
            Exit();
        }
    }

    /// <summary>Reads <c>--pipe &lt;name&gt;</c> from the process command line, if present.</summary>
    private static string? ResolvePipeName()
    {
        var argv = Environment.GetCommandLineArgs();
        for (var i = 1; i < argv.Length - 1; i++)
        {
            if (string.Equals(argv[i], "--pipe", StringComparison.OrdinalIgnoreCase))
            {
                return argv[i + 1];
            }
        }

        return null;
    }

    /// <summary>Reads <c>--parent &lt;pid&gt;</c> from the process command line; 0 if absent/unparseable.</summary>
    private static int ResolveParentPid()
    {
        var argv = Environment.GetCommandLineArgs();
        for (var i = 1; i < argv.Length - 1; i++)
        {
            if (string.Equals(argv[i], "--parent", StringComparison.OrdinalIgnoreCase))
            {
                return int.TryParse(argv[i + 1], out var pid) ? pid : 0;
            }
        }

        return 0;
    }

    /// <summary>
    /// Backup orphan guard: wait on the engine process handle and hard-exit when it dies. The handle is
    /// bound to the specific process instance at startup, so it is immune to later PID reuse. If we cannot
    /// attach (parent already gone, or access denied), we either exit immediately or fall back to the
    /// engine-side job object and pipe EOF.
    /// </summary>
    private static void StartParentWatchdog(int parentPid)
    {
        System.Diagnostics.Process parent;
        try
        {
            parent = System.Diagnostics.Process.GetProcessById(parentPid);
        }
        catch (ArgumentException)
        {
            // Parent already exited before we even looked — nothing to drive this overlay; exit now.
            OverlayLog.Write($"Parent pid={parentPid} not found at startup — overlay self-terminating");
            Environment.Exit(0);
            return;
        }
        catch (Exception ex)
        {
            OverlayLog.Warn($"Parent watchdog setup failed for pid={parentPid}: {ex.Message}");
            return;
        }

        var watcher = new System.Threading.Thread(() =>
        {
            try
            {
                parent.WaitForExit();
            }
            catch (Exception ex)
            {
                // Could not synchronize on the parent; the job object + pipe EOF remain as guards.
                OverlayLog.Warn($"Parent watchdog wait failed for pid={parentPid}: {ex.Message}");
                return;
            }

            OverlayLog.Write($"Parent process {parentPid} exited — overlay self-terminating");
            Environment.Exit(0);
        })
        {
            IsBackground = true,
            Name = "ScribeOverlayParentWatch",
        };
        watcher.Start();
        OverlayLog.Write($"Parent watchdog armed for pid={parentPid}");
    }
}
