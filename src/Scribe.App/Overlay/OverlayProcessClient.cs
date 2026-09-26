using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Scribe.Core.Audio;
using Scribe.Core.Infrastructure;
using Scribe.Core.Models;
using Scribe.Core.Overlay;

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
/// throttled before being sent as integer <c>METER</c> commands to avoid flooding the pipe. Every line
/// is built by <see cref="OverlayPipeProtocol"/>, whose verbs the overlay parses (a test keeps the two equal).
///
/// That consumer also carries out the helper's lifetime, and only carries it out: when to wake, whether
/// an idle helper is suspended, whether a command launches the helper or waits out a cooldown after
/// failures, how a lost helper is judged, how long a dictation's outcome keeps it, and what shutdown ends
/// are all decided by <see cref="OverlayHelperLifetime"/>, and position previews are sequenced by
/// <see cref="OverlayPreviewGate"/>, both in Scribe.Core where they are tested against a scripted clock.
/// Due work runs between commands on this same thread and the wait for it is the queue wait itself, so
/// nothing here sleeps, an EXIT is never held up, and stale commands never pile up behind a wait.
/// </summary>
public sealed class OverlayProcessClient : IOverlayController, IDisposable
{
    private const int ConnectTimeoutMs = 8000;
    private const int WriteTimeoutMs = 1500;
    private const int GracefulExitMs = 1000;
    private const long MeterIntervalMs = 25; // ~40 meter updates/sec is plenty for a VU bar
    private static readonly TimeSpan ConnectObserveBound = TimeSpan.FromSeconds(1);

#if DEBUG
    private const string BuildConfig = "Debug";
#else
    private const string BuildConfig = "Release";
#endif

    private readonly IAudioCaptureService _audio;
    private readonly ILogger<OverlayProcessClient>? _log;
    private readonly BlockingCollection<Command> _queue = new();
    private readonly Thread _consumer;

    // Cancelled by CloseOverlay, so a launch still waiting for the helper's pipe gives up at once
    // instead of holding EXIT behind the connect timeout.
    private readonly CancellationTokenSource _closing = new();

    // The decisions. Only command stamps and preview generations are touched off the consumer thread.
    private readonly OverlayHelperLifetime _lifetime = new();
    private readonly OverlayPreviewGate _preview = new();

    private Process? _process;
    private HelperExitWatch? _exitWatch;
    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;
    private string? _exePath;
    private bool _loggedMissing;
    private long _launchAttempts; // consumer only, for the log

    // The keep-warm minutes last pushed. The push that comes with every state change queues nothing when
    // the value is unchanged; the consumer starts at "never suspend" until the first push.
    private int _keepWarmMinutes = int.MinValue;

    private bool _subscribed;
    private float _meterLevel;
    private long _lastMeterMs;
    private volatile bool _closed;
    private int _meterQueued;
    private int _latestMeter;

    // The latest state the engine asked for, replayed to a relaunched helper. One immutable object, so
    // the consumer can never pair one state's command with another state's demand.
    private volatile DesiredState _desired = DesiredState.Hidden;

    // The applied (saved) anchor. Volatile: written from the UI thread, read by the consumer thread
    // when a relaunch replays it to the fresh process. Previews change what is on screen but never
    // this field, so a cancelled settings dialog costs nothing.
    private volatile OverlayPosition _position = OverlayPosition.BottomCenter;

    /// <param name="audio">Source of the live input level shown while recording.</param>
    /// <param name="log">Optional logger.</param>
    public OverlayProcessClient(IAudioCaptureService audio, ILogger<OverlayProcessClient>? log = null)
    {
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _log = log;
        _consumer = new Thread(Consume) { IsBackground = true, Name = "ScribeOverlayIpc" };
        _consumer.Start();
    }

    public void Warmup() => Enqueue(OverlayPipeProtocol.Warmup, ensureAlive: true);

    public void ShowRecording()
    {
        CancelPreview();
        Subscribe();
        _meterLevel = 0f;
        _desired = DesiredState.Recording;
        Enqueue(OverlayPipeProtocol.Recording, ensureAlive: true);
    }

    public void ShowRecordingWarning(string? reason)
    {
        CancelPreview();
        Subscribe();
        _desired = DesiredState.Recording;
        Enqueue(OverlayPipeProtocol.WarningLine(reason), ensureAlive: true);
    }

    public void ShowProcessing(bool aiPolishing)
    {
        CancelPreview();
        Unsubscribe();
        var desired = aiPolishing ? DesiredState.Polishing : DesiredState.Transcribing;
        _desired = desired;
        Enqueue(desired.Line, ensureAlive: true);
    }

    public void ShowOutcome(PillOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        CancelPreview();
        Unsubscribe();
        var desired = new DesiredState(OverlayPipeProtocol.OutcomeLine(outcome), OverlayDemand.Transient);
        _desired = desired;
        Enqueue(desired.Line, ensureAlive: true, showsFor: outcome.OnScreen);
    }

    public void HideOverlay()
    {
        CancelPreview();
        Unsubscribe();
        _desired = DesiredState.Hidden;
        // The engine's hide cancels a pending relaunch retry; a preview's cosmetic end does not.
        Enqueue(OverlayPipeProtocol.Hide, ensureAlive: false, cancelsRetry: true);
    }

    public void SetPosition(OverlayPosition position)
    {
        CancelPreview();
        _position = position;
        // Don't launch the helper just to move it; a relaunch replays the anchor from _position.
        Enqueue(OverlayPipeProtocol.PositionLine(position), ensureAlive: false);
        Enqueue(_desired.ReplayLine, ensureAlive: false);
    }

    public void SetKeepWarm(int minutes)
    {
        // The consumer applies whatever value is latest when it takes the command, so pushes from any
        // thread, in any order, settle on the last one written here.
        if (Interlocked.Exchange(ref _keepWarmMinutes, minutes) != minutes)
        {
            TryAdd(Command.KeepWarmChanged);
        }
    }

    public void ReleaseWhenIdle() => EnqueueStamped(new Command(CommandKind.Release));

    public void Preview(OverlayPosition position)
    {
        var generation = _preview.BeginPreview();

        EnqueuePreview(generation, OverlayPreviewRole.Anchor, OverlayPipeProtocol.PositionLine(position), ensureAlive: true);
        EnqueuePreview(generation, OverlayPreviewRole.Step, OverlayPipeProtocol.Recording, ensureAlive: true);

        _ = Task.Run(async () =>
        {
            try
            {
                // Synthetic level sweep so the preview pill reads as "recording" rather than dead.
                const int steps = 24;
                for (var i = 0; i < steps; i++)
                {
                    await Task.Delay(70).ConfigureAwait(false);
                    if (!_preview.IsCurrent(generation))
                    {
                        return; // superseded: the consumer drops what is queued and restores the anchor
                    }

                    var level = (Math.Sin(i / 2.5) + 1) / 2 * 0.8 + 0.1;
                    EnqueuePreview(
                        generation,
                        OverlayPreviewRole.Step,
                        OverlayPipeProtocol.MeterLine((int)(level * 1000)),
                        ensureAlive: false);
                }

                // Only saves queuing a stale end: one superseded after this check is dropped when taken.
                if (_preview.IsCurrent(generation))
                {
                    EnqueuePreview(generation, OverlayPreviewRole.End, string.Empty, ensureAlive: false);
                }
            }
            catch
            {
                // Preview is cosmetic; never let it surface an error.
            }
        });
    }

    public void CloseOverlay()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        CancelPreview();
        Unsubscribe();

        try
        {
            _closing.Cancel();
        }
        catch (Exception ex)
        {
            TryLog(LogLevel.Debug, ex, "Cancelling an in-flight overlay launch failed.");
        }

        try
        {
            _queue.Add(Command.Exit);
            _queue.CompleteAdding();
        }
        catch (InvalidOperationException)
        {
            // queue already completed
        }

        if (_consumer.Join(TimeSpan.FromSeconds(3)))
        {
            _closing.Dispose();
        }

        // Only a stuck consumer leaves a helper behind here; kill it outright rather than wait on it.
        KillProcess(graceful: false);
    }

    public void Dispose() => CloseOverlay();

    private void CancelPreview() => _preview.Supersede();

    // ---- Command queue plumbing -----------------------------------------------------------------

    private void Enqueue(string text, bool ensureAlive, bool cancelsRetry = false, TimeSpan showsFor = default) =>
        EnqueueStamped(new Command(
            CommandKind.State, text, ensureAlive, cancelsRetry, ShowsForMs: (long)showsFor.TotalMilliseconds));

    private void EnqueuePreview(long generation, OverlayPreviewRole role, string text, bool ensureAlive) =>
        EnqueueStamped(new Command(CommandKind.State, text, ensureAlive, Role: role, Generation: generation));

    private void EnqueueStamped(Command command)
    {
        if (_queue.IsAddingCompleted)
        {
            return;
        }

        // Stamped after the caller published its state and before the add, so an idle suspend or a pause
        // release committing meanwhile already sees this command and stands down.
        TryAdd(command with { Stamp = _lifetime.IssueStamp() });
    }

    private void TryAdd(Command command)
    {
        try
        {
            _queue.Add(command);
        }
        catch (InvalidOperationException)
        {
            // queue completed between the guard and the add; overlay is shutting down
        }
    }

    private void Consume()
    {
        while (true)
        {
            try
            {
                // Due work first, then wait only until the next wake the lifetime names. The wait IS the
                // timer, so nothing here sleeps: an EXIT or any other command ends it immediately.
                RunDueWork();

                if (!_queue.TryTake(out var item, WaitMilliseconds()))
                {
                    if (_queue.IsCompleted)
                    {
                        return;
                    }

                    continue; // a deadline or a retry fell due; the top of the loop handles it
                }

                if (!HandleCommand(item))
                {
                    return; // EXIT handled
                }
            }
            catch (Exception ex)
            {
                // Nothing above is expected to throw. If something does, keep the consumer alive so the
                // pill can still be driven; the lifetime updates its state before returning a decision, so
                // a repeating fault cannot spin this loop on the same due work.
                TryLog(LogLevel.Warning, ex, "Overlay command loop hit an unexpected error; continuing.");
            }
        }
    }

    private void RunDueWork()
    {
        var nowMs = Environment.TickCount64;
        if (_lifetime.NextWakeAtMs() is not { } wakeMs || wakeMs > nowMs)
        {
            return;
        }

        var helper = ObserveHelper(nowMs);
        // The demand is read here, at the decision, never earlier: one read before a blocking launch could
        // suspend the helper under a long recording, which sends nothing but unstamped meters.
        var work = _lifetime.TakeDueWork(nowMs, _desired.Demand, helper);
        if (helper.Status == OverlayHelperStatus.Lost)
        {
            DiscardLostHelper();
        }

        switch (work)
        {
            case OverlayDueWork.Suspend:
                EndHelper();
                TryLog(LogLevel.Information, null,
                    "Overlay helper suspended after {IdleMinutes} idle minutes; the next show relaunches it.",
                    _lifetime.IdlePeriodMs / 60_000);
                break;

            case OverlayDueWork.Release:
                EndHelper();
                TryLog(LogLevel.Information, null,
                    "Overlay helper released because dictation was paused, once the outcome on screen had hidden; " +
                    "the next show relaunches it.");
                break;

            case OverlayDueWork.Retry:
                TryLog(LogLevel.Information, null,
                    "Overlay relaunch retry due after a {CooldownMs} ms cooldown.", _lifetime.LastCooldownMs);
                Launch();
                break;
        }
    }

    private int WaitMilliseconds()
    {
        if (_lifetime.NextWakeAtMs() is not { } wakeMs)
        {
            return Timeout.Infinite;
        }

        var remainingMs = wakeMs - Environment.TickCount64;
        return remainingMs <= 0 ? 0 : (int)Math.Min(remainingMs, int.MaxValue);
    }

    // Returns false once EXIT has been handled and the consumer should stop.
    private bool HandleCommand(Command item)
    {
        if (item.Kind == CommandKind.Exit)
        {
            _lifetime.OnExit();
            EndHelper();
            return false;
        }

        try
        {
            switch (item.Kind)
            {
                case CommandKind.KeepWarm:
                    ApplyKeepWarm(Volatile.Read(ref _keepWarmMinutes));
                    break;
                case CommandKind.Release:
                    HandleRelease(item.Stamp);
                    break;
                case CommandKind.Meter:
                    HandleMeter();
                    break;
                default:
                    HandleState(item);
                    break;
            }
        }
        catch (Exception ex)
        {
            // The command word only: a WARNING or outcome argument is user-facing text.
            TryLog(LogLevel.Warning, ex, "Overlay command {Command} failed; tearing down for relaunch.", item.Verb);
            RecoverFromFailedWrite();
        }
        finally
        {
            if (item.Kind == CommandKind.Meter)
            {
                Volatile.Write(ref _meterQueued, 0);
            }
        }

        return true;
    }

    private void HandleState(Command item)
    {
        var verdict = _preview.OnCommand(item.Role, item.Generation);
        if (verdict == OverlayPreviewVerdict.Drop)
        {
            _lifetime.OnCommandDropped(item.Stamp);
            return;
        }

        var nowMs = Environment.TickCount64;
        var helper = ObserveHelper(nowMs);
        var action = _lifetime.OnStateCommand(
            nowMs, item.Stamp, item.EnsureAlive, item.CancelsRetry, _desired.Demand, helper, item.ShowsForMs);
        if (!Prepare(action, helper, nowMs))
        {
            return;
        }

        if (verdict == OverlayPreviewVerdict.RestoreAnchorThenDeliver)
        {
            WriteWithTimeout(AppliedAnchorLine);
        }

        if (item.Role == OverlayPreviewRole.End)
        {
            WritePreviewEnd();
        }
        else
        {
            WriteWithTimeout(item.Text);
        }
    }

    private void HandleMeter()
    {
        var nowMs = Environment.TickCount64;
        var helper = ObserveHelper(nowMs);
        if (Prepare(_lifetime.OnMeter(nowMs, _desired.Demand, helper), helper, nowMs))
        {
            WriteWithTimeout(OverlayPipeProtocol.MeterLine(Volatile.Read(ref _latestMeter)));
        }
    }

    private void HandleRelease(long stamp)
    {
        var nowMs = Environment.TickCount64;
        var helper = ObserveHelper(nowMs);
        var work = _lifetime.OnReleaseWhenIdle(nowMs, stamp, _desired.Demand, helper);
        if (helper.Status == OverlayHelperStatus.Lost)
        {
            DiscardLostHelper();
        }

        if (work == OverlayDueWork.Release)
        {
            EndHelper();
            TryLog(LogLevel.Information, null,
                "Overlay helper released because dictation was paused; the next show relaunches it.");
        }
        else if (_lifetime.ReleaseDueAtMs is { } dueMs)
        {
            TryLog(LogLevel.Debug, null,
                "Overlay release on pause waits {WaitMs} ms for the outcome on screen to hide.", Math.Max(0, dueMs - nowMs));
        }
        else if (HasHelperState)
        {
            TryLog(LogLevel.Debug, null,
                "Overlay release on pause skipped: a newer command or an on-screen state still needs the helper.");
        }
    }

    private void ApplyKeepWarm(int minutes)
    {
        var idleMs = minutes > 0 ? minutes * 60_000L : 0;
        if (idleMs == _lifetime.IdlePeriodMs)
        {
            return;
        }

        _lifetime.SetIdlePeriodMs(idleMs);
        TryLog(LogLevel.Debug, null,
            "Overlay keep-warm period set to {Minutes} minutes (0 keeps the helper resident).", idleMs / 60_000);
    }

    /// <summary>
    /// Carries out a command decision up to the write: a lost helper's leftovers are discarded, a launch
    /// is attempted, a held-back launch is logged. Returns true when the command should be written now.
    /// </summary>
    private bool Prepare(OverlayCommandAction action, OverlayHelperObservation helper, long nowMs)
    {
        if (helper.Status == OverlayHelperStatus.Lost)
        {
            DiscardLostHelper();
        }

        switch (action)
        {
            case OverlayCommandAction.Write:
                return true;
            case OverlayCommandAction.Launch:
                return Launch();
            case OverlayCommandAction.Hold:
                LogHeldBack(nowMs);
                return false;
            default:
                return false;
        }
    }

    private string AppliedAnchorLine => OverlayPipeProtocol.PositionLine(_position);

    // A preview ends by handing the pill back to what the engine wants: a sustained state it covered comes
    // back at the applied anchor; otherwise the pill hides first and moves back while hidden.
    private void WritePreviewEnd()
    {
        var desired = _desired;
        if (desired.Demand == OverlayDemand.Sustained)
        {
            WriteWithTimeout(AppliedAnchorLine);
            WriteWithTimeout(desired.Line);
        }
        else
        {
            WriteWithTimeout(OverlayPipeProtocol.Hide);
            WriteWithTimeout(AppliedAnchorLine);
        }
    }

    private void WriteWithTimeout(string text)
    {
        var write = _writer!.WriteLineAsync(text);
        if (!write.Wait(WriteTimeoutMs))
        {
            ObserveFaultLater(write); // it faults when the pipe is torn down, with nobody left to observe it
            throw new TimeoutException($"Overlay pipe write exceeded {WriteTimeoutMs} ms.");
        }
    }

    private bool IsAlive
    {
        get
        {
            try
            {
                return _process is { HasExited: false } && _pipe is { IsConnected: true } && _writer is not null;
            }
            catch (Exception)
            {
                return false; // an unusable process handle is as good as gone
            }
        }
    }

    private bool HasHelperState => _process is not null || _pipe is not null || _writer is not null;

    private OverlayHelperObservation ObserveHelper(long nowMs)
    {
        if (IsAlive)
        {
            return OverlayHelperObservation.Alive;
        }

        if (!HasHelperState)
        {
            return OverlayHelperObservation.Absent;
        }

        // The process's own exit time when the OS reported it: nothing is written to a gone helper, so a
        // loss can be noticed long after it happened, and it is judged by how long the helper really ran.
        return OverlayHelperObservation.Lost(_exitWatch?.ExitedAtMs ?? nowMs);
    }

    private void DiscardLostHelper()
    {
        if (_lifetime.LastLoss is { CooldownMs: { } cooldownMs } early)
        {
            TryLog(LogLevel.Warning, null,
                "Overlay helper was lost {LivedMs} ms after its launch, inside the {StableMs} ms stable window; " +
                "counted as {Failures} consecutive failures, cooldown {CooldownMs} ms.",
                early.LivedMs, _lifetime.StableAfterMs, _lifetime.ConsecutiveFailures, cooldownMs);
        }
        else
        {
            TryLog(LogLevel.Information, null,
                "Overlay helper was lost after running {LivedMs} ms; it comes back when the pill is needed.",
                _lifetime.LastLoss?.LivedMs);
        }

        KillProcess(graceful: false); // it is gone or unreachable, so there is nothing to wait for
    }

    private void RecoverFromFailedWrite()
    {
        try
        {
            if (!HasHelperState)
            {
                return; // nothing was running, so nothing was lost
            }

            var nowMs = Environment.TickCount64;
            var action = _lifetime.OnWriteFailed(nowMs, _exitWatch?.ExitedAtMs ?? nowMs, _desired.Demand);
            DiscardLostHelper();

            // A hidden pill needs no helper. Anything on screen gets one back through the same cooldown gate
            // as every other launch, so a helper that keeps dying is not relaunched on every write.
            if (action == OverlayCommandAction.Launch)
            {
                Launch();
            }
            else if (action == OverlayCommandAction.Hold)
            {
                LogHeldBack(nowMs);
            }
        }
        catch (Exception ex)
        {
            TryLog(LogLevel.Warning, ex, "Overlay recovery after a failed command failed.");
        }
    }

    private void LogHeldBack(long nowMs) =>
        TryLog(LogLevel.Debug, null,
            "Overlay launch held back: {RemainingMs} ms of cooldown left after {Failures} consecutive failures; " +
            "retry pending {RetryPending}.",
            _lifetime.CooldownRemainingMs(nowMs), _lifetime.ConsecutiveFailures, _lifetime.RetryDueAtMs is not null);

    /// <summary>Runs one launch the lifetime asked for and reports its outcome. Returns true when a helper is up.</summary>
    private bool Launch()
    {
        LaunchOutcome outcome;
        long attempt = 0;
        var failuresBefore = _lifetime.ConsecutiveFailures;
        if (_closing.IsCancellationRequested)
        {
            outcome = LaunchOutcome.Abandoned; // closing never spawns a helper just to kill it again
        }
        else
        {
            attempt = ++_launchAttempts;
            TryLog(LogLevel.Debug, null,
                "Overlay launch attempt {Attempt} starting after {Failures} consecutive failures.", attempt, failuresBefore);
            outcome = TryLaunch();
        }

        // Read after the attempt, which can block: the state may have moved on meanwhile.
        _lifetime.OnLaunchCompleted(Environment.TickCount64, ToResult(outcome), _desired.Demand);

        switch (outcome)
        {
            case LaunchOutcome.Launched:
                if (failuresBefore > 0)
                {
                    TryLog(LogLevel.Information, null,
                        "Overlay launch attempt {Attempt} succeeded after {Failures} consecutive failures; backoff reset.",
                        attempt, failuresBefore);
                }

                break;

            case LaunchOutcome.Abandoned:
                break; // closing; not a failure

            default:
                TryLog(
                    // A missing executable is already reported once by the resolver; a Warning on every
                    // dictation after that would only bury the log.
                    outcome == LaunchOutcome.ExecutableMissing ? LogLevel.Debug : LogLevel.Warning,
                    null,
                    "Overlay launch attempt {Attempt} failed ({Outcome}); {Failures} consecutive failures, " +
                    "next attempt allowed in {CooldownMs} ms, retry pending {RetryPending}.",
                    attempt, outcome, _lifetime.ConsecutiveFailures, _lifetime.LastCooldownMs,
                    _lifetime.RetryDueAtMs is not null);
                break;
        }

        return outcome == LaunchOutcome.Launched;
    }

    private static OverlayLaunchResult ToResult(LaunchOutcome outcome) => outcome switch
    {
        LaunchOutcome.Launched => OverlayLaunchResult.Launched,
        LaunchOutcome.Abandoned => OverlayLaunchResult.Abandoned,
        _ => OverlayLaunchResult.Failed,
    };

    private LaunchOutcome TryLaunch()
    {
        try
        {
            // A previously-resolved path can vanish if a dev rebuild moves the overlay's output; drop it
            // so we re-resolve rather than relaunch-looping against a dead path.
            if (_exePath is not null && !File.Exists(_exePath))
            {
                TryLog(LogLevel.Warning, null, "Cached overlay exe path no longer exists; re-resolving: {Exe}", _exePath);
                _exePath = null;
            }

            _exePath ??= ResolveOverlayExe();
        }
        catch (Exception ex)
        {
            // The dev fallback scans directories, which can throw. A resolve that throws is a failed launch
            // and cools down like one, instead of rescanning on every command.
            TryLog(LogLevel.Warning, ex, "Resolving the overlay executable failed.");
            _exePath = null;
            return LaunchOutcome.Failed;
        }

        if (_exePath is null)
        {
            return LaunchOutcome.ExecutableMissing;
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

            // A close can land after this launch was decided; never spawn a helper only to kill it again.
            if (_closing.IsCancellationRequested)
            {
                TryLog(LogLevel.Debug, null, "Overlay launch abandoned because the overlay is closing.");
                return LaunchOutcome.Abandoned;
            }

            var process = Process.Start(psi) ?? throw new InvalidOperationException("The overlay process did not start.");
            _process = process;

            // Watched from the start, so the connect below stops waiting the moment the helper dies and a
            // later loss is judged by the process's own exit time.
            var exitWatch = HelperExitWatch.Attach(process, _log);
            _exitWatch = exitWatch;

            // OS-level safety net: tie the overlay's lifetime to ours so it can never orphan, even if
            // we are force-killed in the window before the pipe connects (pipe EOF only fires once a
            // connection exists). KILL_ON_JOB_CLOSE fires when our process handle table is torn down.
            OverlayChildJob.TryAssign(process, _log);

            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            _pipe = pipe;
            if (!ConnectBeforeExit(pipe, exitWatch))
            {
                TryLog(LogLevel.Warning, null,
                    "Overlay helper exited during startup before opening its pipe (exit code {ExitCode}).",
                    TryGetExitCode(process));
                KillProcess(graceful: false);
                return LaunchOutcome.Failed;
            }

            var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            _writer = writer;

            // A fresh process starts at the built-in default anchor; replay the applied position so
            // relaunches (crash recovery, lazy launch) come up where the user chose and in the latest
            // visible state rather than waiting for a future dictation transition. A dictation's outcome is
            // not replayed (see DesiredState.ReplayLine): its own command, written after this, shows it.
            writer.WriteLine(OverlayPipeProtocol.PositionLine(_position));
            writer.WriteLine(_desired.ReplayLine);
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
            TryLog(LogLevel.Debug, null, "Overlay launch abandoned because the overlay is closing.");
            KillProcess(graceful: false); // it never connected, so it cannot be asked to exit
            return LaunchOutcome.Abandoned;
        }
        catch (Exception ex)
        {
            // Only genuine launch/connect I/O is inside the try, and logging here is non-throwing, so
            // this catch reliably means the overlay really failed to start.
            TryLog(LogLevel.Error, ex, "Failed to launch/connect the overlay process at {Exe}.", _exePath);
            if (_exePath is not null && !File.Exists(_exePath))
            {
                _exePath = null; // the exe vanished mid-flight, so force re-resolution next time
            }

            KillProcess(graceful: false); // it never connected, or its pipe broke during the replay
            return LaunchOutcome.Failed;
        }

        // Logged only after a confirmed-good launch, and via a non-throwing helper. Previously this sat
        // INSIDE the try above, so a transient log-file lock threw here, was caught as a "launch
        // failure", and KillProcess() tore down a perfectly healthy overlay; this was a root cause of the
        // intermittent "pill disappears" regressions.
        TryLog(
            LogLevel.Information, null,
            "Overlay process launched pid={Pid} pipe={Pipe} exe={Exe}",
            _process?.Id, pipeName, _exePath);
        return LaunchOutcome.Launched;
    }

    /// <summary>
    /// Races the pipe connect against the helper's exit, so a helper that dies during startup fails the
    /// launch at once instead of after the whole connect timeout. Returns false when the helper exited
    /// first; throws on a connect timeout or I/O error, and with the close's cancellation when closing.
    /// </summary>
    private bool ConnectBeforeExit(NamedPipeClientStream pipe, HelperExitWatch exitWatch)
    {
        using var connectCancel = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        // The cancellable overload, unlike Connect(timeout), lets CloseOverlay and a dead helper end the wait.
        var connect = pipe.ConnectAsync(ConnectTimeoutMs, connectCancel.Token);
        if (Task.WhenAny(connect, exitWatch.Exited).GetAwaiter().GetResult() == connect)
        {
            connect.GetAwaiter().GetResult(); // rethrows a timeout, an I/O error, or the close's cancellation
            return true;
        }

        // Let the connect loop see its cancellation (it checks at least every 50 ms) before the caller
        // disposes the pipe that loop is still using.
        connectCancel.Cancel();
        try
        {
            if (!connect.Wait(ConnectObserveBound))
            {
                ObserveFaultLater(connect);
            }
        }
        catch (Exception)
        {
            // cancelled or failed; either is how it was expected to end
        }

        _closing.Token.ThrowIfCancellationRequested();
        return false;
    }

    // An abandoned task that may still fault must not surface later as an unobserved task exception,
    // which the app logs as an error.
    private static void ObserveFaultLater(Task task) =>
        _ = task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static int? TryGetExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Ends a running helper on purpose (idle suspend, pause release, shutdown): EXIT first, so it closes
    // itself, with the kill as the backstop.
    private void EndHelper()
    {
        var sentExit = IsAlive && TryWrite(OverlayPipeProtocol.Exit);
        KillProcess(graceful: sentExit);
    }

    // Non-throwing logging for the overlay-launch path. A diagnostics failure (e.g. a transient
    // shared-log-file lock) must never surface as an exception here, because nearby catch blocks treat
    // any throw as an overlay failure and respond destructively with KillProcess().
    private void TryLog(LogLevel level, Exception? ex, string message, params object?[] args) =>
        TryLogTo(_log, level, ex, message, args);

    private static void TryLogTo(ILogger? log, LogLevel level, Exception? ex, string message, params object?[] args)
    {
        try
        {
            if (log is null)
            {
                return;
            }

            // The failure goes in by shape as one more value, never as the exception, like every log line
            // in the app shell (see FailureShape).
            var template = ex is null ? message : message + " ({Failure})";
            object?[] values = ex is null ? args : [.. args, Scribe.Core.Diagnostics.FailureShape.Describe(ex)];
            log.Log(level, template, values);
        }
        catch
        {
            // best-effort
        }
    }

    // Timed like every other write, so a helper that stopped reading cannot hold EXIT up.
    private bool TryWrite(string text)
    {
        try
        {
            WriteWithTimeout(text);
            return true;
        }
        catch (Exception ex)
        {
            TryLog(LogLevel.Debug, ex, "Overlay EXIT write failed (process likely already gone).");
            return false;
        }
    }

    private void KillProcess(bool graceful)
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
            // Only a helper that was sent EXIT gets a moment to close itself. One that never connected
            // (its server would wait out its own 12 s connection timeout), died, or stopped reading is
            // killed at once instead of costing the consumer a second.
            if (_process is { HasExited: false } proc && (!graceful || !proc.WaitForExit(GracefulExitMs)))
            {
                proc.Kill(entireProcessTree: true);
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
            _exitWatch = null;
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
        EnqueueMeter(scaled);
    }

    private void EnqueueMeter(int scaled)
    {
        Volatile.Write(ref _latestMeter, scaled);
        if (Interlocked.Exchange(ref _meterQueued, 1) != 0)
        {
            return;
        }

        if (_queue.IsAddingCompleted || !_queue.TryAdd(Command.Meter))
        {
            Volatile.Write(ref _meterQueued, 0);
        }
    }

    // ---- Overlay executable resolution ----------------------------------------------------------

    private string? ResolveOverlayExe()
    {
        // (1) Explicit override, handy for testing a specific overlay build.
        var env = Environment.GetEnvironmentVariable("SCRIBE_OVERLAY_EXE");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            TryLog(LogLevel.Information, null, "Overlay exe via SCRIBE_OVERLAY_EXE: {Path}", env);
            return env;
        }

        // (2) Installer layout: shipped self-contained next to the app under Overlay\.
        var installed = Path.Combine(AppContext.BaseDirectory, "Overlay", "Scribe.Overlay.exe");
        if (File.Exists(installed))
        {
            TryLog(LogLevel.Information, null, "Overlay exe via installer layout: {Path}", installed);
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
                var arch = RuntimeInformation.ProcessArchitecture;
                var best = OverlayExecutableSelector.Select(matches, BuildConfig, arch);
                if (best is not null)
                {
                    TryLog(
                        LogLevel.Information, null,
                        "Overlay exe via dev fallback ({Config}/{Arch}): {Path}", BuildConfig, arch, best);
                    return best;
                }

                if (matches.Length > 0 && !_loggedMissing)
                {
                    _loggedMissing = true;
                    TryLog(
                        LogLevel.Error, null,
                        "Found {Count} overlay build(s) under {Bin} but none targets {Arch}; the recording pill is " +
                        "disabled. Build it with: dotnet build src/Scribe.Overlay/Scribe.Overlay.csproj -c {Config} " +
                        "-p:Platform={Platform}",
                        matches.Length,
                        overlayBin,
                        arch,
                        BuildConfig,
                        arch == Architecture.Arm64 ? "ARM64" : "x64");
                    return null;
                }
            }
        }

        if (!_loggedMissing)
        {
            _loggedMissing = true;
            TryLog(LogLevel.Error, null, "Overlay exe not found (env / installer / dev fallback). The recording pill is disabled.");
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

    private enum CommandKind
    {
        State,
        Meter,
        Exit,
        KeepWarm,
        Release,
    }

    private enum LaunchOutcome
    {
        Launched,
        Failed,
        ExecutableMissing,
        Abandoned,
    }

    private readonly record struct Command(
        CommandKind Kind,
        string Text = "",
        bool EnsureAlive = false,
        bool CancelsRetry = false,
        long Stamp = 0,
        OverlayPreviewRole Role = OverlayPreviewRole.None,
        long Generation = 0,
        long ShowsForMs = 0)
    {
        public static Command Exit { get; } = new(CommandKind.Exit);

        /// <summary>Coalesced and unstamped: it carries no level, and it does not count as activity.</summary>
        public static Command Meter { get; } = new(CommandKind.Meter);

        /// <summary>Unstamped and valueless: the consumer reads the latest pushed period; a setting is not activity.</summary>
        public static Command KeepWarmChanged { get; } = new(CommandKind.KeepWarm);

        /// <summary>
        /// The command word alone, for the log. A WARNING or outcome argument is user-facing text: a reason, or a next step
        /// that can name a microphone.
        /// </summary>
        public string Verb => Kind switch
        {
            CommandKind.Meter => OverlayPipeProtocol.Meter,
            CommandKind.Exit => OverlayPipeProtocol.Exit,
            CommandKind.KeepWarm => "KEEPWARM",
            CommandKind.Release => "RELEASE",
            _ when Role == OverlayPreviewRole.End => "PREVIEW-END",
            _ => Text.IndexOf(' ') is var space and >= 0 ? Text[..space] : Text,
        };
    }

    /// <summary>The latest state the engine asked for: the pipe line that shows it, and what it needs from the helper.</summary>
    private sealed record DesiredState(string Line, OverlayDemand Demand)
    {
        public static DesiredState Hidden { get; } = new(OverlayPipeProtocol.Hide, OverlayDemand.None);
        public static DesiredState Recording { get; } = new(OverlayPipeProtocol.Recording, OverlayDemand.Sustained);
        public static DesiredState Transcribing { get; } = new(OverlayPipeProtocol.ProcessingLine(aiCleanup: false), OverlayDemand.Sustained);
        public static DesiredState Polishing { get; } = new(OverlayPipeProtocol.ProcessingLine(aiCleanup: true), OverlayDemand.Sustained);

        /// <summary>
        /// What a relaunched helper, or one being moved, is told to show: the state itself, unless it hides itself (a
        /// dictation's outcome). That is shown once, by its own command; replayed, a settings save minutes after a
        /// dictation would flash its "Typed" again.
        /// </summary>
        public string ReplayLine => Demand == OverlayDemand.Transient ? OverlayPipeProtocol.Hide : Line;
    }

    /// <summary>
    /// Records when a helper process exits, on the monotonic clock, from the process's own exit
    /// notification rather than from whenever the consumer next looks. A launch can stop waiting for the
    /// pipe the moment the helper dies, and a loss noticed late is still judged by how long it really ran.
    /// </summary>
    private sealed class HelperExitWatch
    {
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _exitedAtMs;

        /// <summary>Completes when the process exits.</summary>
        public Task Exited => _exited.Task;

        /// <summary>When the process exited, or null while it runs (or when its exit could not be watched).</summary>
        public long? ExitedAtMs => _exited.Task.IsCompleted ? Volatile.Read(ref _exitedAtMs) : null;

        public static HelperExitWatch Attach(Process process, ILogger? log)
        {
            var watch = new HelperExitWatch();

            // Subscribed before enabling, so an exit that already happened is still reported: enabling
            // registers a wait on the process handle, which fires at once if the handle is already signaled.
            process.Exited += watch.OnExited;
            try
            {
                process.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                // Without the watch, the connect falls back to its timeout and a loss is timed when noticed.
                TryLogTo(log, LogLevel.Debug, ex, "Could not watch the overlay helper for exit.");
            }

            return watch;
        }

        // Raised once, inside the Process object's lock, by the thread-pool wait on the process handle (or by
        // whichever thread first reads HasExited after the exit): record and signal, nothing else.
        private void OnExited(object? sender, EventArgs e)
        {
            Volatile.Write(ref _exitedAtMs, Environment.TickCount64);
            _exited.TrySetResult();
        }
    }

    /// <summary>
    /// A process-wide Win32 job object configured with <c>KILL_ON_JOB_CLOSE</c>. The overlay process is
    /// assigned to it at launch, so when this (the engine) process exits for any reason (clean shutdown,
    /// crash, or external force-kill) the OS tears down the job and kills the overlay with it. This is the
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

        // Runs inside the launch's try block, so its logging goes through the non-throwing helper: a
        // throwing logger here would read as a failed launch and kill a healthy helper.
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
                        TryLogTo(log, LogLevel.Debug, null,
                            "AssignProcessToJobObject failed (err={Err}).", Marshal.GetLastWin32Error());
                    }
                }
                catch (Exception ex)
                {
                    TryLogTo(log, LogLevel.Debug, ex,
                        "AssignProcessToJobObject threw (overlay still guarded by parent watchdog).");
                }
            }
        }

        private static bool TryCreate(ILogger? log)
        {
            var handle = IntPtr.Zero;
            try
            {
                handle = CreateJobObject(IntPtr.Zero, null);
                if (handle == IntPtr.Zero)
                {
                    TryLogTo(log, LogLevel.Debug, null,
                        "CreateJobObject returned null (err={Err}).", Marshal.GetLastWin32Error());
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
                        TryLogTo(log, LogLevel.Debug, null,
                            "SetInformationJobObject failed (err={Err}).", Marshal.GetLastWin32Error());
                        CloseHandle(handle);
                        handle = IntPtr.Zero;
                        return false;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }

                _handle = handle; // kept open for the engine's lifetime; OS closes it on exit -> kills overlay
                handle = IntPtr.Zero;
                return true;
            }
            catch (Exception ex)
            {
                TryLogTo(log, LogLevel.Debug, ex, "Job object creation failed; relying on parent watchdog instead.");
                if (handle != IntPtr.Zero)
                {
                    CloseHandle(handle);
                }

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

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

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
