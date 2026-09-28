using System.Buffers;
using System.Numerics;

namespace Scribe.Core.Audio;

/// <summary>
/// The scratch arrays the capture conversion grows its result through (see
/// <see cref="AudioCaptureService.ReadAll(NAudio.Wave.ISampleProvider)"/>), kept by the capture service rather than by
/// <see cref="ArrayPool{T}.Shared"/> so that an idle release can let them go.
/// </summary>
/// <remarks>
/// <para>
/// The shared pool keeps a returned array in a slot of the thread that returned it, and drops it only once two
/// collections at least 30 s apart have seen it there, so the one forced collection an idle release makes never gave the
/// conversion's scratch back, and every pool thread that converted a capture kept a set of its own (4 MB after a 30 s
/// dictation at 48 kHz, measured). This pool keeps one set for the whole service, and
/// <see cref="ReleaseRetained"/> drops it before that collection.
/// </para>
/// <para>
/// Lengths are exactly the ones the shared pool hands out, the next power of two and at least 16, and never a larger
/// one: the first scratch length decides what the resampler is asked for, which at rates that are not a multiple of
/// 16 kHz changes the samples. That is also why this is not <see cref="ArrayPool{T}.Create()"/>, which may hand out an
/// array from a larger bucket when the right one is empty.
/// </para>
/// <para>
/// At most one array of each length is kept, and none longer than <see cref="MaxRetainedLength"/>, so no more than
/// <see cref="MaxRetainedBytes"/> stay resident. The conversion clears every array before it gives it back, because it
/// held a dictation's audio, so what is kept here holds none.
/// </para>
/// </remarks>
internal sealed class CaptureScratchPool : ArrayPool<float>
{
    /// <summary>
    /// The longest array kept: 2^21 floats (8 MiB), the last scratch array of a dictation up to about 131 s long. The
    /// larger arrays of a longer dictation are allocated for it and let go afterwards.
    /// </summary>
    internal const int MaxRetainedLength = 1 << 21;

    /// <summary>The shortest length handed out, as the shared pool does.</summary>
    internal const int MinLength = 16;

    /// <summary>The most this pool keeps: one array of every power-of-two length from 16 to <see cref="MaxRetainedLength"/>.</summary>
    internal const long MaxRetainedBytes = ((2L * MaxRetainedLength) - MinLength) * sizeof(float);

    // Beyond this the shared pool no longer rounds a request up, and neither does this one.
    private const int MaxRoundedLength = 1 << 30;

    private readonly object _gate = new();
    private readonly float[]?[] _kept = new float[]?[BitOperations.Log2(MaxRetainedLength) + 1];

    /// <summary>Bytes of scratch kept for the next conversion.</summary>
    public long RetainedBytes
    {
        get
        {
            lock (_gate)
            {
                return KeptBytes();
            }
        }
    }

    public override float[] Rent(int minimumLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumLength);
        if (minimumLength == 0)
        {
            return [];
        }

        if (minimumLength > MaxRoundedLength)
        {
            return new float[minimumLength];
        }

        var length = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(minimumLength, MinLength));
        if (length <= MaxRetainedLength)
        {
            var index = BitOperations.Log2((uint)length);
            lock (_gate)
            {
                if (_kept[index] is { } kept)
                {
                    _kept[index] = null;
                    return kept;
                }
            }
        }

        return new float[length];
    }

    /// <summary>
    /// Keeps <paramref name="array"/> for the next rent of its length, unless one is already kept, it is longer than
    /// <see cref="MaxRetainedLength"/>, or it is not a length this pool hands out; those are simply let go.
    /// </summary>
    public override void Return(float[] array, bool clearArray = false)
    {
        ArgumentNullException.ThrowIfNull(array);
        var length = array.Length;
        if (length < MinLength || length > MaxRetainedLength || !BitOperations.IsPow2(length))
        {
            return;
        }

        if (clearArray)
        {
            Array.Clear(array);
        }

        var index = BitOperations.Log2((uint)length);
        lock (_gate)
        {
            _kept[index] ??= array;
        }
    }

    /// <summary>
    /// Drops every kept array and returns how many bytes they held. Never touches an array a conversion has rented, and
    /// clears nothing: a kept array was cleared before it came back.
    /// </summary>
    public long ReleaseRetained()
    {
        lock (_gate)
        {
            var bytes = KeptBytes();
            Array.Clear(_kept);
            return bytes;
        }
    }

    // Caller holds _gate.
    private long KeptBytes()
    {
        long bytes = 0;
        foreach (var kept in _kept)
        {
            bytes += (kept?.LongLength ?? 0) * sizeof(float);
        }

        return bytes;
    }
}
