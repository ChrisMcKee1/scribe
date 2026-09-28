using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Diagnostics;
using Scribe.Core.TextInjection;

namespace Scribe.Benchmarks;

/// <summary>Production's IN1 typing loop with fake native boundaries; no worker, input, clipboard or real sleep.</summary>
[MemoryDiagnoser]
public class InputTimingsBenchmarks
{
    private TextInjector _injector = null!;
    private string _text = "";

    [Params(130, 770)]
    public int Characters { get; set; }

#if PERF_BASELINE_BUILD
    [Params(false)]
#else
    [Params(false, true)]
#endif
    public bool Timings { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _text = new string('x', Characters);
#if PERF_BASELINE_BUILD
        _injector = new TextInjector(NullLogger<TextInjector>.Instance, new Platform(), new NoClipboard());
#else
        _injector = new TextInjector(NullLogger<TextInjector>.Instance, new Platform(), new NoClipboard(),
            Timings ? PerfFlags.Parse(PerfFlags.InputTimings) : PerfFlags.None);
#endif
    }

    [Benchmark]
    public int TypeWithNoNativeInput()
    {
#if PERF_BASELINE_BUILD
        return _injector.TypeUnicode(_text, 42, true, default, TypingPace.Local).Sent;
#else
        var timing = Timings ? new InjectionTimingMeasurement(TimeProvider.System) : null;
        timing?.StartWorker();
        var result = _injector.TypeUnicode(_text, 42, true, default, TypingPace.Local, timing);
        timing?.StopWorker();
        return result.Sent;
#endif
    }

    private sealed class Platform : IInjectionPlatform
    {
        public nint GetForegroundWindow() => 42;
        public uint SendInput(ReadOnlySpan<InjectionNativeMethods.INPUT> inputs) => (uint)inputs.Length;
        public bool TryInsertIntoStandardEdit(string text, nint expectedForegroundWindow) => false;
        public void Sleep(int milliseconds) { }
        public KeyScanCode ScanCodeOf(ushort virtualKey) => default;
    }

    private sealed class NoClipboard : IClipboardNative
    {
        public uint SequenceNumber => throw new InvalidOperationException();
        public int FormatCount => throw new InvalidOperationException();
        public bool IsFormatAvailable(uint format) => throw new InvalidOperationException();
        public uint RegisterFormat(string name) => throw new InvalidOperationException();
        public bool TryOpen() => throw new InvalidOperationException();
        public void Close() => throw new InvalidOperationException();
        public bool Empty() => throw new InvalidOperationException();
        public bool TryReadText([NotNullWhen(true)] out string? text) => throw new InvalidOperationException();
        public bool SetText(string text) => throw new InvalidOperationException();
        public bool SetData(uint format, ReadOnlySpan<byte> data) => throw new InvalidOperationException();
        public bool TryReadData(uint format, Span<byte> destination) => throw new InvalidOperationException();
    }
}
