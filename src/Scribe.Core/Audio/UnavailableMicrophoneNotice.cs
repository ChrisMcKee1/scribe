namespace Scribe.Core.Audio;

/// <summary>
/// Decides when to tell the user that the microphone they chose is not available and dictation used the Windows default
/// instead: once when that starts, not on every press while it lasts. The episode ends when the chosen microphone opens
/// again, when the user chooses another one or the Windows default, or when the app restarts.
/// </summary>
/// <remarks>
/// Fed the outcome of each capture start and nothing else, so it is deterministic. Thread safe, though the controller
/// only calls it from the one thread that starts recordings.
/// </remarks>
public sealed class UnavailableMicrophoneNotice
{
    private readonly object _gate = new();
    private string? _noticedFor;

    /// <summary>
    /// Records a capture that started. <paramref name="requestedDeviceId"/> is the microphone the capture asked for
    /// (null or blank for the Windows default), and <paramref name="fellBack"/> says whether it recorded from the Windows
    /// default instead. Returns true exactly when the user should be told now.
    /// </summary>
    public bool ShouldNotify(string? requestedDeviceId, bool fellBack)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(requestedDeviceId) || !fellBack)
            {
                _noticedFor = null;
                return false;
            }

            if (string.Equals(_noticedFor, requestedDeviceId, StringComparison.Ordinal))
            {
                return false;
            }

            _noticedFor = requestedDeviceId;
            return true;
        }
    }
}
