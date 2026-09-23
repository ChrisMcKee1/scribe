namespace Scribe.Core.Models;

/// <summary>An active input device that can be selected for capture.</summary>
/// <param name="Id">The endpoint ID string Windows gave the device. Opaque: compare it, never parse it.</param>
/// <param name="Name">The friendly name Windows shows for it.</param>
/// <param name="IsDefault">
/// True for the device Scribe records from while it follows Windows: the default input device, the one Windows Settings
/// shows under Sound, Input (see <see cref="Audio.DefaultInputDevice"/>).
/// </param>
/// <param name="IsCommunicationsDefault">
/// True for the device Windows gives voice calls (the communications role). It can be a different device from the
/// default, which is why it is reported separately.
/// </param>
public sealed record AudioDevice(string Id, string Name, bool IsDefault, bool IsCommunicationsDefault = false);

/// <summary>
/// A finished mono PCM capture, normalized to <see cref="SampleRate"/> Hz as 32-bit floats
/// in the range [-1, 1], the format sherpa-onnx and Silero VAD expect.
/// </summary>
public sealed record CapturedAudio(float[] Samples, int SampleRate = 16000)
{
    public static CapturedAudio Empty { get; } = new(Array.Empty<float>());

    public bool IsEmpty => Samples.Length == 0;

    public TimeSpan Duration =>
        SampleRate <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((double)Samples.Length / SampleRate);
}
