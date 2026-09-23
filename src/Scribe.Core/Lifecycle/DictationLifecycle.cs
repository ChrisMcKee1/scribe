using Scribe.Core.Audio;
using Scribe.Core.Persistence;

namespace Scribe.Core.Lifecycle;

/// <summary>Where the dictation loop is. Pausing is not a phase: it only stops new recordings from starting.</summary>
public enum DictationPhase
{
    /// <summary>Waiting for the hotkey.</summary>
    Idle,

    /// <summary>A recording is live.</summary>
    Recording,

    /// <summary>A stopped recording is being transcribed, cleaned up and inserted.</summary>
    Processing,
}

/// <summary>How <see cref="DictationLifecycle{TCapture}.TryBeginRecording"/> decided one activation.</summary>
public enum ActivationDecision
{
    /// <summary>A new recording started.</summary>
    Started,

    /// <summary>Dictation is paused; the activation was ignored.</summary>
    Paused,

    /// <summary>A recording is already live.</summary>
    AlreadyRecording,

    /// <summary>The previous dictation is still processing; the activation was turned away and counted.</summary>
    StillProcessing,

    /// <summary>Shutdown has begun; nothing new starts.</summary>
    Closing,
}

/// <summary>The decision on one activation.</summary>
/// <param name="Decision">What happened.</param>
/// <param name="DictationId">The new recording's id when one started; otherwise the current dictation's, for the log.</param>
/// <param name="RejectedWhileProcessing">
/// When <paramref name="Decision"/> is <see cref="ActivationDecision.StillProcessing"/>: how many activations the current
/// dictation has turned away so far, this one included.
/// </param>
/// <param name="Capture">The context the factory created for the new recording, when one started.</param>
public readonly record struct DictationActivation<TCapture>(
    ActivationDecision Decision,
    long DictationId,
    int RejectedWhileProcessing,
    TCapture? Capture)
    where TCapture : class;

/// <summary>
/// A stopped recording admitted to processing. Hand it back to <see cref="DictationLifecycle{TCapture}.EndProcessing"/>
/// as the processing task's very last step, after it has returned to idle and finished its own cleanup.
/// </summary>
public sealed class ProcessingAdmission<TCapture>
    where TCapture : class
{
    internal ProcessingAdmission(long dictationId, TCapture capture, InFlightWork.Lease lease, CancellationToken lifetime)
    {
        DictationId = dictationId;
        Capture = capture;
        Lease = lease;
        Lifetime = lifetime;
    }

    /// <summary>The dictation being processed.</summary>
    public long DictationId { get; }

    /// <summary>The context captured when its recording started.</summary>
    public TCapture Capture { get; }

    /// <summary>Canceled when shutdown begins; the processing observes it at its checkpoints.</summary>
    public CancellationToken Lifetime { get; }

    internal InFlightWork.Lease Lease { get; }
}

/// <summary>What the shell shows for the dictation loop after one transition.</summary>
/// <param name="Revision">
/// Numbers the change, taken under the lifecycle's gate together with the transition that made it: positive, and higher
/// for every later change. The shell hears about a change only after the gate is released, often on another thread, so
/// two changes can reach it in the opposite order; it shows one only when its revision is higher than the last it showed
/// (see <see cref="PresentationRelay{T}"/>), so an older change can never undo a newer one.
/// </param>
/// <param name="Phase">
/// What to show: idle, a live recording (only once its microphone has opened), or processing.
/// </param>
/// <param name="Paused">With <see cref="DictationPhase.Idle"/>, whether dictation is paused; false otherwise.</param>
public readonly record struct DictationPresentation(long Revision, DictationPhase Phase, bool Paused);

/// <summary>The decision on one stop request.</summary>
/// <param name="Admission">The admission when this stop ended the live recording; null when it was ignored.</param>
/// <param name="ObservedPhase">The phase the stop found, for the log.</param>
/// <param name="CurrentDictationId">The dictation id the stop found, for the log.</param>
/// <param name="Presentation">
/// When the stop was admitted, the Processing change it made to what the shell shows; revision 0 otherwise.
/// </param>
public readonly record struct StopDecision<TCapture>(
    ProcessingAdmission<TCapture>? Admission,
    DictationPhase ObservedPhase,
    long CurrentDictationId,
    DictationPresentation Presentation = default)
    where TCapture : class;

/// <summary>
/// What <see cref="DictationLifecycle{TCapture}.ReturnToIdle"/> (or <see cref="DictationLifecycle{TCapture}.TryAbandonRecording"/>)
/// found on the way back.
/// </summary>
/// <param name="Paused">Dictation is paused, so the shell should show Paused rather than Idle.</param>
/// <param name="RejectedWhileProcessing">Activations turned away while the dictation that just ended was processing.</param>
/// <param name="Presentation">The Idle or Paused change this return made to what the shell shows.</param>
public readonly record struct IdleReturn(bool Paused, int RejectedWhileProcessing, DictationPresentation Presentation);

/// <summary>What <see cref="DictationLifecycle{TCapture}.SetPaused"/> changed.</summary>
/// <param name="Changed">False when the requested pause state was already in effect.</param>
/// <param name="WasRecording">A recording was live when the pause state changed.</param>
/// <param name="WasIdle">The loop was idle when the pause state changed.</param>
/// <param name="Sequence">
/// Numbers the change, taken under the same gate that made it: positive, and higher for every later change. A caller
/// that hands the new state on after the gate is released (the keyboard hook, from the controller) passes this along,
/// so two changes that reach the hook in the opposite order still leave it in the state that was set last. Zero when
/// nothing changed.
/// </param>
/// <param name="Presentation">
/// When the loop was idle, the Idle or Paused change this made to what the shell shows; null otherwise, because a pause
/// that lands mid-dictation shows only when that dictation returns to idle.
/// </param>
public readonly record struct PauseChange(
    bool Changed,
    bool WasRecording,
    bool WasIdle,
    long Sequence,
    DictationPresentation? Presentation = null);

/// <summary>A silence auto-stop that fell due (see <see cref="DictationLifecycle{TCapture}.UpdateSilence"/>).</summary>
/// <param name="DictationId">
/// The recording the tracker was attached to. Pass it to the stop, so the stop can only ever end that recording.
/// </param>
/// <param name="Tracker">The tracker that decided, already detached, so nothing updates it any more; read it for the log.</param>
public readonly record struct SilenceStop(long DictationId, SilenceAutoStopTracker Tracker);

/// <summary>Who takes a capture whose microphone has just opened (see <see cref="DictationLifecycle{TCapture}.HandOffOpenedCapture"/>).</summary>
public enum OpenedCaptureOwner
{
    /// <summary>The recording itself: it is still live, and is now shown as recording.</summary>
    Recording,

    /// <summary>
    /// The recording's processing: a stop admitted the recording while its microphone was opening or just after, and that
    /// processing takes the capture, with every sample recorded until the stop, when its own stop reaches the capture service.
    /// </summary>
    Processing,

    /// <summary>Nobody: shutdown began before any stop admitted the recording, so the opener has to stop the capture itself.</summary>
    Nobody,
}

/// <summary>The decision on a capture whose microphone has just opened.</summary>
/// <param name="Owner">Who takes the capture.</param>
/// <param name="Presentation">With <see cref="OpenedCaptureOwner.Recording"/>, the Recording change to raise; null otherwise.</param>
public readonly record struct OpenedCaptureHandOff(OpenedCaptureOwner Owner, DictationPresentation? Presentation);

/// <summary>What <see cref="DictationLifecycle{TCapture}.Shutdown"/> left behind.</summary>
/// <param name="AlreadyShutDown">An earlier call already ran the shutdown; nothing was done this time.</param>
/// <param name="Processing">How the bounded wait for processing ended.</param>
/// <param name="StillRunningDictationId">The dictation still processing when that wait ended, if any.</param>
/// <param name="History">How the history writer's completion ended; null when completing it failed.</param>
public readonly record struct DictationShutdown(
    bool AlreadyShutDown,
    WorkDrainResult Processing,
    long? StillRunningDictationId,
    HistoryDrainResult? History);

/// <summary>
/// The dictation controller's lifecycle core. One gate orders every decision about whether something may start: a
/// recording, processing, a timer schedule, an idle release or a duration-ceiling stop. The controller keeps the platform
/// work (hotkey, microphone, speech services, events) and asks this type each of those questions, so every rule has one
/// implementation and a test.
/// </summary>
/// <remarks>
/// <para>
/// Nothing slow runs under the gate. The only callback invoked under it is the capture factory handed to
/// <see cref="TryBeginRecording"/>, which must only read cheap platform state and must not call back into this type; the
/// silence tracker's update also runs under it, and is allocation-free arithmetic.
/// The timer callbacks run on the thread pool, outside the gate, and may arrive after shutdown began.
/// </para>
/// <para>
/// Processing is tracked by its own completion (<see cref="EndProcessing"/>), never by the phase it publishes: a
/// dictation that has already returned to idle is still running its final cleanup, and shutdown must wait for that too.
/// Clearing that tracking on the way back to idle is exactly how the controller's shutdown once missed a finishing
/// dictation and disposed a timer underneath it.
/// </para>
/// <para>
/// Every transition the shell shows is numbered here, under the gate that made it (<see cref="DictationPresentation"/>),
/// and each is made only by the dictation that owns the phase it leaves. The controller raises changes after the gate is
/// released, from whichever thread made them, so the number is what lets the shell keep the newest when two arrive out of
/// order: the previous dictation's idle can reach it after the next recording's start.
/// </para>
/// </remarks>
/// <typeparam name="TCapture">The controller's per-recording context, opaque to this type.</typeparam>
public sealed class DictationLifecycle<TCapture>
    where TCapture : class
{
    private readonly object _gate = new();
    private readonly InFlightWork _processing = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TimeProvider _time;
    private readonly ClosableTimer _idleReleaseTimer;
    private readonly ClosableTimer _durationLimitTimer;

    private DictationPhase _phase = DictationPhase.Idle;
    private TCapture? _capture;
    private long _dictationSeq;
    private long _dictationId;
    private long _recordingStartedTimestamp;
    private bool _paused;
    private long _pauseSequence;
    private bool _started;
    private bool _closing;
    private bool _shutDown;

    // Advances every time a recording starts. An idle release claims the value it saw and skips its heap compaction and
    // its announcement if the value moved, because a dictation began while it was unloading.
    private long _activityEpoch;

    // True while an idle release runs, so a pause-triggered release and a timer tick can never run two at once.
    private bool _idleReleaseRunning;

    // Activations turned away while the current dictation processes, reported when it returns to idle.
    private int _rejectedWhileProcessing;

    // The recording the duration ceiling was armed for, and its length. A ceiling tick is honored only for that recording
    // and only once it has actually run that long.
    private long _durationLimitDictationId;
    private TimeSpan _durationLimit;

    // The dictation admitted to processing and not yet ended, so a shutdown that outlives its wait can name it.
    private ProcessingAdmission<TCapture>? _active;

    // What the shell was last told to show, and the number the next change takes.
    private DictationPresentation _presented = new(0, DictationPhase.Idle, false);
    private long _presentationRevision;

    // The silence auto-stop tracker attached to the live recording, and that recording's id. Attached only while that
    // recording is live and dropped by whatever ends it, both under the gate, so a tracker never outlives its recording.
    // One that did was fed the next recording's levels long past its own limits and ended it a few buffers in.
    private SilenceAutoStopTracker? _silenceTracker;
    private long _silenceDictationId;

    // The recording most recently admitted to processing. A capture that opens for it after the admission belongs to that
    // processing (see HandOffOpenedCapture).
    private long _lastAdmittedDictationId;

    /// <param name="onIdleReleaseDue">The idle release timer's callback. Runs on the thread pool, possibly after shutdown began.</param>
    /// <param name="onDurationLimitDue">The duration ceiling timer's callback. Same threading as the idle release.</param>
    /// <param name="timeProvider">The clock behind both timers and the ceiling check; the system clock by default.</param>
    public DictationLifecycle(Action onIdleReleaseDue, Action onDurationLimitDue, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(onIdleReleaseDue);
        ArgumentNullException.ThrowIfNull(onDurationLimitDue);
        _time = timeProvider ?? TimeProvider.System;
        _idleReleaseTimer = new ClosableTimer(onIdleReleaseDue, _time);
        _durationLimitTimer = new ClosableTimer(onDurationLimitDue, _time);
    }

    /// <summary>True once shutdown has begun. Read without the gate, for paths that only need to stand down.</summary>
    public bool IsClosing => Volatile.Read(ref _closing);

    /// <summary>True while dictation is paused.</summary>
    public bool IsPaused
    {
        get { lock (_gate) { return _paused; } }
    }

    /// <summary>The current phase.</summary>
    public DictationPhase Phase
    {
        get { lock (_gate) { return _phase; } }
    }

    /// <summary>The id of the most recent recording, 0 before the first one.</summary>
    public long CurrentDictationId
    {
        get { lock (_gate) { return _dictationId; } }
    }

    /// <summary>The live recording's context, from the moment it starts until processing returns to idle.</summary>
    public TCapture? CurrentCapture
    {
        get { lock (_gate) { return _capture; } }
    }

    /// <summary>What the shell was last told to show: revision 0 (idle) before the first change.</summary>
    public DictationPresentation Presentation
    {
        get { lock (_gate) { return _presented; } }
    }

    /// <summary>Test hook: true once shutdown disposed the lifetime token source.</summary>
    internal bool LifetimeDisposed { get; private set; }

    /// <summary>Marks the loop started and arms the first idle release countdown. Idempotent.</summary>
    /// <param name="idleReleaseAfter">The idle release delay; <see cref="Timeout.InfiniteTimeSpan"/> keeps models resident.</param>
    public void Start(TimeSpan idleReleaseAfter)
    {
        lock (_gate)
        {
            if (_started)
            {
                return;
            }

            _started = true;
            ScheduleIdleReleaseLocked(idleReleaseAfter);
        }
    }

    /// <summary>
    /// Re-arms the idle release countdown with a new delay, but only while the loop is started, idle and not closing:
    /// a recording or a processing dictation re-arms it on its own way back to idle.
    /// </summary>
    public void RescheduleIdleRelease(TimeSpan idleReleaseAfter)
    {
        lock (_gate)
        {
            ScheduleIdleReleaseLocked(idleReleaseAfter);
        }
    }

    /// <summary>
    /// Changes the pause state and reports what the loop was doing when it changed. While idle, the change is also a change
    /// to what the shell shows (Idle or Paused), numbered with it.
    /// </summary>
    public PauseChange SetPaused(bool paused)
    {
        lock (_gate)
        {
            if (_paused == paused)
            {
                return default;
            }

            _paused = paused;
            var idle = _phase == DictationPhase.Idle;
            return new PauseChange(
                true,
                _phase == DictationPhase.Recording,
                idle,
                ++_pauseSequence,
                idle ? PresentLocked(DictationPhase.Idle, paused) : null);
        }
    }

    /// <summary>
    /// Starts a recording when the loop is idle, not paused and not closing. Starting advances the activity epoch (which
    /// invalidates any idle release in progress) and disarms the idle release countdown, atomically with the phase change.
    /// </summary>
    /// <param name="captureFactory">
    /// Creates the recording's context under the gate, so it describes the exact moment the recording began. Invoked only
    /// when a recording starts; if it throws, nothing changes.
    /// </param>
    public DictationActivation<TCapture> TryBeginRecording(Func<TCapture> captureFactory)
    {
        ArgumentNullException.ThrowIfNull(captureFactory);

        lock (_gate)
        {
            if (_closing)
            {
                return new(ActivationDecision.Closing, _dictationId, 0, null);
            }

            if (_paused)
            {
                return new(ActivationDecision.Paused, _dictationId, 0, null);
            }

            switch (_phase)
            {
                case DictationPhase.Recording:
                    return new(ActivationDecision.AlreadyRecording, _dictationId, 0, null);

                case DictationPhase.Processing:
                    return new(ActivationDecision.StillProcessing, _dictationId, ++_rejectedWhileProcessing, null);
            }

            var capture = captureFactory();
            _phase = DictationPhase.Recording;
            _capture = capture;
            _activityEpoch++;
            _recordingStartedTimestamp = _time.GetTimestamp();
            _dictationId = ++_dictationSeq;
            _idleReleaseTimer.Cancel();
            _silenceTracker = null;
            return new(ActivationDecision.Started, _dictationId, 0, capture);
        }
    }

    /// <summary>
    /// Shows recording <paramref name="dictationId"/> as recording, once its microphone has opened, if it is still the live
    /// recording and shutdown has not begun. Returns null otherwise: a pause can stop a recording while its device is still
    /// opening, and the processing that owns it from then on has already moved the shell on, so showing it as recording
    /// now would put back a state that is over.
    /// </summary>
    public DictationPresentation? TryPresentRecording(long dictationId)
    {
        lock (_gate)
        {
            return TryPresentRecordingLocked(dictationId);
        }
    }

    /// <summary>
    /// Decides who takes the capture recording <paramref name="dictationId"/> has just opened. Call it only once the capture
    /// service has opened that capture for this recording as its owner: the service refuses to open for a recording whose
    /// stop has already reached it, so an open that succeeded proves any stop for this recording will find this capture.
    /// </summary>
    /// <remarks>
    /// While the recording is live it takes the capture and is shown as recording. When a stop (a pause, a fault) admitted it
    /// to processing meanwhile, the capture is left to that processing, which receives every sample from its own stop: a
    /// reclaim here threw those samples away and the processing then found nothing. Only when no stop admitted it, because
    /// shutdown began first, does nobody own it, and the opener stops it again.
    /// </remarks>
    public OpenedCaptureHandOff HandOffOpenedCapture(long dictationId)
    {
        lock (_gate)
        {
            if (TryPresentRecordingLocked(dictationId) is { } shown)
            {
                return new(OpenedCaptureOwner.Recording, shown);
            }

            return new(
                _lastAdmittedDictationId == dictationId ? OpenedCaptureOwner.Processing : OpenedCaptureOwner.Nobody, null);
        }
    }

    /// <summary>
    /// The Recording change recording <paramref name="dictationId"/> is shown with, if it is still the live recording, has
    /// been shown as recording, and shutdown has not begun; null otherwise. A notice about that recording (a muted
    /// microphone, the duration ceiling) carries this revision, so the shell shows it only while that same change is still
    /// what it shows, and a notice raised late can never bring back a recording that a pause or a stop already ended.
    /// </summary>
    public DictationPresentation? TryGetRecordingPresentation(long dictationId)
    {
        lock (_gate)
        {
            if (_closing
                || _phase != DictationPhase.Recording
                || _dictationId != dictationId
                || _presented.Phase != DictationPhase.Recording)
            {
                return null;
            }

            return _presented;
        }
    }

    /// <summary>
    /// Returns recording <paramref name="dictationId"/> to idle when it never got going (its device failed to open), but
    /// only while it is still the live recording and no stop has admitted it to processing. Returns null otherwise: a pause
    /// can admit it while its device is still opening, and that processing owns the way back to idle; publishing idle here
    /// as well would end a phase this recording no longer owns, and the recording that starts next along with it.
    /// </summary>
    public IdleReturn? TryAbandonRecording(long dictationId, TimeSpan idleReleaseAfter)
    {
        lock (_gate)
        {
            if (_phase != DictationPhase.Recording || _dictationId != dictationId)
            {
                return null;
            }

            _phase = DictationPhase.Idle;
            _capture = null;
            _silenceTracker = null;
            ScheduleIdleReleaseLocked(idleReleaseAfter);
            return new IdleReturn(_paused, 0, PresentLocked(DictationPhase.Idle, _paused));
        }
    }

    /// <summary>
    /// Attaches <paramref name="tracker"/> to recording <paramref name="dictationId"/>, if that is still the live recording
    /// and shutdown has not begun. Returns false otherwise: a fault or a pause can end the recording between its microphone
    /// opening and this call, and a tracker attached after that would be left behind for the next recording to trip over.
    /// Replaces nothing that matters: a recording starts with no tracker, and every way it ends drops its tracker.
    /// </summary>
    public bool TryAttachSilenceTracker(long dictationId, SilenceAutoStopTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);

        lock (_gate)
        {
            if (_closing || _phase != DictationPhase.Recording || _dictationId != dictationId)
            {
                return false;
            }

            _silenceTracker = tracker;
            _silenceDictationId = dictationId;
            return true;
        }
    }

    /// <summary>
    /// Feeds one input level to the live recording's silence tracker, if it has one. Returns the stop that fell due, with
    /// the tracker already detached, or null when there is no tracker or it says to keep going. Runs on the audio capture
    /// thread for every buffer. The tracker is allocation-free arithmetic, so it runs under the gate, which is what keeps a
    /// tracker from ever being fed a recording it was not attached to.
    /// </summary>
    public SilenceStop? UpdateSilence(float level, long timestampMs)
    {
        lock (_gate)
        {
            if (_silenceTracker is not { } tracker || !tracker.Update(level, timestampMs))
            {
                return null;
            }

            _silenceTracker = null;
            return new SilenceStop(_silenceDictationId, tracker);
        }
    }

    /// <summary>
    /// Arms the duration ceiling for recording <paramref name="dictationId"/>, if that recording is still the live one.
    /// A fault or an auto-stop can end it while the activation path is still opening the device, and arming after that
    /// would leave a tick waiting for whatever recording comes next.
    /// </summary>
    public void ArmDurationLimit(long dictationId, TimeSpan limit)
    {
        lock (_gate)
        {
            if (_closing || _phase != DictationPhase.Recording || _dictationId != dictationId)
            {
                return;
            }

            _durationLimitDictationId = dictationId;
            _durationLimit = limit;
            _durationLimitTimer.Schedule(limit);
        }
    }

    /// <summary>
    /// Decides whether a duration ceiling tick may end the live recording, returning that recording's id, or null when the
    /// tick has nothing to stop: shutdown began, nothing is recording, the ceiling was armed for an earlier recording, or
    /// the live recording has not actually run for its ceiling yet.
    /// </summary>
    /// <remarks>
    /// The last check is what makes a late tick harmless even if it got past the timer's own stale-tick check: a tick
    /// queued for one recording, delivered just after the next one armed the same ceiling, finds a recording that has
    /// barely started.
    /// </remarks>
    public long? TryAcceptDurationLimit()
    {
        lock (_gate)
        {
            if (_closing || _phase != DictationPhase.Recording || _dictationId != _durationLimitDictationId)
            {
                return null;
            }

            return _time.GetElapsedTime(_recordingStartedTimestamp) >= _durationLimit ? _dictationId : null;
        }
    }

    /// <summary>
    /// Ends the live recording and admits it to processing, when a recording is live, shutdown has not begun, and it is
    /// the recording <paramref name="expectedDictationId"/> names (0 accepts whichever is live). Only the stop that is
    /// admitted disarms the duration ceiling, so a late or redundant stop cannot cancel the ceiling of a newer recording.
    /// </summary>
    public StopDecision<TCapture> TryBeginProcessing(long expectedDictationId = 0)
    {
        lock (_gate)
        {
            var observed = _phase;
            var current = _dictationId;

            // Admitted together with the phase change, under the gate BeginShutdown closes admission under, so closing
            // can never land between the two and leave a dictation marked Processing with nothing tracking it. The token
            // is read here too, while the source is guaranteed to be alive.
            if (_phase != DictationPhase.Recording
                || _closing
                || (expectedDictationId != 0 && _dictationId != expectedDictationId)
                || _processing.TryBegin() is not { } lease)
            {
                return new(null, observed, current);
            }

            _durationLimitTimer.Cancel();
            _phase = DictationPhase.Processing;
            _rejectedWhileProcessing = 0;

            // Only the stop that ends the recording drops its silence tracking, like its ceiling above.
            _silenceTracker = null;
            _lastAdmittedDictationId = current;
            var admission = new ProcessingAdmission<TCapture>(current, _capture!, lease, _lifetime.Token);
            _active = admission;
            return new(admission, observed, current, PresentLocked(DictationPhase.Processing, paused: false));
        }
    }

    /// <summary>
    /// Publishes idle: from here the next activation is accepted. Clears the recording's context, hands back the count of
    /// activations turned away meanwhile, and re-arms the idle release countdown unless shutdown has begun. Called by the
    /// processing dictation, which owns the phase until it returns; a recording that never reached processing goes back
    /// through <see cref="TryAbandonRecording"/> instead, which cannot end a phase it does not own.
    /// </summary>
    /// <remarks>
    /// Deliberately does not end processing tracking: the dictation calling this is still finishing, and only its own
    /// <see cref="EndProcessing"/> may say it is done.
    /// </remarks>
    public IdleReturn ReturnToIdle(TimeSpan idleReleaseAfter)
    {
        lock (_gate)
        {
            _phase = DictationPhase.Idle;
            _capture = null;
            _silenceTracker = null;
            var rejected = _rejectedWhileProcessing;
            _rejectedWhileProcessing = 0;
            ScheduleIdleReleaseLocked(idleReleaseAfter);
            return new IdleReturn(_paused, rejected, PresentLocked(DictationPhase.Idle, _paused));
        }
    }

    /// <summary>
    /// Marks a processing dictation completely finished, recording whether it ended in an unexpected fault. Call it as the
    /// processing task's very last step. Only the first call for an admission counts.
    /// </summary>
    public void EndProcessing(ProcessingAdmission<TCapture> admission, bool faulted = false)
    {
        ArgumentNullException.ThrowIfNull(admission);

        lock (_gate)
        {
            if (ReferenceEquals(_active, admission))
            {
                _active = null;
            }
        }

        admission.Lease.Complete(faulted);
    }

    /// <summary>
    /// Runs one idle release (see <see cref="IdleModelRelease"/>) against this loop: claimed only while idle, with no
    /// processing in flight, not closing, and no other release running; invalidated by any recording that starts after the
    /// claim. The steps run outside the gate.
    /// </summary>
    public IdleReleaseOutcome RunIdleRelease(
        Func<bool> anythingResident,
        Action unload,
        Action compact,
        Action announce,
        Action? releaseRetained = null)
    {
        var claimed = false;
        try
        {
            return IdleModelRelease.Run(
                tryClaim: () =>
                {
                    lock (_gate)
                    {
                        if (_closing || _idleReleaseRunning || _phase != DictationPhase.Idle || _processing.Running > 0)
                        {
                            return null;
                        }

                        _idleReleaseRunning = true;
                        claimed = true;
                        return _activityEpoch;
                    }
                },
                isStillIdle: claim =>
                {
                    lock (_gate)
                    {
                        return !_closing && _activityEpoch == claim;
                    }
                },
                anythingResident,
                unload,
                compact,
                announce,
                releaseRetained);
        }
        finally
        {
            if (claimed)
            {
                lock (_gate)
                {
                    _idleReleaseRunning = false;
                }
            }
        }
    }

    /// <summary>
    /// Begins shutdown: from here nothing new starts (no recording, no processing admission, no timer schedule, no idle
    /// release), and events a caller raises can check <see cref="IsClosing"/> to stand down. Returns false when shutdown
    /// had already begun. Waits for nothing, so it is safe to call as the very first step of the app's exit.
    /// </summary>
    public bool BeginShutdown()
    {
        lock (_gate)
        {
            if (_closing)
            {
                return false;
            }

            _closing = true;
            _silenceTracker = null;
            _processing.Close();
            return true;
        }
    }

    /// <summary>
    /// Shuts the loop down in its one safe order: begin shutdown, close both timers, cancel processing, run the caller's
    /// <paramref name="stopInputs"/> steps, wait for processing to finish, then complete the history writer, and dispose
    /// the lifetime token source only if processing finished. Never throws for a step's failure: each is reported through
    /// <paramref name="onStepFailure"/> and the sequence carries on. Only the first call does anything.
    /// </summary>
    /// <remarks>
    /// History completes after the processing wait so the last dictation's entry, queued on its way out, is committed
    /// rather than refused. The token source outlives a dictation that is still running past the wait, which keeps using
    /// its token, so it is then left to the garbage collector.
    /// </remarks>
    public DictationShutdown Shutdown(
        IReadOnlyList<TeardownStep> stopInputs,
        IHistoryWriter history,
        TimeSpan processingDrainTimeout,
        TimeSpan historyDrainTimeout,
        Action<string, Exception>? onStepFailure = null)
    {
        ArgumentNullException.ThrowIfNull(stopInputs);
        ArgumentNullException.ThrowIfNull(history);

        BeginShutdown();
        lock (_gate)
        {
            if (_shutDown)
            {
                return new DictationShutdown(AlreadyShutDown: true, default, null, null);
            }

            _shutDown = true;
        }

        // Closed outside the gate. Nothing can re-arm them now: every schedule checks closing under the gate first, and a
        // closed timer ignores a schedule instead of throwing. Cancellation callbacks run synchronously on this thread,
        // one more reason none of this happens under the gate.
        StagedTeardown.Run(
            [
                new("close the idle release timer", _idleReleaseTimer.Close),
                new("close the duration limit timer", _durationLimitTimer.Close),
                new("cancel processing", () => _lifetime.Cancel()),
                .. stopInputs,
            ],
            onStepFailure);

        var processing = _processing.WaitForDrain(processingDrainTimeout);
        long? stillRunning;
        lock (_gate)
        {
            stillRunning = _active?.DictationId;
        }

        HistoryDrainResult? historyResult = null;
        StagedTeardown.Run(
            [new("complete history", () => historyResult = history.Complete(historyDrainTimeout))],
            onStepFailure);

        if (processing.Drained)
        {
            _lifetime.Dispose();
            LifetimeDisposed = true;
        }

        return new DictationShutdown(AlreadyShutDown: false, processing, stillRunning, historyResult);
    }

    // Caller holds _gate, so the number is taken atomically with the transition it describes.
    private DictationPresentation PresentLocked(DictationPhase phase, bool paused) =>
        _presented = new DictationPresentation(++_presentationRevision, phase, paused);

    // Caller holds _gate.
    private DictationPresentation? TryPresentRecordingLocked(long dictationId) =>
        _closing || _phase != DictationPhase.Recording || _dictationId != dictationId
            ? null
            : PresentLocked(DictationPhase.Recording, paused: false);

    // Caller holds _gate. Scheduling under the gate orders it against shutdown (which marks closing under the same gate
    // before it closes the timer) and against an activation (which disarms the timer under it).
    private void ScheduleIdleReleaseLocked(TimeSpan idleReleaseAfter)
    {
        if (_closing || !_started || _phase != DictationPhase.Idle)
        {
            return;
        }

        _idleReleaseTimer.Schedule(idleReleaseAfter);
    }
}
