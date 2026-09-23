namespace Scribe.Core.Audio;

/// <summary>
/// The raw device-format bytes of one capture: appended by the capture callback, read once when the
/// capture stops, then handed back to the <see cref="CaptureBufferPool"/> it came from.
/// </summary>
internal sealed class CaptureRecording
{
    internal CaptureRecording(byte[] buffer, bool reused)
    {
        Buffer = buffer;
        Reused = reused;
    }

    /// <summary>The backing array. Only the first <see cref="Length"/> bytes are audio.</summary>
    public byte[] Buffer { get; private set; }

    /// <summary>Bytes of audio written so far.</summary>
    public int Length { get; private set; }

    /// <summary>True when the backing array was retained from an earlier capture.</summary>
    public bool Reused { get; }

    /// <summary>True when the capture outgrew its reservation and moved to a larger array.</summary>
    public bool Grew { get; private set; }

    public ReadOnlySpan<byte> Written => Buffer.AsSpan(0, Length);

    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        var required = checked(Length + data.Length);
        if (required > Buffer.Length)
        {
            Grow(required);
        }

        data.CopyTo(Buffer.AsSpan(Length));
        Length = required;
    }

    /// <summary>
    /// Takes the backing array away from this recording and zeroes the audio it held. The recording
    /// is left empty, so a write that ever arrived afterwards would grow into a fresh array instead
    /// of reaching an array the pool may already have handed to the next capture.
    /// </summary>
    internal byte[] Detach()
    {
        var buffer = Buffer;
        var length = Length;
        Buffer = [];
        Length = 0;
        Array.Clear(buffer, 0, length);
        return buffer;
    }

    // Same growth rule as MemoryStream, which this replaces: at least double, capped at the largest
    // array the runtime allows, so a long dictation still costs a logarithmic number of copies.
    private void Grow(int required)
    {
        var doubled = Math.Max(Buffer.Length * 2L, 256L);
        var capacity = (int)Math.Max(required, Math.Min(doubled, Array.MaxLength));

        var expanded = new byte[capacity];
        Buffer.AsSpan(0, Length).CopyTo(expanded);

        // The outgrown array holds the start of this dictation and is about to become garbage.
        Array.Clear(Buffer, 0, Length);
        Buffer = expanded;
        Grew = true;
    }
}

/// <summary>
/// Keeps the working buffer of the last capture so the next one does not allocate a fresh
/// 30-second reservation (11,520,000 bytes at 48 kHz stereo float, a large object heap allocation)
/// on every press.
/// <para>
/// At most one buffer is retained, and it never holds audio: a recording is zeroed when it comes
/// back, before it can be kept. A recording that outgrew its reservation is dropped rather than
/// kept, so what stays resident is bounded by the reservation clamp in
/// <see cref="AudioCaptureService.ReservationBytes"/>. Retaining is not returning memory to the OS;
/// <see cref="ReleaseRetained"/> lets an idle-time release give it back to the garbage collector.
/// </para>
/// <para>
/// Renting takes the retained buffer exclusively, so two recordings can never share an array even
/// if captures were ever allowed to overlap; the second would simply allocate.
/// </para>
/// <para>
/// Every member is safe to call from any thread at any time. The pool references a buffer only
/// while no recording holds it: <see cref="Rent"/> moves it out under the gate, and
/// <see cref="Return"/> detaches it from the recording and publishes it only after zeroing it.
/// So <see cref="ReleaseRetained"/>, which touches nothing but that reference, can never clear or
/// drop a buffer an active capture is using, whether it lands before a capture starts, during
/// recording or conversion, or while a finished capture is being returned.
/// </para>
/// </summary>
internal sealed class CaptureBufferPool
{
    private readonly object _gate = new();
    private readonly Action? _afterWipe;
    private byte[]? _retained;

    public CaptureBufferPool()
    {
    }

    /// <summary>
    /// Test seam: <paramref name="afterWipe"/> runs inside <see cref="Return"/> after the buffer is
    /// detached from the recording and zeroed and before it is published, outside the gate, so a
    /// test can land a release or a rental in exactly that window.
    /// </summary>
    internal CaptureBufferPool(Action afterWipe) => _afterWipe = afterWipe;

    /// <summary>Bytes currently kept for the next capture.</summary>
    public long RetainedBytes
    {
        get
        {
            lock (_gate)
            {
                return _retained?.LongLength ?? 0;
            }
        }
    }

    public CaptureRecording Rent(int reservationBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(reservationBytes);

        byte[]? reuse;
        lock (_gate)
        {
            reuse = _retained;
            _retained = null;
        }

        // A buffer sized for a lower-rate device is too small for this one; letting it go and
        // reserving the right size once is cheaper than growing through it mid-capture.
        if (reuse is not null && reuse.Length < reservationBytes)
        {
            reuse = null;
        }

        return reuse is null
            ? new CaptureRecording(new byte[reservationBytes], reused: false)
            : new CaptureRecording(reuse, reused: true);
    }

    /// <summary>
    /// Takes the buffer back from the recording and zeroes it, and when <paramref name="retain"/> is
    /// set and the recording never outgrew its reservation, keeps it for the next capture. Only call
    /// once nothing can still write to the recording; a write that arrives anyway lands in a fresh
    /// array of its own, never in the kept one.
    /// </summary>
    public void Return(CaptureRecording recording, bool retain)
    {
        ArgumentNullException.ThrowIfNull(recording);

        var grew = recording.Grew;
        var buffer = recording.Detach();
        _afterWipe?.Invoke();
        if (!retain || grew)
        {
            return;
        }

        lock (_gate)
        {
            _retained ??= buffer;
        }
    }

    /// <summary>
    /// Drops the retained buffer, if any, and returns how many bytes it held. Never touches a buffer
    /// a recording holds, and never clears anything: a retained buffer is already zeroed.
    /// </summary>
    public long ReleaseRetained()
    {
        lock (_gate)
        {
            var bytes = _retained?.LongLength ?? 0;
            _retained = null;
            return bytes;
        }
    }
}
