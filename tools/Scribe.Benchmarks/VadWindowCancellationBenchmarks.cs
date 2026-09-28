using System.Reflection;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Diagnostics;
using Scribe.Core.Models;
using Scribe.Core.Vad;

namespace Scribe.Benchmarks;

/// <summary>
/// AUDIO-A-08 (<c>PerfFlags.VadWindowCancellation</c>): a trim that nobody cancels, over 200 s of speech through the real
/// Silero model, with the flag off and on. On, the trim checks its token before every one of its 6,250 windows; the row
/// allows that 3% at most. Both arms call the token overload with a live token when the build has it (found by
/// reflection, so this file also builds on the baseline, whose only arm is <c>Trim(audio)</c>).
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("Audio")]
public class VadWindowCancellationBenchmarks
{
    private static readonly MethodInfo? TokenOverload =
        typeof(VadService).GetMethod("Trim", BindingFlags.Instance | BindingFlags.Public, [typeof(CapturedAudio), typeof(CancellationToken)]);

    private static readonly ConstructorInfo? FlagsConstructor = typeof(VadService).GetConstructor(
        BindingFlags.Instance | BindingFlags.Public,
        [typeof(Scribe.Core.Infrastructure.ModelLocator), typeof(ILogger<VadService>), typeof(PerfFlags)]);

    private readonly CancellationTokenSource _live = new();
    private VadService? _vad;
    private Func<CapturedAudio, CapturedAudio> _trim = static audio => audio;
    private CapturedAudio _speech = CapturedAudio.Empty;

    [ParamsSource(nameof(Modes))]
    public string Cancellation { get; set; } = string.Empty;

    public static IEnumerable<string> Modes =>
        TokenOverload is null || FlagsConstructor is null ? ["flag off"] : ["flag off", "flag on"];

    [GlobalSetup]
    public void Setup()
    {
        var locator = AudioBenchmarkInputs.Models();
        _vad = FlagsConstructor is null
            ? new VadService(locator, NullLogger<VadService>.Instance)
            : (VadService)FlagsConstructor.Invoke(
                [locator, NullLogger<VadService>.Instance, Cancellation == "flag on" ? PerfFlags.Parse("VadWindowCancellation") : PerfFlags.None]);
        _vad.Initialize();
        if (!_vad.IsAvailable)
        {
            throw new InvalidOperationException("The Silero VAD model was not found; set SCRIBE_MODELS_DIR to the models folder.");
        }

        _speech = new CapturedAudio(AudioBenchmarkInputs.Speech(200), 16_000);
        if (TokenOverload is null)
        {
            _trim = _vad.Trim;
        }
        else
        {
            var withToken = TokenOverload.CreateDelegate<Func<CapturedAudio, CancellationToken, CapturedAudio>>(_vad);
            var token = _live.Token;
            _trim = audio => withToken(audio, token);
        }

        if (_trim(_speech).IsEmpty)
        {
            throw new InvalidOperationException("The VAD found no speech in the benchmark's input.");
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _vad?.Dispose();
        _live.Dispose();
    }

    [Benchmark]
    public CapturedAudio UncanceledTrim() => _trim(_speech);
}
