using NAudio.Wave;

namespace Scribe.Core.Audio;

/// <summary>
/// Downmixes an arbitrary-channel float source to a single mono channel by averaging the
/// channels of each frame. Works for any channel count (1..N), unlike NAudio's
/// <c>StereoToMonoSampleProvider</c> which only handles two channels.
/// </summary>
internal sealed class MonoDownmixSampleProvider : ISampleProvider
{
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

    public int Read(float[] buffer, int offset, int count)
    {
        if (_channels == 1)
        {
            return _source.Read(buffer, offset, count);
        }

        int needed = count * _channels;
        if (_buffer.Length < needed)
        {
            _buffer = new float[needed];
        }

        int read = _source.Read(_buffer, 0, needed);
        int frames = read / _channels;
        for (int frame = 0; frame < frames; frame++)
        {
            float sum = 0f;
            int baseIndex = frame * _channels;
            for (int channel = 0; channel < _channels; channel++)
            {
                sum += _buffer[baseIndex + channel];
            }

            buffer[offset + frame] = sum / _channels;
        }

        return frames;
    }
}
