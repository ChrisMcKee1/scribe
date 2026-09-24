using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using Scribe.Core.Diagnostics;
using Scribe.Core.Infrastructure;
using Scribe.Core.Lifecycle;
using Scribe.Core.Models;

namespace Scribe.Core.Audio;

/// <summary>What an endpoint notification was about, as far as it can be told inside the notification callback.</summary>
internal enum EndpointChange
{
    /// <summary>A default capture endpoint changed, for any role.</summary>
    CaptureDefault,

    /// <summary>A default render endpoint changed. Never a microphone.</summary>
    RenderDefault,

    /// <summary>
    /// An endpoint was added, removed or changed state. Whether it is a microphone is decided later, outside the callback,
    /// because finding out means asking the enumerator about it.
    /// </summary>
    Device,

    /// <summary>An endpoint's friendly name changed.</summary>
    Name,

    /// <summary>Any other property changed, which a volume change does constantly.</summary>
    OtherProperty,
}

/// <summary>
/// A registration for endpoint notifications on a device enumerator of its own. Disposing it unregisters, then releases
/// the enumerator.
/// </summary>
internal interface IEndpointNotifications : IDisposable
{
    /// <summary>A cheap call on the enumerator holding the registration; throws when it has lost the audio service.</summary>
    void Probe();
}

/// <summary>
/// Watches the capture endpoints and raises <see cref="Changed"/> when a reading finds that the microphones Windows offers,
/// or its default microphone, have actually changed: its own reading once a burst of notifications has gone quiet, or a
/// caller's reading through <see cref="Read"/>. Capture never depends on it (each dictation asks Windows for its device
/// afresh); it exists so an open Settings window, and the log, keep up with what Windows is doing.
/// </summary>
/// <remarks>
/// <para>
/// The notification callbacks run on a Windows audio worker thread and Microsoft's rules for them are strict: the methods
/// "must be nonblocking" and the client "should never wait on a synchronization object during an event callback"; it
/// "should never call RegisterEndpointNotificationCallback or UnregisterEndpointNotificationCallback" in them; and it
/// "should never release the final reference on an MMDevice API object during an event callback"
/// (https://learn.microsoft.com/windows/win32/api/mmdeviceapi/nn-mmdeviceapi-immnotificationclient). So a callback here
/// does nothing but a few interlocked operations and, at most once per burst, queue a work item. It takes no lock, makes
/// no COM call and touches no device. Everything else (the quiet period, reading the endpoints, comparing, logging and
/// raising the event) happens on the thread pool, and registration and unregistration happen only in
/// <see cref="Start"/>, <see cref="EnsureListening"/> and <see cref="Dispose"/>.
/// </para>
/// <para>
/// One user action arrives as a burst: changing the default input in Windows Settings raises OnDefaultDeviceChanged once
/// per role that moved (https://learn.microsoft.com/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-immnotificationclient-ondefaultdevicechanged),
/// and plugging a device in adds endpoints, changes states and sets properties. The endpoints are read once the burst has
/// been quiet for <see cref="DefaultQuietPeriod"/>, and the event is raised only when the capture picture differs from the
/// one read before, so render changes and property churn never reach a subscriber.
/// </para>
/// <para>
/// The registration is the one device enumerator Scribe keeps for any length of time. When the audio service goes away
/// under it, COM hands its client an error instead of an answer (see <see cref="AudioServiceFailure"/>), and
/// <see cref="EnsureListening"/> replaces it: bounded by <see cref="DefaultRecoveryInterval"/>, and logged once per episode.
/// It runs whenever a list is about to be shown, and, while something listens for changes (<see cref="SetWatched"/>),
/// also on that interval by itself, so an open Settings window regains live updates without being touched. While
/// nothing listens it stays lazy: nothing shown can go stale, and the next list shown checks first.
/// </para>
/// <para>
/// Every reading goes through <see cref="Read"/> or the watcher's own reading after a burst, one at a time and against one
/// baseline, and whichever reading finds a different picture announces it. A list shown from an earlier reading therefore
/// always hears about a change, even one another caller read first: when the watcher kept a baseline of its own, a change
/// that landed before its first reading became that baseline and was never announced to a Settings window opened before
/// it.
/// </para>
/// </remarks>
internal sealed class InputDeviceWatcher : IDisposable
{
    /// <summary>How long a burst of notifications must stay quiet before the endpoints are read.</summary>
    internal static readonly TimeSpan DefaultQuietPeriod = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// The least time between two attempts to replace a registration that lost the audio service, and how often the
    /// registration is checked while something listens.
    /// </summary>
    internal static readonly TimeSpan DefaultRecoveryInterval = TimeSpan.FromSeconds(30);

    private readonly Func<Action<EndpointChange>, IEndpointNotifications> _register;
    private readonly Func<IReadOnlyList<AudioDevice>> _read;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly Action<Action> _queue;
    private readonly TimeSpan _quietPeriod;
    private readonly TimeSpan _recoveryInterval;
    private readonly ClosableTimer _quiet;
    private readonly ClosableTimer _health;
    private readonly Action<EndpointChange> _onChange;

    // Taken by Start, EnsureListening and Dispose only; never on a notification callback.
    private readonly object _registrationGate = new();
    private IEndpointNotifications? _notifications;
    private long? _lastRecoveryAttempt;
    private bool _recoveryEpisode;

    // Every reading of the endpoints happens under this gate, the watcher's own on the thread pool and a caller's about to
    // show a list, so readings are compared and announced in the order they were taken and a slow one never overwrites a
    // newer one. Never taken on a notification callback. The last successful reading is null once a reading failed:
    // whoever read then shows no list, so the next reading is news to them.
    private readonly object _readGate = new();
    private IReadOnlyList<AudioDevice>? _lastRead;
    private bool _anyReading;

    // 1 while a request to restart the quiet period is queued and has not run yet.
    private int _armQueued;

    // 1 while something listens for changes, which keeps the registration checked on the recovery interval.
    private int _watched;
    private int _disposed;

    /// <param name="register">
    /// Registers for endpoint notifications, calling the given callback on the audio worker thread for every one.
    /// </param>
    /// <param name="read">Reads the active capture endpoints and their defaults as they are now.</param>
    /// <param name="logger">Receives shapes and device names only.</param>
    /// <param name="time">The clock for the quiet period and the recovery bound.</param>
    /// <param name="queue">Runs work on the thread pool without waiting for it.</param>
    /// <param name="quietPeriod">Defaults to <see cref="DefaultQuietPeriod"/>.</param>
    /// <param name="recoveryInterval">Defaults to <see cref="DefaultRecoveryInterval"/>.</param>
    public InputDeviceWatcher(
        Func<Action<EndpointChange>, IEndpointNotifications> register,
        Func<IReadOnlyList<AudioDevice>> read,
        ILogger logger,
        TimeProvider? time = null,
        Action<Action>? queue = null,
        TimeSpan? quietPeriod = null,
        TimeSpan? recoveryInterval = null)
    {
        ArgumentNullException.ThrowIfNull(register);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(logger);
        _register = register;
        _read = read;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _queue = queue ?? QueueOnThreadPool;
        _quietPeriod = quietPeriod ?? DefaultQuietPeriod;
        _recoveryInterval = recoveryInterval ?? DefaultRecoveryInterval;
        _quiet = new ClosableTimer(ReadAndCompare, _time);
        _health = new ClosableTimer(CheckWhileWatched, _time);
        _onChange = OnEndpointChange;
    }

    /// <summary>
    /// Raised when a reading, the watcher's own after a burst of notifications went quiet or one a caller took through
    /// <see cref="Read"/>, finds the active capture endpoints or their defaults different from the last reading. Carries
    /// the new list, on the thread that read it. Handlers must not block.
    /// </summary>
    public event Action<IReadOnlyList<AudioDevice>>? Changed;

    /// <summary>True while a registration is in place.</summary>
    public bool IsListening
    {
        get { lock (_registrationGate) { return _notifications is not null; } }
    }

    /// <summary>
    /// Registers for notifications. Never throws: a registration that fails is logged, and <see cref="EnsureListening"/>
    /// tries again later. Nothing is read here: the first reading anyone takes is the baseline, and nobody can hold an
    /// older one.
    /// </summary>
    public void Start()
    {
        lock (_registrationGate)
        {
            if (IsDisposed || _notifications is not null)
            {
                return;
            }

            try
            {
                _notifications = _register(_onChange);
            }
            catch (Exception ex)
            {
                _lastRecoveryAttempt = _time.GetTimestamp();
                _recoveryEpisode = true;
                TryLog(log => log.LogWarning(
                    "Could not watch for audio device changes ({Failure}); the microphone list refreshes when it is next " +
                    "opened instead.",
                    FailureShape.Describe(ex)));
            }
        }
    }

    /// <summary>
    /// Reads the endpoints for a caller about to show them, against the same baseline as the watcher's own readings: when
    /// the picture differs from the last reading, whoever took that one, <see cref="Changed"/> is raised with it, so a
    /// list shown earlier catches up. Throws what the reading throws, after which the next reading counts as news.
    /// </summary>
    public IReadOnlyList<AudioDevice> Read()
    {
        lock (_readGate)
        {
            IReadOnlyList<AudioDevice> now;
            try
            {
                now = _read();
            }
            catch
            {
                ForgetLastReading();
                throw;
            }

            Publish(now);
            return now;
        }
    }

    /// <summary>
    /// Whether anything listens for changes. While something does, the registration is also checked every recovery
    /// interval (<see cref="EnsureListening"/>, with the same bound and logging), so a list left open regains live updates
    /// after the audio service comes back; while nothing does, the check waits for the next list to be shown.
    /// </summary>
    public void SetWatched(bool watched)
    {
        Volatile.Write(ref _watched, watched ? 1 : 0);
        if (IsDisposed)
        {
            return;
        }

        if (watched)
        {
            _health.Schedule(_recoveryInterval);
        }
        else
        {
            _health.Cancel();
        }
    }

    /// <summary>
    /// Replaces a registration that has lost the audio service, or one that could never be made: at most once per call and
    /// once per recovery interval, logged once per episode. Called where a fresh microphone list is about to be shown, and
    /// on the recovery interval while something listens (<see cref="SetWatched"/>). Never throws, and never runs on a
    /// notification callback.
    /// </summary>
    public void EnsureListening()
    {
        if (IsDisposed)
        {
            return;
        }

        var reconnected = false;
        lock (_registrationGate)
        {
            if (IsDisposed)
            {
                return;
            }

            Exception? lost = null;
            if (_notifications is { } current)
            {
                try
                {
                    current.Probe();
                    return;
                }
                catch (Exception ex) when (AudioServiceFailure.IsConnectionLost(ex))
                {
                    lost = ex;
                }
                catch (Exception ex)
                {
                    // Not the audio service going away, so a new registration would not fix it.
                    TryLog(log => log.LogDebug(
                        "The audio device watcher could not check its registration ({Failure}).", FailureShape.Describe(ex)));
                    return;
                }
            }

            var now = _time.GetTimestamp();
            if (_lastRecoveryAttempt is { } previous && _time.GetElapsedTime(previous, now) < _recoveryInterval)
            {
                return;
            }

            _lastRecoveryAttempt = now;
            if (lost is not null)
            {
                var stale = _notifications;
                _notifications = null;
                TryDispose(stale);
            }

            reconnected = TryReconnect(lost);
        }

        if (reconnected)
        {
            // Nothing was heard while the registration was gone, so read now in case something changed meanwhile.
            TryQueue(Arm);
        }
    }

    /// <summary>
    /// Stops watching: no reading is scheduled after this, the registration is removed and then its enumerator released,
    /// and a reading already under way raises nothing. Idempotent; never throws.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _quiet.Close();
        _health.Close();

        IEndpointNotifications? notifications;
        lock (_registrationGate)
        {
            notifications = _notifications;
            _notifications = null;
        }

        TryDispose(notifications);
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    // Caller holds _registrationGate. One warning per episode of trouble, whether or not the reconnection works at once.
    private bool TryReconnect(Exception? lost)
    {
        var firstInEpisode = !_recoveryEpisode;
        try
        {
            _notifications = _register(_onChange);
            _recoveryEpisode = false;
            TryLog(log =>
            {
                if (lost is not null && firstInEpisode)
                {
                    log.LogWarning(
                        "The audio device watcher lost the audio service ({Failure}) and has reconnected.",
                        FailureShape.Describe(lost));
                }
                else
                {
                    log.LogInformation("Watching for audio device changes again.");
                }
            });
            return true;
        }
        catch (Exception ex)
        {
            _recoveryEpisode = true;
            TryLog(log =>
            {
                if (firstInEpisode)
                {
                    log.LogWarning(
                        "The audio device watcher lost the audio service ({Failure}) and could not reconnect yet ({Retry}); " +
                        "dictation is unaffected, and it tries again when the microphone list is next opened.",
                        FailureShape.Describe(lost ?? ex),
                        FailureShape.Describe(ex));
                }
                else
                {
                    log.LogDebug("The audio device watcher still could not reconnect ({Failure}).", FailureShape.Describe(ex));
                }
            });
            return false;
        }
    }

    // The audio worker thread, for every endpoint notification. See the remarks: interlocked operations and, once per
    // burst, one queued work item. Never a lock, a wait, a COM call, a device, a registration or an exception.
    private void OnEndpointChange(EndpointChange change)
    {
        try
        {
            if (change is EndpointChange.RenderDefault or EndpointChange.OtherProperty || IsDisposed)
            {
                return;
            }

            if (Interlocked.Exchange(ref _armQueued, 1) == 0)
            {
                try
                {
                    _queue(Arm);
                }
                catch
                {
                    // Only a thread pool that cannot take work (out of memory) gets here; the next notification tries again.
                    Volatile.Write(ref _armQueued, 0);
                }
            }
        }
        catch
        {
            // Nothing may escape into the audio service's thread.
        }
    }

    // Thread pool. Cleared before the quiet period restarts, so a notification that arrives while it restarts queues
    // another restart instead of being folded into this one: the reading always follows the last notification.
    private void Arm()
    {
        Volatile.Write(ref _armQueued, 0);
        _quiet.Schedule(_quietPeriod);
    }

    // Thread pool, once a burst has been quiet for the quiet period. Never throws: it runs on a timer thread.
    private void ReadAndCompare()
    {
        try
        {
            lock (_readGate)
            {
                if (IsDisposed)
                {
                    return;
                }

                IReadOnlyList<AudioDevice> now;
                try
                {
                    now = _read();
                }
                catch (Exception ex)
                {
                    // The next notification, or the next list shown, reads again.
                    ForgetLastReading();
                    TryLog(log => log.LogDebug("Could not read the audio input devices ({Failure}).", FailureShape.Describe(ex)));
                    return;
                }

                Publish(now);
            }
        }
        catch (Exception ex)
        {
            TryLog(log => log.LogWarning(
                "Reacting to an audio device change failed ({Failure}).", FailureShape.DescribeWithStack(ex)));
        }
    }

    // Caller holds _readGate. The event is raised under it, so two readings are announced in the order they were taken and
    // the last one announced is always the newest; handlers must not block, and none takes this gate. The very first
    // reading is nobody's news: no list can have been shown from an earlier one.
    private void Publish(IReadOnlyList<AudioDevice> now)
    {
        var before = _lastRead;
        var first = !_anyReading;
        _anyReading = true;
        _lastRead = now;
        if (first || IsDisposed || (before is not null && SamePicture(before, now)))
        {
            return;
        }

        TryLog(log => log.LogInformation("Audio input devices changed: {Change}", DescribeChange(before, now)));
        ResilientEvent.InvokeAll(
            Changed,
            now,
            ex => TryLog(log => log.LogWarning(
                "An audio device change handler threw ({Failure}).", FailureShape.DescribeWithStack(ex))));
    }

    // Caller holds _readGate. A reading failed, so whoever took it shows no list: the next reading is news to them even if
    // it matches the one before.
    private void ForgetLastReading()
    {
        _anyReading = true;
        _lastRead = null;
    }

    // Thread pool, on the recovery interval while something listens. Never throws: it runs on a timer thread. A tick can
    // already be on its way when the last listener leaves, so both the check and the re-arm look at the flag again.
    private void CheckWhileWatched()
    {
        try
        {
            if (IsDisposed || Volatile.Read(ref _watched) == 0)
            {
                return;
            }

            EnsureListening();
            if (!IsDisposed && Volatile.Read(ref _watched) != 0)
            {
                _health.Schedule(_recoveryInterval);
            }
        }
        catch (Exception ex)
        {
            TryLog(log => log.LogDebug(
                "Checking the audio device watcher's registration failed ({Failure}).", FailureShape.Describe(ex)));
        }
    }

    /// <summary>Test seam: the periodic check, as a tick that was already past its timer delivers it.</summary>
    internal void DeliverWatchedCheck() => CheckWhileWatched();

    /// <summary>True when two readings name the same devices, with the same names and the same defaults, in any order.</summary>
    internal static bool SamePicture(IReadOnlyList<AudioDevice> before, IReadOnlyList<AudioDevice> now) =>
        before.Count == now.Count &&
        before.OrderBy(device => device.Id, StringComparer.Ordinal)
            .SequenceEqual(now.OrderBy(device => device.Id, StringComparer.Ordinal));

    /// <summary>
    /// One line for the log: the default and communications default by name, the count, and which names came and went.
    /// Device names only, never endpoint IDs.
    /// </summary>
    internal static string DescribeChange(IReadOnlyList<AudioDevice>? before, IReadOnlyList<AudioDevice> now)
    {
        static string Name(AudioDevice? device) => device is null ? "none" : $"'{device.Name}'";

        var defaultDevice = now.FirstOrDefault(device => device.IsDefault);
        var communications = now.FirstOrDefault(device => device.IsCommunicationsDefault);
        var line = $"default={Name(defaultDevice)} communications={Name(communications)} inputs={now.Count}";
        if (before is null)
        {
            return line;
        }

        var previousDefault = before.FirstOrDefault(device => device.IsDefault);
        if (!string.Equals(previousDefault?.Id, defaultDevice?.Id, StringComparison.Ordinal))
        {
            line += $" previousDefault={Name(previousDefault)}";
        }

        var added = now.Where(device => before.All(old => !string.Equals(old.Id, device.Id, StringComparison.Ordinal)))
            .Select(device => $"'{device.Name}'").ToList();
        var removed = before.Where(device => now.All(current => !string.Equals(current.Id, device.Id, StringComparison.Ordinal)))
            .Select(device => $"'{device.Name}'").ToList();
        if (added.Count > 0)
        {
            line += $" added={string.Join(",", added)}";
        }

        if (removed.Count > 0)
        {
            line += $" removed={string.Join(",", removed)}";
        }

        return line;
    }

    private void TryQueue(Action work)
    {
        try
        {
            _queue(work);
        }
        catch
        {
            // Only a thread pool that cannot take work (out of memory) gets here; the next notification queues again.
        }
    }

    private static void QueueOnThreadPool(Action work) =>
        ThreadPool.UnsafeQueueUserWorkItem(static state => state(), work, preferLocal: false);

    private void TryDispose(IEndpointNotifications? notifications)
    {
        if (notifications is null)
        {
            return;
        }

        try
        {
            notifications.Dispose();
        }
        catch (Exception ex)
        {
            TryLog(log => log.LogDebug(
                "Releasing the audio device watcher's registration failed ({Failure}).", FailureShape.Describe(ex)));
        }
    }

    // The watcher only describes what Windows is doing; a log call that throws must never become a failure of its own.
    private void TryLog(Action<ILogger> write)
    {
        try
        {
            write(_logger);
        }
        catch
        {
            // Nothing useful is left to do.
        }
    }
}

/// <summary>
/// The failures that mean the audio service, or the COM connection to it, has gone away, so an object obtained before
/// cannot answer again and a new one has to be made. The MMDevice API documentation does not say which code a stale
/// device enumerator returns after the audio service restarts, so this is the documented set for a server that has gone.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>AUDCLNT_E_SERVICE_NOT_RUNNING, "The Windows audio service is not running"
/// (https://learn.microsoft.com/windows/win32/api/audioclient/nf-audioclient-iaudioclient-initialize).</description></item>
/// <item><description>RPC_E_DISCONNECTED and CO_E_OBJNOTCONNECTED, what a proxy returns once its object has disconnected;
/// "It is up to the client to destroy the proxy"
/// (https://learn.microsoft.com/windows/win32/api/objidl/nf-objidl-imarshal-disconnectobject, with the values in
/// https://learn.microsoft.com/windows/win32/com/com-error-codes-3 and
/// https://learn.microsoft.com/windows/win32/com/com-error-codes-1).</description></item>
/// <item><description>RPC_E_SERVER_DIED and RPC_E_SERVER_DIED_DNE, "The callee (server [not server application]) is not
/// available and disappeared; all connections are invalid" (com-error-codes-3).</description></item>
/// <item><description>RPC_S_SERVER_UNAVAILABLE, RPC_S_CALL_FAILED and RPC_S_CALL_FAILED_DNE as HRESULTs
/// (https://learn.microsoft.com/windows/win32/debug/system-error-codes--1700-3999-).</description></item>
/// </list>
/// </remarks>
internal static class AudioServiceFailure
{
    internal const int AudioServiceNotRunning = unchecked((int)0x88890010);
    internal const int Disconnected = unchecked((int)0x80010108);
    internal const int ObjectNotConnected = unchecked((int)0x800401FD);
    internal const int ServerDied = unchecked((int)0x80010007);
    internal const int ServerDiedDidNotExecute = unchecked((int)0x80010012);
    internal const int RpcServerUnavailable = unchecked((int)0x800706BA);
    internal const int RpcCallFailed = unchecked((int)0x800706BE);
    internal const int RpcCallFailedDidNotExecute = unchecked((int)0x800706BF);

    /// <summary>True for a COM failure carrying one of the codes above.</summary>
    public static bool IsConnectionLost(Exception exception) =>
        exception is COMException && IsConnectionLost(exception.HResult);

    /// <summary>True for one of the codes above.</summary>
    public static bool IsConnectionLost(int hresult) => hresult is
        AudioServiceNotRunning or
        Disconnected or
        ObjectNotConnected or
        ServerDied or
        ServerDiedDidNotExecute or
        RpcServerUnavailable or
        RpcCallFailed or
        RpcCallFailedDidNotExecute;
}

/// <summary>The production registration, through NAudio 3.0.1's event-based notification client.</summary>
/// <remarks>
/// NAudio 3.0.1 keeps IMMNotificationClient and RegisterEndpointNotificationCallback internal; its public route is
/// <see cref="MMDeviceEnumerator.CreateNotificationClient"/>, which registers a generated COM callback, keeps it alive
/// for the registration (Windows does not AddRef it), and unregisters when the client is disposed. Created with
/// <c>useSynchronizationContext: false</c> so the events are raised on the audio worker thread itself, rather than posted
/// to whatever synchronization context the creating thread has (the WPF dispatcher at startup): the handlers only record
/// the change, and the watcher decides everything else on the thread pool.
/// </remarks>
internal sealed class WasapiEndpointNotifications : IEndpointNotifications
{
    private readonly MMDeviceEnumerator _enumerator;
    private readonly MMDeviceNotificationClient _client;

    private WasapiEndpointNotifications(Action<EndpointChange> notify)
    {
        _enumerator = new MMDeviceEnumerator();
        try
        {
            _client = _enumerator.CreateNotificationClient(useSynchronizationContext: false);
        }
        catch
        {
            _enumerator.Dispose();
            throw;
        }

        _client.DefaultDeviceChanged += (_, e) =>
            notify(e.Flow == DataFlow.Render ? EndpointChange.RenderDefault : EndpointChange.CaptureDefault);
        _client.DeviceAdded += (_, _) => notify(EndpointChange.Device);
        _client.DeviceRemoved += (_, _) => notify(EndpointChange.Device);
        _client.DeviceStateChanged += (_, _) => notify(EndpointChange.Device);
        _client.PropertyValueChanged += (_, e) =>
            notify(IsFriendlyName(e.PropertyKey) ? EndpointChange.Name : EndpointChange.OtherProperty);
    }

    public static IEndpointNotifications Register(Action<EndpointChange> notify) => new WasapiEndpointNotifications(notify);

    public void Probe() => _enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console);

    // Unregistered first, then the enumerator released: the client must stay registered on a live enumerator until
    // UnregisterEndpointNotificationCallback has run
    // (https://learn.microsoft.com/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-immdeviceenumerator-registerendpointnotificationcallback).
    public void Dispose()
    {
        _client.Dispose();
        _enumerator.Dispose();
    }

    private static bool IsFriendlyName(PropertyKey key) =>
        key.formatId == PropertyKeys.PKEY_Device_FriendlyName.formatId &&
        key.propertyId == PropertyKeys.PKEY_Device_FriendlyName.propertyId;
}
