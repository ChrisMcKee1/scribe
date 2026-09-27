using System.Runtime.InteropServices;
using NAudio.Wave;
using Scribe.Core.Audio;
using Scribe.Core.Tests.Concurrency;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// Pins that the capture service converts through a scratch pool of its own and that the idle release gives that scratch
/// back together with the working buffer, before the compaction that returns large buffers to the OS. With the shared
/// pool, the scratch stayed in the converting thread's own slots through that collection (4 MB after a 30 s dictation).
/// </summary>
public sealed class AudioCaptureServiceScratchTests
{
    private const int SamplesPerPacket = 160; // 10 ms at 16 kHz, the fake capture's format

    [Fact]
    public void The_idle_release_frees_the_conversion_scratch_with_the_working_buffer()
    {
        var stack = new FakeCaptureStack();
        using var service = stack.CreateService(BlockedThreads.SafetyTimeout);

        Assert.True(service.Start(deviceId: null, owner: 1));
        var format = stack.Capture.WaveFormat;
        Speak(stack, service, packets: 200);
        var audio = service.Stop(owner: 1);

        // 2 s of 16 kHz mono grows the conversion's scratch through 16,384 and 32,768 samples, both kept afterwards.
        Assert.Equal(200 * SamplesPerPacket, audio.Samples.Length);
        var expected = AudioCaptureService.ReservationBytes(format) + ((16_384 + 32_768) * sizeof(float));
        Assert.Equal(expected, service.ReleaseRetainedBuffers());
        Assert.Equal(0, service.ReleaseRetainedBuffers());
    }

    [Fact]
    public void Two_dictations_leave_one_set_of_scratch_and_both_results_stay_owned()
    {
        var stack = new FakeCaptureStack();
        using var service = stack.CreateService(BlockedThreads.SafetyTimeout);

        Assert.True(service.Start(deviceId: null, owner: 1));
        var format = stack.Capture.WaveFormat;
        Speak(stack, service, packets: 200);
        var first = service.Stop(owner: 1);
        var firstSnapshot = (float[])first.Samples.Clone();

        Assert.True(service.Start(deviceId: null, owner: 2));
        Speak(stack, service, packets: 200, amplitude: 0.1f);
        var second = service.Stop(owner: 2);

        // The owned results stay as they were converted, whatever the reused scratch held in between.
        Assert.Equal(firstSnapshot, first.Samples);
        Assert.NotSame(first.Samples, second.Samples);
        Assert.All(second.Samples, sample => Assert.InRange(sample, -0.1f, 0.1f));

        // The second conversion reused the first one's scratch: one set is kept, not one per conversion.
        var expected = AudioCaptureService.ReservationBytes(format) + ((16_384 + 32_768) * sizeof(float));
        Assert.Equal(expected, service.ReleaseRetainedBuffers());
    }

    // Delivers speech-like 10 ms packets to the live capture and returns once the service has recorded all of them.
    private static void Speak(FakeCaptureStack stack, AudioCaptureService service, int packets, float amplitude = 0.25f)
    {
        using var recorded = new CountdownEvent(packets);
        void OnLevel(object? sender, float level) => recorded.Signal();
        service.LevelChanged += OnLevel;
        try
        {
            for (var i = 0; i < packets; i++)
            {
                stack.Capture.Deliver(Packet(amplitude));
            }

            Assert.True(recorded.Wait(BlockedThreads.SafetyTimeout));
        }
        finally
        {
            service.LevelChanged -= OnLevel;
        }
    }

    private static byte[] Packet(float amplitude)
    {
        var samples = new float[SamplesPerPacket];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = amplitude * MathF.Sin(i * 0.05f);
        }

        return MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
    }
}
