using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Hotkeys;
using Scribe.Core.TextInjection;
using static Scribe.Core.TextInjection.InjectionNativeMethods;

namespace Scribe.Benchmarks;

[MemoryDiagnoser]
public class TextInjectionBenchmarks
{
    private const nint Window = 0x4242;
    private readonly Consumer _consumer = new();
    private readonly TextInjector _injector = new(NullLogger<TextInjector>.Instance, new QuietPlatform(), new QuietClipboard());
    private string _text = string.Empty;

    [Params(TextInjectionScenario.LocalPlain, TextInjectionScenario.RemotePlain, TextInjectionScenario.LocalLineBreaks)]
    public TextInjectionScenario Scenario { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _text = Scenario switch
        {
            TextInjectionScenario.LocalPlain => Words(746),
            TextInjectionScenario.RemotePlain => Words(746),
            TextInjectionScenario.LocalLineBreaks => Paragraphs(746),
            _ => throw new InvalidOperationException("Unknown text injection benchmark scenario."),
        };
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("TextInjection")]
    public int OldListBuilder()
    {
        var pace = Pace();
        int batches = 0;
        for (int start = 0; start < _text.Length;)
        {
            int count = TextInjector.ChunkLength(_text, start, pace.BatchUnits, pace.PreferWordBoundary);
            var inputs = OldBuildUnicodeChunk(_text, start, count, shiftEnter: true);
            _consumer.Consume(inputs);
            batches++;
            start += count;
        }

        return batches;
    }

    // The shipping typing loop, TypeUnicode: every batch written into one buffer made after the first focus check and
    // handed to SendInput as a span, through a platform that answers without allocating. perf-051 measured its exact
    // array per batch here (DirectArrayBuilder); after the reconciliation BuildUnicodeChunk is a test-only wrapper, so the
    // arm runs the loop itself. Its allocation is everything typing allocates; OldListBuilder's is everything 0.5.0's did.
    [Benchmark]
    [BenchmarkCategory("TextInjection")]
    public int ReusedBufferTyping() => _injector.TypeUnicode(_text, Window, shiftEnter: true, default, Pace()).Sent;

    private TypingPace Pace() =>
        Scenario == TextInjectionScenario.RemotePlain ? TypingPace.RemoteSession : TypingPace.Local;

    private static INPUT[] OldBuildUnicodeChunk(
        string text, int start, int count, bool shiftEnter = true, InjectionKeys keys = default)
    {
        var inputs = new List<INPUT>(count * 2);
        int end = start + count;
        for (int i = start; i < end; i++)
        {
            char ch = text[i];
            if (ch is '\r' or '\n')
            {
                inputs.AddRange(OldBuildLineBreak(shiftEnter, keys));
                if (ch == '\r' && i + 1 < end && text[i + 1] == '\n')
                {
                    i++;
                }

                continue;
            }

            inputs.Add(UnicodeKey(ch, keyUp: false));
            inputs.Add(UnicodeKey(ch, keyUp: true));
        }

        return [.. inputs];
    }

    private static IEnumerable<INPUT> OldBuildLineBreak(bool shiftEnter, InjectionKeys keys = default)
    {
        if (!shiftEnter)
        {
            return [KeyDown(VK_RETURN, keys.Return), KeyUp(VK_RETURN, keys.Return)];
        }

        return
        [
            KeyDown(VK_SHIFT, keys.Shift),
            KeyDown(VK_RETURN, keys.Return),
            KeyUp(VK_RETURN, keys.Return),
            KeyUp(VK_SHIFT, keys.Shift),
        ];
    }

    private static INPUT KeyDown(ushort virtualKey, KeyScanCode scan) =>
        KeyboardInput(virtualKey, scan.Code, KeyScanCodes.Flags(scan, keyUp: false));

    private static INPUT KeyUp(ushort virtualKey, KeyScanCode scan) =>
        KeyboardInput(virtualKey, scan.Code, KeyScanCodes.Flags(scan, keyUp: true));

    private static INPUT UnicodeKey(char ch, bool keyUp)
    {
        uint flags = KEYEVENTF_UNICODE | (keyUp ? KEYEVENTF_KEYUP : 0);
        return KeyboardInput(0, ch, flags);
    }

    private static INPUT KeyboardInput(ushort virtualKey, ushort scanCode, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = virtualKey,
                wScan = scanCode,
                dwFlags = flags,
                time = 0,
                dwExtraInfo = SyntheticInputMarker.Value,
            },
        },
    };

    private static string Words(int length)
    {
        const string sentence = "Please send the revised figures to the team before the review on Friday. ";
        return RepeatToLength(sentence, length);
    }

    private static string Paragraphs(int length)
    {
        const string paragraph = "Please send the revised figures to the team before the review on Friday.\r\n";
        return RepeatToLength(paragraph, length);
    }

    private static string RepeatToLength(string unit, int length)
    {
        var text = string.Concat(Enumerable.Repeat(unit, (length / unit.Length) + 1));
        return text[..length];
    }

    // Answers every call without allocating, so ReusedBufferTyping measures only the injector.
    private sealed class QuietPlatform : IInjectionPlatform
    {
        public nint GetForegroundWindow() => Window;

        public uint SendInput(ReadOnlySpan<INPUT> inputs) => (uint)inputs.Length;

        public bool TryInsertIntoStandardEdit(string text, nint expectedForegroundWindow) => false;

        public void Sleep(int milliseconds)
        {
        }

        public KeyScanCode ScanCodeOf(ushort virtualKey) => default;
    }

    // Typing never reaches the clipboard; every call refuses.
    private sealed class QuietClipboard : IClipboardNative
    {
        public uint SequenceNumber => 0;

        public int FormatCount => 0;

        public bool IsFormatAvailable(uint format) => false;

        public uint RegisterFormat(string name) => 0;

        public bool TryOpen() => false;

        public void Close()
        {
        }

        public bool Empty() => false;

        public bool TryReadText([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? text)
        {
            text = null;
            return false;
        }

        public bool SetText(string text) => false;

        public bool SetData(uint format, ReadOnlySpan<byte> data) => false;

        public bool TryReadData(uint format, Span<byte> destination) => false;
    }
}

public enum TextInjectionScenario
{
    LocalPlain,
    RemotePlain,
    LocalLineBreaks,
}
