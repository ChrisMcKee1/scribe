using NAudio.Wave;

namespace Scribe.Core.Audio;

/// <summary>
/// Downmixes an arbitrary-channel float source to a single mono channel by averaging the
/// channels of each frame. Works for any channel count (1..N), unlike NAudio's
/// <c>StereoToMonoSampleProvider</c> which only handles two channels. A mono source passes
/// through unchanged.
/// </summary>
/// <remarks>
/// The source is read at most <see cref="MaxSamplesPerRead"/> samples at a time, however much the caller asks for.
/// NAudio's converters size their byte buffer to each request, and the capture conversion can ask for up to half a
/// capture in one read (more once the resampler scales it to the device rate), so unbounded reads grew this buffer and
/// the converter's through a series of large object heap arrays on every dictation. Every sample and every frame is
/// converted on its own, so how a read is split never changes what the caller receives; a short read, which only the end
/// of the stream produces, ends the read as it did when it was one call.
/// </remarks>
internal sealed class MonoDownmixSampleProvider : ISampleProvider
{
    /// <summary>
    /// Most samples one read of the source asks for: 64 KB of float, so neither this buffer nor the
    /// converter's reaches the large object heap.
    /// </summary>
    internal const int MaxSamplesPerRead = 16_384;

    private readonly ISampleProvider _source;
    private readonly int _channels;
    private float[] _buffer = [];

    public MonoDownmixSampleProvider(ISampleProvider source)
    {
        _source = source;
        _channels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(Span<float> buffer) => _channels == 1 ? ReadMono(buffer) : ReadDownmixed(buffer);

    private int ReadMono(Span<float> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var wanted = Math.Min(MaxSamplesPerRead, buffer.Length - total);
            var read = _source.Read(buffer.Slice(total, wanted));
            if (read <= 0)
            {
                break;
            }

            total += read;
            if (read < wanted)
            {
                break;
            }
        }

        return total;
    }

    private int ReadDownmixed(Span<float> buffer)
    {
        var framesPerRead = Math.Max(1, MaxSamplesPerRead / _channels);
        var total = 0;
        while (total < buffer.Length)
        {
            int needed = Math.Min(framesPerRead, buffer.Length - total) * _channels;
            if (_buffer.Length < needed)
            {
                _buffer = new float[needed];
            }

            int read = _source.Read(_buffer.AsSpan(0, needed));
            int frames = read / _channels;
            for (int frame = 0; frame < frames; frame++)
            {
                float sum = 0f;
                int baseIndex = frame * _channels;
                for (int channel = 0; channel < _channels; channel++)
                {
                    sum += _buffer[baseIndex + channel];
                }

                buffer[total + frame] = sum / _channels;
            }

            total += frames;
            if (read < needed)
            {
                break;
            }
        }

        return total;
    }
}
