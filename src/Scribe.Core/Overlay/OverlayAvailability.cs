namespace Scribe.Core.Overlay;

/// <summary>
/// A consumer-published view of the helper, not of the user's indicator preference. A failed episode stays open until a
/// replacement survives the stable window; connecting its pipe alone is not recovery.
/// </summary>
public sealed record OverlayAvailability(bool IsAvailable, bool IsFaulted, long FailureEpisode)
{
    public static OverlayAvailability Initial { get; } = new(false, false, 0);
}
