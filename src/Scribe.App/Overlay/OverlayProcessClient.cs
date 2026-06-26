using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Scribe.Core.Audio;

namespace Scribe.App.Overlay;

/// <summary>
/// Drives the out-of-process WinUI 3 recording pill. The pill renders in a separate kept-warm
/// process (<c>Scribe.Overlay.exe</c>) over a one-way named pipe, so the transparent surface is
/// produced by DWM composition rather than the WPF <c>AllowsTransparency</c>/layered-window path that
/// caused the recurring black box.
///
/// All pipe/process work is serialised onto a single background thread fed by a command queue: public
/// methods only enqueue, so the UI thread never blocks on process launch or pipe I/O, and commands are
/// delivered in order. The live input-level meter is smoothed here (matching the old WPF curve) and
/// throttled before being sent as integer <c>METER</c> commands to avoid flooding the pipe.
/// </summary>
public sealed class OverlayProcessClient : IOverlayController, IDisposable
{
    private const int ConnectTimeoutMs = 8000;
    private const long MeterIntervalMs = 25; // ~40 meter updates/sec is plenty for a VU bar

#if DEBUG
    private const string BuildConfig = "Debug";
#else
    private const string BuildConfig = "Release";
#endif

    private readonly IAudioCaptureService _audio;
    private readonly ILogger<OverlayProcessClient>? _log;
    private readonly BlockingCollection<Command> _queue = new();
    private readonly Thread _consumer;

    private Process? _process;
    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;
    private string? _exePath;
    private bool _loggedMissing;

    private bool _subscribed;
    private float _meterLevel;
    private long _lastMeterMs;
    private bool _closed;

    public OverlayProcessClient(IAudioCaptureService audio, ILogger<OverlayProcessClient>? log = null)
    {
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _log = log;
        _consumer = new Thread(Consume) { IsBackground = true, Name = "ScribeOverlayIpc" };
        _consumer.Start();
    }

    public void Warmup() => Enqueue("WARMUP", ensureAlive: true);

    public void ShowRecording()
    {
        Subscribe();
        _meterLevel = 0f;
        Enqueue("RECORDING", ensureAlive: true);
    }

    public void ShowProcessing(bool polishing)
    {
        Unsubscribe();
        Enqueue($"PROCESSING {(polishing ? 1 : 0)}", ensureAlive: false);
    }

    public void ShowFailed(string? reason)
    {
        Unsubscribe();
        var clean = (reason ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        Enqueue($"FAILED {clean}", ensureAlive: false);
    }

    public void HideOverlay()
    {
        Unsubscribe();
        Enqueue("HIDE", ensureAlive: false);
    }

    public void CloseOverlay()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        Unsubscribe();

        try
        {
            _queue.Add(Command.Exit);
            _queue.CompleteAdding();
        }
        catch (InvalidOperationException)
        {
            // queue already completed
        }

        _consumer.Join(TimeSpan.FromSeconds(3));
        KillProcess();
    }

    public void Dispose() => CloseOverlay();

    // ---- Command queue plumbing -----------------------------------------------------------------

    private void Enqueue(string text, bool ensureAlive)
    {
        if (_queue.IsAddingCompleted)
        {
            return;
        }

        try
        {
            _queue.Add(new Command(text, ensureAlive, false));
        }
        catch (InvalidOperationException)
        {
            // queue completed between the guard and the add — overlay is shutting down
        }
    }

    private void Consume()
    {
        foreach (var item in _queue.GetConsumingEnumerable())
        {
            try
            {
                if (item.IsExit)
                {
                    if (IsAlive)
                    {
                        TryWrite("EXIT");
                    }

                    KillProcess();
                    break;
                }

                if (item.EnsureAlive)
                {
                    EnsureLaunched();
                }

                if (!IsAlive)
                {
                    continue; // drop non-essential commands when the overlay isn't running
                }

                _writer!.WriteLine(item.Text);
            }
            catch (Exception ex)
            {
                TryLog(LogLevel.Warning, ex, "Overlay command '{Cmd}' failed; tearing down for relaunch.", item.Text);
                KillProcess();
            }
        }
    }

    private bool IsAlive =>
        _process is { HasExited: false } && _pipe is { IsConnected: true } && _writer is not null;

    private void EnsureLaunched()
    {
        if (IsAlive)
        {
            return;
        }

        // Clean up any half-dead state (crashed process, broken pipe) before relaunching.
        KillProcess();

        // A previously-resolved path can vanish if a dev rebuild moves the overlay's output — drop it
        // so we re-resolve rather than relaunch-looping against a dead path.
        if (_exePath is not null && !File.Exists(_exePath))
        {
            _log?.LogWarning("Cached overlay exe path no longer exists; re-resolving: {Exe}", _exePath);
            _exePath = null;
        }

        _exePath ??= ResolveOverlayExe();
        if (_exePath is null)
        {
            return;
        }

        var pipeName = "Scribe.Overlay." + Guid.NewGuid().ToString("N");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _exePath,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--pipe");
            psi.ArgumentList.Add(pipeName);
            // The overlay watches this pid and self-exits if we die before the pipe ever connects.
            psi.ArgumentList.Add("--parent");
            psi.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            _process = Process.Start(psi);

            // OS-level safety net: tie the overlay's lifetime to ours so it can never orphan, even if
            // we are force-killed in the window before the pipe connects (pipe EOF only fires once a
            // connection exists). KILL_ON_JOB_CLOSE fires when our process handle table is torn down.
            if (_process is not null)
            {
                OverlayChildJob.TryAssign(_process, _log);
            }

            _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            _pipe.Connect(ConnectTimeoutMs);
            _writer = new StreamWriter(_pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        }
        catch (Exception ex)
        {
            // Only genuine launch/connect I/O is inside the try — and logging here is non-throwing — so
            // this catch reliably means the overlay really failed to start.
            TryLog(LogLevel.Error, ex, "Failed to launch/connect the overlay process at {Exe}.", _exePath);
            if (_exePath is not null && !File.Exists(_exePath))
            {
                _exePath = null; // the exe vanished mid-flight — force re-resolution next time
            }

            KillProcess();
            return;
        }

        // Logged only after a confirmed-good launch, and via a non-throwing helper. Previously this sat
        // INSIDE the try above, so a transient log-file lock threw here, was caught as a "launch
        // failure", and KillProcess() tore down a perfectly healthy overlay — a root cause of the
        // intermittent "pill disappears" regressions.
        TryLog(
            LogLevel.Information, null,
            "Overlay process launched pid={Pid} pipe={Pipe} exe={Exe}",
            _process?.Id, pipeName, _exePath);
    }

    // Non-throwing logging for the overlay-launch path. A diagnostics failure (e.g. a transient
    // shared-log-file lock) must never surface as an exception here, because nearby catch blocks treat
    // any throw as an overlay failure and respond destructively with KillProcess().
    private void TryLog(LogLevel level, Exception? ex, string message, params object?[] args)
    {
        try
        {
            _log?.Log(level, ex, message, args);
        }
        catch
        {
            // best-effort
        }
    }

    private void TryWrite(string text)
    {
        try
        {
            _writer?.WriteLine(text);
        }
        catch (Exception ex)
        {
            _log?.LogDebug(ex, "Overlay EXIT write failed (process likely already gone).");
        }
    }

    private void KillProcess()
    {
        try
        {
            _writer?.Dispose();
        }
        catch
        {
            // ignore
        }

        try
        {
            _pipe?.Dispose();
        }
        catch
        {
            // ignore
        }

        _writer = null;
        _pipe = null;

        try
        {
            if (_process is { HasExited: false } proc)
            {
                if (!proc.WaitForExit(1000))
                {
                    proc.Kill(entireProcessTree: true);
                }
            }
        }
        catch
        {
            // best-effort teardown
        }
        finally
        {
            try
            {
                _process?.Dispose();
            }
            catch
            {
                // ignore
            }

            _process = null;
        }
    }

    // ---- Live input-level meter -----------------------------------------------------------------

    private void Subscribe()
    {
        if (_subscribed)
        {
            return;
        }

        _audio.LevelChanged += OnLevelChanged;
        _subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!_subscribed)
        {
            return;
        }

        _audio.LevelChanged -= OnLevelChanged;
        _subscribed = false;
    }

    private void OnLevelChanged(object? sender, float level)
    {
        // Perceptual curve + fast-attack / gentle-decay smoothing, matching the old WPF overlay so the
        // bar reads like a VU meter. Smoothing updates every sample; only the send is throttled.
        var v = level <= 0f ? 0f : MathF.Sqrt(Math.Min(1f, level));
        _meterLevel = v > _meterLevel ? v : (_meterLevel * 0.82f) + (v * 0.18f);

        var now = Environment.TickCount64;
        if (now - _lastMeterMs < MeterIntervalMs)
        {
            return;
        }

        _lastMeterMs = now;
        var scaled = (int)Math.Round(Math.Clamp(_meterLevel, 0f, 1f) * 1000f);
        Enqueue("METER " + scaled.ToString(CultureInfo.InvariantCulture), ensureAlive: false);
    }

    // ---- Overlay executable resolution ----------------------------------------------------------

    private string? ResolveOverlayExe()
    {
        // (1) Explicit override — handy for testing a specific overlay build.
        var env = Environment.GetEnvironmentVariable("SCRIBE_OVERLAY_EXE");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            _log?.LogInformation("Overlay exe via SCRIBE_OVERLAY_EXE: {Path}", env);
            return env;
        }

        // (2) Installer layout: shipped self-contained next to the app under Overlay\.
        var installed = Path.Combine(AppContext.BaseDirectory, "Overlay", "Scribe.Overlay.exe");
        if (File.Exists(installed))
        {
            _log?.LogInformation("Overlay exe via installer layout: {Path}", installed);
            return installed;
        }

        // (3) Dev fallback: walk up to the repo root and use the overlay's build output.
        var root = FindRepoRoot(AppContext.BaseDirectory);
        if (root is not null)
        {
            var overlayBin = Path.Combine(root, "src", "Scribe.Overlay", "bin");
            if (Directory.Exists(overlayBin))
            {
                var matches = Directory.GetFiles(overlayBin, "Scribe.Overlay.exe", SearchOption.AllDirectories);
                var marker = $"{Path.DirectorySeparatorChar}{BuildConfig}{Path.DirectorySeparatorChar}";
                var best = Array.Find(matches, p => p.Contains(marker, StringComparison.OrdinalIgnoreCase))
                           ?? (matches.Length > 0 ? matches[0] : null);
                if (best is not null)
                {
                    _log?.LogInformation("Overlay exe via dev fallback ({Config}): {Path}", BuildConfig, best);
                    return best;
                }
            }
        }

        if (!_loggedMissing)
        {
            _loggedMissing = true;
            _log?.LogError("Overlay exe not found (env / installer / dev fallback). The recording pill is disabled.");
        }

        return null;
    }

    private static string? FindRepoRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Scribe.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private readonly record struct Command(string Text, bool EnsureAlive, bool IsExit)
    {
        public static Command Exit { get; } = new(string.Empty, false, true);
    }

    /// <summary>
    /// A process-wide Win32 job object configured with <c>KILL_ON_JOB_CLOSE</c>. The overlay process is
    /// assigned to it at launch, so when this (the engine) process exits for any reason — clean shutdown,
    /// crash, or external force-kill — the OS tears down the job and kills the overlay with it. This is the
    /// authoritative guard against orphaning, independent of the pipe and of any managed teardown running.
    /// The job handle is intentionally never closed; it is released by the OS as our process dies, which is
    /// exactly the moment the kill should fire.
    /// </summary>
    private static class OverlayChildJob
    {
        private const uint JobObjectLimitKillOnJobClose = 0x2000;
        private const int JobObjectExtendedLimitInformation = 9;

        private static readonly object Gate = new();
        private static IntPtr _handle = IntPtr.Zero;
        private static bool _attempted;
        private static bool _available;

        public static void TryAssign(Process process, ILogger? log)
        {
            lock (Gate)
            {
                if (!_attempted)
                {
                    _attempted = true;
                    _available = TryCreate(log);
                }

                if (!_available)
                {
                    return;
                }

                try
                {
                    if (!AssignProcessToJobObject(_handle, process.Handle))
                    {
                        log?.LogDebug("AssignProcessToJobObject failed (err={Err}).", Marshal.GetLastWin32Error());
                    }
                }
                catch (Exception ex)
                {
                    log?.LogDebug(ex, "AssignProcessToJobObject threw (overlay still guarded by parent watchdog).");
                }
            }
        }

        private static bool TryCreate(ILogger? log)
        {
            try
            {
                var handle = CreateJobObject(IntPtr.Zero, null);
                if (handle == IntPtr.Zero)
                {
                    log?.LogDebug("CreateJobObject returned null (err={Err}).", Marshal.GetLastWin32Error());
                    return false;
                }

                var info = default(JOBOBJECT_EXTENDED_LIMIT_INFORMATION);
                info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;

                var length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                var ptr = Marshal.AllocHGlobal(length);
                try
                {
                    Marshal.StructureToPtr(info, ptr, false);
                    if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ptr, (uint)length))
                    {
                        log?.LogDebug("SetInformationJobObject failed (err={Err}).", Marshal.GetLastWin32Error());
                        return false;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }

                _handle = handle; // kept open for the engine's lifetime; OS closes it on exit -> kills overlay
                return true;
            }
            catch (Exception ex)
            {
                log?.LogDebug(ex, "Job object creation failed; relying on parent watchdog instead.");
                return false;
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        // These mirror the Win32 job-object structures; most fields are populated by the marshaller, not
        // by managed code, so CS0649 ("never assigned") is expected and intentional here.
#pragma warning disable CS0649
        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }
#pragma warning restore CS0649
    }
}
