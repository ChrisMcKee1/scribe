namespace Scribe.Core.Overlay;

/// <summary>Consumer-owned duplicate suppression; health and lifetime decisions always run before this gate.</summary>
public sealed class OverlayMeterDelivery(bool enabled)
{
    public static readonly TimeSpan ResendInterval = TimeSpan.FromMilliseconds(250);

    private bool _hasValue;
    private int _lastValue;
    private long _lastSentMs;

    public bool ShouldSend(int value, long nowMs) =>
        !enabled || !_hasValue || value != _lastValue || nowMs - _lastSentMs >= ResendInterval.TotalMilliseconds;

    public void Sent(int value, long nowMs)
    {
        _hasValue = true;
        _lastValue = value;
        _lastSentMs = nowMs;
    }

    public void Reset() => _hasValue = false;
}
