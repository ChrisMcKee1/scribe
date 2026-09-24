using Scribe.Core.Lifecycle;

namespace Scribe.Core.Audio;

/// <summary>How the user hears that the chosen microphone was unavailable for one capture.</summary>
public enum UnavailableMicrophoneAnnouncement
{
    /// <summary>Nothing to say: the chosen microphone opened, none was chosen, or this episode was already told.</summary>
    None,

    /// <summary>The recording is live on the Windows default: the pill says so while it records, and the tray says so.</summary>
    WhileRecording,

    /// <summary>A pause or a fault took the recording to processing as its microphone opened: the tray says so.</summary>
    AfterRecording,
}

/// <summary>
/// The microphone a capture asked for, and the selection it was asked under. Taken where the capture's settings are read,
/// before the open, and handed back with its outcome.
/// </summary>
/// <param name="DeviceId">The chosen microphone's endpoint ID, or null to follow the Windows default.</param>
/// <param name="Generation">The committed selection's generation when the capture was asked for.</param>
public readonly record struct MicrophoneRequest(string? DeviceId, long Generation);

/// <summary>
/// Tracks episodes of the chosen microphone being unavailable, and decides when to tell the user that dictation recorded
/// from the Windows default instead: once per episode, not on every press while it lasts.
/// </summary>
/// <remarks>
/// <para>
/// It is fed two things, and decides from them alone, so it is deterministic and tested on its own. First, every
/// committed change of the chosen microphone (a Settings save, a tray choice): only a change of the endpoint ID counts,
/// it ends the episode, it moves the selection to a new generation, and it is never itself a fallback. Second, the outcome
/// of every capture that opened a microphone (<see cref="ReportOpen"/>), with the request taken before it opened.
/// </para>
/// <para>
/// For a request made under the committed selection: the chosen microphone opening ends the episode, even when shutdown
/// then reclaimed the capture; a fallback whose audio is kept (the recording is live, or a pause or a fault took it to
/// processing) is announced once per episode; and a fallback whose audio was discarded, because shutdown reclaimed it,
/// is neither announced nor counted as told, since nobody heard about it. A capture that opened nothing reports nothing:
/// the capture service's last values then belong to an earlier start.
/// </para>
/// <para>
/// A request made under an older selection, because the choice was committed while that capture's microphone was
/// opening, may still be announced about that capture when it fell back and kept its audio, but it never changes the
/// episode of the selection committed since. Matching the endpoint ID as well as the generation keeps a request taken
/// halfway through a commit on the stale side, whichever of the two it read first.
/// </para>
/// <para>
/// Before this tracker, the notice learned only from live capture starts. A fallback announced for microphone A was
/// never forgotten when the user chose B and then A again without dictating, so A going away a second time was silent;
/// and a capture a pause or fault took to processing as it opened neither announced its fallback nor ended an episode.
/// </para>
/// </remarks>
public sealed class UnavailableMicrophoneNotice
{
    private readonly object _gate = new();
    private string? _committedId;
    private long _generation;
    private bool _announced;

    /// <summary>
    /// The chosen microphone as committed now: null or blank for the Windows default. A different endpoint ID than the
    /// committed one ends the episode and starts a new selection generation; the same ID changes nothing.
    /// </summary>
    public void SelectionCommitted(string? deviceId)
    {
        var id = Normalize(deviceId);
        lock (_gate)
        {
            if (string.Equals(id, _committedId, StringComparison.Ordinal))
            {
                return;
            }

            _committedId = id;
            _generation++;
            _announced = false;
        }
    }

    /// <summary>The request for a capture about to open <paramref name="deviceId"/>, under the selection committed now.</summary>
    public MicrophoneRequest Begin(string? deviceId)
    {
        var id = Normalize(deviceId);
        lock (_gate)
        {
            return new MicrophoneRequest(id, _generation);
        }
    }

    /// <summary>
    /// Records how the capture asked for by <paramref name="request"/> opened, and says how to tell the user, if at all.
    /// </summary>
    public UnavailableMicrophoneAnnouncement ReportOpen(MicrophoneRequest request, RecordingOpen open)
    {
        if (open.Outcome == RecordingOpenOutcome.NotOpened)
        {
            return UnavailableMicrophoneAnnouncement.None;
        }

        var retained = open.Outcome is RecordingOpenOutcome.Live or RecordingOpenOutcome.LeftToProcessing;
        if (!Report(request, open.RequestedDeviceUnavailable, retained))
        {
            return UnavailableMicrophoneAnnouncement.None;
        }

        return open.Outcome == RecordingOpenOutcome.Live
            ? UnavailableMicrophoneAnnouncement.WhileRecording
            : UnavailableMicrophoneAnnouncement.AfterRecording;
    }

    // True when the user should be told about this capture now.
    private bool Report(MicrophoneRequest request, bool fellBack, bool retained)
    {
        if (request.DeviceId is null)
        {
            // Following Windows: there is no choice to fall back from.
            return false;
        }

        lock (_gate)
        {
            var current = request.Generation == _generation &&
                string.Equals(request.DeviceId, _committedId, StringComparison.Ordinal);
            if (!current)
            {
                return fellBack && retained;
            }

            if (!fellBack)
            {
                _announced = false;
                return false;
            }

            if (!retained || _announced)
            {
                return false;
            }

            _announced = true;
            return true;
        }
    }

    private static string? Normalize(string? deviceId) => string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;
}
