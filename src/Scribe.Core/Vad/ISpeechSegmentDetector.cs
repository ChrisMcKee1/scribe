namespace Scribe.Core.Vad;

/// <summary>
/// A loaded voice activity detector as <see cref="VadService"/> drives it. The production implementation wraps
/// sherpa-onnx's Silero detector; the seam lets the service's locking and lifetime be tested without loading the
/// native model.
/// </summary>
/// <remarks>Not thread-safe. The service touches it only while holding its gate.</remarks>
internal interface ISpeechSegmentDetector : IDisposable
{
    /// <summary>Samples per window that <see cref="AcceptWaveform"/> expects.</summary>
    int WindowSize { get; }

    /// <summary>Clears all detector state so the next capture starts fresh.</summary>
    void Reset();

    /// <summary>Feeds exactly one window of 16 kHz samples. The buffer is reused by the caller afterwards.</summary>
    void AcceptWaveform(float[] window);

    /// <summary>Ends the capture so a trailing speech segment is emitted.</summary>
    void Flush();

    /// <summary>Removes the oldest completed speech segment, reporting its absolute sample offset and length.</summary>
    bool TryPopSegment(out int start, out int length);
}
